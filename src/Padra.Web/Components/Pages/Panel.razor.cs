using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Padra.Core.Clients;
using Padra.Core.Configuration;
using Padra.Core.Discovery;
using Padra.Core.Networking;
using Padra.Core.Runtime;
using Padra.Web.Services;

namespace Padra.Web.Components.Pages;

public enum LibraryTab
{
    Local,
    Catalog,
}

/// <summary>
/// The control panel. Everything is server-side: the component owns a refresh
/// timer, calls the same services the HTTP API uses, and never duplicates
/// business logic in the browser.
/// </summary>
public sealed partial class Panel
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(6);

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _refreshLoop;
    private DateTimeOffset _noticeShownAt = DateTimeOffset.MinValue;

    private string Error { get; set; } = string.Empty;

    private string Notice { get; set; } = string.Empty;

    private bool Busy { get; set; }

    private LibraryTab Tab { get; set; } = LibraryTab.Local;

    private string Search { get; set; } = string.Empty;

    private string RepoInput { get; set; } = string.Empty;

    private string QuantInput { get; set; } = string.Empty;

    private string AliasInput { get; set; } = string.Empty;

    /// <summary>The last install/restore report, shown inline so the outcome is visible without scrolling the log.</summary>
    private IReadOnlyList<string> InstallOutput { get; set; } = Array.Empty<string>();

    private string Scope { get; set; } = "local";

    private string ProjectFolder { get; set; } = string.Empty;

    private string LaunchAlias { get; set; } = string.Empty;

    private string ClientModelAlias { get; set; } = string.Empty;

    private bool ShareOnLan { get; set; }

    private int PortInput { get; set; }

    private int IdleInput { get; set; }

    private string Warnings { get; set; } = string.Empty;

    private HashSet<string> SelectedTargets { get; set; } = new(StringComparer.Ordinal);

    private bool MakeDefault { get; set; } = true;

    private bool FullToolAccess { get; set; }

    private bool CanRestore { get; set; }

    private EndpointSource SourceInput { get; set; } = EndpointSource.Gateway;

    private string UnslothUrlInput { get; set; } = string.Empty;

    private string UnslothKeyInput { get; set; } = string.Empty;

    private string UnslothUserInput { get; set; } = "unsloth";

    private string UnslothPasswordInput { get; set; } = string.Empty;

    private UnslothServerStatus Unsloth { get; set; } =
        UnslothServerStatus.Unreachable(UnslothServer.DefaultOrigin, string.Empty);

    /// <summary>Progress of the load in flight on Unsloth's server, when there is one.</summary>
    private UnslothLoadProgress LoadProgress { get; set; } = UnslothLoadProgress.None;

    /// <summary>The operation whose outcome has already been shown, so it is shown once.</summary>
    private UnslothOperation? ReportedOperation { get; set; }

    /// <summary>True when the agents are configured against Unsloth's own server.</summary>
    private bool DirectMode => Fleet.Gateway.EndpointSource == EndpointSource.Unsloth;

    /// <summary>The model the agents are pointed at in direct mode, as the server lists it.</summary>
    private UnslothModel? EffectiveUnslothModel => Resolver.EffectiveModel(Unsloth);

    private UnslothOperation? PendingOperation => Resolver.Operation is { InFlight: true } busy ? busy : null;

    /// <summary>
    /// The models the launch picker offers: the fleet against the gateway, the
    /// server's own list in direct mode.
    /// </summary>
    private IReadOnlyList<(string Value, string Label)> LaunchChoices => DirectMode
        ? Unsloth.Models.Select(m => (m.Spec, m.Spec == EffectiveUnslothModel?.Spec
            ? L["option.model_default", m.Spec].Value
            : m.Spec)).ToList()
        : Fleet.Options.Models.Select(m => (m.Alias, m.Alias == Fleet.DefaultAlias
            ? L["option.model_default", m.Alias].Value
            : m.Alias)).ToList();

    private string UnslothStartCommand =>
        string.Join(' ', new[] { Fleet.Gateway.UnslothBin }.Concat(
            UnslothServer.StartCommand(
                EffectiveUnslothModel?.Spec
                ?? Fleet.Options.Models.FirstOrDefault()?.ModelSpec
                ?? "unsloth/<repo>-GGUF",
                Unsloth.BaseUrl)));

    private string UnslothStateText => Unsloth switch
    {
        { Reachable: false } => L["state.unsloth_unreachable"].Value,
        { HaveKey: false } => L["state.unsloth_no_key"].Value,
        { Authorised: false } => L["state.unsloth_unauthorised"].Value,
        { Models.Count: 0 } => L["state.unsloth_no_models"].Value,
        _ when Unsloth.Runtime.IsLoading => L["state.unsloth_loading",
            string.Join(L["format.list_separator"].Value, Unsloth.Runtime.Loading)].Value,
        _ when Unsloth.LoadedModels.Count == 0 => L["state.unsloth_nothing_resident",
            CultureFormat.Number(Unsloth.Models.Count)].Value,
        _ => L["state.unsloth_ready", CultureFormat.Number(Unsloth.Models.Count)].Value,
    };

    private string UnslothBadgeClass => Unsloth switch
    {
        { Usable: true } when Unsloth.LoadedModels.Count > 0 => "badge badge-loaded",
        { Reachable: true } => "badge badge-attached",
        _ => "badge badge-failed",
    };

    private string UnslothServerLabel => Unsloth.ServerName.Length > 0
        ? Unsloth.ServerVersion.Length > 0
            ? $"{Unsloth.ServerName} {Unsloth.ServerVersion}"
            : Unsloth.ServerName
        : string.Empty;

    private static string UnslothModelBadge(UnslothModel model, bool loading) => loading
        ? "badge badge-attached"
        : model.Loaded
            ? "badge badge-loaded"
            : "badge";

    private string UnslothModelState(UnslothModel model, bool loading) => loading
        ? L["state.loading"].Value
        : model.Loaded
            ? L["state.loaded"].Value
            : L["state.on_disk"].Value;

    private string OperationText
    {
        get
        {
            if (PendingOperation is not { } operation)
            {
                return string.Empty;
            }

            if (operation.Kind == "unload")
            {
                return L["progress.unloading", operation.Spec].Value;
            }

            if (LoadProgress.InFlight && LoadProgress.BytesTotal > 0)
            {
                return L["progress.loading_bytes", operation.Spec,
                    CultureFormat.Number((long)Math.Round(LoadProgress.Fraction * 100)),
                    CultureFormat.Bytes(LoadProgress.BytesLoaded, L),
                    CultureFormat.Bytes(LoadProgress.BytesTotal, L)].Value;
            }

            return L["progress.loading", operation.Spec].Value;
        }
    }

    private void SyncClientModel()
    {
        if (LaunchChoices.Any(choice =>
                string.Equals(choice.Value, ClientModelAlias, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ClientModelAlias = LaunchChoices.FirstOrDefault().Value ?? string.Empty;
    }

    private HardwareSnapshot Hardware { get; set; } = HardwareSnapshot.Unknown;

    private Dictionary<string, ModelFootprint> Footprints { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The alias whose runtime form is open, or empty when none is.</summary>
    private string RuntimeAlias { get; set; } = string.Empty;

    /// <summary>The runtime block being edited, applied only when it is saved.</summary>
    private RuntimeOptions Draft { get; set; } = new();

    private int ContextInput { get; set; }

    private int GpuLayersInput { get; set; }

    private int FitContextInput { get; set; }

    private int CpuMoeInput { get; set; }

    private int ThreadsInput { get; set; }

    private IReadOnlyList<LibraryEntry> LocalModels { get; set; } = Array.Empty<LibraryEntry>();

    private long LocalBytes { get; set; }

    private IReadOnlyList<CatalogRepo> CatalogRepos { get; set; } = Array.Empty<CatalogRepo>();

    private HashSet<string> DownloadedRepos { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private CatalogRepo? SelectedRepo { get; set; }

    private IReadOnlyList<LogEntry> Entries { get; set; } = Array.Empty<LogEntry>();

    private long LogFloor { get; set; }

    private EndpointMap Endpoints { get; set; } = default!;

    private string CurrentCulture => CultureInfo.CurrentUICulture.Name;

    private int LoadedCount => Fleet.Backends.Count(b => b.IsRunning);

    private string LocalUrl => ClientContext.DefaultBaseUrl(Fleet.Options);

    private string StatusText
    {
        get
        {
            var loaded = Fleet.Backends.Where(b => b.IsRunning).Select(b => b.Alias).ToList();
            return loaded.Count > 0
                ? L["status.running_with", string.Join(L["format.list_separator"].Value, loaded)].Value
                : L["status.running_idle"].Value;
        }
    }

    private string Summary =>
        $"{L["footer.models_loaded", CultureFormat.Number(LoadedCount), CultureFormat.Number(Fleet.Options.Models.Count)].Value}" +
        $" · {L["footer.updated", CultureFormat.Time(DateTimeOffset.Now)].Value}";

    protected override async Task OnInitializedAsync()
    {
        var settings = Settings.Snapshot();
        SelectedTargets = settings.InstallTargets.ToHashSet(StringComparer.Ordinal);
        MakeDefault = settings.MakeDefaultProvider;
        FullToolAccess = settings.FullToolAccess;
        ProjectFolder = settings.ProjectFolder;

        // The remembered choice is reconciled with whatever the configured server
        // serves once that is known: the fleet now, Unsloth's list after the probe.
        LaunchAlias = settings.SelectedAlias;
        ClientModelAlias = settings.ClientModel.Length > 0
            ? settings.ClientModel
            : settings.SelectedAlias;

        RefreshNetworkInputs();
        ShareOnLan = Endpoints.Shared || Fleet.Gateway.AllowLan;
        PortInput = Fleet.Gateway.Port;
        IdleInput = Fleet.Gateway.IdleTimeout;

        Entries = Log.Since(LogFloor);
        UpdateWarnings();

        SourceInput = Fleet.Gateway.EndpointSource;
        UnslothUrlInput = Fleet.Gateway.UnslothServerUrl;
        UnslothKeyInput = Fleet.Gateway.UnslothApiKey;

        await LoadLibraryAsync(false);
        RefreshFootprints(false);
        RefreshRestoreState();

        // Only probe when it is the configured endpoint: a panel that pings a server
        // nobody chose would report a failure nobody has to care about.
        if (SourceInput == EndpointSource.Unsloth)
        {
            Unsloth = await Resolver.UnslothStatusAsync();
        }

        SyncLaunchAlias();
        SyncClientModel();
        StartRefreshLoop();
    }

    // ------------------------------------------------------------ endpoint #
    private async Task OnSourceChanged(ChangeEventArgs args)
    {
        SourceInput = ParseEnum(args.Value, EndpointSource.Gateway);
        if (SourceInput == EndpointSource.Unsloth)
        {
            await CheckUnslothAsync();
        }
    }

    private Task CheckUnslothAsync() => RunAsync(async () =>
    {
        Unsloth = await Resolver.UnslothStatusAsync(refresh: true);
        SyncLaunchAlias();
    });

    private Task ApplyEndpointAsync() => RunAsync(async () =>
    {
        var draft = Fleet.Options.Clone();
        draft.Gateway.EndpointSource = SourceInput;

        var url = UnslothUrlInput.Trim();
        if (url.Length > 0 && !Uri.TryCreate(UnslothServer.Normalise(url), UriKind.Absolute, out _))
        {
            Error = L["error.request", url];
            return;
        }

        draft.Gateway.UnslothServerUrl = url.Length > 0 ? UnslothServer.Normalise(url) : string.Empty;
        draft.Gateway.UnslothApiKey = UnslothKeyInput.Trim();

        ApplyDraft(draft);
        Resolver.Invalidate();

        UnslothUrlInput = Fleet.Gateway.UnslothServerUrl;
        if (SourceInput == EndpointSource.Unsloth)
        {
            Unsloth = await Resolver.UnslothStatusAsync(refresh: true);
        }

        UpdateWarnings();
        SyncLaunchAlias();
        ShowNotice(L["notice.endpoint_saved",
            L[$"serve.{SourceInput.ToString().ToLowerInvariant()}"].Value]);
    });

    /// <summary>Sign in to Unsloth Studio once and keep the key it hands out.</summary>
    private Task CreateUnslothKeyAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(UnslothUserInput) || string.IsNullOrEmpty(UnslothPasswordInput))
        {
            Error = L["error.sign_in_required"];
            return;
        }

        // The address typed into the form is what the sign-in must go to, even
        // before Apply was pressed; save it first so the probe and the key agree.
        var draft = Fleet.Options.Clone();
        var url = UnslothUrlInput.Trim();
        if (url.Length > 0 && !Uri.TryCreate(UnslothServer.Normalise(url), UriKind.Absolute, out _))
        {
            Error = L["error.request", url];
            return;
        }

        draft.Gateway.UnslothServerUrl = url.Length > 0 ? UnslothServer.Normalise(url) : string.Empty;
        if (!draft.Gateway.UnslothServerUrl.Equals(Fleet.Gateway.UnslothServerUrl, StringComparison.Ordinal))
        {
            ApplyDraft(draft);
            Resolver.Invalidate();
        }

        try
        {
            var key = await Resolver.MintKeyAsync(UnslothUserInput, UnslothPasswordInput);
            UnslothKeyInput = key;
            UnslothPasswordInput = string.Empty;
        }
        catch (UnslothServerException ex)
        {
            Error = L["error.sign_in_failed", ex.Message];
            return;
        }

        Unsloth = await Resolver.UnslothStatusAsync(refresh: true);
        SyncLaunchAlias();
        ShowNotice(L["notice.key_created", Unsloth.BaseUrl]);
    });

    /// <summary>Point the agents at one of the models Unsloth's server lists.</summary>
    private Task SelectUnslothModelAsync(string spec) => RunAsync(async () =>
    {
        try
        {
            var model = await Resolver.SelectModelAsync(spec);
            Unsloth = await Resolver.UnslothStatusAsync();
            LaunchAlias = model.Spec;
            ShowNotice(L["notice.unsloth_selected", model.Spec]);
        }
        catch (EndpointUnavailableException ex)
        {
            Error = L["error.endpoint_unavailable", ex.Message];
        }
    });

    /// <summary>Ask Unsloth's server to make a model resident; progress arrives through the refresh loop.</summary>
    private Task LoadUnslothModelAsync(string spec) => RunAsync(async () =>
    {
        try
        {
            var operation = await Resolver.StartLoadAsync(spec, select: true);
            Unsloth = await Resolver.UnslothStatusAsync();
            LaunchAlias = spec;
            ReportedOperation = operation.InFlight ? null : ReportedOperation;
            Log.Write(L["log.unsloth_loading", spec]);
        }
        catch (EndpointUnavailableException ex)
        {
            Error = L["error.endpoint_unavailable", ex.Message];
        }
    });

    private Task UnloadUnslothModelAsync(string spec) => RunAsync(async () =>
    {
        try
        {
            var operation = await Resolver.StartUnloadAsync(spec);
            ReportedOperation = operation.InFlight ? null : ReportedOperation;
            Log.Write(L["log.unsloth_unloading", spec]);
        }
        catch (EndpointUnavailableException ex)
        {
            Error = L["error.endpoint_unavailable", ex.Message];
        }
    });

    /// <summary>
    /// Keep the launch picker on a model the configured server actually serves:
    /// the chosen Unsloth model in direct mode, a fleet alias otherwise.
    /// </summary>
    private void SyncLaunchAlias()
    {
        if (DirectMode)
        {
            if (Unsloth.Find(LaunchAlias) is null)
            {
                LaunchAlias = EffectiveUnslothModel?.Spec ?? string.Empty;
            }

            return;
        }

        if (!Fleet.Options.Models.Any(m => m.Alias == LaunchAlias))
        {
            LaunchAlias = Fleet.DefaultAlias;
        }
    }

    /// <summary>What one refresh tick learned about Unsloth's server, off the dispatcher.</summary>
    private sealed record UnslothPoll(
        UnslothServerStatus? Status,
        UnslothLoadProgress Progress,
        UnslothOperation? Operation,
        bool Finished);

    /// <summary>
    /// Follow Unsloth's server from the refresh loop. This runs off the
    /// dispatcher on purpose: an unreachable host costs a whole connection
    /// timeout, and the panel has to stay responsive while that elapses.
    /// </summary>
    private async Task<UnslothPoll> PollUnslothAsync(CancellationToken token)
    {
        var operation = Resolver.Operation;
        var finished = operation is { InFlight: false } && !ReferenceEquals(operation, ReportedOperation);

        if (operation is { InFlight: true })
        {
            var progress = operation.Kind == "load"
                ? await Resolver.LoadProgressAsync(token)
                : UnslothLoadProgress.None;
            return new UnslothPoll(await Resolver.UnslothStatusAsync(token: token), progress, operation, false);
        }

        if (finished)
        {
            return new UnslothPoll(await Resolver.UnslothStatusAsync(refresh: true, token),
                UnslothLoadProgress.None, operation, true);
        }

        // The server can change under the panel — a load started in Studio's own
        // window, a model unloaded there — so the table is kept honest.
        return DirectMode
            ? new UnslothPoll(await Resolver.UnslothStatusAsync(token: token), UnslothLoadProgress.None, null, false)
            : new UnslothPoll(null, UnslothLoadProgress.None, null, false);
    }

    /// <summary>Apply what the tick learned, on the dispatcher, and report an outcome once.</summary>
    private void ApplyUnslothPoll(UnslothPoll poll)
    {
        LoadProgress = poll.Progress;
        if (poll.Status is not null)
        {
            Unsloth = poll.Status;
        }

        if (!poll.Finished || poll.Operation is not { } operation)
        {
            return;
        }

        ReportedOperation = operation;
        SyncLaunchAlias();

        var result = operation.Result;
        if (result is { Ok: true })
        {
            ShowNotice(operation.Kind == "load"
                ? L["notice.unsloth_loaded", operation.Spec].Value
                : L["notice.unsloth_unloaded", operation.Spec].Value);
        }
        else if (result is not null)
        {
            Error = L["error.unsloth_operation", operation.Spec, result.Detail];
        }
    }

    /// <summary>
    /// Re-measure the machine and re-size every model against it. The scan the
    /// estimate needs is already cached by <see cref="LoadLibraryAsync"/>, so this
    /// costs nothing but arithmetic.
    /// </summary>
    private void RefreshFootprints(bool refreshHardware)
    {
        Hardware = HardwareProbe.Read(refreshHardware);
        Footprints = Fleet.Options.Models.ToDictionary(
            m => m.Alias, m => Fleet.Footprint(m), StringComparer.Ordinal);
    }

    private void RefreshRestoreState() =>
        CanRestore = ClientInstallState.Open()
            .HasRecordFor(ClientConfigWriter.AllTargets.Select(ClientConfigWriter.Slug));

    private ModelFootprint FootprintFor(string alias) =>
        Footprints.TryGetValue(alias, out var footprint) ? footprint : ModelFootprint.Unknown;

    private static string FitClass(FitVerdict verdict) => verdict switch
    {
        FitVerdict.Gpu => "badge badge-loaded",
        FitVerdict.Offload or FitVerdict.Cpu => "badge badge-attached",
        FitVerdict.Disk => "badge badge-failed",
        _ => "badge",
    };

    private static TEnum ParseEnum<TEnum>(object? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value?.ToString(), out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    private void StartRefreshLoop()
    {
        _refreshLoop = new CancellationTokenSource();
        _timer = new PeriodicTimer(RefreshInterval);
        var token = _refreshLoop.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    // Network probes happen here, on the pool thread, so a slow or
                    // absent server never stalls the dispatcher that renders the panel.
                    var poll = await PollUnslothAsync(token);

                    await InvokeAsync(() =>
                    {
                        Entries = Log.Since(LogFloor);
                        RefreshNetworkInputs();
                        ApplyUnslothPoll(poll);
                        if (Notice.Length > 0 && DateTimeOffset.UtcNow - _noticeShownAt > NoticeLifetime)
                        {
                            Notice = string.Empty;
                        }

                        StateHasChanged();
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The circuit is closing.
            }
        }, token);
    }

    private void RefreshNetworkInputs() =>
        Endpoints = NetworkInfo.EndpointUrls(Fleet.Gateway.Host, Fleet.Gateway.Port);

    private void UpdateWarnings()
    {
        var parts = new List<string>();

        // A local `unsloth` command only matters when something here would run it:
        // the gateway spawning backends, or a direct server on this machine.
        var needsCli = !DirectMode || Resolver.UnslothIsLocal;
        if (needsCli && ExecutableFinder.Find(Fleet.Gateway.UnslothBin) is null)
        {
            parts.Add(L["warn.unsloth_missing"]);
        }

        var missing = AgentLauncher.AllAvailability()
            .Where(a => !a.Installed)
            .Select(a => L[$"client.{AgentLauncher.Slug(a.Agent)}"].Value)
            .ToList();
        if (missing.Count > 0)
        {
            parts.Add(L["warn.agents_missing", string.Join(L["format.list_separator"].Value, missing)]);
        }

        Warnings = string.Join(" · ", parts);
    }

    private static string BadgeClass(ModelBackend? backend) => backend switch
    {
        { External: true } => "badge badge-attached",
        { IsRunning: true } => "badge badge-loaded",
        { LastError.Length: > 0 } => "badge badge-failed",
        _ => "badge",
    };

    private static string BadgeKey(ModelBackend? backend) => backend switch
    {
        { External: true } => "state.attached",
        { IsRunning: true } => "state.loaded",
        { LastError.Length: > 0 } => "state.failed",
        _ => "state.idle",
    };

    // ------------------------------------------------------------- actions #
    /// <summary>
    /// Runs one panel action with a busy flag and a single error surface.
    /// <para>
    /// Nothing in this component uses <c>ConfigureAwait(false)</c>: continuations
    /// have to come back on the Blazor dispatcher, and calling
    /// <see cref="ComponentBase.StateHasChanged"/> from a pool thread terminates
    /// the circuit. `PanelDispatcherTests` guards that.
    /// </para>
    /// </summary>
    private async Task RunAsync(Func<Task> action)
    {
        Busy = true;
        Error = string.Empty;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is FleetFileException or BackendStartException
                                       or CatalogException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException
                                       or UnknownModelException or NoModelsConfiguredException
                                       or JsonException or HttpRequestException
                                       or TaskCanceledException)
        {
            // Anything a panel action can raise has to land here: an unhandled
            // exception in a Blazor event handler tears down the circuit, and the
            // whole panel stops responding. A model removed in another tab between a
            // render and a click is the ordinary way that happens.
            Error = L["error.request", ex.Message];
        }
        finally
        {
            Busy = false;
            Entries = Log.Since(LogFloor);
            StateHasChanged();
        }
    }

    private void ShowNotice(string message)
    {
        Notice = message;
        _noticeShownAt = DateTimeOffset.UtcNow;
    }

    private Task RefreshAsync() => RunAsync(async () =>
    {
        RefreshNetworkInputs();
        UpdateWarnings();
        await LoadLibraryAsync(false);
        RefreshFootprints(false);
        RefreshRestoreState();
    });

    // ---------------------------------------------------- hardware and fit #
    private Task RefreshHardwareAsync() => RunAsync(async () =>
    {
        await LoadLibraryAsync(true);
        RefreshFootprints(true);
    });

    private void ToggleRuntime(string alias)
    {
        if (RuntimeAlias == alias)
        {
            CloseRuntime();
            return;
        }

        OpenRuntime(alias);
    }

    /// <summary>Load one model's runtime block into the form.</summary>
    private void OpenRuntime(string alias)
    {
        var model = Fleet.Options.Models.FirstOrDefault(m => m.Alias == alias);
        if (model is null)
        {
            return;
        }

        RuntimeAlias = alias;
        Draft = model.Runtime.Clone();
        ContextInput = model.ContextLength ?? 0;

        // The form uses 0 for "leave it automatic"; -1 keeps its meaning of "all".
        GpuLayersInput = Draft.GpuLayers ?? 0;
        FitContextInput = Draft.FitContext ?? 0;
        CpuMoeInput = Draft.CpuMoeLayers ?? 0;
        ThreadsInput = Draft.Threads ?? 0;
    }

    private void CloseRuntime()
    {
        RuntimeAlias = string.Empty;
        Draft = new RuntimeOptions();
    }

    private Task SaveRuntimeAsync(string alias) => RunAsync(async () =>
    {
        Draft.GpuLayers = GpuLayersInput == 0 ? null : GpuLayersInput;
        Draft.FitContext = FitContextInput > 0 ? FitContextInput : null;
        Draft.CpuMoeLayers = CpuMoeInput == 0 ? null : CpuMoeInput;
        Draft.Threads = ThreadsInput > 0 ? ThreadsInput : null;

        // Applying a changed runtime block replaces the backend, which stops the
        // process if it is running. Doing that here would block the renderer for as
        // long as the process takes to die, so it is unloaded first, asynchronously.
        await UnloadIfRunningAsync(alias);

        var draft = Fleet.Options.Clone();
        FleetFile.SetRuntime(draft, alias, Draft);
        FleetFile.SetContextLength(draft, alias, ContextInput);
        ApplyDraft(draft);

        RefreshFootprints(false);
        CloseRuntime();
        ShowNotice(L["notice.runtime_saved", alias]);
    });

    private async Task UnloadIfRunningAsync(string alias)
    {
        if (Fleet.Find(alias) is { IsRunning: true } backend)
        {
            await Fleet.UnloadAsync(backend);
        }
    }

    private Task TuneAsync(string alias) => RunAsync(async () =>
    {
        var model = Fleet.Options.Models.FirstOrDefault(m => m.Alias == alias);
        if (model is null)
        {
            return;
        }

        await LoadLibraryAsync(false);
        RefreshFootprints(false);

        var footprint = FootprintFor(alias);
        if (footprint.Verdict == FitVerdict.Unknown)
        {
            Error = L["fit.unknown"];
            return;
        }

        await UnloadIfRunningAsync(alias);

        var header = Library.Weights(model)?.Header ?? GgufMetadata.Empty;
        var draft = Fleet.Options.Clone();
        FleetFile.SetRuntime(draft, alias,
            ModelFootprintCalculator.SuggestRuntime(model.Runtime, footprint, header));
        ApplyDraft(draft);

        RefreshFootprints(false);
        if (RuntimeAlias == alias)
        {
            OpenRuntime(alias);   // the open form must show what was just applied
        }

        ShowNotice(L["notice.runtime_tuned", alias]);
    });

    private Task LoadAsync(string alias) => RunAsync(async () =>
    {
        var backend = Fleet.Resolve(alias, fallback: false);
        Log.Write(L["log.loading_model", alias]);
        await Fleet.EnsureAsync(backend);
    });

    private Task UnloadAsync(string alias) => RunAsync(async () =>
    {
        var backend = Fleet.Resolve(alias, fallback: false);
        await Fleet.UnloadAsync(backend);
    });

    private Task ActivateAsync(string alias) => RunAsync(async () =>
    {
        var backend = Fleet.Resolve(alias, fallback: false);
        Fleet.SetDefault(backend.Alias);
        Settings.Update(s => s.SelectedAlias = backend.Alias);
        if (!DirectMode)
        {
            LaunchAlias = backend.Alias;
        }

        // In exclusive mode EnsureAsync swaps the others out itself, draining
        // their in-flight requests first.
        await Fleet.EnsureAsync(backend);
        ShowNotice(L["notice.activated", backend.Alias]);
    });

    /// <summary>A browser confirm; a failed interop counts as "no" rather than a crash.</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        try
        {
            return await Js.InvokeAsync<bool>("confirm", message);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException
                                       or TaskCanceledException)
        {
            return false;
        }
    }

    private Task RemoveAsync(string alias) => RunAsync(async () =>
    {
        if (!await ConfirmAsync(L["confirm.remove_model", alias]))
        {
            return;
        }

        var backend = Fleet.Find(alias);
        if (backend is null)
        {
            return;
        }

        if (backend.IsRunning)
        {
            await Fleet.UnloadAsync(backend);
        }

        var draft = Fleet.Options.Clone();
        FleetFile.RemoveModel(draft, alias);
        ApplyDraft(draft);
        Library.Invalidate();
        Log.Write(L["log.model_removed", alias]);
        await LoadLibraryAsync(false);
        RefreshFootprints(false);
    });

    private Task AddAsync(string repo, string quant, int? contextLength) => RunAsync(async () =>
    {
        var draft = Fleet.Options.Clone();
        var entry = FleetFile.AddModel(draft, repo, quant, contextLength: contextLength);
        ApplyDraft(draft);
        Library.Invalidate();
        ShowNotice(L["notice.model_added", entry.Alias]);
        await LoadLibraryAsync(false);
        RefreshFootprints(false);
        if (SelectedRepo is { } record)
        {
            await ShowVariantsAsync(record.Repo);
        }
    });

    /// <summary>Add a model typed by hand: any repository, any quantisation, any name.</summary>
    private Task AddManualAsync() => RunAsync(async () =>
    {
        var repo = RepoInput.Trim();
        if (repo.Length == 0)
        {
            Error = L["error.repo_required"];
            return;
        }

        var draft = Fleet.Options.Clone();
        var entry = FleetFile.AddModel(draft, repo, QuantInput.Trim(), AliasInput.Trim());
        ApplyDraft(draft);
        Library.Invalidate();

        RepoInput = string.Empty;
        QuantInput = string.Empty;
        AliasInput = string.Empty;

        ShowNotice(L["notice.model_added", entry.Alias]);
        RefreshFootprints(false);
        await LoadLibraryAsync(false);
    });

    private void ApplyDraft(FleetOptions draft)
    {
        var validated = FleetFile.Validate(draft);
        FleetFile.Save(Fleet.ConfigPath, validated);
        foreach (var alias in Fleet.Apply(validated))
        {
            Log.Write($"{alias}: stopped because its configuration changed");
        }

        SyncLaunchAlias();
        SyncClientModel();
    }

    private async Task LoadLibraryAsync(bool refresh)
    {
        var snapshot = await Library.LocalAsync(refresh);
        LocalModels = snapshot.Models;
        LocalBytes = snapshot.TotalBytes;
        DownloadedRepos = snapshot.Models.Select(m => m.Model.Repo.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void SelectTab(LibraryTab tab)
    {
        Tab = tab;
        if (tab == LibraryTab.Catalog && CatalogRepos.Count == 0)
        {
            _ = SearchCatalogAsync();
        }
    }

    private async Task OnSearchKey(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await SearchCatalogAsync();
        }
    }

    private Task SearchCatalogAsync() => RunAsync(async () =>
    {
        CatalogRepos = await Library.Catalog.RepositoriesAsync(Search);
        SelectedRepo = null;
    });

    private Task ShowVariantsAsync(string repo) => RunAsync(async () =>
    {
        var record = await Library.Catalog.VariantsAsync(repo);
        var local = await Library.LocalModelsAsync();
        SelectedRepo = HuggingFaceCatalog.MergeDownloaded(record, local);
    });

    private void ToggleTarget(string slug, bool selected)
    {
        if (selected)
        {
            SelectedTargets.Add(slug);
        }
        else
        {
            SelectedTargets.Remove(slug);
        }

        Settings.Update(s => s.InstallTargets = SelectedTargets.ToList());
    }

    private void SetMakeDefault(bool selected)
    {
        MakeDefault = selected;
        Settings.Update(s => s.MakeDefaultProvider = selected);
    }

    private void SetFullToolAccess(bool selected)
    {
        FullToolAccess = selected;
        Settings.Update(s => s.FullToolAccess = selected);
    }

    private void SetClientModel(string value)
    {
        ClientModelAlias = value;
        Settings.Update(s => s.ClientModel = value);
    }

    private Task InstallAsync() => RunAsync(async () =>
    {
        if (SelectedTargets.Count == 0)
        {
            Error = L["error.no_targets"];
            return;
        }

        // Against the gateway the models are the fleet; in direct mode they are
        // whatever Unsloth's server lists, and an empty fleet is no obstacle.
        if (!DirectMode && Fleet.Options.Models.Count == 0)
        {
            Error = L["error.no_model"];
            return;
        }

        var targets = ClientConfigWriter.AllTargets
            .Where(t => SelectedTargets.Contains(ClientConfigWriter.Slug(t)))
            .ToList();

        ClientContext context;
        try
        {
            context = await Resolver.ContextAsync(Scope);
        }
        catch (EndpointUnavailableException ex)
        {
            Error = L["error.endpoint_unavailable", ex.Message];
            return;
        }

        if (context.Models.Count == 0)
        {
            Error = L[DirectMode ? "error.no_unsloth_model" : "error.no_model"];
            return;
        }

        context = context.WithDefaultModel(ClientModelAlias);

        var output = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Fleet.ConfigPath) ?? ".", "generated");

        var installOptions = new InstallOptions(MakeDefault, PersistEnvironment: true,
            FullToolAccess: FullToolAccess);
        var report = await Task.Run(() =>
        {
            ClientConfigWriter.WriteGenerated(context, output, targets, FullToolAccess);
            return ClientConfigWriter.Install(context, targets, installOptions);
        });

        foreach (var line in report.Lines)
        {
            Log.Write(line);
        }

        InstallOutput = report.Lines.ToList();
        Settings.Update(s =>
        {
            s.InstallTargets = targets.Select(ClientConfigWriter.Slug).ToList();
            s.MakeDefaultProvider = MakeDefault;
            s.FullToolAccess = FullToolAccess;
        });
        RefreshRestoreState();

        if (report.Ok)
        {
            ShowNotice(L["notice.install_ok", CultureFormat.Number(report.Written.Count),
                context.BaseUrl]);
        }
        else
        {
            Error = L["error.install_failed", string.Join(", ", report.Failed)];
        }

        if (DirectMode)
        {
            Unsloth = await Resolver.UnslothStatusAsync();
            if (EffectiveUnslothModel is { Loaded: false } chosen && !Unsloth.IsLoading(chosen.Spec))
            {
                // Written, but the agents would talk to a model the server has not
                // brought up: say so here, where the Load button is one scroll away.
                Warnings = L["warn.unsloth_not_resident", chosen.Spec];
            }
        }
    });

    private Task RestoreAsync() => RunAsync(async () =>
    {
        // The same agents the install covers, not all of them: a restore must not
        // reach into an agent the user never asked this launcher to configure.
        var targets = ClientConfigWriter.AllTargets
            .Where(t => SelectedTargets.Contains(ClientConfigWriter.Slug(t)))
            .ToList();
        if (targets.Count == 0)
        {
            Error = L["error.no_targets"];
            return;
        }

        if (!await ConfirmAsync(L["confirm.restore"]))
        {
            return;
        }

        var report = await Task.Run(() => ClientConfigWriter.Uninstall(targets));

        foreach (var line in report.Lines)
        {
            Log.Write(line);
        }

        InstallOutput = report.Lines.ToList();
        RefreshRestoreState();

        if (!report.Ok)
        {
            Error = L["error.restore_failed", string.Join(", ", report.Failed)];
            return;
        }

        ShowNotice(report.Restored.Count > 0
            ? L["notice.restore_ok", CultureFormat.Number(report.Restored.Count)].Value
            : L["notice.restore_nothing"].Value);
    });

    private Task LaunchAsync(CodingAgent agent) => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(ProjectFolder))
        {
            Error = L["error.folder", L["value.none"].Value];
            return;
        }

        if (string.IsNullOrWhiteSpace(LaunchAlias))
        {
            Error = L[DirectMode ? "error.no_unsloth_model" : "error.no_model"];
            return;
        }

        var folder = Environment.ExpandEnvironmentVariables(ProjectFolder.Trim());
        if (!Directory.Exists(folder))
        {
            Error = L["error.folder", folder];
            return;
        }

        if (!AgentLauncher.Availability(agent).Installed)
        {
            Error = L[$"dialog.missing.{AgentLauncher.Slug(agent)}"];
            return;
        }

        ClientContext context;
        try
        {
            context = await Resolver.ContextAsync("local");
        }
        catch (EndpointUnavailableException ex)
        {
            Error = L["error.endpoint_unavailable", ex.Message];
            return;
        }

        // The model has to be one the configured server knows: a fleet alias, or in
        // direct mode a model id Unsloth's server lists.
        var model = context.Models.FirstOrDefault(m =>
                        string.Equals(m.Alias, LaunchAlias, StringComparison.OrdinalIgnoreCase))
                    ?? context.Models.FirstOrDefault(m =>
                        context.Direct &&
                        string.Equals(m.Repo, LaunchAlias, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            Error = L[DirectMode ? "error.no_unsloth_model" : "error.no_model"];
            return;
        }

        var alias = model.Alias;

        // Codex resolves `--profile <alias>` from its own config file, so make sure
        // that file exists before the terminal opens. Installing is idempotent.
        var target = agent switch
        {
            CodingAgent.Codex => ClientTarget.Codex,
            CodingAgent.Qwen => ClientTarget.Qwen,
            _ => ClientTarget.Claude,
        };

        var report = await Task.Run(() => ClientConfigWriter.Install(context, new[] { target },
            new InstallOptions(MakeDefault, PersistEnvironment: true,
                FullToolAccess: FullToolAccess)));
        foreach (var line in report.Lines)
        {
            Log.Write(line);
        }

        if (!report.Ok)
        {
            Error = L["error.install_failed", string.Join(", ", report.Failed)];
            return;
        }

        Settings.Update(s =>
        {
            s.ProjectFolder = folder;
            s.SelectedAlias = alias;
            s.LastAgent = AgentLauncher.Slug(agent);
        });

        var result = await Task.Run(() => AgentLauncher.Launch(
            agent, alias, folder,
            AgentLauncher.Environment(agent, context, alias),
            AgentLauncher.CommandLine(agent, alias, context), Log));

        if (result.Started)
        {
            Log.Write(L["log.launched", AgentLauncher.Slug(agent), alias, folder]);
        }
        else
        {
            Error = L["log.no_terminal", result.ScriptPath];
        }
    });

    private Task ApplyNetworkAsync() => RunAsync(async () =>
    {
        var draft = Fleet.Options.Clone();
        draft.Gateway.AllowLan = ShareOnLan;
        draft.Gateway.Host = ShareOnLan ? "0.0.0.0" : "127.0.0.1";
        draft.Gateway.Port = PortInput;
        draft.Gateway.IdleTimeout = IdleInput;

        if (ShareOnLan && Fleet.ApiKey.Length == 0)
        {
            Error = L["error.no_key_for_lan"];
            return;
        }

        var restart = draft.Gateway.Host != Fleet.Gateway.Host ||
                      draft.Gateway.Port != Fleet.Gateway.Port;
        ApplyDraft(draft);
        RefreshNetworkInputs();
        ShowNotice(restart ? L["notice.restart_required"] : L["notice.saved"]);
        await Task.CompletedTask;
    });

    private Task OpenFirewallAsync() => RunAsync(async () =>
    {
        var result = await FirewallHelper.OpenAsync(Fleet.Gateway.Port);
        Log.Write($"firewall rule for port {Fleet.Gateway.Port}: " +
                  (result.Ok ? "added" : $"not added — run: {result.Command}"));
        if (result.Ok)
        {
            ShowNotice(L["notice.firewall_added"]);
        }
        else
        {
            Error = L["error.firewall", result.Command];
        }
    });

    private Task OnModeChanged(ChangeEventArgs args) => RunAsync(async () =>
    {
        if (Enum.TryParse<FleetMode>(args.Value?.ToString(), out var mode))
        {
            await Fleet.SetModeAsync(mode);
            Log.Write(L["log.mode_changed", L[$"mode.{mode.ToString().ToLowerInvariant()}"].Value]);
        }
    });

    private void OnCultureChanged(ChangeEventArgs args)
    {
        var culture = args.Value?.ToString();
        if (string.IsNullOrWhiteSpace(culture))
        {
            return;
        }

        Settings.Update(s => s.Culture = culture);
        Navigation.NavigateTo(
            $"culture?culture={Uri.EscapeDataString(culture)}&redirect={Uri.EscapeDataString("/")}",
            forceLoad: true);
    }

    private void ClearLog() => LogFloor = Log.Cursor;

    private async Task Copy(string text, string messageKey)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            Log.Write(L[messageKey]);
        }
        catch (JSException)
        {
            Error = L["error.clipboard", text];
        }
        catch (InvalidOperationException)
        {
            Error = L["error.clipboard", text];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_refreshLoop is not null)
        {
            await _refreshLoop.CancelAsync().ConfigureAwait(false);
            _refreshLoop.Dispose();
        }

        _timer?.Dispose();
    }
}
