namespace NativeVox.App.Views;

using System.Windows;
using System.Windows.Media.Animation;
using NativeVox.App.Helpers;

public partial class OverlayWindow : Window
{
    private Storyboard? _pulseStoryboard;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowHelper.EnableNoActivate(this);
        _pulseStoryboard = (Storyboard)Resources["PulsingStoryboard"];
    }

    public void ShowNearCursor()
    {
        if (WindowHelper.GetCursorPos(out var pt))
        {
            // Position slightly offset to the bottom-right of the mouse cursor
            double targetLeft = pt.X + 16;
            double targetTop = pt.Y + 16;

            // Screen boundary clamping
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            if (targetLeft + Width > screenWidth)
            {
                targetLeft = pt.X - Width - 16;
            }

            if (targetTop + Height > screenHeight)
            {
                targetTop = pt.Y - Height - 16;
            }

            Left = Math.Max(0, targetLeft);
            Top = Math.Max(0, targetTop);
        }

        Show();
        _pulseStoryboard?.Begin();
    }

    public void HideOverlay()
    {
        _pulseStoryboard?.Stop();
        Hide();
    }

    public void UpdateAudioLevel(float level)
    {
        // Smoothly scale the pulse ring with real-time mic volume level
        Dispatcher.InvokeAsync(() =>
        {
            if (Visibility != Visibility.Visible) return;
            double scale = 1.0 + Math.Min(0.5, level * 2.0);
            PulseRingScale.ScaleX = scale;
            PulseRingScale.ScaleY = scale;
        });
    }
}
