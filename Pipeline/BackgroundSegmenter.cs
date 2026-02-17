using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Marshal = System.Runtime.InteropServices.Marshal;

namespace plusnot.Pipeline;

public enum SegmentationModel { MODNet, MediaPipe, SINet }

public sealed class BackgroundSegmenter : IDisposable
{
    private InferenceSession? session;
    private SegmentationModel model;
    private readonly object sLock = new(), fLock = new();
    private byte[]? latestMask;
    private volatile bool busy, running;
    private Thread? thread;
    private Mat? pending, submitBuf;
    private int pw, ph;

    private Mat? refBg;
    private volatile bool useRef;

    public int BlurSize { get; set; } = 7;
    public int MaskThreshold { get; set; }
    public int FeatherErode { get; set; }
    public int OnnxThreads { get; set; } = 4;
    public bool HasReferenceBackground => useRef;
    public SegmentationModel ActiveModel => model;

    static readonly Dictionary<SegmentationModel, (string file, int size)> Models = new()
    {
        [SegmentationModel.MODNet] = ("modnet.onnx", 512),
        [SegmentationModel.MediaPipe] = ("mediapipe_selfie.onnx", 256),
        [SegmentationModel.SINet] = ("sinet.onnx", 320),
    };

    public bool Initialize(SegmentationModel m)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", Models[m].file);
        if (!File.Exists(path)) return false;
        lock (sLock)
        {
            session?.Dispose();
            int t = Math.Clamp(OnnxThreads, 1, 16);
            var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, InterOpNumThreads = Math.Max(1, t / 2), IntraOpNumThreads = t };
            o.EnableMemoryPattern = true;
            session = new InferenceSession(path, o); model = m;
        }
        return true;
    }

    public void SetReferenceBackground(Mat avg) { lock (sLock) { refBg?.Dispose(); refBg = avg.Clone(); useRef = true; } }
    public void ClearReferenceBackground() { lock (sLock) { useRef = false; refBg?.Dispose(); refBg = null; } }

    public void StartAsync()
    {
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "Segmentation", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public void SubmitFrame(Mat bgr, int ow, int oh)
    {
        if (busy) return;
        lock (fLock)
        {
            if (submitBuf == null || submitBuf.Rows != bgr.Rows || submitBuf.Cols != bgr.Cols || submitBuf.Type() != bgr.Type())
            { submitBuf?.Dispose(); submitBuf = new Mat(bgr.Rows, bgr.Cols, bgr.Type()); }
            bgr.CopyTo(submitBuf); pending = submitBuf; pw = ow; ph = oh;
        }
    }

    public byte[]? GetLatestMask() => latestMask;

    void Loop()
    {
        Mat? work = null;
        while (running)
        {
            int w, h;
            lock (fLock)
            {
                if (pending == null) { Thread.Sleep(1); continue; }
                if (work == null || work.Rows != pending.Rows || work.Cols != pending.Cols || work.Type() != pending.Type())
                { work?.Dispose(); work = new Mat(pending.Rows, pending.Cols, pending.Type()); }
                pending.CopyTo(work); pending = null; w = pw; h = ph;
            }
            busy = true;
            try { var m = Segment(work, w, h); if (m != null) latestMask = m; }
            finally { busy = false; }
        }
        work?.Dispose();
    }

    byte[]? Segment(Mat cam, int ow, int oh)
    {
        lock (sLock)
        {
            if (session == null) return null;
            int sz = Models[model].size;
            using var resized = new Mat(); Cv2.Resize(cam, resized, new Size(sz, sz));
            var tensor = new DenseTensor<float>(new[] { 1, 3, sz, sz });
            FillTensor(resized, tensor, sz);

            var inp = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor) };
            using var res = session.Run(inp);
            var outT = res.First().AsTensor<float>();
            var mask = ExtractMask(outT, sz);

            using var maskMat = new Mat(sz, sz, MatType.CV_8UC1);
            Marshal.Copy(mask, 0, maskMat.Data, mask.Length);

            using var proc = new Mat(); PostProcess(maskMat, proc);

            // Optional background-diff combination
            Mat final_ = proc;
            Mat? diffResult = null;
            if (useRef && refBg != null)
            {
                using var cr = new Mat(); Cv2.Resize(cam, cr, new Size(refBg.Cols, refBg.Rows));
                using var diff = new Mat(); Cv2.Absdiff(cr, refBg, diff);
                using var gray = new Mat(); Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
                using var bin = new Mat(); Cv2.Threshold(gray, bin, 25, 255, ThresholdTypes.Binary);
                using var kern = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
                using var dil = new Mat(); Cv2.Dilate(bin, dil, kern);
                using var dr = new Mat(); Cv2.Resize(dil, dr, new Size(proc.Cols, proc.Rows));
                diffResult = new Mat(); Cv2.BitwiseAnd(proc, dr, diffResult);
                final_ = diffResult;
            }

            using var outMat = new Mat(); Cv2.Resize(final_, outMat, new Size(ow, oh));
            diffResult?.Dispose();
            var result = new byte[ow * oh];
            Marshal.Copy(outMat.Data, result, 0, result.Length);
            return result;
        }
    }

    void PostProcess(Mat src, Mat dst)
    {
        Mat cur = src; Mat? t1 = null, t2 = null;
        int th = MaskThreshold; if (th > 0) { t1 = new Mat(); Cv2.Threshold(cur, t1, th, 255, ThresholdTypes.Binary); cur = t1; }
        int er = FeatherErode; if (er > 0) { t2 = new Mat(); var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(er * 2 + 1, er * 2 + 1)); Cv2.Erode(cur, t2, k); cur = t2; }
        int bl = BlurSize; if (bl > 1) { int k = bl | 1; Cv2.GaussianBlur(cur, dst, new Size(k, k), k / 3.0); } else cur.CopyTo(dst);
        t1?.Dispose(); t2?.Dispose();
    }

    unsafe void FillTensor(Mat m, DenseTensor<float> tensor, int sz)
    {
        byte* ptr = (byte*)m.Data; int step = (int)m.Step();
        bool mod = model == SegmentationModel.MODNet;
        for (int y = 0; y < sz; y++)
        {
            byte* row = ptr + y * step;
            for (int x = 0; x < sz; x++)
            {
                int o = x * 3; float b = row[o], g = row[o + 1], r = row[o + 2];
                if (mod) { tensor[0, 0, y, x] = (r - 127.5f) / 127.5f; tensor[0, 1, y, x] = (g - 127.5f) / 127.5f; tensor[0, 2, y, x] = (b - 127.5f) / 127.5f; }
                else { tensor[0, 0, y, x] = r / 255f; tensor[0, 1, y, x] = g / 255f; tensor[0, 2, y, x] = b / 255f; }
            }
        }
    }

    byte[] ExtractMask(Tensor<float> o, int sz)
    {
        var m = new byte[sz * sz];
        if (model == SegmentationModel.SINet) { for (int y = 0; y < sz; y++) for (int x = 0; x < sz; x++) m[y * sz + x] = (byte)(Math.Clamp(o[0, 1, y, x], 0f, 1f) * 255); }
        else { for (int i = 0; i < m.Length; i++) m[i] = (byte)(Math.Clamp(o.GetValue(i), 0f, 1f) * 255); }
        return m;
    }

    public void StopAsync() { running = false; thread?.Join(2000); }
    public void Dispose()
    {
        StopAsync();
        lock (sLock) { session?.Dispose(); session = null; refBg?.Dispose(); refBg = null; }
        lock (fLock) { pending = null; submitBuf?.Dispose(); submitBuf = null; }
    }
}
