using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenCvSharp;
using plusnot.Rendering;

namespace plusnot.Pipeline;

public sealed class FramePipeline : IDisposable
{
    readonly CameraCapture camera = new();
    readonly BackgroundSegmenter seg = new();
    readonly AudioCapture audio = new();
    readonly Compositor comp = new();
    Thread? thread; volatile bool running;
    readonly Dispatcher disp;
    readonly TextBlock fpsTb;
    readonly Stopwatch sw = new();

    volatile bool capBg; Mat? bgAcc; int bgCount; const int BgNeeded = 15; Action<string>? bgCb;

    public bool SegmentationEnabled { get; set; } = true;
    public bool HudEnabled { get; set; } = true;
    public SegmentationModel ActiveModel => seg.ActiveModel;
    public bool CameraAvailable => camera.IsOpen;
    public bool HasReferenceBackground => seg.HasReferenceBackground;
    public int BlurSize { get => seg.BlurSize; set => seg.BlurSize = value; }
    public int MaskThreshold { get => seg.MaskThreshold; set => seg.MaskThreshold = value; }
    public int FeatherErode { get => seg.FeatherErode; set => seg.FeatherErode = value; }
    public bool PipelineDebugEnabled { get => seg.DebugCapture; set => seg.DebugCapture = value; }
    public int DiffThreshold { get => seg.DiffThreshold; set => seg.DiffThreshold = value; }
    public int DiffDilate { get => seg.DiffDilate; set => seg.DiffDilate = value; }

    public FramePipeline(Dispatcher d, TextBlock fps) { disp = d; fpsTb = fps; }

    public string? Start(Image img, int cw = 640, int ch = 480)
    {
        bool ok = camera.Start(0, cw, ch);
        if (!seg.Initialize(SegmentationModel.MediaPipe))
            if (!seg.Initialize(SegmentationModel.MODNet))
                seg.Initialize(SegmentationModel.SINet);
        audio.Start();
        var bg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "default_bg.jpg");
        if (File.Exists(bg)) comp.SetBackground(bg);
        sw.Start(); running = true;
        if (!ok) { var err = camera.Error ?? "Camera unavailable"; ShowError(img, err); return err; }
        seg.StartAsync();
        thread = new Thread(() => Loop(img)) { IsBackground = true, Name = "FramePipeline" };
        thread.Start();
        return null;
    }

    void ShowError(Image img, string err) => disp.Invoke(() => { int w = 1280, h = 720; var b = comp.EnsureBitmap(w, h); comp.ComposeError(b, w, h, err); img.Source = b; });

    void Loop(Image img)
    {
        using var mat = new Mat(); using var bgra = new Mat();
        bool srcSet = false; int fc = 0; double lastFt = 0; int pend = 0;
        byte[]? px = null; float[] wav = new float[256];
        int segFrame = 0; byte[]? prevRawMask = null, prevMaskCopy = null, smoothMask = null;

        while (running)
        {
            if (!camera.Read(mat)) { Thread.Sleep(1); continue; }
            if (pend > 0) continue;
            int w = mat.Width, h = mat.Height;

            if (capBg)
            {
                using var ff = new Mat(); mat.ConvertTo(ff, MatType.CV_32FC3);
                bgAcc ??= new Mat(h, w, MatType.CV_32FC3, Scalar.All(0));
                Cv2.Add(bgAcc, ff, bgAcc); bgCount++;
                var cb = bgCb; int cnt = bgCount, tot = BgNeeded;
                if (bgCount >= BgNeeded)
                {
                    using var a32 = new Mat(); bgAcc.ConvertTo(a32, MatType.CV_32FC3, 1.0 / bgCount);
                    using var a8 = new Mat(); a32.ConvertTo(a8, MatType.CV_8UC3);
                    seg.SetReferenceBackground(a8);
                    bgAcc.Dispose(); bgAcc = null; capBg = false;
                    disp.BeginInvoke(() => cb?.Invoke("Background captured!"));
                }
                else disp.BeginInvoke(() => cb?.Invoke($"Capturing... {cnt}/{tot}"));
            }

            if (SegmentationEnabled && !capBg && (++segFrame & 1) == 0) seg.SubmitFrame(mat, w, h);
            byte[]? mask = (SegmentationEnabled && !capBg) ? seg.GetLatestMask() : null;
            if (mask != null && mask != prevRawMask)
            {
                if (smoothMask == null || smoothMask.Length != mask.Length) smoothMask = new byte[mask.Length];
                if (prevMaskCopy != null && prevMaskCopy.Length == mask.Length)
                    for (int i = 0; i < mask.Length; i++) smoothMask[i] = (byte)(mask[i] * 0.7f + prevMaskCopy[i] * 0.3f);
                else Buffer.BlockCopy(mask, 0, smoothMask, 0, mask.Length);
                if (prevMaskCopy == null || prevMaskCopy.Length != mask.Length) prevMaskCopy = new byte[mask.Length];
                Buffer.BlockCopy(smoothMask, 0, prevMaskCopy, 0, smoothMask.Length);
                prevRawMask = mask;
            }
            if (smoothMask != null) mask = smoothMask;

            Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
            int need = w * h * 4;
            if (px == null || px.Length != need) px = new byte[need];
            System.Runtime.InteropServices.Marshal.Copy(bgra.Data, px, 0, need);
            audio.Fill(wav);

            double t = sw.Elapsed.TotalSeconds;
            PipelineDebugData? debugData = null;
            if (seg.DebugCapture && mask != null)
            {
                var (raw, post, diff, dsz) = seg.GetDebugMasks();
                if (raw != null && post != null)
                    debugData = new PipelineDebugData { RawMask = raw, PostMask = post, DiffMask = diff, FinalMask = mask, RawW = dsz, RawH = dsz, FinalW = w, FinalH = h };
            }
            comp.ComposeOffscreen(px, mask, wav, w, h, HudEnabled, t, SegmentationEnabled, seg.ActiveModel.ToString(), debugData);

            fc++; string? fps = null;
            if (t - lastFt >= 1) { fps = $"FPS: {fc / (t - lastFt):F1}"; fc = 0; lastFt = t; }

            bool setS = !srcSet; int bw = w, bh = h;
            Interlocked.Increment(ref pend);
            disp.BeginInvoke(() =>
            {
                try
                {
                    var bmp = comp.EnsureBitmap(bw, bh);
                    comp.BlitToWriteableBitmap(bmp, bw, bh);
                    if (setS) img.Source = bmp;
                    if (fps != null) fpsTb.Text = fps;
                }
                finally { Interlocked.Decrement(ref pend); }
            });
            srcSet = true;
        }
    }

    public void CaptureBackground(Action<string> cb) { bgCb = cb; bgAcc?.Dispose(); bgAcc = null; bgCount = 0; capBg = true; }
    public void ClearReferenceBackground() => seg.ClearReferenceBackground();
    public void Stop() { running = false; seg.StopAsync(); thread?.Join(2000); }
    public void SetBackground(string p) => comp.SetBackground(p);
    public bool SetModel(SegmentationModel m) => seg.Initialize(m);
    public void OpenCameraSettings() => camera.OpenSettings();
    public void Dispose() { audio.Dispose(); seg.Dispose(); camera.Dispose(); comp.Dispose(); bgAcc?.Dispose(); }
}
