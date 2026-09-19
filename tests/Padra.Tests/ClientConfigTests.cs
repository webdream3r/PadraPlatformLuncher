using System.Text.Json;
using System.Text.Json.Nodes;
using Padra.Core.Clients;
using Padra.Core.Configuration;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Padra.Tests;

[Collection("environment")]
public sealed class ClientConfigTests
{
    [Fact]
    public void Client_context_can_select_a_different_default_without_removing_models()
    {
        var context = new ClientContext
        {
            BaseUrl = "http://127.0.0.1:8900/v1",
            ApiKey = "test",
            DefaultAlias = "first",
            Models =
            [
                new ClientModel("first", "repo/first", "Q4", 131072),
                new ClientModel("second", "repo/second", "Q4", 131072),
            ],
        };

        var selected = context.WithDefaultModel("second");

        Assert.Equal("second", selected.DefaultAlias);
        Assert.Equal(2, selected.Models.Count);
        Assert.Same(context.Models, selected.Models);
    }

    private static ClientContext Context(string baseUrl = "http://127.0.0.1:8900/v1")
    {
        var options = FleetFile.Parse(string.Empty);
        options.Gateway.ApiKey = "sk-padra-test-key";
        FleetFile.AddModel(options, "unsloth/Qwen3.5-4B-GGUF", "UD-Q4_K_XL", contextLength: 262144);
        FleetFile.AddModel(options, "unsloth/gemma-4-26B-A4B-it-GGUF", "UD-Q4_K_XL",
            contextLength: 131072);
        return ClientContext.FromFleet(options, baseUrl);
    }

    /// <summary>
    /// Installs the same way the panel does, minus the per-user environment: that
    /// step writes to the Windows user environment block, which no temporary HOME
    /// can redirect. <see cref="The_key_is_persisted_for_the_codex_extension"/>
    /// covers it where it can be redirected.
    /// </summary>
    private static readonly InstallOptions Options = new(MakeDefault: true, PersistEnvironment: false);

    private static InstallReport Install(ClientContext context, params ClientTarget[] targets) =>
        ClientConfigWriter.Install(context, targets, Options, State());

    private static ClientInstallState State() =>
        ClientInstallState.OpenAt(Path.Combine(ClientConfigWriter.UserHome(), ".padra", "client-state"));

    private static JsonNode Read(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!;

    // ================================================================= Codex #
    [Fact]
    public void The_codex_block_is_a_table_only()
    {
        var block = ClientConfigWriter.CodexProviderBlock(Context());

        Assert.StartsWith("[model_providers.unsloth_gw]", block, StringComparison.Ordinal);
        Assert.Contains("base_url             = \"http://127.0.0.1:8900/v1\"", block, StringComparison.Ordinal);
        Assert.Contains("env_key              = \"UNSLOTH_GATEWAY_KEY\"", block, StringComparison.Ordinal);
        Assert.Contains("wire_api             = \"responses\"", block, StringComparison.Ordinal);
        Assert.Contains("requires_openai_auth = false", block, StringComparison.Ordinal);
        Assert.DoesNotContain("oss_provider", block, StringComparison.Ordinal);

        // Nothing before the header: bare keys appended to an existing file would be
        // swallowed by whatever section came last.
        var firstMeaningfulLine = block.Split('\n').First(l => l.Trim().Length > 0);
        Assert.StartsWith("[", firstMeaningfulLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_managed_block_is_replaced_not_duplicated()
    {
        var first = ClientConfigWriter.ReplaceManagedBlock(string.Empty, "[model_providers.a]\nx = 1");
        var second = ClientConfigWriter.ReplaceManagedBlock(first, "[model_providers.a]\nx = 2");

        Assert.Equal(1, CountOccurrences(second, ClientConfigWriter.BlockBegin));
        Assert.Contains("x = 2", second, StringComparison.Ordinal);
        Assert.DoesNotContain("x = 1", second, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_codex_file_keeps_its_own_content()
    {
        var existing = "model = \"gpt-5.6\"\n\n[tui]\nnotifications = true\n";
        var merged = ClientConfigWriter.ReplaceManagedBlock(
            existing, ClientConfigWriter.CodexProviderBlock(Context()));

        Assert.Contains("model = \"gpt-5.6\"", merged, StringComparison.Ordinal);
        Assert.Contains("[tui]", merged, StringComparison.Ordinal);
        Assert.Contains("[model_providers.unsloth_gw]", merged, StringComparison.Ordinal);
        // The user's root key still precedes the first table.
        Assert.True(merged.IndexOf("model = \"gpt-5.6\"", StringComparison.Ordinal) <
                    merged.IndexOf("[tui]", StringComparison.Ordinal));
    }

    [Fact]
    public void Codex_profiles_are_separate_files_with_top_level_keys()
    {
        using var environment = new TestEnvironment();
        var report = Install(Context(), ClientTarget.Codex);
        Assert.True(report.Ok);

        var profile = File.ReadAllText(environment.Path_(".codex", "qwen3.5-4b.config.toml"));
        Assert.Contains("model_provider       = \"unsloth_gw\"", profile, StringComparison.Ordinal);
        Assert.Contains("model                = \"qwen3.5-4b\"", profile, StringComparison.Ordinal);
        Assert.Contains("model_context_window = 262144", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("[profiles", profile, StringComparison.Ordinal);

        Assert.Contains("[model_providers.unsloth_gw]",
            File.ReadAllText(environment.Path_(".codex", "config.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Full_tool_access_writes_native_unrestricted_settings()
    {
        using var environment = new TestEnvironment();
        var context = Context();
        var vscodeDirectory = environment.VsCodeUserDirectory();
        var options = new InstallOptions(MakeDefault: true, PersistEnvironment: false,
            FullToolAccess: true);
        var state = State();

        var report = ClientConfigWriter.Install(context,
            new[] { ClientTarget.Codex, ClientTarget.Qwen, ClientTarget.Claude, ClientTarget.VsCode },
            options, state);

        Assert.True(report.Ok);
        var codex = File.ReadAllText(environment.Path_(".codex", "config.toml"));
        Assert.Contains("approval_policy = \"never\"", codex, StringComparison.Ordinal);
        Assert.Contains("sandbox_mode    = \"danger-full-access\"", codex,
            StringComparison.Ordinal);
        Assert.Contains("approval_policy       = \"never\"",
            File.ReadAllText(environment.Path_(".codex", "qwen3.5-4b.config.toml")),
            StringComparison.Ordinal);

        var qwen = JsonNode.Parse(File.ReadAllText(environment.Path_(".qwen", "settings.json")))!;
        Assert.Equal("yolo", qwen["tools"]!["approvalMode"]!.GetValue<string>());

        var claude = JsonNode.Parse(
            File.ReadAllText(environment.Path_(".claude", "settings.json")))!;
        Assert.Equal("bypassPermissions",
            claude["permissions"]!["defaultMode"]!.GetValue<string>());

        var vscode = Path.Combine(vscodeDirectory, "settings.json");
        var vscodeSettings = JsonNode.Parse(File.ReadAllText(vscode))!;
        Assert.True(vscodeSettings["chat.tools.global.autoApprove"]!.GetValue<bool>());
    }

    [Fact]
    public void Full_tool_access_does_not_make_codex_default_when_that_option_is_off()
    {
        var config = ClientConfigWriter.ApplyCodexDefaults(
            "model = \"my-existing-default\"\n", Context(), makeDefault: false,
            fullToolAccess: true);

        Assert.Contains("approval_policy = \"never\"", config, StringComparison.Ordinal);
        Assert.Contains("sandbox_mode    = \"danger-full-access\"", config,
            StringComparison.Ordinal);
        Assert.DoesNotContain("oss_provider", config, StringComparison.Ordinal);
        Assert.DoesNotContain("model_provider", config, StringComparison.Ordinal);
        Assert.Contains("model = \"my-existing-default\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabling_full_tool_access_removes_only_launcher_permission_values()
    {
        using var environment = new TestEnvironment();
        var context = Context();
        var vscodeDirectory = environment.VsCodeUserDirectory();
        var state = State();
        ClientConfigWriter.Install(context, new[]
        {
            ClientTarget.Codex, ClientTarget.Qwen, ClientTarget.Claude, ClientTarget.VsCode,
        }, new InstallOptions(FullToolAccess: true), state);

        var report = ClientConfigWriter.Install(context, new[]
        {
            ClientTarget.Codex, ClientTarget.Qwen, ClientTarget.Claude, ClientTarget.VsCode,
        }, new InstallOptions(FullToolAccess: false), state);

        Assert.True(report.Ok);
        Assert.DoesNotContain("approval_policy = \"never\"",
            File.ReadAllText(environment.Path_(".codex", "config.toml")),
            StringComparison.Ordinal);
        var qwen = JsonNode.Parse(File.ReadAllText(environment.Path_(".qwen", "settings.json")))!;
        Assert.Null(qwen["tools"]);
        var claude = JsonNode.Parse(
            File.ReadAllText(environment.Path_(".claude", "settings.json")))!;
        Assert.Null(claude["permissions"]);
        var vscode = Path.Combine(vscodeDirectory, "settings.json");
        Assert.Null(JsonNode.Parse(File.ReadAllText(vscode))!["chat.tools.global.autoApprove"]);
    }

    [Fact]
    public void Reinstalling_codex_is_idempotent_and_prunes_stale_profiles()
    {
        using var environment = new TestEnvironment();
        var context = Context();
        Install(context, ClientTarget.Codex);

        var stale = environment.Path_(".codex", "gone.config.toml");
        File.WriteAllText(stale, ClientConfigWriter.CodexProfile(
            new ClientModel("gone", "unsloth/Gone-GGUF", "Q4_K_M", 4096)));

        var report = Install(context, ClientTarget.Codex);

        Assert.True(report.Ok);
        Assert.False(File.Exists(stale));
        Assert.Equal(1, CountOccurrences(
            File.ReadAllText(environment.Path_(".codex", "config.toml")),
            ClientConfigWriter.BlockBegin));
        Assert.Contains(report.Lines, l => l.StartsWith("already current", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_codex_config_is_backed_up()
    {
        using var environment = new TestEnvironment();
        var codex = environment.Path_(".codex");
        Directory.CreateDirectory(codex);
        File.WriteAllText(Path.Combine(codex, "config.toml"), "model = \"gpt-5.6\"\n");

        Install(Context(), ClientTarget.Codex);

        Assert.Equal("model = \"gpt-5.6\"\n",
            File.ReadAllText(Path.Combine(codex, "config.toml.bak")));
    }

    [Fact]
    public void The_root_defaults_make_the_gateway_what_codex_uses()
    {
        using var environment = new TestEnvironment();
        Install(Context(), ClientTarget.Codex);

        var config = File.ReadAllText(environment.Path_(".codex", "config.toml"));

        // Root keys are only root keys above the first table.
        var firstTable = config.IndexOf("[model_providers.", StringComparison.Ordinal);
        var provider = config.IndexOf("model_provider = \"unsloth_gw\"", StringComparison.Ordinal);
        var model = config.IndexOf("model          = \"qwen3.5-4b\"", StringComparison.Ordinal);

        Assert.True(provider >= 0 && provider < firstTable, "model_provider is not a root key");
        Assert.True(model >= 0 && model < firstTable, "model is not a root key");
        Assert.Equal(1, CountOccurrences(config, ClientConfigWriter.DefaultsBegin));
    }

    [Fact]
    public void A_conflicting_root_key_is_moved_aside_not_duplicated()
    {
        using var environment = new TestEnvironment();
        var codex = environment.Path_(".codex");
        Directory.CreateDirectory(codex);
        File.WriteAllText(Path.Combine(codex, "config.toml"),
            "model = \"gpt-5.6\"\nmodel_provider = \"openai\"\n\n[tui]\nmodel = \"kept\"\n");

        Install(Context(), ClientTarget.Codex);
        var config = File.ReadAllText(Path.Combine(codex, "config.toml"));

        // Two root `model` keys would make Codex refuse to parse the file.
        Assert.Contains(ClientConfigWriter.DisabledPrefix + "model = \"gpt-5.6\"", config,
            StringComparison.Ordinal);
        Assert.Contains(ClientConfigWriter.DisabledPrefix + "model_provider = \"openai\"", config,
            StringComparison.Ordinal);

        // A key of the same name inside a table is not a root key and is left alone.
        Assert.Contains("\nmodel = \"kept\"", config.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_without_the_default_leaves_codex_selection_alone()
    {
        using var environment = new TestEnvironment();
        var codex = environment.Path_(".codex");
        Directory.CreateDirectory(codex);
        File.WriteAllText(Path.Combine(codex, "config.toml"), "model = \"gpt-5.6\"\n");

        ClientConfigWriter.Install(Context(), new[] { ClientTarget.Codex },
            new InstallOptions(MakeDefault: false, PersistEnvironment: false), State());

        var config = File.ReadAllText(Path.Combine(codex, "config.toml"));
        Assert.DoesNotContain(ClientConfigWriter.DefaultsBegin, config, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-5.6\"", config, StringComparison.Ordinal);
        Assert.Contains("[model_providers.unsloth_gw]", config, StringComparison.Ordinal);
    }

    // ============================================================= Qwen Code #
    [Fact]
    public void Qwen_settings_merge_keeps_unrelated_keys()
    {
        using var environment = new TestEnvironment();
        var qwen = environment.Path_(".qwen");
        Directory.CreateDirectory(qwen);
        File.WriteAllText(Path.Combine(qwen, "settings.json"), """
            {
              "ui": { "theme": "dark" },
              "modelProviders": { "anthropic": [{ "id": "keep" }] },
              "security": { "auth": { "selectedType": "qwen-oauth" } }
            }
            """);

        Install(Context(), ClientTarget.Qwen);
        var data = Read(Path.Combine(qwen, "settings.json"));

        Assert.Equal("dark", data["ui"]!["theme"]!.GetValue<string>());
        Assert.Equal("keep", data["modelProviders"]!["anthropic"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(new[] { "qwen3.5-4b", "gemma-4-26b-a4b-it" },
            data["modelProviders"]!["openai"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal("openai", data["security"]!["auth"]!["selectedType"]!.GetValue<string>());
        Assert.Equal("qwen3.5-4b", data["model"]!["name"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8900/v1", data["env"]!["OPENAI_BASE_URL"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(qwen, "settings.json.bak")));
    }

    [Fact]
    public void Qwen_gets_a_dotenv_that_keeps_the_users_own_lines()
    {
        using var environment = new TestEnvironment();
        var qwen = environment.Path_(".qwen");
        Directory.CreateDirectory(qwen);
        File.WriteAllText(Path.Combine(qwen, ".env"), "MY_OWN=1\n");

        Install(Context(), ClientTarget.Qwen);
        var path = Path.Combine(qwen, ".env");
        var text = File.ReadAllText(path);

        Assert.Contains("MY_OWN=1", text, StringComparison.Ordinal);
        Assert.Contains("UNSLOTH_GATEWAY_KEY=sk-padra-test-key", text, StringComparison.Ordinal);
        Assert.Contains("OPENAI_API_KEY=sk-padra-test-key", text, StringComparison.Ordinal);
        Assert.Contains("OPENAI_BASE_URL=http://127.0.0.1:8900/v1", text, StringComparison.Ordinal);
        Assert.Contains("OPENAI_MODEL=qwen3.5-4b", text, StringComparison.Ordinal);

        // Idempotent: a second install refreshes the one block instead of stacking.
        Install(Context(), ClientTarget.Qwen);
        text = File.ReadAllText(path);
        Assert.Equal(1, text.Split(ClientConfigWriter.BlockBegin).Length - 1);
        Assert.Contains("MY_OWN=1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Qwen_providers_of_the_users_own_survive_install_and_restore()
    {
        using var environment = new TestEnvironment();
        var qwen = environment.Path_(".qwen");
        Directory.CreateDirectory(qwen);

        // A cloud provider the user configured by hand, in the same `openai` list
        // this launcher writes into. It must survive every install and the restore.
        File.WriteAllText(Path.Combine(qwen, "settings.json"), """
            {
              "env": { "CLOUD_KEY": "sk-cloud" },
              "modelProviders": {
                "openai": [
                  { "id": "qwen3.7-plus", "name": "[Cloud] qwen3.7-plus",
                    "baseUrl": "https://cloud.example/v1", "envKey": "CLOUD_KEY" },
                  { "id": "qwen3.5-4b", "name": "stale — Padra (Unsloth)",
                    "baseUrl": "http://old:1/v1", "envKey": "UNSLOTH_GATEWAY_KEY" }
                ]
              }
            }
            """);

        Install(Context(), ClientTarget.Qwen);
        Install(Context(), ClientTarget.Qwen);
        var providers = Read(Path.Combine(qwen, "settings.json"))["modelProviders"]!["openai"]!.AsArray();

        Assert.Equal(new[] { "qwen3.7-plus", "qwen3.5-4b", "gemma-4-26b-a4b-it" },
            providers.Select(p => p!["id"]!.GetValue<string>()));
        Assert.Equal("https://cloud.example/v1", providers[0]!["baseUrl"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8900/v1", providers[1]!["baseUrl"]!.GetValue<string>());
        Assert.Equal(262144,
            providers[1]!["generationConfig"]!["contextWindowSize"]!.GetValue<int>());
        Assert.Equal("sk-cloud",
            Read(Path.Combine(qwen, "settings.json"))["env"]!["CLOUD_KEY"]!.GetValue<string>());

        // A semantic strip (no snapshot, but a record that this launcher installed
        // Qwen) removes only the launcher's entries.
        var forgotten = ClientInstallState.OpenAt(Path.Combine(environment.Work, "forgotten"));
        forgotten.CaptureEnvironment("qwen", "UNSLOTH_GATEWAY_KEY", EnvScope.ProfileFile,
            Path.Combine(environment.Work, "no-such-profile"), null);
        File.Delete(Path.Combine(qwen, "settings.json.bak"));
        ClientConfigWriter.Uninstall(new[] { ClientTarget.Qwen }, forgotten);

        var stripped = Read(Path.Combine(qwen, "settings.json"));
        Assert.Equal(new[] { "qwen3.7-plus" },
            stripped["modelProviders"]!["openai"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()));
        Assert.Equal("sk-cloud", stripped["env"]!["CLOUD_KEY"]!.GetValue<string>());

        // With a snapshot the file comes back byte for byte, stale entry included.
        var state = ClientInstallState.OpenAt(Path.Combine(environment.Work, "recorded"));
        var before = File.ReadAllText(Path.Combine(qwen, "settings.json"));
        ClientConfigWriter.Install(Context(), new[] { ClientTarget.Qwen }, Options, state);
        Assert.NotEqual(before, File.ReadAllText(Path.Combine(qwen, "settings.json")));
        ClientConfigWriter.Uninstall(new[] { ClientTarget.Qwen }, state);
        Assert.Equal(before, File.ReadAllText(Path.Combine(qwen, "settings.json")));
    }

    [Fact]
    public void Qwen_settings_carry_only_documented_keys()
    {
        var settings = ClientConfigWriter.QwenSettings(Context());

        // `model.name` is what Qwen Code itself persists; `model.baseUrl` never
        // existed in its schema and an older build wrote it anyway.
        var model = settings["model"]!.AsObject();
        Assert.Equal("qwen3.5-4b", model["name"]!.GetValue<string>());
        Assert.False(model.ContainsKey("baseUrl"));

        Assert.Equal("qwen3.5-4b", settings["env"]!["OPENAI_MODEL"]!.GetValue<string>());

        var provider = settings["modelProviders"]!["openai"]![0]!.AsObject();
        Assert.Equal("UNSLOTH_GATEWAY_KEY", provider["envKey"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8900/v1", provider["baseUrl"]!.GetValue<string>());
    }

    [Fact]
    public void An_older_qwen_install_is_cleaned_of_the_undocumented_key()
    {
        using var environment = new TestEnvironment();
        var qwen = environment.Path_(".qwen");
        Directory.CreateDirectory(qwen);
        File.WriteAllText(Path.Combine(qwen, "settings.json"), """
            { "model": { "name": "old", "baseUrl": "http://old:1/v1" } }
            """);

        Install(Context(), ClientTarget.Qwen);
        var data = Read(Path.Combine(qwen, "settings.json"));

        Assert.Equal("qwen3.5-4b", data["model"]!["name"]!.GetValue<string>());
        Assert.Null(data["model"]!["baseUrl"]);
    }

    [Fact]
    public void Qwen_settings_survive_a_corrupt_file()
    {
        using var environment = new TestEnvironment();
        var qwen = environment.Path_(".qwen");
        Directory.CreateDirectory(qwen);
        File.WriteAllText(Path.Combine(qwen, "settings.json"), "{ this is not json");

        var report = Install(Context(), ClientTarget.Qwen);

        Assert.True(report.Ok);
        Assert.Equal("openai",
            Read(Path.Combine(qwen, "settings.json"))["security"]!["auth"]!["selectedType"]!
                .GetValue<string>());
    }

    // =========================================================== Claude Code #
    [Fact]
    public void Claude_settings_use_the_anthropic_surface()
    {
        using var environment = new TestEnvironment();
        Install(Context(), ClientTarget.Claude);

        var data = Read(environment.Path_(".claude", "settings.json"));
        var env = data["env"]!;

        Assert.Equal("http://127.0.0.1:8900", env["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal("sk-padra-test-key", env["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal("qwen3.5-4b", env["ANTHROPIC_MODEL"]!.GetValue<string>());

        // Every alias family has to land on the local model, or that traffic —
        // subagents on haiku, a session on fable — goes to Anthropic instead.
        foreach (var family in new[] { "HAIKU", "SONNET", "OPUS", "FABLE" })
        {
            Assert.Equal("qwen3.5-4b",
                env[$"ANTHROPIC_DEFAULT_{family}_MODEL"]!.GetValue<string>());
        }

        Assert.Equal("qwen3.5-4b", data["model"]!.GetValue<string>());
        Assert.Null(env["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void Claude_settings_drop_a_conflicting_subscription_key()
    {
        using var environment = new TestEnvironment();
        var claude = environment.Path_(".claude");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, "settings.json"), """
            { "env": { "ANTHROPIC_API_KEY": "sk-ant-real", "MY_VAR": "keep" },
              "permissions": { "allow": ["Bash"] } }
            """);

        Install(Context(), ClientTarget.Claude);
        var data = Read(Path.Combine(claude, "settings.json"));

        Assert.Null(data["env"]!["ANTHROPIC_API_KEY"]);
        Assert.Equal("keep", data["env"]!["MY_VAR"]!.GetValue<string>());
        Assert.Equal("Bash", data["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    // ================================================ Continue and VS Code #
    [Fact]
    public void The_continue_config_is_valid_yaml()
    {
        using var environment = new TestEnvironment();
        Install(Context(), ClientTarget.Continue);

        var yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Deserialize<Dictionary<string, object>>(
                File.ReadAllText(environment.Path_(".continue", "config.yaml")));

        Assert.Equal("padra-unsloth", yaml["name"]);
        Assert.Equal("v1", yaml["schema"]);
        var models = Assert.IsType<List<object>>(yaml["models"]);
        var first = Assert.IsType<Dictionary<object, object>>(models[0]);
        Assert.Equal("openai", first["provider"]);
        Assert.Equal("http://127.0.0.1:8900/v1", first["apiBase"]);
    }

    [Fact]
    public void Vscode_settings_and_copilot_endpoints_are_written()
    {
        using var environment = new TestEnvironment();
        var userDirectory = environment.VsCodeUserDirectory();
        File.WriteAllText(Path.Combine(userDirectory, "settings.json"),
            "{\n  // a comment survives parsing\n  \"editor.fontSize\": 13,\n}\n");

        Install(Context(), ClientTarget.VsCode);

        var settings = Read(Path.Combine(userDirectory, "settings.json"));
        Assert.Equal(13, settings["editor.fontSize"]!.GetValue<int>());
        Assert.True(settings["claudeCode.disableLoginPrompt"]!.GetValue<bool>());
        Assert.Contains("ANTHROPIC_BASE_URL",
            settings["claudeCode.environmentVariables"]!.AsArray()
                .Select(n => n!["name"]!.GetValue<string>()));

        // Qwen Code exposes no endpoint setting; this is what moves it off the Qwen
        // sign-in and onto the providers written into ~/.qwen/settings.json.
        Assert.Equal("api-key", settings["qwen-code.provider"]!.GetValue<string>());

        var terminal = settings[ClientConfigWriter.TerminalEnvKey]!;
        Assert.Equal("sk-padra-test-key", terminal[ClientConfigWriter.EnvKeyName]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8900/v1", terminal["OPENAI_BASE_URL"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8900", terminal["ANTHROPIC_BASE_URL"]!.GetValue<string>());

        var providers = Read(Path.Combine(userDirectory, "chatLanguageModels.json")).AsArray();
        var provider = Assert.Single(providers)!;
        Assert.Equal(ClientConfigWriter.CopilotProviderName, provider["name"]!.GetValue<string>());
        Assert.Equal("customendpoint", provider["vendor"]!.GetValue<string>());
        Assert.Equal("chat-completions", provider["apiType"]!.GetValue<string>());
        Assert.Equal("sk-padra-test-key", provider["apiKey"]!.GetValue<string>());

        var models = provider["models"]!.AsArray();
        Assert.Equal(new[] { "qwen3.5-4b", "gemma-4-26b-a4b-it" },
            models.Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal("http://127.0.0.1:8900/v1/chat/completions",
            models[0]!["url"]!.GetValue<string>());
        Assert.Equal(262144, models[0]!["maxInputTokens"]!.GetValue<int>());
    }

    [Fact]
    public void Reinstalling_vscode_does_not_duplicate_endpoints()
    {
        using var environment = new TestEnvironment();
        var userDirectory = environment.VsCodeUserDirectory();
        File.WriteAllText(Path.Combine(userDirectory, "chatLanguageModels.json"),
            """[{ "name": "Someone else", "vendor": "azure" }]""");
        File.WriteAllText(Path.Combine(userDirectory, "settings.json"),
            $$"""{ "{{ClientConfigWriter.TerminalEnvKey}}": { "MY_VAR": "keep" } }""");

        Install(Context(), ClientTarget.VsCode);
        Install(Context(), ClientTarget.VsCode);

        var providers = Read(Path.Combine(userDirectory, "chatLanguageModels.json")).AsArray();
        Assert.Equal(new[] { "Someone else", ClientConfigWriter.CopilotProviderName },
            providers.Select(n => n!["name"]!.GetValue<string>()));

        // Somebody else's terminal variables are merged with, not replaced by, ours.
        var terminal = Read(Path.Combine(userDirectory, "settings.json"))[
            ClientConfigWriter.TerminalEnvKey]!;
        Assert.Equal("keep", terminal["MY_VAR"]!.GetValue<string>());
        Assert.Equal("sk-padra-test-key", terminal[ClientConfigWriter.EnvKeyName]!.GetValue<string>());
    }

    [Fact]
    public void A_missing_vscode_folder_is_reported_not_fatal()
    {
        using var environment = new TestEnvironment();
        var report = Install(Context(), ClientTarget.VsCode);

        Assert.True(report.Ok);
        Assert.Contains(report.Lines, l => l.Contains("VS Code", StringComparison.Ordinal));
    }

    // ============================================================== bundle #
    [Fact]
    public void The_generated_bundle_covers_every_target()
    {
        using var environment = new TestEnvironment();
        var context = Context("http://192.168.1.20:8900/v1");
        var written = ClientConfigWriter.WriteGenerated(context, environment.Work);

        var names = written
            .Select(p => Path.GetRelativePath(environment.Work, p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Superset(new HashSet<string>(StringComparer.Ordinal)
        {
            "codex/config.toml", "codex/qwen3.5-4b.config.toml", "qwen/settings.json",
            "qwen/.env", "claude/settings.json", "continue/config.yaml", "vscode/settings.json",
            "vscode/chatLanguageModels.json", "vscode/tasks.json",
            "env.sh", "env.ps1", "README.txt",
        }, names);

        var tasks = Read(Path.Combine(environment.Work, "vscode", "tasks.json"));
        Assert.Equal(new[] { "qwen3.5-4b", "gemma-4-26b-a4b-it" },
            tasks["inputs"]![0]!["options"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("Authorization: Bearer", tasks["tasks"]![1]!["command"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Contains("192.168.1.20",
            File.ReadAllText(Path.Combine(environment.Work, "env.sh")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lenient_json_reader_handles_comments_and_trailing_commas()
    {
        var node = ClientConfigWriter.ParseLenient("""
            {
              // line comment
              "a": 1, /* block */
              "b": "http://x/y // not a comment",
              "c": [1, 2,],
            }
            """);

        Assert.Equal(1, node!["a"]!.GetValue<int>());
        Assert.Equal("http://x/y // not a comment", node["b"]!.GetValue<string>());
        Assert.Equal(2, node["c"]!.AsArray().Count);
        Assert.Null(ClientConfigWriter.ParseLenient("   "));
    }

    [Fact]
    public void Target_names_round_trip()
    {
        Assert.True(ClientConfigWriter.TryParseTarget("vscode", out var vscode));
        Assert.Equal(ClientTarget.VsCode, vscode);
        Assert.True(ClientConfigWriter.TryParseTarget("Claude", out var claude));
        Assert.Equal(ClientTarget.Claude, claude);
        Assert.False(ClientConfigWriter.TryParseTarget("emacs", out _));
        Assert.Equal(new[] { "codex", "qwen", "claude", "continue", "vscode" },
            ClientConfigWriter.AllTargets.Select(ClientConfigWriter.Slug));
    }

    // ============================================================== launch #
    [Fact]
    public void Agent_command_lines_match_the_installed_configuration()
    {
        Assert.Equal(new[] { "codex", "--profile", "demo" },
            AgentLauncher.CommandLine(CodingAgent.Codex, "demo"));
        Assert.Equal(new[] { "qwen", "--model", "demo" },
            AgentLauncher.CommandLine(CodingAgent.Qwen, "demo"));
        Assert.Equal(new[] { "claude", "--model", "demo" },
            AgentLauncher.CommandLine(CodingAgent.Claude, "demo"));
    }

    [Fact]
    public void Agent_environments_target_the_right_api()
    {
        var context = Context();

        var claude = AgentLauncher.Environment(CodingAgent.Claude, context, "demo");
        Assert.Equal("http://127.0.0.1:8900", claude["ANTHROPIC_BASE_URL"]);
        Assert.Equal("demo", claude["ANTHROPIC_MODEL"]);
        Assert.Equal("sk-padra-test-key", claude[ClientConfigWriter.EnvKeyName]);

        var qwen = AgentLauncher.Environment(CodingAgent.Qwen, context, "demo");
        Assert.Equal("http://127.0.0.1:8900/v1", qwen["OPENAI_BASE_URL"]);
        Assert.Equal("demo", qwen["OPENAI_MODEL"]);
    }

    [Fact]
    public void The_launch_script_quotes_folders_and_keys()
    {
        using var environment = new TestEnvironment();
        var folder = Path.Combine(environment.Work, "a folder with spaces");
        Directory.CreateDirectory(folder);

        var result = AgentLauncher.Launch(CodingAgent.Codex, "demo", folder,
            new Dictionary<string, string> { ["KEY"] = "va\"lue with spaces" },
            AgentLauncher.CommandLine(CodingAgent.Codex, "demo"),
            new Padra.Core.Runtime.ActivityLog());

        var script = File.ReadAllText(result.ScriptPath);
        Assert.Contains("a folder with spaces", script, StringComparison.Ordinal);
        Assert.Contains("codex", script, StringComparison.Ordinal);
        Assert.Contains("va\"lue with spaces", script, StringComparison.Ordinal);
        File.Delete(result.ScriptPath);
    }

    // =========================================================== restoration #
    [Fact]
    public void Restoring_puts_every_original_file_back_byte_for_byte()
    {
        using var environment = new TestEnvironment();
        var state = State();

        var codex = environment.Path_(".codex");
        var qwen = environment.Path_(".qwen");
        var claude = environment.Path_(".claude");
        var vscode = environment.VsCodeUserDirectory();
        foreach (var directory in new[] { codex, qwen, claude })
        {
            Directory.CreateDirectory(directory);
        }

        var originals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Path.Combine(codex, "config.toml")] = "model = \"gpt-5.6\"\n\n[tui]\nnotifications = true\n",
            [Path.Combine(qwen, "settings.json")] =
                "{\n  \"ui\": { \"theme\": \"dark\" },\n  \"modelProviders\": { \"openai\": [{ \"id\": \"mine\" }] }\n}\n",
            [Path.Combine(claude, "settings.json")] =
                "{\n  \"env\": { \"ANTHROPIC_API_KEY\": \"sk-ant-real\" }\n}\n",
            [Path.Combine(vscode, "settings.json")] = "{\n  \"editor.fontSize\": 13\n}\n",
        };

        foreach (var (path, content) in originals)
        {
            File.WriteAllText(path, content);
        }

        ClientConfigWriter.Install(Context(), null, Options, state);
        ClientConfigWriter.Install(Context(), null, Options, state);   // twice, on purpose

        foreach (var path in originals.Keys)
        {
            Assert.NotEqual(originals[path], File.ReadAllText(path));
        }

        var report = ClientConfigWriter.Uninstall(null, state);

        Assert.True(report.Ok);
        foreach (var (path, content) in originals)
        {
            Assert.Equal(content, File.ReadAllText(path));
        }
    }

    [Fact]
    public void Restoring_deletes_the_files_the_launcher_created()
    {
        using var environment = new TestEnvironment();
        var state = State();
        environment.VsCodeUserDirectory();

        ClientConfigWriter.Install(Context(), null, Options, state);

        var created = new[]
        {
            environment.Path_(".codex", "config.toml"),
            environment.Path_(".codex", "qwen3.5-4b.config.toml"),
            environment.Path_(".qwen", "settings.json"),
            environment.Path_(".claude", "settings.json"),
            environment.Path_(".continue", "config.yaml"),
        };
        Assert.All(created, path => Assert.True(File.Exists(path), path));

        ClientConfigWriter.Uninstall(null, state);

        Assert.All(created, path => Assert.False(File.Exists(path), path));
        Assert.False(File.Exists(environment.Path_(".codex", "config.toml.bak")));
    }

    [Fact]
    public void Restoring_without_a_record_cleans_only_what_it_can_identify()
    {
        using var environment = new TestEnvironment();
        var userDirectory = environment.VsCodeUserDirectory();

        // Installed by an earlier build: the files exist, the manifest does not.
        ClientConfigWriter.Install(Context(), null, Options, State());
        var forgotten = ClientInstallState.OpenAt(Path.Combine(environment.Work, "empty-state"));

        File.WriteAllText(Path.Combine(userDirectory, "settings.json"),
            $$"""
            {
              "editor.fontSize": 13,
              "claudeCode.disableLoginPrompt": true,
              "{{ClientConfigWriter.TerminalEnvKey}}": { "MY_VAR": "keep", "OPENAI_API_KEY": "x" }
            }
            """);

        ClientConfigWriter.Uninstall(null, forgotten);

        // Codex is identified by the markers and the provider id it carries, so it is
        // cleaned even with no manifest.
        var codex = File.ReadAllText(environment.Path_(".codex", "config.toml"));
        Assert.DoesNotContain(ClientConfigWriter.DefaultsBegin, codex, StringComparison.Ordinal);
        Assert.DoesNotContain("[model_providers.unsloth_gw]", codex, StringComparison.Ordinal);
        Assert.False(File.Exists(environment.Path_(".codex", "qwen3.5-4b.config.toml")));

        // VS Code's settings are shared with everything else the user runs and carry
        // nothing that proves they came from here, so with no record they are left
        // exactly as they are.
        var settings = Read(Path.Combine(userDirectory, "settings.json"));
        Assert.Equal(13, settings["editor.fontSize"]!.GetValue<int>());
        Assert.True(settings["claudeCode.disableLoginPrompt"]!.GetValue<bool>());
        Assert.Equal("x",
            settings[ClientConfigWriter.TerminalEnvKey]!["OPENAI_API_KEY"]!.GetValue<string>());
    }

    [Fact]
    public void Restoring_one_agent_does_not_touch_another_agents_own_settings()
    {
        using var environment = new TestEnvironment();
        var state = State();
        var userDirectory = environment.VsCodeUserDirectory();

        // The user points Claude Code and Qwen Code at their own endpoints, by hand.
        var claude = environment.Path_(".claude", "settings.json");
        var qwen = environment.Path_(".qwen", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(claude)!);
        Directory.CreateDirectory(Path.GetDirectoryName(qwen)!);

        const string claudeOwn =
            "{\n  \"env\": { \"ANTHROPIC_BASE_URL\": \"https://proxy.corp\", \"API_TIMEOUT_MS\": \"1000\" }\n}\n";
        const string qwenOwn =
            "{\n  \"env\": { \"OPENAI_API_KEY\": \"sk-mine\", \"OPENAI_BASE_URL\": \"https://mine\" }\n}\n";
        const string vscodeOwn =
            "{\n  \"editor.fontSize\": 13,\n  \"claudeCode.disableLoginPrompt\": true\n}\n";

        File.WriteAllText(claude, claudeOwn);
        File.WriteAllText(qwen, qwenOwn);
        File.WriteAllText(Path.Combine(userDirectory, "settings.json"), vscodeOwn);

        // Only Codex is installed — which is what "Open Codex" and a single-target
        // install do.
        ClientConfigWriter.Install(Context(), new[] { ClientTarget.Codex }, Options, state);
        ClientConfigWriter.Uninstall(null, state);

        Assert.Equal(claudeOwn, File.ReadAllText(claude));
        Assert.Equal(qwenOwn, File.ReadAllText(qwen));
        Assert.Equal(vscodeOwn, File.ReadAllText(Path.Combine(userDirectory, "settings.json")));
    }

    [Fact]
    public void The_bundle_config_can_be_pasted_and_then_installed_over()
    {
        using var environment = new TestEnvironment();
        var codex = environment.Path_(".codex");
        Directory.CreateDirectory(codex);

        // What the README tells the user to do with the downloaded bundle.
        File.WriteAllText(Path.Combine(codex, "config.toml"),
            ClientConfigWriter.CodexRootConfig(Context()));

        Install(Context(), ClientTarget.Codex);
        Install(Context(), ClientTarget.Codex);

        var config = File.ReadAllText(Path.Combine(codex, "config.toml"));

        // A second `[model_providers.unsloth_gw]` table is not merely untidy: TOML
        // forbids redefining a table, so Codex refuses the whole file.
        Assert.Equal(1, CountOccurrences(config, "[model_providers.unsloth_gw]"));
        Assert.Equal(1, CountOccurrences(config, ClientConfigWriter.BlockBegin));
        Assert.Equal(1, CountOccurrences(config, ClientConfigWriter.DefaultsBegin));
    }

    [Fact]
    public void Restoring_re_enables_a_root_key_the_install_moved_aside()
    {
        using var environment = new TestEnvironment();
        var codex = environment.Path_(".codex");
        Directory.CreateDirectory(codex);
        File.WriteAllText(Path.Combine(codex, "config.toml"), "model = \"gpt-5.6\"\n");

        ClientConfigWriter.Install(Context(), new[] { ClientTarget.Codex }, Options,
            ClientInstallState.OpenAt(Path.Combine(environment.Work, "used")));
        ClientConfigWriter.Uninstall(new[] { ClientTarget.Codex },
            ClientInstallState.OpenAt(Path.Combine(environment.Work, "empty")));

        var config = File.ReadAllText(Path.Combine(codex, "config.toml"));
        Assert.Contains("model = \"gpt-5.6\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientConfigWriter.DisabledPrefix, config, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_is_persisted_for_the_codex_extension()
    {
        // The Codex VS Code extension reads the key from the editor's own process
        // environment, so the launcher has to write it somewhere a login shell
        // exports. On Windows that is the user environment block, which no
        // temporary HOME can redirect, so this covers the POSIX path only.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var state = State();

        ClientConfigWriter.Install(Context(), new[] { ClientTarget.Codex },
            new InstallOptions(MakeDefault: true, PersistEnvironment: true), state);

        var profile = Path.Combine(environment.Home, ".profile");
        Assert.Contains($"export {ClientConfigWriter.EnvKeyName}='sk-padra-test-key'",
            File.ReadAllText(profile), StringComparison.Ordinal);

        ClientConfigWriter.Uninstall(new[] { ClientTarget.Codex }, state);

        Assert.False(File.Exists(profile) &&
                     File.ReadAllText(profile).Contains(ClientConfigWriter.EnvKeyName,
                         StringComparison.Ordinal));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
