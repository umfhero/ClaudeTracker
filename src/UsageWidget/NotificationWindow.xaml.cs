using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace UsageWidget;

/// <summary>
/// Minimal toast popup (bottom-right, auto-dismisses). Used instead of native
/// Windows toasts to avoid pulling in the WinRT projection assemblies.
/// </summary>
public partial class NotificationWindow : Window
{
    private static NotificationWindow? _current;
    private readonly DispatcherTimer _closeTimer;

    private NotificationWindow(string title, string body, bool critical)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        SeverityDot.Fill = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(critical ? "#D03B3B" : "#FAB219"));

        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 16;
            Top = work.Bottom - ActualHeight - 16;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        };

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _closeTimer.Tick += (_, _) => FadeOut();
        _closeTimer.Start();
    }

    public static void ShowToast(string title, string body, bool critical)
    {
        _current?.Close();
        _current = new NotificationWindow(title, body, critical);
        _current.Closed += (s, _) => { if (ReferenceEquals(_current, s)) _current = null; };
        _current.Show();
    }

    private void FadeOut()
    {
        _closeTimer.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => FadeOut();

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer.Stop();
        base.OnClosed(e);
    }
}
