using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Padra.Desktop;

public partial class MainWindow : Window
{
    private readonly string _origin;
    private readonly string _logPath;
    private readonly GatewaySupervisor _gateway;

    public MainWindow(string origin, string logPath, GatewaySupervisor gateway)
    {
        InitializeComponent();
        _origin = origin;
        _logPath = logPath;
        _gateway = gateway;
        _gateway.Exited += OnGatewayExited;
        Loaded += OnLoaded;
        Closed += (_, _) => _gateway.Exited -= OnGatewayExited;
    }

    /// <summary>If the gateway dies the window is useless, so close it too.</summary>
    private void OnGatewayExited() => Dispatcher.Invoke(Close);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FallbackTitle.Text = "Starting the control panel…";
        FallbackDetail.Text = _origin;

        try
        {
            // Keep the browser profile beside the application so a portable copy
            // stays portable, and so uninstalling leaves nothing in %LOCALAPPDATA%.
            var profile = Path.Combine(AppContext.BaseDirectory, "webview2");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment
                .CreateAsync(userDataFolder: profile).ConfigureAwait(true);

            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            Browser.Source = new Uri(_origin + "/");
            Browser.Visibility = Visibility.Visible;
            Fallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or
                                       InvalidOperationException or IOException)
        {
            // No WebView2 runtime on this machine: offer the browser instead.
            FallbackTitle.Text = "The built-in window needs the WebView2 runtime.";
            FallbackDetail.Text =
                $"Install it from https://developer.microsoft.com/microsoft-edge/webview2 " +
                $"or open the panel in your browser.\n\n{_origin}\n\nDetails: {ex.Message}\n\n" +
                $"Log: {_logPath}";
            OpenInBrowser.Visibility = Visibility.Visible;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // Links to documentation belong in the real browser, not in this shell.
        args.Handled = true;
        Open(args.Uri);
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e) => Open(_origin + "/");

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Could not open {url}:\n{ex.Message}", "Padra Platform Launcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
