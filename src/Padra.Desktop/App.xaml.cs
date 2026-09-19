using System.IO;
using System.Windows;
using System.Windows.Threading;
using Padra.Core.Configuration;

namespace Padra.Desktop;

/// <summary>
/// The desktop shell: starts the gateway, shows the control panel in a WebView2
/// window, and asks the gateway to unload every model when the window closes.
/// <para>
/// The gateway runs as a child process rather than inside this one. That keeps
/// the Blazor static assets owned by the web project that generates them, and it
/// means a crash in the UI shell can never leave a model half-loaded: the
/// supervisor below always gets a chance to stop it.
/// </para>
/// </summary>
public partial class App : Application
{
    private GatewaySupervisor? _gateway;

    public string ConfigPath { get; private set; } = string.Empty;

    public string LogPath { get; private set; } = string.Empty;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;

        var directory = AppContext.BaseDirectory;
        ConfigPath = Path.Combine(directory, "models.yaml");
        LogPath = Path.Combine(directory, "padra-launcher.log");

        if (!File.Exists(ConfigPath))
        {
            try
            {
                File.WriteAllText(ConfigPath, StarterConfig.Yaml);
            }
            catch (IOException ex)
            {
                Fail($"models.yaml could not be created next to the application:\n\n{ex.Message}");
                return;
            }
        }

        int port;
        try
        {
            port = FleetFile.Load(ConfigPath).Gateway.Port;
        }
        catch (FleetFileException ex)
        {
            Fail($"models.yaml could not be read:\n\n{ex.Message}");
            return;
        }

        _gateway = new GatewaySupervisor(directory, ConfigPath, LogPath, port);

        try
        {
            _gateway.Start();
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                       or System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            // A missing server, or one the OS refused to start (policy, antivirus):
            // either way the window has nothing to show, so say why and stop.
            Fail($"The gateway could not be started.\n\n{ex.Message}");
            return;
        }

        if (!await _gateway.WaitUntilReadyAsync().ConfigureAwait(true))
        {
            Fail("The gateway did not start.\n\n" +
                 $"Its output is in:\n{LogPath}\n\n" +
                 "The usual cause is the port already being in use.");
            return;
        }

        var window = new MainWindow(_gateway.Origin, LogPath, _gateway);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _gateway?.Stop();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"{e.Exception.Message}\n\nThe details are in:\n{LogPath}",
            "Padra Platform Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void Fail(string message)
    {
        _gateway?.Stop();
        MessageBox.Show(message, "Padra Platform Launcher",
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }
}
