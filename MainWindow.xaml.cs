using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using plusnot.Pipeline;

namespace plusnot;

public partial class MainWindow : Window
{
    FramePipeline? p;
    int camW = 640, camH = 480;
    DispatcherTimer? cdTimer;
    int cdRemain;

    public MainWindow() => InitializeComponent();

    void Window_Loaded(object s, RoutedEventArgs e) { p = new FramePipeline(Dispatcher, FpsText); p.Start(DisplayImage, camW, camH); }
    void Window_Closing(object? s, CancelEventArgs e) { p?.Stop(); p?.Dispose(); }

    void BtnCam_Click(object s, RoutedEventArgs e) => p?.OpenCameraSettings();
    void BtnBg_Click(object s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Images|*.jpg;*.png;*.bmp" }; if (d.ShowDialog() == true) { p?.SetBackground(d.FileName); BtnCaptureBg_Click(s, e); } }
    void BtnTuning_Click(object s, RoutedEventArgs e) => TuningPanel.Visibility = TuningPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    void CmbModel_Changed(object s, SelectionChangedEventArgs e)
    {
        if (p == null) return;
        if ((CmbModel.SelectedItem as ComboBoxItem)?.Tag is string t)
            p.SetModel(t switch { "MODNet" => SegmentationModel.MODNet, "SINet" => SegmentationModel.SINet, _ => SegmentationModel.MediaPipe });
    }

    void CmbResolution_Changed(object s, SelectionChangedEventArgs e)
    {
        if (p == null) return;
        if ((CmbResolution.SelectedItem as ComboBoxItem)?.Tag is string t)
        {
            p.Stop(); p.Dispose();
            camW = t == "1280" ? 1280 : 640; camH = t == "1280" ? 720 : 480;
            p = new FramePipeline(Dispatcher, FpsText); p.Start(DisplayImage, camW, camH);
        }
    }

    void SliderBlur_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { int v = (int)e.NewValue; if (v > 0) v |= 1; if (LblBlur != null) LblBlur.Text = v.ToString(); if (p != null) p.BlurSize = v; }
    void SliderThreshold_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { int v = (int)e.NewValue; if (LblThreshold != null) LblThreshold.Text = v.ToString(); if (p != null) p.MaskThreshold = v; }
    void SliderFeather_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { int v = (int)e.NewValue; if (LblFeather != null) LblFeather.Text = v.ToString(); if (p != null) p.FeatherErode = v; }
    void SliderThreads_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { if (LblThreads != null) LblThreads.Text = ((int)e.NewValue).ToString(); }
    void ChkSeg_Changed(object s, RoutedEventArgs e) { if (p != null) p.SegmentationEnabled = ChkSegmentation.IsChecked == true; }
    void ChkHud_Changed(object s, RoutedEventArgs e) { if (p != null) p.HudEnabled = ChkHud.IsChecked == true; }
    void ChkDebug_Changed(object s, RoutedEventArgs e) { if (p != null) p.PipelineDebugEnabled = ChkDebug.IsChecked == true; }
    void SliderDiffThresh_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { int v = (int)e.NewValue; if (LblDiffThresh != null) LblDiffThresh.Text = v.ToString(); if (p != null) p.DiffThreshold = v; }
    void SliderDiffDilate_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { int v = (int)e.NewValue; if (LblDiffDilate != null) LblDiffDilate.Text = v.ToString(); if (p != null) p.DiffDilate = v; }

    void BtnCaptureBg_Click(object s, RoutedEventArgs e)
    {
        if (p == null || !p.CameraAvailable) return;
        BtnCaptureBg.IsEnabled = false; cdRemain = 5;
        BgOverlayText.Text = "Step away... Capturing in 5..."; BgOverlayText.Visibility = Visibility.Visible;
        cdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        cdTimer.Tick += CdTick; cdTimer.Start();
    }

    void CdTick(object? s, EventArgs e)
    {
        if (--cdRemain > 0) { BgOverlayText.Text = $"Step away... Capturing in {cdRemain}..."; return; }
        cdTimer?.Stop(); cdTimer = null;
        BgOverlayText.Text = "Capturing background...";
        p?.CaptureBackground(st =>
        {
            BgOverlayText.Text = st;
            if (st == "Background captured!")
            {
                var ht = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                ht.Tick += (_, _) => { ht.Stop(); BgOverlayText.Visibility = Visibility.Collapsed; BtnCaptureBg.IsEnabled = true; };
                ht.Start();
            }
        });
    }
}
