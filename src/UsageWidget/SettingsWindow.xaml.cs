using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace UsageWidget;

public partial class SettingsWindow : Window
{
    public string? ApiKey { get; private set; }

    public SettingsWindow(string? currentKey)
    {
        InitializeComponent();
        KeyBox.Text = currentKey ?? "";
        Loaded += (_, _) =>
        {
            KeyBox.Focus();
            KeyBox.CaretIndex = KeyBox.Text.Length;
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string trimmed = KeyBox.Text.Trim();
        ApiKey = trimmed.Length == 0 ? null : trimmed;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }
}
