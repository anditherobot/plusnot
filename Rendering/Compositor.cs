using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace plusnot.Rendering;

public sealed class Compositor : IDisposable
{
    private WriteableBitmap? bmp;
    private SKBitmap? bgImage, bgCache;
    private int cacheW, cacheH;
    private readonly HudRenderer hud = new();
    private readonly WaveformRenderer wave = new();
    private byte[]? offBuf; private GCHandle offPin; private SKSurface? offSurf; private int offW, offH;

    public WriteableBitmap EnsureBitmap(int w, int h)
    {
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h)
            bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        return bmp;
    }

    public void SetBackground(string path) { bgImage?.Dispose(); bgCache?.Dispose(); bgCache = null; bgImage = SKBitmap.Decode(path); }

    SKBitmap? ResizedBg(int w, int h)
    {
        if (bgImage == null) return null;
        if (bgCache != null && cacheW == w && cacheH == h) return bgCache;
        bgCache?.Dispose();
        bgCache = bgImage.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
        cacheW = w; cacheH = h;
        return bgCache;
    }

    void EnsureOffscreen(int w, int h)
    {
        if (offBuf != null && offW == w && offH == h) return;
        offSurf?.Dispose(); if (offPin.IsAllocated) offPin.Free();
        offBuf = new byte[w * h * 4];
        offPin = GCHandle.Alloc(offBuf, GCHandleType.Pinned);
        offSurf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), offPin.AddrOfPinnedObject(), w * 4);
        offW = w; offH = h;
    }

    public void ComposeError(WriteableBitmap b, int w, int h, string msg)
    {
        b.Lock();
        try
        {
            using var s = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), b.BackBuffer, b.BackBufferStride);
            var c = s.Canvas; c.Clear(new SKColor(10, 10, 26));
            using var p1 = new SKPaint { Color = new SKColor(255, 60, 60), TextSize = 64, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI"), TextAlign = SKTextAlign.Center };
            c.DrawText("CAMERA UNAVAILABLE", w / 2f, h / 2f - 60, p1);
            using var p2 = new SKPaint { Color = new SKColor(0, 255, 204), TextSize = 18, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Consolas"), TextAlign = SKTextAlign.Center };
            float y = h / 2f; foreach (var l in msg.Split('\n')) { c.DrawText(l, w / 2f, y, p2); y += 28; }
            c.Flush(); b.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally { b.Unlock(); }
    }

    public void ComposeOffscreen(byte[] px, byte[]? mask, float[] wav, int w, int h, bool hudOn, double t, bool segOn = true, string model = "")
    {
        EnsureOffscreen(w, h);
        var c = offSurf!.Canvas; c.Clear(SKColors.Black);
        if (bgImage != null && mask != null)
        {
            var bg = ResizedBg(w, h); if (bg != null) c.DrawBitmap(bg, 0, 0);
            int n = Math.Min(mask.Length, px.Length / 4);
            for (int i = 0; i < n; i++) px[i * 4 + 3] = mask[i];
            Blit(c, px, w, h, SKAlphaType.Unpremul);
        }
        else Blit(c, px, w, h, SKAlphaType.Premul);
        if (hudOn) hud.Draw(c, w, h, t, segOn, model);
        wave.Draw(c, wav, w, h);
        c.Flush();
    }

    public void BlitToWriteableBitmap(WriteableBitmap b, int w, int h)
    {
        if (offBuf == null || offW != w || offH != h) return;
        b.Lock();
        try { Marshal.Copy(offBuf, 0, b.BackBuffer, w * h * 4); b.AddDirtyRect(new Int32Rect(0, 0, w, h)); }
        finally { b.Unlock(); }
    }

    static unsafe void Blit(SKCanvas c, byte[] px, int w, int h, SKAlphaType a)
    {
        fixed (byte* p = px) { using var img = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Bgra8888, a), (IntPtr)p, w * 4); c.DrawImage(img, 0, 0); }
    }

    public void Dispose()
    {
        offSurf?.Dispose(); if (offPin.IsAllocated) offPin.Free();
        bgCache?.Dispose(); bgImage?.Dispose();
    }
}
