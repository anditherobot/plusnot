using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace plusnot.Pipeline;

public enum SegmentationModel
{
    MODNet,
    MediaPipe,
    SINet
}

public sealed class BackgroundSegmenter : IDisposable
{
    private InferenceSession? session;
    private SegmentationModel activeModel;
    private readonly object switchLock = new();

    private byte[]? latestMask;
    private volatile bool segmentationBusy;
    private Thread? segThread;
    private volatile bool running;
    private Mat? pendingFrame;
    private Mat? submitBuffer;
    private int pendingW, pendingH;
    private readonly object frameLock = new();

    // Background reference for combined ML + diff masking
    private Mat? referenceBackground;
    private volatile bool useBackgroundDiff;

    // Tunable settings — all safe to change from any thread
    public int BlurSize { get; set; } = 7;        // 0 = off, odd 1-21
    public int MaskThreshold { get; set; }         // 0 = soft, 1-254 = harder cutoff
    public int FeatherErode { get; set; }          // 0 = off, 1-20 = erode pixels from mask edge
    public int OnnxThreads { get; set; } = 4;
    public bool HasReferenceBackground => useBackgroundDiff;

    private static readonly Dictionary<SegmentationModel, string> ModelFiles = new()
    {
        [SegmentationModel.MODNet] = "modnet.onnx",
        [SegmentationModel.MediaPipe] = "mediapipe_selfie.onnx",
        [SegmentationModel.SINet] = "sinet.onnx",
    };

    private static readonly Dictionary<SegmentationModel, int> ModelSizes = new()
    {
        [SegmentationModel.MODNet] = 512,
        [SegmentationModel.MediaPipe] = 256,
        [SegmentationModel.SINet] = 320,
    };

    public SegmentationModel ActiveModel => activeModel;

    public void SetReferenceBackground(Mat averaged)
    {
        lock (switchLock)
        {
            referenceBackground?.Dispose();
            referenceBackground = averaged.Clone();
            useBackgroundDiff = true;
        }
    }

    public void ClearReferenceBackground()
    {
        lock (switchLock)
        {
            useBackgroundDiff = false;
            referenceBackground?.Dispose();
            referenceBackground = null;
        }
    }

    public bool Initialize(SegmentationModel model)
    {
        var modelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
        var modelPath = Path.Combine(modelsDir, ModelFiles[model]);

        if (!File.Exists(modelPath)) return false;

        lock (switchLock)
        {
            session?.Dispose();
            int threads = Math.Clamp(OnnxThreads, 1, 16);
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = Math.Max(1, threads / 2),
                IntraOpNumThreads = threads,
            };
            options.EnableMemoryPattern = true;
            session = new InferenceSession(modelPath, options);
            activeModel = model;
        }
        return true;
    }

    public void StartAsync()
    {
        running = true;
        segThread = new Thread(SegmentationLoop)
        {
            IsBackground = true,
            Name = "Segmentation",
            Priority = ThreadPriority.BelowNormal
        };
        segThread.Start();
    }

    public void SubmitFrame(Mat bgrFrame, int outputW, int outputH)
    {
        if (segmentationBusy) return;

        lock (frameLock)
        {
            if (submitBuffer == null || submitBuffer.Rows != bgrFrame.Rows ||
                submitBuffer.Cols != bgrFrame.Cols || submitBuffer.Type() != bgrFrame.Type())
            {
                submitBuffer?.Dispose();
                submitBuffer = new Mat(bgrFrame.Rows, bgrFrame.Cols, bgrFrame.Type());
            }
            bgrFrame.CopyTo(submitBuffer);
            pendingFrame = submitBuffer;
            pendingW = outputW;
            pendingH = outputH;
        }
    }

    public byte[]? GetLatestMask() => latestMask;

    private void SegmentationLoop()
    {
        Mat? workFrame = null;

        while (running)
        {
            int w, h;

            lock (frameLock)
            {
                if (pendingFrame == null)
                {
                    Thread.Sleep(1);
                    continue;
                }
                // Copy into reusable work frame
                if (workFrame == null || workFrame.Rows != pendingFrame.Rows ||
                    workFrame.Cols != pendingFrame.Cols || workFrame.Type() != pendingFrame.Type())
                {
                    workFrame?.Dispose();
                    workFrame = new Mat(pendingFrame.Rows, pendingFrame.Cols, pendingFrame.Type());
                }
                pendingFrame.CopyTo(workFrame);
                pendingFrame = null;
                w = pendingW;
                h = pendingH;
            }

            segmentationBusy = true;
            try
            {
                var mask = Segment(workFrame, w, h);
                if (mask != null)
                    latestMask = mask;
            }
            finally
            {
                segmentationBusy = false;
            }
        }

        workFrame?.Dispose();
    }

    private byte[]? Segment(Mat cameraMat, int outputW, int outputH)
    {
        lock (switchLock)
        {
            if (session == null) return null;

            int size = ModelSizes[activeModel];

            using var resized = new Mat();
            Cv2.Resize(cameraMat, resized, new Size(size, size));

            var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
            FillTensor(resized, tensor, size, activeModel);

            var inputName = session.InputMetadata.Keys.First();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, tensor)
            };

            using var results = session.Run(inputs);
            var output = results.First().AsTensor<float>();

            var maskBytes = ExtractMask(output, size, activeModel);

            using var maskMat = new Mat(size, size, MatType.CV_8UC1);
            System.Runtime.InteropServices.Marshal.Copy(maskBytes, 0, maskMat.Data, maskBytes.Length);

            using var processed = new Mat();
            PostProcessMask(maskMat, processed);

            // Combine with background diff mask if reference is available
            if (useBackgroundDiff && referenceBackground != null)
            {
                using var diffMask = CombineWithDiffMask(cameraMat, processed, size);
                using var outputMat = new Mat();
                Cv2.Resize(diffMask, outputMat, new Size(outputW, outputH));

                var result = new byte[outputW * outputH];
                System.Runtime.InteropServices.Marshal.Copy(outputMat.Data, result, 0, result.Length);
                return result;
            }
            else
            {
                using var outputMat = new Mat();
                Cv2.Resize(processed, outputMat, new Size(outputW, outputH));

                var result = new byte[outputW * outputH];
                System.Runtime.InteropServices.Marshal.Copy(outputMat.Data, result, 0, result.Length);
                return result;
            }
        }
    }

    private Mat CombineWithDiffMask(Mat currentBgr, Mat mlMask, int maskSize)
    {
        // Resize current frame to reference size for comparison
        using var currentResized = new Mat();
        Cv2.Resize(currentBgr, currentResized, new Size(referenceBackground!.Cols, referenceBackground.Rows));

        // Absdiff between current and reference
        using var diff = new Mat();
        Cv2.Absdiff(currentResized, referenceBackground, diff);

        // Convert to grayscale
        using var diffGray = new Mat();
        Cv2.CvtColor(diff, diffGray, ColorConversionCodes.BGR2GRAY);

        // Threshold to get binary diff mask
        using var diffBinary = new Mat();
        Cv2.Threshold(diffGray, diffBinary, 25, 255, ThresholdTypes.Binary);

        // Dilate to fill small gaps
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var diffDilated = new Mat();
        Cv2.Dilate(diffBinary, diffDilated, kernel);

        // Resize diff mask to match ML mask size
        using var diffResized = new Mat();
        Cv2.Resize(diffDilated, diffResized, new Size(mlMask.Cols, mlMask.Rows));

        // Multiply: foreground only where BOTH ML and diff agree
        var combined = new Mat();
        Cv2.BitwiseAnd(mlMask, diffResized, combined);
        return combined;
    }

    private void PostProcessMask(Mat src, Mat dst)
    {
        var current = src;
        Mat? temp1 = null, temp2 = null;

        // Threshold: 0 = soft (no threshold), higher = harder cutoff
        int thresh = MaskThreshold;
        if (thresh > 0)
        {
            temp1 = new Mat();
            Cv2.Threshold(current, temp1, thresh, 255, ThresholdTypes.Binary);
            current = temp1;
        }

        // Erode: shrinks mask to remove fringe pixels around edges
        int erode = FeatherErode;
        if (erode > 0)
        {
            temp2 = new Mat();
            var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(erode * 2 + 1, erode * 2 + 1));
            Cv2.Erode(current, temp2, kernel);
            current = temp2;
        }

        // Blur: softens edges after threshold/erode
        int blur = BlurSize;
        if (blur > 1)
        {
            int k = blur | 1; // ensure odd
            Cv2.GaussianBlur(current, dst, new Size(k, k), k / 3.0);
        }
        else
        {
            current.CopyTo(dst);
        }

        temp1?.Dispose();
        temp2?.Dispose();
    }

    private static unsafe void FillTensor(Mat resized, DenseTensor<float> tensor, int size, SegmentationModel model)
    {
        byte* ptr = (byte*)resized.Data;
        int step = (int)resized.Step();

        for (int y = 0; y < size; y++)
        {
            byte* row = ptr + y * step;
            for (int x = 0; x < size; x++)
            {
                int offset = x * 3;
                float b = row[offset];
                float g = row[offset + 1];
                float r = row[offset + 2];

                if (model == SegmentationModel.MODNet)
                {
                    tensor[0, 0, y, x] = (r - 127.5f) / 127.5f;
                    tensor[0, 1, y, x] = (g - 127.5f) / 127.5f;
                    tensor[0, 2, y, x] = (b - 127.5f) / 127.5f;
                }
                else
                {
                    tensor[0, 0, y, x] = r / 255f;
                    tensor[0, 1, y, x] = g / 255f;
                    tensor[0, 2, y, x] = b / 255f;
                }
            }
        }
    }

    private static byte[] ExtractMask(Tensor<float> output, int size, SegmentationModel model)
    {
        var maskBytes = new byte[size * size];

        if (model == SegmentationModel.SINet)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float val = Math.Clamp(output[0, 1, y, x], 0f, 1f);
                    maskBytes[y * size + x] = (byte)(val * 255);
                }
        }
        else
        {
            for (int i = 0; i < maskBytes.Length; i++)
            {
                float val = Math.Clamp(output.GetValue(i), 0f, 1f);
                maskBytes[i] = (byte)(val * 255);
            }
        }

        return maskBytes;
    }

    public void StopAsync()
    {
        running = false;
        segThread?.Join(2000);
    }

    public void Dispose()
    {
        StopAsync();
        lock (switchLock)
        {
            session?.Dispose();
            session = null;
            referenceBackground?.Dispose();
            referenceBackground = null;
        }
        lock (frameLock)
        {
            pendingFrame = null;
            submitBuffer?.Dispose();
            submitBuffer = null;
        }
    }
}
