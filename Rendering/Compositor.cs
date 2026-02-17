using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace plusnot.Rendering;

public sealed class Compositor : IDisposable
{
    private WriteableBitmap? bitmap;
    private SKBitmap? backgroundImage;
    private SKBitmap? bgResizedCache;
    private int bgCacheW, bgCacheH;
    private readonly HudRenderer hudRenderer = new();
    private readonly WaveformRenderer waveformRenderer = new();

    // Offscreen rendering state
    private byte[]? offscreenBuffer;
    private GCHandle offscreenHandle;
    private SKSurface? offscreenSurface;
    private int offscreenW, offscreenH;

    public WriteableBitmap EnsureBitmap(int w, int h)
    {
        if (bitmap == null || bitmap.PixelWidth != w || bitmap.PixelHeight != h)
        {
            bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        }
        return bitmap;
    }

    public void SetBackground(string imagePath)
    {
        backgroundImage?.Dispose();
        bgResizedCache?.Dispose();
        bgResizedCache = null;
        backgroundImage = SKBitmap.Decode(imagePath);
    }

    private SKBitmap? GetResizedBackground(int w, int h)
    {
        if (backgroundImage == null) return null;
        if (bgResizedCache != null && bgCacheW == w && bgCacheH == h)
            return bgResizedCache;

        bgResizedCache?.Dispose();
        bgResizedCache = backgroundImage.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
        bgCacheW = w;
        bgCacheH = h;
        return bgResizedCache;
    }

    private void EnsureOffscreen(int w, int h)
    {
        if (offscreenBuffer != null && offscreenW == w && offscreenH == h)
            return;

        // Free old resources
        offscreenSurface?.Dispose();
        offscreenSurface = null;
        if (offscreenHandle.IsAllocated)
            offscreenHandle.Free();

        // Allocate pinned buffer
        offscreenBuffer = new byte[w * h * 4];
        offscreenHandle = GCHandle.Alloc(offscreenBuffer, GCHandleType.Pinned);

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        offscreenSurface = SKSurface.Create(info, offscreenHandle.AddrOfPinnedObject(), w * 4);
        offscreenW = w;
        offscreenH = h;
    }

    public void ComposeError(WriteableBitmap bmp, int w, int h, string errorMessage)
    {
        bmp.Lock();
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, bmp.BackBuffer, bmp.BackBufferStride);
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(10, 10, 26));

            using var iconPaint = new SKPaint
            {
                Color = new SKColor(255, 60, 60),
                TextSize = 64,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Segoe UI"),
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText("CAMERA UNAVAILABLE", w / 2f, h / 2f - 60, iconPaint);

            using var msgPaint = new SKPaint
            {
                Color = new SKColor(0, 255, 204),
                TextSize = 18,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Consolas"),
                TextAlign = SKTextAlign.Center
            };
            float y = h / 2f;
            foreach (var line in errorMessage.Split('\n'))
            {
                canvas.DrawText(line, w / 2f, y, msgPaint);
                y += 28;
            }

            using var hintPaint = new SKPaint
            {
                Color = new SKColor(0, 255, 204, 120),
                TextSize = 14,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Consolas"),
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText("Close other apps using the camera, then restart plusnot",
                            w / 2f, h / 2f + 100, hintPaint);

            canvas.Flush();
            bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            bmp.Unlock();
        }
    }

    /// <summary>
    /// Renders the full frame to an offscreen pinned buffer. Called on the pipeline thread.
    /// </summary>
    public void ComposeOffscreen(byte[] cameraPixels, byte[]? alphaMask,
                                  float[] waveform, int w, int h, bool hudOn, double elapsed,
                                  bool segOn = true, string modelName = "")
    {
        EnsureOffscreen(w, h);

        var canvas = offscreenSurface!.Canvas;
        canvas.Clear(SKColors.Black);

        if (backgroundImage != null && alphaMask != null)
        {
            var bg = GetResizedBackground(w, h);
            if (bg != null)
                canvas.DrawBitmap(bg, 0, 0);

            ApplyAlphaMask(cameraPixels, alphaMask);
            DrawPixels(canvas, cameraPixels, w, h, SKAlphaType.Unpremul);
        }
        else
        {
            DrawPixels(canvas, cameraPixels, w, h, SKAlphaType.Premul);
        }

        if (hudOn)
            hudRenderer.Draw(canvas, w, h, elapsed, segOn, modelName);

        waveformRenderer.Draw(canvas, waveform, w, h);

        canvas.Flush();
    }

    /// <summary>
    /// Fast blit from offscreen buffer to WriteableBitmap. Called on the UI thread.
    /// </summary>
    public void BlitToWriteableBitmap(WriteableBitmap bmp, int w, int h)
    {
        if (offscreenBuffer == null || offscreenW != w || offscreenH != h)
            return;

        bmp.Lock();
        try
        {
            Marshal.Copy(offscreenBuffer, 0, bmp.BackBuffer, w * h * 4);
            bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            bmp.Unlock();
        }
    }

    private static void ApplyAlphaMask(byte[] pixels, byte[] mask)
    {
        int count = Math.Min(mask.Length, pixels.Length / 4);
        for (int i = 0; i < count; i++)
            pixels[i * 4 + 3] = mask[i];
    }

    private static unsafe void DrawPixels(SKCanvas canvas, byte[] pixels, int w, int h, SKAlphaType alpha)
    {
        fixed (byte* ptr = pixels)
        {
            var bmpInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, alpha);
            using var img = SKImage.FromPixelCopy(bmpInfo, (IntPtr)ptr, w * 4);
            canvas.DrawImage(img, 0, 0);
        }
    }

    public void Dispose()
    {
        offscreenSurface?.Dispose();
        offscreenSurface = null;
        if (offscreenHandle.IsAllocated)
            offscreenHandle.Free();
        offscreenBuffer = null;

        bgResizedCache?.Dispose();
        backgroundImage?.Dispose();
    }
}
