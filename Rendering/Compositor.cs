using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using plusnot.Pipeline;

namespace plusnot.Rendering;

public sealed class Compositor : IDisposable
{
    private WriteableBitmap? bmp;
    private SKBitmap? bgImage, bgCache;
    private int cacheW, cacheH;
    private readonly HudRenderer hud = new();
    private readonly WaveformRenderer wave = new();
    private readonly SKPaint thumbBg = new() { Color = new SKColor(10, 10, 26, 200), Style = SKPaintStyle.Fill };
    private readonly SKPaint thumbBorder = new() { Color = new SKColor(0, 255, 204, 150), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint thumbLabel = new() { Color = new SKColor(0, 255, 204, 200), Typeface = SKTypeface.FromFamilyName("Consolas"), TextSize = 10, IsAntialias = true };
    private byte[]? thumbPixBuf;
    private readonly SKPaint bgPaint = new();
    private byte[]? offBuf; private GCHandle offPin; private SKSurface? offSurf; private int offW, offH;

    public WriteableBitmap EnsureBitmap(int w, int h)
    {
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h)
            bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        return bmp;
    }

    public void SetBackground(string path) { bgImage?.Dispose(); bgCache?.Dispose(); bgCache = null; bgImage = SKBitmap.Decode(path); }

    public unsafe void SetBackgroundFromBgr(IntPtr bgrData, int w, int h, int step)
    {
        bgImage?.Dispose(); bgCache?.Dispose(); bgCache = null;
        bgImage = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        byte* src = (byte*)bgrData, dst = (byte*)bgImage.GetPixels();
        for (int y = 0; y < h; y++)
        {
            byte* sRow = src + y * step, dRow = dst + y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int s = x * 3, d = x * 4;
                dRow[d] = sRow[s]; dRow[d + 1] = sRow[s + 1]; dRow[d + 2] = sRow[s + 2]; dRow[d + 3] = 255;
            }
        }
    }

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

    public void ComposeOffscreen(byte[] px, byte[]? mask, float[] wav, int w, int h, bool hudOn, double t, bool segOn = true, string model = "", PipelineDebugData? debugData = null)
    {
        EnsureOffscreen(w, h);
        var c = offSurf!.Canvas; c.Clear(SKColors.Black);
        if (bgImage != null && mask != null)
        {
            var bg = ResizedBg(w, h); if (bg != null) c.DrawBitmap(bg, 0, 0, bgPaint);
            int n = Math.Min(mask.Length, px.Length / 4);
            unsafe { fixed (byte* pp = px, mp = mask) { byte* pe = pp + n * 4; byte* m = mp; for (byte* p = pp + 3; p < pe; p += 4) *p = *m++; } }
            Blit(c, px, w, h, SKAlphaType.Unpremul);
        }
        else Blit(c, px, w, h, SKAlphaType.Premul);
        if (hudOn) hud.Draw(c, w, h, t, segOn, model);
        wave.Draw(c, wav, w, h);
        if (debugData.HasValue) DrawDebugThumbnails(c, debugData.Value, w, h);
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

    void DrawDebugThumbnails(SKCanvas c, PipelineDebugData dbg, int w, int h)
    {
        const int tw = 120, th = 90, sp = 8;
        float x = 40, y = h - 90 - th - 8;
        var items = new (string label, byte[]? data, int dw, int dh)[]
        {
            ("AI", dbg.RawMask, dbg.RawW, dbg.RawH),
            ("CLEANUP", dbg.PostMask, dbg.RawW, dbg.RawH),
            ("BG DIFF", dbg.DiffMask, dbg.RawW, dbg.RawH),
            ("FINAL", dbg.FinalMask, dbg.FinalW, dbg.FinalH),
        };
        foreach (var (label, data, dw, dh) in items)
        {
            c.DrawRect(x, y, tw, th, thumbBg);
            c.DrawRect(x, y, tw, th, thumbBorder);
            c.DrawText(label, x + 4, y + th - 4, thumbLabel);
            if (data != null && dw > 0 && dh > 0)
                DrawGrayscaleThumbnail(c, data, dw, dh, new SKRect(x + 2, y + 2, x + tw - 2, y + th - 16));
            x += tw + sp;
        }
    }

    unsafe void DrawGrayscaleThumbnail(SKCanvas c, byte[] gray, int gw, int gh, SKRect dst)
    {
        int n = Math.Min(gray.Length, gw * gh);
        int need = n * 4;
        if (thumbPixBuf == null || thumbPixBuf.Length < need) thumbPixBuf = new byte[need];
        for (int i = 0; i < n; i++) { byte v = gray[i]; int o = i * 4; thumbPixBuf[o] = v; thumbPixBuf[o + 1] = v; thumbPixBuf[o + 2] = v; thumbPixBuf[o + 3] = 255; }
        fixed (byte* p = thumbPixBuf)
        {
            using var img = SKImage.FromPixelCopy(new SKImageInfo(gw, gh, SKColorType.Bgra8888, SKAlphaType.Premul), (IntPtr)p, gw * 4);
            c.DrawImage(img, new SKRect(0, 0, gw, gh), dst);
        }
    }

    public void Dispose()
    {
        offSurf?.Dispose(); if (offPin.IsAllocated) offPin.Free();
        bgCache?.Dispose(); bgImage?.Dispose(); bgPaint.Dispose();
        thumbBg.Dispose(); thumbBorder.Dispose(); thumbLabel.Dispose();
    }
}
