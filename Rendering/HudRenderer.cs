using SkiaSharp;

namespace plusnot.Rendering;

public sealed class HudRenderer
{
    private readonly SKPaint bracketPaint;
    private readonly SKPaint thinPaint;
    private readonly SKPaint textPaint;
    private readonly SKPaint scanPaint;
    private readonly SKPaint dimPaint;
    private readonly SKPaint timerPaint;
    private readonly SKPaint boxBorderPaint;
    private readonly SKPaint boxTextPaint;
    private readonly SKPaint boxBgPaint;

    private int frameCount;
    private double lastFpsTime;
    private double currentFps;

    // Star text box state
    private readonly string[] starPatterns;
    private readonly List<string> visibleLines = new();
    private double lastStarTime;
    private int nextPatternIndex;

    public HudRenderer()
    {
        bracketPaint = new SKPaint
        {
            Color = SKColor.Parse("#00FFCC"),
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        thinPaint = new SKPaint
        {
            Color = SKColor.Parse("#00FFCC"),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        textPaint = new SKPaint
        {
            Color = SKColor.Parse("#00FFCC"),
            Typeface = SKTypeface.FromFamilyName("Consolas"),
            TextSize = 13,
            IsAntialias = true
        };

        scanPaint = new SKPaint
        {
            Color = new SKColor(0, 255, 204, 60),
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4)
        };

        dimPaint = new SKPaint
        {
            Color = SKColor.Parse("#004444"),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        timerPaint = new SKPaint
        {
            Color = SKColor.Parse("#00FFCC"),
            Typeface = SKTypeface.FromFamilyName("Consolas"),
            TextSize = 30,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.5f)
        };

        boxBorderPaint = new SKPaint
        {
            Color = new SKColor(0, 255, 204, 100),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        boxTextPaint = new SKPaint
        {
            Color = new SKColor(0, 255, 204, 180),
            Typeface = SKTypeface.FromFamilyName("Consolas"),
            TextSize = 12,
            IsAntialias = true
        };

        boxBgPaint = new SKPaint
        {
            Color = new SKColor(10, 10, 26, 180),
            Style = SKPaintStyle.Fill
        };

        // Pre-generate 20 star-line patterns with seeded random
        var rng = new Random(42);
        starPatterns = new string[20];
        for (int i = 0; i < 20; i++)
        {
            int len = rng.Next(4, 15);
            starPatterns[i] = new string('*', len);
        }
    }

    public void Draw(SKCanvas canvas, int w, int h, double elapsed,
                     bool segOn = true, string modelName = "")
    {
        // FPS tracking
        frameCount++;
        if (elapsed - lastFpsTime >= 1.0)
        {
            currentFps = frameCount / (elapsed - lastFpsTime);
            frameCount = 0;
            lastFpsTime = elapsed;
        }

        int inset = 30;
        int arm = 60;

        // --- CORNER BRACKETS ---
        byte pulsingAlpha = (byte)(180 + 75 * Math.Sin(elapsed * 2.0));
        bracketPaint.Color = new SKColor(0, 255, 204, pulsingAlpha);

        // Top-Left
        canvas.DrawLine(inset, inset, inset + arm, inset, bracketPaint);
        canvas.DrawLine(inset, inset, inset, inset + arm, bracketPaint);
        for (int t = 1; t <= 3; t++)
        {
            float pos = t * 15f;
            canvas.DrawLine(inset + pos, inset, inset + pos, inset + 6, thinPaint);
            canvas.DrawLine(inset, inset + pos, inset + 6, inset + pos, thinPaint);
        }

        // Top-Right
        canvas.DrawLine(w - inset, inset, w - inset - arm, inset, bracketPaint);
        canvas.DrawLine(w - inset, inset, w - inset, inset + arm, bracketPaint);
        for (int t = 1; t <= 3; t++)
        {
            float pos = t * 15f;
            canvas.DrawLine(w - inset - pos, inset, w - inset - pos, inset + 6, thinPaint);
            canvas.DrawLine(w - inset, inset + pos, w - inset - 6, inset + pos, thinPaint);
        }

        // Bottom-Left
        canvas.DrawLine(inset, h - inset, inset + arm, h - inset, bracketPaint);
        canvas.DrawLine(inset, h - inset, inset, h - inset - arm, bracketPaint);
        for (int t = 1; t <= 3; t++)
        {
            float pos = t * 15f;
            canvas.DrawLine(inset + pos, h - inset, inset + pos, h - inset - 6, thinPaint);
            canvas.DrawLine(inset, h - inset - pos, inset + 6, h - inset - pos, thinPaint);
        }

        // Bottom-Right
        canvas.DrawLine(w - inset, h - inset, w - inset - arm, h - inset, bracketPaint);
        canvas.DrawLine(w - inset, h - inset, w - inset, h - inset - arm, bracketPaint);
        for (int t = 1; t <= 3; t++)
        {
            float pos = t * 15f;
            canvas.DrawLine(w - inset - pos, h - inset, w - inset - pos, h - inset - 6, thinPaint);
            canvas.DrawLine(w - inset, h - inset - pos, w - inset - 6, h - inset - pos, thinPaint);
        }

        // --- SCANNING LINE ---
        float scanY = (float)((elapsed % 4.0 / 4.0) * h);
        canvas.DrawLine(0, scanY, w, scanY, scanPaint);

        // --- CENTER RETICLE ---
        float cx = w / 2f;
        float cy = h / 2f;
        canvas.DrawCircle(cx, cy, 80, thinPaint);
        canvas.DrawCircle(cx, cy, 40, dimPaint);

        // Crosshair
        canvas.DrawLine(cx - 15, cy, cx + 15, cy, thinPaint);
        canvas.DrawLine(cx, cy - 15, cx, cy + 15, thinPaint);

        // Rotating tick marks
        canvas.Save();
        canvas.RotateDegrees((float)(elapsed * 30), cx, cy);
        for (int i = 0; i < 4; i++)
        {
            canvas.Save();
            canvas.RotateDegrees(i * 90, cx, cy);
            canvas.DrawLine(cx, cy - 76, cx, cy - 84, thinPaint);
            canvas.Restore();
        }
        canvas.Restore();

        // --- DATA READOUTS ---
        float textX = inset + 10;
        float textY = inset + arm + 20;
        canvas.DrawText("SYS: ONLINE", textX, textY, textPaint);
        canvas.DrawText($"FPS: {currentFps:F1}", textX, textY + 18, textPaint);
        canvas.DrawText($"RES: {w}x{h}", textX, textY + 36, textPaint);

        // Timestamp top-right (large timer inside bracket)
        string timestamp = DateTime.Now.ToString("HH:mm:ss.ff");
        float tsWidth = timerPaint.MeasureText(timestamp);
        canvas.DrawText(timestamp, w - inset - 10 - tsWidth, inset + 30, timerPaint);

        // Segmentation status bottom-left (above waveform area)
        string segStatus = segOn ? "ON" : "OFF";
        canvas.DrawText($"SEG: {segStatus}", textX, h - 100, textPaint);
        if (segOn && !string.IsNullOrEmpty(modelName))
            canvas.DrawText($"MDL: {modelName}", textX, h - 82, textPaint);

        // --- STAR TEXT BOX (bottom-right, above waveform) ---
        DrawStarTextBox(canvas, w, h, elapsed);

        // --- EDGE LINES ---
        float ruleY1 = 20;
        float ruleY2 = h - 20;
        canvas.DrawLine(20, ruleY1, w - 20, ruleY1, thinPaint);
        canvas.DrawLine(20, ruleY2, w - 20, ruleY2, thinPaint);

        // Notch marks every 80px
        for (float nx = 20; nx < w - 20; nx += 80)
        {
            canvas.DrawLine(nx, ruleY1, nx, ruleY1 + 5, thinPaint);
            canvas.DrawLine(nx, ruleY2, nx, ruleY2 - 5, thinPaint);
        }
    }

    private void DrawStarTextBox(SKCanvas canvas, int w, int h, double elapsed)
    {
        // Update star lines every ~0.7 seconds
        if (elapsed - lastStarTime >= 0.7 && elapsed > 0.1)
        {
            lastStarTime = elapsed;
            visibleLines.Add(starPatterns[nextPatternIndex % starPatterns.Length]);
            nextPatternIndex++;

            // Keep max 6 visible lines (oldest scrolls off)
            while (visibleLines.Count > 6)
                visibleLines.RemoveAt(0);
        }

        // Box dimensions
        float boxW = 160;
        float boxH = 120;
        float boxX = w - 30 - boxW;  // right side, inside inset
        float boxY = h - 100 - boxH; // above waveform area

        // Background
        canvas.DrawRect(boxX, boxY, boxW, boxH, boxBgPaint);
        // Border
        canvas.DrawRect(boxX, boxY, boxW, boxH, boxBorderPaint);

        // Draw star lines
        float lineX = boxX + 8;
        float lineY = boxY + 16;
        float lineSpacing = 15;

        for (int i = 0; i < visibleLines.Count; i++)
        {
            canvas.DrawText(visibleLines[i], lineX, lineY + i * lineSpacing, boxTextPaint);
        }

        // Blinking cursor on next empty line
        bool cursorVisible = ((int)(elapsed * 2.5)) % 2 == 0;
        if (cursorVisible)
        {
            float cursorY = lineY + visibleLines.Count * lineSpacing;
            if (cursorY < boxY + boxH - 4)
            {
                canvas.DrawText("_", lineX, cursorY, boxTextPaint);
            }
        }
    }
}
