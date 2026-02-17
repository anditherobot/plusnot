using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using plusnot.Pipeline;

namespace plusnot;

public partial class MainWindow : Window
{
    private FramePipeline? pipeline;
    private int camWidth = 640;
    private int camHeight = 480;
    private DispatcherTimer? bgCountdownTimer;
    private int bgCountdownRemaining;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        pipeline = new FramePipeline(Dispatcher, FpsText);
        pipeline.Start(DisplayImage, camWidth, camHeight);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        pipeline?.Stop();
        pipeline?.Dispose();
    }

    // --- Toolbar ---

    private void BtnCameraSettings_Click(object sender, RoutedEventArgs e)
    {
        pipeline?.OpenCameraSettings();
    }

    private void BtnBackground_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Image files (*.jpg;*.png;*.bmp)|*.jpg;*.png;*.bmp|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            pipeline?.SetBackground(dlg.FileName);
    }

    private void BtnTuning_Click(object sender, RoutedEventArgs e)
    {
        TuningPanel.Visibility = TuningPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // --- Tuning panel ---

    private void CmbModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (pipeline == null) return;
        var item = CmbModel.SelectedItem as ComboBoxItem;
        if (item?.Tag is string tag)
        {
            var model = tag switch
            {
                "MODNet" => SegmentationModel.MODNet,
                "SINet" => SegmentationModel.SINet,
                _ => SegmentationModel.MediaPipe,
            };
            pipeline.SetModel(model);
        }
    }

    private void CmbResolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (pipeline == null) return;
        var item = CmbResolution.SelectedItem as ComboBoxItem;
        if (item?.Tag is string tag)
        {
            // Restart pipeline with new resolution
            pipeline.Stop();
            pipeline.Dispose();

            camWidth = tag == "1280" ? 1280 : 640;
            camHeight = tag == "1280" ? 720 : 480;

            pipeline = new FramePipeline(Dispatcher, FpsText);
            pipeline.Start(DisplayImage, camWidth, camHeight);
        }
    }

    private void SliderBlur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)e.NewValue;
        if (val > 0) val = val | 1; // ensure odd
        if (LblBlur != null) LblBlur.Text = val.ToString();
        if (pipeline != null) pipeline.BlurSize = val;
    }

    private void SliderThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)e.NewValue;
        if (LblThreshold != null) LblThreshold.Text = val.ToString();
        if (pipeline != null) pipeline.MaskThreshold = val;
    }

    private void SliderFeather_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)e.NewValue;
        if (LblFeather != null) LblFeather.Text = val.ToString();
        if (pipeline != null) pipeline.FeatherErode = val;
    }

    private void SliderThreads_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)e.NewValue;
        if (LblThreads != null) LblThreads.Text = val.ToString();
        // Thread count takes effect on next model switch
        if (pipeline != null)
        {
            // Store for next Initialize call
        }
    }

    private void ChkSegmentation_Changed(object sender, RoutedEventArgs e)
    {
        if (pipeline != null)
            pipeline.SegmentationEnabled = ChkSegmentation.IsChecked == true;
    }

    private void ChkHud_Changed(object sender, RoutedEventArgs e)
    {
        if (pipeline != null)
            pipeline.HudEnabled = ChkHud.IsChecked == true;
    }

    // --- Background capture ---

    private void BtnCaptureBg_Click(object sender, RoutedEventArgs e)
    {
        if (pipeline == null || !pipeline.CameraAvailable) return;

        BtnCaptureBg.IsEnabled = false;
        bgCountdownRemaining = 3;
        BgOverlayText.Text = "Step away... Capturing in 3...";
        BgOverlayText.Visibility = Visibility.Visible;

        bgCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bgCountdownTimer.Tick += BgCountdown_Tick;
        bgCountdownTimer.Start();
    }

    private void BgCountdown_Tick(object? sender, EventArgs e)
    {
        bgCountdownRemaining--;

        if (bgCountdownRemaining > 0)
        {
            BgOverlayText.Text = $"Step away... Capturing in {bgCountdownRemaining}...";
        }
        else
        {
            bgCountdownTimer?.Stop();
            bgCountdownTimer = null;

            BgOverlayText.Text = "Capturing background...";
            pipeline?.CaptureBackground(status =>
            {
                BgOverlayText.Text = status;
                if (status == "Background captured!")
                {
                    // Hide overlay after a short delay
                    var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    hideTimer.Tick += (_, _) =>
                    {
                        hideTimer.Stop();
                        BgOverlayText.Visibility = Visibility.Collapsed;
                        BtnCaptureBg.IsEnabled = true;
                    };
                    hideTimer.Start();
                }
            });
        }
    }
}
