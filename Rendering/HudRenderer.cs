using SkiaSharp;

namespace plusnot.Rendering;

public sealed class HudRenderer
{
    static readonly SKColor C = SKColor.Parse("#00FFCC");
    readonly SKPaint bracket = new() { Color = C, StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
    readonly SKPaint thin = new() { Color = C, StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
    readonly SKPaint text = new() { Color = C, Typeface = SKTypeface.FromFamilyName("Consolas"), TextSize = 13, IsAntialias = true };
    readonly SKPaint scan = new() { Color = new SKColor(0, 255, 204, 60), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4) };
    readonly SKPaint dim = new() { Color = SKColor.Parse("#004444"), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
    readonly SKPaint timer = new() { Color = C, Typeface = SKTypeface.FromFamilyName("Consolas"), TextSize = 30, IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.5f) };
    readonly SKPaint boxBg = new() { Color = new SKColor(10, 10, 26, 180), Style = SKPaintStyle.Fill };
    readonly SKPaint boxBorder = new() { Color = new SKColor(0, 255, 204, 100), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
    readonly SKPaint boxText = new() { Color = new SKColor(0, 255, 204, 180), Typeface = SKTypeface.FromFamilyName("Consolas"), TextSize = 12, IsAntialias = true };

    int fc; double lastFt, fps; float timerWidth; string cachedRes = "", cachedFps = "FPS: 0.0"; int cachedW, cachedH;
    readonly string[] stars;
    readonly List<string> lines = new();
    double lastStar; int starIdx;

    public HudRenderer()
    {
        var r = new Random(42);
        stars = Enumerable.Range(0, 20).Select(_ => new string('*', r.Next(4, 15))).ToArray();
    }

    public void Draw(SKCanvas c, int w, int h, double t, bool segOn = true, string model = "")
    {
        fc++;
        if (t - lastFt >= 1) { fps = fc / (t - lastFt); fc = 0; lastFt = t; cachedFps = $"FPS: {fps:F1}"; }

        int ins = 30, arm = 60;
        byte pa = (byte)(180 + 75 * Math.Sin(t * 2));
        bracket.Color = new SKColor(0, 255, 204, pa);

        // Corner brackets (4 corners, each: 2 arms + 3 ticks per axis)
        DrawCorner(c, ins, ins, 1, 1, arm);
        DrawCorner(c, w - ins, ins, -1, 1, arm);
        DrawCorner(c, ins, h - ins, 1, -1, arm);
        DrawCorner(c, w - ins, h - ins, -1, -1, arm);

        // Scan line
        c.DrawLine(0, (float)(t % 4 / 4 * h), w, (float)(t % 4 / 4 * h), scan);

        // Reticle
        float cx = w / 2f, cy = h / 2f;
        c.DrawCircle(cx, cy, 80, thin); c.DrawCircle(cx, cy, 40, dim);
        c.DrawLine(cx - 15, cy, cx + 15, cy, thin); c.DrawLine(cx, cy - 15, cx, cy + 15, thin);
        c.Save(); c.RotateDegrees((float)(t * 30), cx, cy);
        for (int i = 0; i < 4; i++) { c.Save(); c.RotateDegrees(i * 90, cx, cy); c.DrawLine(cx, cy - 76, cx, cy - 84, thin); c.Restore(); }
        c.Restore();

        // Data readouts
        float tx = ins + 10, ty = ins + arm + 20;
        c.DrawText("SYS: ONLINE", tx, ty, text);
        c.DrawText(cachedFps, tx, ty + 18, text);
        if (w != cachedW || h != cachedH) { cachedRes = $"RES: {w}x{h}"; cachedW = w; cachedH = h; }
        c.DrawText(cachedRes, tx, ty + 36, text);

        // Large timer top-right
        var ts = DateTime.Now.ToString("HH:mm:ss.ff");
        if (timerWidth <= 0) timerWidth = timer.MeasureText(ts);
        c.DrawText(ts, w - ins - 10 - timerWidth, ins + 30, timer);

        // Seg status
        c.DrawText($"SEG: {(segOn ? "ON" : "OFF")}", tx, h - 100, text);
        if (segOn && model.Length > 0) c.DrawText($"MDL: {model}", tx, h - 82, text);

        // Star box
        DrawStarBox(c, w, h, t);

        // Edge lines + notches
        c.DrawLine(20, 20, w - 20, 20, thin); c.DrawLine(20, h - 20, w - 20, h - 20, thin);
        for (float nx = 20; nx < w - 20; nx += 80) { c.DrawLine(nx, 20, nx, 25, thin); c.DrawLine(nx, h - 20, nx, h - 25, thin); }
    }

    void DrawCorner(SKCanvas c, float x, float y, int dx, int dy, int arm)
    {
        c.DrawLine(x, y, x + dx * arm, y, bracket);
        c.DrawLine(x, y, x, y + dy * arm, bracket);
        for (int t = 1; t <= 3; t++)
        {
            float p = t * 15f;
            c.DrawLine(x + dx * p, y, x + dx * p, y + dy * 6, thin);
            c.DrawLine(x, y + dy * p, x + dx * 6, y + dy * p, thin);
        }
    }

    void DrawStarBox(SKCanvas c, int w, int h, double t)
    {
        if (t - lastStar >= 0.7 && t > 0.1)
        {
            lastStar = t;
            lines.Add(stars[starIdx++ % stars.Length]);
            while (lines.Count > 6) lines.RemoveAt(0);
        }
        float bx = w - 190, by = h - 220;
        c.DrawRect(bx, by, 160, 120, boxBg);
        c.DrawRect(bx, by, 160, 120, boxBorder);
        for (int i = 0; i < lines.Count; i++)
            c.DrawText(lines[i], bx + 8, by + 16 + i * 15, boxText);
        if ((int)(t * 2.5) % 2 == 0 && by + 16 + lines.Count * 15 < by + 116)
            c.DrawText("_", bx + 8, by + 16 + lines.Count * 15, boxText);
    }
}
