using SkiaSharp;

namespace plusnot.Rendering;

public sealed class WaveformRenderer
{
    private readonly SKPaint stroke = new() { Color = SKColor.Parse("#00FFCC"), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2) };
    private readonly SKPaint fill = new() { Color = new SKColor(0, 255, 204, 0x22), IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint baseline = new() { Color = SKColor.Parse("#004444"), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };

    public void Draw(SKCanvas c, float[]? s, int w, int h)
    {
        if (s == null || s.Length == 0) return;
        int mx = 40; float cy = h - 50, hh = 35, rw = w - 2 * mx;
        c.DrawLine(mx, cy, w - mx, cy, baseline);

        using var path = new SKPath();
        for (int i = 0; i < s.Length; i++)
        {
            float x = mx + i / (float)s.Length * rw, y = cy - s[i] * hh;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        c.DrawPath(path, stroke);

        using var fp = new SKPath(path);
        fp.LineTo(mx + rw, cy); fp.LineTo(mx, cy); fp.Close();
        c.DrawPath(fp, fill);
    }
}
