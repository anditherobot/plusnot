using SkiaSharp;

namespace plusnot.Rendering;

public sealed class WaveformRenderer
{
    private readonly SKPaint wavePaint;
    private readonly SKPaint fillPaint;
    private readonly SKPaint basePaint;

    public WaveformRenderer()
    {
        wavePaint = new SKPaint
        {
            Color = SKColor.Parse("#00FFCC"),
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2)
        };

        fillPaint = new SKPaint
        {
            Color = new SKColor(0, 255, 204, 0x22),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        basePaint = new SKPaint
        {
            Color = SKColor.Parse("#004444"),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
    }

    public void Draw(SKCanvas canvas, float[]? samples, int w, int h)
    {
        if (samples == null || samples.Length == 0) return;

        int marginX = 40;
        float centerY = h - 50;
        float halfH = 35;
        float regionW = w - 2 * marginX;

        // Baseline
        canvas.DrawLine(marginX, centerY, w - marginX, centerY, basePaint);

        // Waveform path
        using var path = new SKPath();
        for (int i = 0; i < samples.Length; i++)
        {
            float x = marginX + (i / (float)samples.Length) * regionW;
            float y = centerY - samples[i] * halfH;

            if (i == 0)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }

        // Stroke waveform with glow
        canvas.DrawPath(path, wavePaint);

        // Filled version underneath
        using var fillPath = new SKPath(path);
        fillPath.LineTo(marginX + regionW, centerY);
        fillPath.LineTo(marginX, centerY);
        fillPath.Close();
        canvas.DrawPath(fillPath, fillPaint);
    }
}
