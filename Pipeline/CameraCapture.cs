using OpenCvSharp;

namespace plusnot.Pipeline;

public sealed class CameraCapture : IDisposable
{
    private VideoCapture? capture;

    public bool IsOpen => capture?.IsOpened() == true;
    public string? ErrorMessage { get; private set; }

    public bool Start(int deviceIndex = 0, int width = 1280, int height = 720)
    {
        try
        {
            capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);

            if (!capture.IsOpened())
            {
                ErrorMessage = $"Camera {deviceIndex} is busy or not available.\nClose other apps using the camera and restart.";
                capture.Dispose();
                capture = null;
                return false;
            }

            capture.Set(VideoCaptureProperties.FrameWidth, width);
            capture.Set(VideoCaptureProperties.FrameHeight, height);
            capture.Set(VideoCaptureProperties.Fps, 30);
            ErrorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to open camera {deviceIndex}:\n{ex.Message}";
            capture?.Dispose();
            capture = null;
            return false;
        }
    }

    public bool ReadFrame(Mat mat)
    {
        if (capture == null) return false;
        capture.Read(mat);
        return !mat.Empty();
    }

    public void OpenSettings()
    {
        if (capture != null && capture.IsOpened())
        {
            capture.Set(VideoCaptureProperties.Settings, 1);
        }
    }

    public void Dispose()
    {
        capture?.Release();
        capture?.Dispose();
        capture = null;
    }
}
