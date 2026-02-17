using OpenCvSharp;

namespace plusnot.Pipeline;

public sealed class CameraCapture : IDisposable
{
    private VideoCapture? cap;
    public bool IsOpen => cap?.IsOpened() == true;
    public string? Error { get; private set; }

    public bool Start(int dev = 0, int w = 640, int h = 480)
    {
        try
        {
            cap = new VideoCapture(dev, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened())
            {
                Error = $"Camera {dev} busy or unavailable.";
                cap.Dispose(); cap = null; return false;
            }
            cap.Set(VideoCaptureProperties.FrameWidth, w);
            cap.Set(VideoCaptureProperties.FrameHeight, h);
            cap.Set(VideoCaptureProperties.Fps, 30);
            return true;
        }
        catch (Exception ex) { Error = ex.Message; cap?.Dispose(); cap = null; return false; }
    }

    public bool Read(Mat m) { if (cap == null) return false; cap.Read(m); return !m.Empty(); }
    public void OpenSettings() { if (cap?.IsOpened() == true) cap.Set(VideoCaptureProperties.Settings, 1); }
    public void Dispose() { cap?.Release(); cap?.Dispose(); }
}
