using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenCvSharp;
using plusnot.Rendering;

namespace plusnot.Pipeline;

public sealed class FramePipeline : IDisposable
{
    private readonly CameraCapture camera = new();
    private readonly BackgroundSegmenter segmenter = new();
    private readonly AudioCapture audio = new();
    private readonly Compositor compositor = new();
    private Thread? thread;
    private volatile bool running;
    private readonly Dispatcher dispatcher;
    private readonly TextBlock fpsText;
    private readonly Stopwatch stopwatch = new();

    // Background capture state
    private volatile bool capturingBackground;
    private Mat? bgAccumulator;
    private int bgFrameCount;
    private const int BgFramesNeeded = 15;
    private Action<string>? bgStatusCallback;

    public bool SegmentationEnabled { get; set; } = true;
    public bool HudEnabled { get; set; } = true;
    public SegmentationModel ActiveModel => segmenter.ActiveModel;
    public bool CameraAvailable => camera.IsOpen;
    public bool HasReferenceBackground => segmenter.HasReferenceBackground;

    // Expose segmenter tuning
    public int BlurSize { get => segmenter.BlurSize; set => segmenter.BlurSize = value; }
    public int MaskThreshold { get => segmenter.MaskThreshold; set => segmenter.MaskThreshold = value; }
    public int FeatherErode { get => segmenter.FeatherErode; set => segmenter.FeatherErode = value; }

    public FramePipeline(Dispatcher dispatcher, TextBlock fpsText)
    {
        this.dispatcher = dispatcher;
        this.fpsText = fpsText;
    }

    public string? Start(System.Windows.Controls.Image displayImage, int camWidth = 640, int camHeight = 480)
    {
        bool cameraOk = camera.Start(0, camWidth, camHeight);

        // Default to MediaPipe, fallback to MODNet, then SINet
        if (!segmenter.Initialize(SegmentationModel.MediaPipe))
            if (!segmenter.Initialize(SegmentationModel.MODNet))
                segmenter.Initialize(SegmentationModel.SINet);

        audio.Start();

        var bgPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "default_bg.jpg");
        if (File.Exists(bgPath))
            compositor.SetBackground(bgPath);

        stopwatch.Start();
        running = true;

        if (!cameraOk)
        {
            string error = camera.ErrorMessage ?? "Camera unavailable";
            ShowErrorFrame(displayImage, error);
            return error;
        }

        segmenter.StartAsync();

        thread = new Thread(() => PipelineLoop(displayImage))
        {
            IsBackground = true,
            Name = "FramePipeline"
        };
        thread.Start();
        return null;
    }

    private void ShowErrorFrame(System.Windows.Controls.Image displayImage, string error)
    {
        dispatcher.Invoke(() =>
        {
            int w = 1280, h = 720;
            var bitmap = compositor.EnsureBitmap(w, h);
            compositor.ComposeError(bitmap, w, h, error);
            displayImage.Source = bitmap;
        });
    }

    private void PipelineLoop(System.Windows.Controls.Image displayImage)
    {
        using var mat = new Mat();
        using var bgraMat = new Mat();
        bool sourceSet = false;
        int frameCount = 0;
        double lastFpsTime = 0;
        int pendingFrames = 0;

        // Pre-allocated buffers
        byte[]? pixelBytes = null;
        float[] waveform = new float[256];



        while (running)
        {
            if (!camera.ReadFrame(mat))
            {
                Thread.Sleep(1);
                continue;
            }

            if (pendingFrames > 0)
                continue;

            int w = mat.Width;
            int h = mat.Height;

            // Background capture mode: accumulate frames, skip segmentation
            if (capturingBackground)
            {
                using var floatFrame = new Mat();
                mat.ConvertTo(floatFrame, MatType.CV_32FC3);

                if (bgAccumulator == null)
                {
                    bgAccumulator = new Mat(h, w, MatType.CV_32FC3, Scalar.All(0));
                }
                Cv2.Add(bgAccumulator, floatFrame, bgAccumulator);
                bgFrameCount++;

                var cb = bgStatusCallback;
                int count = bgFrameCount;
                int total = BgFramesNeeded;

                if (bgFrameCount >= BgFramesNeeded)
                {
                    // Compute average
                    using var averaged32 = new Mat();
                    bgAccumulator.ConvertTo(averaged32, MatType.CV_32FC3, 1.0 / bgFrameCount);

                    using var averaged8 = new Mat();
                    averaged32.ConvertTo(averaged8, MatType.CV_8UC3);

                    segmenter.SetReferenceBackground(averaged8);

                    bgAccumulator.Dispose();
                    bgAccumulator = null;
                    capturingBackground = false;

                    dispatcher.BeginInvoke(() => cb?.Invoke("Background captured!"));
                }
                else
                {
                    dispatcher.BeginInvoke(() => cb?.Invoke($"Capturing... {count}/{total}"));
                }

                // During capture, still show camera feed without segmentation
                // Fall through to render without mask
            }

            if (SegmentationEnabled && !capturingBackground)
                segmenter.SubmitFrame(mat, w, h);

            byte[]? mask = (SegmentationEnabled && !capturingBackground) ? segmenter.GetLatestMask() : null;

            // Reuse bgraMat
            Cv2.CvtColor(mat, bgraMat, ColorConversionCodes.BGR2BGRA);

            // Pre-allocate / reuse pixel buffer
            int needed = w * h * 4;
            if (pixelBytes == null || pixelBytes.Length != needed)
                pixelBytes = new byte[needed];

            System.Runtime.InteropServices.Marshal.Copy(bgraMat.Data, pixelBytes, 0, needed);

            // Reuse waveform buffer
            audio.GetWaveformSnapshot(waveform);

            double elapsed = stopwatch.Elapsed.TotalSeconds;
            bool segOn = SegmentationEnabled;
            string modelName = segmenter.ActiveModel.ToString();
            bool hudOn = HudEnabled;

            // --- Render offscreen on pipeline thread ---
            compositor.ComposeOffscreen(pixelBytes, mask, waveform, w, h,
                                        hudOn, elapsed, segOn, modelName);

            frameCount++;
            string? fpsStr = null;
            if (elapsed - lastFpsTime >= 1.0)
            {
                double fps = frameCount / (elapsed - lastFpsTime);
                fpsStr = $"FPS: {fps:F1}";
                frameCount = 0;
                lastFpsTime = elapsed;
            }

            bool needSetSource = !sourceSet;
            int blitW = w, blitH = h;

            Interlocked.Increment(ref pendingFrames);
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var bitmap = compositor.EnsureBitmap(blitW, blitH);
                    compositor.BlitToWriteableBitmap(bitmap, blitW, blitH);

                    if (needSetSource)
                        displayImage.Source = bitmap;

                    if (fpsStr != null)
                        fpsText.Text = fpsStr;
                }
                finally
                {
                    Interlocked.Decrement(ref pendingFrames);
                }
            });

            sourceSet = true;
        }
    }

    public void Stop()
    {
        running = false;
        segmenter.StopAsync();
        thread?.Join(2000);
    }

    public void CaptureBackground(Action<string> statusCallback)
    {
        bgStatusCallback = statusCallback;
        bgAccumulator?.Dispose();
        bgAccumulator = null;
        bgFrameCount = 0;
        capturingBackground = true;
    }

    public void ClearReferenceBackground()
    {
        segmenter.ClearReferenceBackground();
    }

    public void SetBackground(string path) => compositor.SetBackground(path);
    public bool SetModel(SegmentationModel model) => segmenter.Initialize(model);
    public void OpenCameraSettings() => camera.OpenSettings();

    public void Dispose()
    {
        audio.Dispose();
        segmenter.Dispose();
        camera.Dispose();
        compositor.Dispose();
        bgAccumulator?.Dispose();
    }
}
