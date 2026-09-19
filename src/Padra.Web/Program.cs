using Padra.Core.Configuration;
using Padra.Web;

// ---------------------------------------------------------------------------
// Headless entry point: `Padra.Server --config models.yaml [--lan] [--port N]`
// The desktop shell calls PadraHost.Build directly instead.
// ---------------------------------------------------------------------------
var parsed = CommandLine.Parse(args);
if (parsed is null)
{
    return 0;   // --help was printed
}

try
{
    var app = PadraHost.Build(parsed);
    await app.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (FleetFileException ex)
{
    Console.Error.WriteLine($"[gateway] {ex.Message}");
    return 2;
}
catch (IOException ex)
{
    Console.Error.WriteLine($"[gateway] {ex.Message}");
    return 3;
}

internal static class CommandLine
{
    public static PadraHostOptions? Parse(string[] args)
    {
        var config = "models.yaml";
        string? host = null;
        int? port = null;
        FleetMode? mode = null;
        var lan = false;
        var preload = false;
        string? logFile = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config" when index + 1 < args.Length:
                    config = args[++index];
                    break;
                case "--host" when index + 1 < args.Length:
                    host = args[++index];
                    break;
                case "--port" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsedPort) ||
                        parsedPort is < 1 or > 65535)
                    {
                        Console.Error.WriteLine($"[gateway] --port must be a number between 1 and 65535, not `{args[index]}`");
                        return null;
                    }

                    port = parsedPort;
                    break;
                case "--mode" when index + 1 < args.Length:
                    if (!Enum.TryParse<FleetMode>(args[++index], ignoreCase: true, out var parsedMode) ||
                        !Enum.IsDefined(parsedMode))
                    {
                        Console.Error.WriteLine($"[gateway] --mode must be `exclusive` or `concurrent`, not `{args[index]}`");
                        return null;
                    }

                    mode = parsedMode;
                    break;
                case "--log-file" when index + 1 < args.Length:
                    logFile = args[++index];
                    break;
                case "--lan":
                    lan = true;
                    break;
                case "--preload":
                    preload = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return null;
                default:
                    Console.Error.WriteLine($"[gateway] unknown argument: {args[index]}");
                    PrintHelp();
                    return null;
            }
        }

        return new PadraHostOptions
        {
            ConfigPath = Path.GetFullPath(config),
            HostOverride = host,
            PortOverride = port,
            ModeOverride = mode,
            ForceLan = lan,
            ForcePreload = preload,
            LogFile = logFile,
        };
    }

    private static void PrintHelp() => Console.WriteLine("""
        Padra Platform Launcher — gateway and control panel

          --config <path>   fleet definition (default: models.yaml)
          --host <address>  listening address; 0.0.0.0 serves the LAN
          --port <number>   listening port
          --mode <name>     exclusive | concurrent
          --lan             shorthand for --host 0.0.0.0
          --preload         load the default model at start-up
          --log-file <path> mirror the activity log to a file
          -h, --help        this message
        """);
}
