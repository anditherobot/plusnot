using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using plusnot.Pipeline;

namespace plusnot;

public partial class CalibrationOverlay : UserControl
{
    private FramePipeline? pipeline;
    private Image? displayImage;
    private DispatcherTimer? countdownTimer;
    private DispatcherTimer? maskRefreshTimer;
    private int countdownRemain;
    private byte[]? manualMask;
    private bool painting;
    private WriteableBitmap?[] maskBitmaps = new WriteableBitmap?[3];
    private WriteableBitmap? finalPreviewBmp;
    private WriteableBitmap? silhouetteBmp;
    private int currentStep;
    private bool emptySceneCaptured;

    private enum CaptureMode { None, Empty, Human }
    private CaptureMode captureMode;

    public event EventHandler? WizardClosed;

    public CalibrationOverlay() => InitializeComponent();

    public void Open(FramePipeline p, Image display)
    {
        pipeline = p;
        displayImage = display;
        Visibility = Visibility.Visible;
        emptySceneCaptured = false;

        // Sync sliders with current pipeline settings
        WizSliderEdgeShrink.Value = p.Settings.EdgeShrink;
        WizSliderEdgeSoftness.Value = p.Settings.EdgeSoftness;
        WizSliderMaskHardness.Value = p.Settings.MaskHardness;
        WizSliderDiffThresh.Value = p.Settings.DiffThreshold;
        WizSliderDiffSpread.Value = p.Settings.DiffSpread;
        WizSliderStability.Value = (int)(p.Settings.Stability * 100);

        // Enable debug
        p.Settings.DebugEnabled = true;

        GoToStep(1);
    }

    void GoToStep(int step)
    {
        currentStep = step;
        countdownTimer?.Stop();
        captureMode = CaptureMode.None;

        LoadBgPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        CaptureEmptyPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        CaptureHumanPanel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        TunePanel.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        UpdateStepIndicator(step);

        if (step == 4)
        {
            StartMaskRefresh();
            // Sync slider to auto-threshold if it was computed
            WizSliderDiffThresh.Value = pipeline?.Settings.DiffThreshold ?? 25;
            WizLblDiffThresh.Text = ((int)WizSliderDiffThresh.Value).ToString();
            // Show silhouette ref if available
            if (pipeline?.HasHumanReference == true)
            {
                var (silMask, silW, silH) = pipeline.GetSilhouetteMask();
                if (silMask != null && silW > 0)
                {
                    UpdateMaskImage(SilhouetteRef, ref silhouetteBmp, silMask, silW, silH);
                    SilhouetteRefBorder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                SilhouetteRefBorder.Visibility = Visibility.Collapsed;
            }
        }
        else StopMaskRefresh();

        if (step == 2)
        {
            EmptyCountdown.Text = "";
            EmptyStatus.Text = "";
            BtnCaptureEmpty.IsEnabled = true;
            BtnCaptureEmpty.Content = "Capture";
            BtnSkipEmpty.Visibility = Visibility.Visible;
            BtnNextToHuman.Visibility = Visibility.Collapsed;
            BtnFinishEmpty.Visibility = Visibility.Collapsed;
            EmptyThumbnail.Visibility = Visibility.Collapsed;
        }
        if (step == 3)
        {
            HumanCountdown.Text = "";
            HumanStatus.Text = "";
            HumanThumbnails.Visibility = Visibility.Collapsed;
            SilhouetteQuality.Text = "";
            BtnCaptureHuman.IsEnabled = true;
            BtnCaptureHuman.Content = "Capture";
            BtnSkipHuman.Visibility = Visibility.Visible;
            BtnFinishHuman.Visibility = Visibility.Collapsed;
        }
    }

    void UpdateStepIndicator(int step)
    {
        var dots = new[] { StepDot1, StepDot2, StepDot3, StepDot4 };
        var lines = new[] { StepLine1, StepLine2, StepLine3 };
        var completedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00AA88"));
        var activeBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00FFCC"));
        var inactiveBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334444"));
        var darkFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A0A1A"));
        var dimFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#556666"));

        for (int i = 0; i < dots.Length; i++)
        {
            var dot = dots[i];
            var numText = (TextBlock)dot.Child;
            if (i + 1 < step)
            { dot.Background = completedBrush; dot.BorderBrush = completedBrush; numText.Foreground = darkFg; }
            else if (i + 1 == step)
            { dot.Background = activeBrush; dot.BorderBrush = activeBrush; numText.Foreground = darkFg; }
            else
            { dot.Background = Brushes.Transparent; dot.BorderBrush = inactiveBrush; numText.Foreground = dimFg; }
        }
        for (int i = 0; i < lines.Length; i++)
            lines[i].Fill = i + 1 < step ? completedBrush : inactiveBrush;
    }

    void CloseWizard()
    {
        StopMaskRefresh();
        countdownTimer?.Stop();
        if (pipeline != null)
            pipeline.Settings.DebugEnabled = false;
        Visibility = Visibility.Collapsed;
        WizardClosed?.Invoke(this, EventArgs.Empty);
    }

    // --- Step 1: Load Background Image ---

    void BtnChooseBg_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Images|*.jpg;*.png;*.bmp" };
        if (d.ShowDialog() == true)
        {
            pipeline?.SetBackground(d.FileName);
            try
            {
                var bi = new BitmapImage(new Uri(d.FileName));
                BgThumbnail.Source = bi;
                BgThumbnail.Visibility = Visibility.Visible;
            }
            catch { }
            GoToStep(2);
        }
    }

    void BtnSkipBg_Click(object s, RoutedEventArgs e) => GoToStep(2);

    // --- Step 2: Capture Empty Scene ---

    void BtnCaptureEmpty_Click(object s, RoutedEventArgs e)
    {
        if (pipeline == null || !pipeline.CameraAvailable) return;
        captureMode = CaptureMode.Empty;
        countdownRemain = 6;
        EmptyCountdown.Text = "6";
        EmptyStatus.Text = "Step away from the camera...";
        BtnCaptureEmpty.IsEnabled = false;
        EmptyThumbnail.Visibility = Visibility.Collapsed;

        countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        countdownTimer.Tick += CountdownTick;
        countdownTimer.Start();
    }

    void BtnSkipEmpty_Click(object s, RoutedEventArgs e)
    {
        emptySceneCaptured = false;
        GoToStep(4); // Skip steps 2 and 3
    }

    void BtnBackToStep1_Click(object s, RoutedEventArgs e) => GoToStep(1);

    // --- Step 3: Capture Human ---

    void BtnCaptureHuman_Click(object s, RoutedEventArgs e)
    {
        if (pipeline == null || !pipeline.CameraAvailable) return;
        captureMode = CaptureMode.Human;
        countdownRemain = 6;
        HumanCountdown.Text = "6";
        HumanStatus.Text = "Step INTO the frame...";
        BtnCaptureHuman.IsEnabled = false;
        HumanThumbnails.Visibility = Visibility.Collapsed;
        SilhouetteQuality.Text = "";

        countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        countdownTimer.Tick += CountdownTick;
        countdownTimer.Start();
    }

    void BtnSkipHuman_Click(object s, RoutedEventArgs e) => GoToStep(4);
    void BtnBackToStep2_Click(object s, RoutedEventArgs e) => GoToStep(2);
    void BtnNextToHuman_Click(object s, RoutedEventArgs e) => GoToStep(3);
    void BtnFinishCaptures_Click(object s, RoutedEventArgs e) => GoToStep(4);

    // --- Step 4: Back ---

    void BtnBackFromTune_Click(object s, RoutedEventArgs e)
    {
        if (emptySceneCaptured) GoToStep(3);
        else GoToStep(2);
    }

    // --- Shared countdown ---

    void CountdownTick(object? s, EventArgs e)
    {
        if (--countdownRemain > 0)
        {
            if (captureMode == CaptureMode.Empty)
                EmptyCountdown.Text = countdownRemain.ToString();
            else
                HumanCountdown.Text = countdownRemain.ToString();
            return;
        }
        countdownTimer?.Stop();

        if (captureMode == CaptureMode.Empty)
        {
            EmptyCountdown.Text = "";
            EmptyStatus.Text = "Capturing empty scene...";
            pipeline?.CaptureBackground(status =>
            {
                EmptyStatus.Text = status;
                if (status == "Background captured!")
                {
                    pipeline?.SetBackgroundFromReference();
                    emptySceneCaptured = true;

                    var bmp = pipeline?.GetReferenceBackgroundBitmap();
                    if (bmp != null) { EmptyThumbnail.Source = bmp; EmptyThumbnail.Visibility = Visibility.Visible; }
                    EmptyStatus.Text = "Empty scene captured!";

                    // Show post-capture buttons
                    BtnCaptureEmpty.Content = "Recapture";
                    BtnCaptureEmpty.IsEnabled = true;
                    BtnSkipEmpty.Visibility = Visibility.Collapsed;
                    BtnNextToHuman.Visibility = Visibility.Visible;
                    BtnFinishEmpty.Visibility = Visibility.Visible;
                }
            });
        }
        else if (captureMode == CaptureMode.Human)
        {
            HumanCountdown.Text = "";
            HumanStatus.Text = "Capturing human reference...";
            pipeline?.CaptureHumanReference(status =>
            {
                HumanStatus.Text = status;
                if (status == "Human captured!")
                    ShowHumanResults();
            });
        }
    }

    void ShowHumanResults()
    {
        if (pipeline == null) return;

        // Show 3 thumbnails: empty, person, silhouette
        var emptyBmp = pipeline.GetReferenceBackgroundBitmap();
        if (emptyBmp != null) HumanThumbEmpty.Source = emptyBmp;

        var humanBmp = pipeline.GetHumanReferenceBitmap();
        if (humanBmp != null) HumanThumbPerson.Source = humanBmp;

        var (silMask, silW, silH) = pipeline.GetSilhouetteMask();
        if (silMask != null && silW > 0 && silH > 0)
        {
            WriteableBitmap? silBmp = null;
            UpdateMaskImage(HumanThumbSilhouette, ref silBmp, silMask, silW, silH);
        }

        HumanThumbnails.Visibility = Visibility.Visible;

        // Auto-calculate DiffThreshold using Otsu's method
        int thresh = pipeline.ComputeAutoThreshold();
        pipeline.Settings.DiffThreshold = thresh;

        // Evaluate silhouette quality
        var (score, desc) = pipeline.EvaluateSilhouetteQuality();
        SilhouetteQuality.Text = $"Quality: {score}/100 — {desc}";

        HumanStatus.Text = $"Silhouette computed! Auto threshold: {thresh}";
        BtnCaptureHuman.Content = "Recapture";
        BtnCaptureHuman.IsEnabled = true;
        BtnSkipHuman.Visibility = Visibility.Collapsed;
        BtnFinishHuman.Visibility = Visibility.Visible;
    }

    // --- Mask refresh (Step 4) ---

    void StartMaskRefresh()
    {
        if (maskRefreshTimer != null) return;
        maskRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        maskRefreshTimer.Tick += MaskRefreshTick;
        maskRefreshTimer.Start();
    }

    void StopMaskRefresh()
    {
        maskRefreshTimer?.Stop();
        maskRefreshTimer = null;
    }

    void MaskRefreshTick(object? s, EventArgs e)
    {
        if (pipeline == null) return;
        var (raw, post, diff, dsz) = pipeline.GetDebugMasks();

        if (raw != null && dsz > 0) UpdateMaskImage(MaskAI, ref maskBitmaps[0], raw, dsz, dsz);
        if (post != null && dsz > 0) UpdateMaskImage(MaskPost, ref maskBitmaps[1], post, dsz, dsz);
        if (diff != null && dsz > 0) UpdateMaskImage(MaskDiff, ref maskBitmaps[2], diff, dsz, dsz);

        // Update final composited preview
        var (compData, compW, compH) = pipeline.GetLatestComposited();
        if (compData != null && compW > 0)
            UpdateBgraImage(FinalPreview, ref finalPreviewBmp, compData, compW, compH);
    }

    static void UpdateMaskImage(Image img, ref WriteableBitmap? bmp, byte[] gray, int w, int h)
    {
        int n = Math.Min(gray.Length, w * h);
        if (n == 0) return;
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h)
            bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.Lock();
        try
        {
            unsafe
            {
                byte* dst = (byte*)bmp.BackBuffer;
                int stride = bmp.BackBufferStride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = dst + y * stride;
                    int off = y * w;
                    for (int x = 0; x < w && off + x < n; x++)
                    {
                        byte v = gray[off + x];
                        int p = x * 4;
                        row[p] = v; row[p + 1] = v; row[p + 2] = v; row[p + 3] = 255;
                    }
                }
            }
            bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally { bmp.Unlock(); }
        if (img.Source != bmp) img.Source = bmp;
    }

    static void UpdateBgraImage(Image img, ref WriteableBitmap? bmp, byte[] bgra, int w, int h)
    {
        int need = w * h * 4;
        if (bgra.Length < need) return;
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h)
            bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.Lock();
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.BackBuffer, need);
            bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally { bmp.Unlock(); }
        if (img.Source != bmp) img.Source = bmp;
    }

    // --- Slider handlers ---

    void WizSliderEdgeShrink_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)e.NewValue;
        if (WizLblEdgeShrink != null) WizLblEdgeShrink.Text = v.ToString();
        if (pipeline != null) pipeline.Settings.EdgeShrink = v;
    }

    void WizSliderEdgeSoftness_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)e.NewValue;
        if (v > 0) v |= 1; // force odd
        if (WizLblEdgeSoftness != null) WizLblEdgeSoftness.Text = v.ToString();
        if (pipeline != null) pipeline.Settings.EdgeSoftness = v;
    }

    void WizSliderMaskHardness_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)e.NewValue;
        if (WizLblMaskHardness != null) WizLblMaskHardness.Text = v.ToString();
        if (pipeline != null) pipeline.Settings.MaskHardness = v;
    }

    void WizSliderDiffThresh_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)e.NewValue;
        if (WizLblDiffThresh != null) WizLblDiffThresh.Text = v.ToString();
        if (pipeline != null) pipeline.Settings.DiffThreshold = v;
    }

    void WizSliderDiffSpread_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)e.NewValue;
        if (WizLblDiffSpread != null) WizLblDiffSpread.Text = v.ToString();
        if (pipeline != null) pipeline.Settings.DiffSpread = v;
    }

    void WizSliderStability_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)e.NewValue;
        if (WizLblStability != null) WizLblStability.Text = v.ToString();
        if (pipeline != null) pipeline.Settings.Stability = v / 100f;
    }

    // --- Manual Brush ---

    void WizChkBrush_Changed(object s, RoutedEventArgs e)
    {
        bool active = WizChkBrush.IsChecked == true;
        BrushControls.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    void WizSliderBrushSize_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WizLblBrushSize != null) WizLblBrushSize.Text = ((int)e.NewValue).ToString();
    }

    void BtnClearBrush_Click(object s, RoutedEventArgs e)
    {
        manualMask = null;
        pipeline?.ClearManualMask();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (WizChkBrush.IsChecked == true && TunePanel.Visibility == Visibility.Visible)
        {
            painting = true;
            PaintAt(e.GetPosition(this));
            CaptureMouse();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (painting) PaintAt(e.GetPosition(this));
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (painting)
        {
            painting = false;
            ReleaseMouseCapture();
            if (manualMask != null) pipeline?.SetManualMask(manualMask);
        }
        base.OnMouseUp(e);
    }

    void PaintAt(Point screenPt)
    {
        if (pipeline == null || displayImage == null) return;
        int fw = pipeline.FrameWidth, fh = pipeline.FrameHeight;
        if (fw == 0 || fh == 0) return;

        // Transform screen point to camera frame coordinates
        var imgPos = displayImage.TranslatePoint(new Point(0, 0), this);
        double imgW = displayImage.ActualWidth, imgH = displayImage.ActualHeight;
        if (imgW <= 0 || imgH <= 0) return;

        // Uniform stretch: compute actual rendered area within the Image control
        double scaleX = imgW / fw, scaleY = imgH / fh;
        double scale = Math.Min(scaleX, scaleY);
        double renderW = fw * scale, renderH = fh * scale;
        double offX = imgPos.X + (imgW - renderW) / 2;
        double offY = imgPos.Y + (imgH - renderH) / 2;

        double cx = (screenPt.X - offX) / scale;
        double cy = (screenPt.Y - offY) / scale;

        if (cx < 0 || cy < 0 || cx >= fw || cy >= fh) return;

        if (manualMask == null || manualMask.Length != fw * fh)
            manualMask = new byte[fw * fh];

        byte val = RbKeep.IsChecked == true ? (byte)1 : (byte)2;
        int brushR = (int)WizSliderBrushSize.Value;

        int ix = (int)cx, iy = (int)cy;
        int r2 = brushR * brushR;
        for (int dy = -brushR; dy <= brushR; dy++)
        {
            int py = iy + dy;
            if (py < 0 || py >= fh) continue;
            for (int dx = -brushR; dx <= brushR; dx++)
            {
                int px = ix + dx;
                if (px < 0 || px >= fw) continue;
                if (dx * dx + dy * dy <= r2)
                    manualMask[py * fw + px] = val;
            }
        }
        pipeline?.SetManualMask(manualMask);
    }

    void BtnCancel_Click(object s, RoutedEventArgs e) => CloseWizard();
    void BtnDone_Click(object s, RoutedEventArgs e) => CloseWizard();
}
