# Padra Platform Launcher

Configures OpenAI Codex, Qwen Code and Claude Code — CLI and VS Code extension —
to use a local Unsloth model, and puts every one of them back on request. The
agents are pointed either straight at Unsloth's own server or at a gateway here
that fronts a fleet of `unsloth run` backends behind one
OpenAI/Anthropic-compatible endpoint. .NET 8.

## Stack

- .NET 8, C# 12, nullable enabled, warnings as errors
- `Padra.Core` — domain library, no ASP.NET dependency
- `Padra.Web` — ASP.NET Core gateway + Blazor Server panel (`Padra.Server.exe`)
- `Padra.Desktop` — WPF + WebView2 window (Windows only), supervises the gateway
- `Padra.Tests` — xunit, real HTTP against a stub backend

## Commands

```bash
dotnet build                                  # whole solution
dotnet test                                   # 218 tests
./run-server.sh --lan                         # headless gateway on the LAN
pwsh ./publish.ps1                            # dist\Padra Platform Launcher.exe (Windows)
dotnet run --project src/Padra.Web -- --help  # gateway options
```

`Padra.Desktop` targets `net8.0-windows`; it compiles anywhere with
`EnableWindowsTargeting` but only runs on Windows.

## Module map

| Namespace | Owns |
| --- | --- |
| `Padra.Core.Configuration` | `models.yaml` load/validate/mutate/atomic save, user settings, the starter config |
| `Padra.Core.Discovery` | local GGUF scan (Hugging Face cache, LM Studio), GGUF header parsing, Hugging Face catalog |
| `Padra.Core.Networking` | LAN addresses, endpoint URLs, port checks, firewall rule |
| `Padra.Core.Runtime` | `ModelBackend`, `ModelFleet`, `UnslothServer`, `HardwareProbe`, `ModelFootprint`, `ActivityLog`, `OrphanRegistry` |
| `Padra.Core.Clients` | client configuration generation/install/restore, install snapshots, per-user environment, agent launching |
| `Padra.Web.Endpoints` | `/v1`, `/admin`, `/api` |
| `Padra.Web.Components` | the Blazor panel |

## The two endpoints

The point of this project is configuring the agents, not serving models. Unsloth's
own server already answers OpenAI Chat Completions, OpenAI Responses and Anthropic
Messages on one port, so `endpointSource: unsloth` writes configuration straight at
it and this launcher leaves the request path. `endpointSource: gateway` exists for
the one thing that server does not do — several models behind one endpoint, chosen
per request. `EndpointResolver` is the only place that decides which one a caller
gets; nothing else should build a `ClientContext` by hand.

Direct mode discovers, never assumes: origin from config → `UNSLOTH_STUDIO_URL` →
`127.0.0.1:8888` (a bare `host:port` is accepted and may be another machine);
liveness from `GET /api/health` (not `/health`); key from config →
`UNSLOTH_API_KEY`/`UNSLOTH_STUDIO_AUTH_TOKEN` → `~/.unsloth/studio/auth/agent_api_key.json`;
models from that server's `/v1/models`, the resident one from `/v1/status`. Auth is
`Authorization: Bearer` only and is required on loopback, so Claude Code gets
`ANTHROPIC_AUTH_TOKEN`, never `ANTHROPIC_API_KEY`. A model id is a repository path,
so one Codex profile file covers every model instead of one per alias, and the
launcher passes `--model` on the command line.

The server may be remote, so the model it serves is chosen and switched through its
own API, never by running `unsloth` here: `gateway.unslothModel` is the choice,
`POST /v1/load` / `/v1/unload` (padded, minutes-long answers — `LongClientName`
client, bounded by `startTimeout`, tracked as one `UnslothOperation` at a time in
`EndpointResolver`) make it resident, `GET /v1/load-progress` reports progress, and
`POST /api/auth/login` + `POST /api/auth/api-keys` mint a key from the Studio
username and password when none is cached. `EndpointResolver.PreferredModel` is the
only place that decides which model the agents get.

## Conventions

- Every user-facing string is a key in **both** `UiStrings.resx` and
  `UiStrings.fa-IR.resx`, resolved with `IStringLocalizer<UiStrings>`. Never
  inline text in a component; `LocalizationTests` fails otherwise.
- CSS uses logical properties only (`margin-inline`, `inset-inline`,
  `text-align: start`). No `left` / `right`. The panel must render correctly with
  `dir="rtl"` and `dir="ltr"`.
- Numbers, sizes and dates go through `CultureFormat`, which supplies Persian
  digits and the Jalali calendar for `fa-IR`.
- The gateway owns backend processes — and only its own. It never spawns onto an
  occupied port (attach or refuse, never fight), `OrphanRegistry` kills a recorded
  pid only when the process start time matches the record (`ProcessTree.StartedNear`),
  the idle reaper skips backends with `ActiveRequests > 0`, and an exclusive-mode
  swap drains in-flight requests before stopping a backend. Anything that starts a
  process must also stop it on shutdown; the desktop shell calls
  `POST /api/shutdown` before it terminates anything.
- `models.yaml` uses camelCase keys and is rewritten atomically with its header
  comment intact; it stays hand-editable.
- Client configuration is always merged, never overwritten: keep unrelated keys,
  back up to `.bak`, and stay idempotent. Codex text goes inside the
  `# >>> padra-platform-launcher >>>` markers, and only as a `[table]` — bare
  TOML keys appended to a file would be swallowed by the preceding section. Qwen
  Code's `modelProviders.openai[]` is a list the user also writes into, so only
  entries named `… — Padra (Unsloth)` are ours to replace or strip.
- Client configuration is reversible. Anything that writes into a per-user
  location captures the file's pristine bytes into `ClientInstallState` first —
  once, never overwritten — so **Restore original configuration** is a restore,
  not a best-effort undo. A semantic strip (remove only our keys and blocks) is
  the fallback when no record exists; it must never run on a path that was just
  restored from a snapshot.
- Verified client facts, not guesses: Codex uses `env_key` (not `OPENAI_*`) and
  separate profile files; `--oss` selects whatever `oss_provider` names, which the
  install sets; the `openai.chatgpt`
  and `qwenlm.qwen-code-vscode-ide-companion` extensions expose no endpoint
  setting, so they are steered through `~/.codex/config.toml` root keys plus a
  per-user environment variable and `qwen-code.provider` respectively; Qwen Code
  also reads `~/.qwen/.env` (dotenv beats the settings `env` section), so the
  installer maintains a marker-delimited block there; Claude Code's model aliases
  are pinned for every family — haiku, sonnet, opus **and fable** — or subagent
  and background traffic escapes to Anthropic;
  `terminal.integrated.env.*` reaches VS Code terminals but never the extension
  host; Claude Code speaks the Anthropic Messages API at `ANTHROPIC_BASE_URL`
  with real SSE streaming, so the proxy must not buffer.
- No client that talks to a model server may use a proxy. Those servers are on
  loopback or on the LAN, and .NET reads `HTTP_PROXY`/`NO_PROXY` in preference to
  Windows' bypass list, so a proxy meant for the internet swallows a probe of
  `192.168.x.x` and the panel calls a healthy server unreachable. Build them with
  `DirectHttp`; `HuggingFaceCatalog` is the one client that keeps the system proxy.
  Liveness (`UnslothServer.HealthTimeout`) is bounded separately from the rest of
  the probe (`ProbeTimeout`): `/v1/models` walks that server's whole library and
  takes seconds cold, and must not be able to fail the liveness answer.
- Unsloth's `/v1/models` is that server's entire library — image, speech and
  embedding models included. The panel shows all of it; `EndpointResolver` hands
  the generators only `UnslothModel.IsChat`, because a model an agent can select
  and cannot talk to is worse than one that is absent.
- `unsloth run` forwards unknown flags to its inference server (last wins) but
  rejects a managed set — model identity, bind address, API key, TLS, parallel
  slots. `UnslothArguments.Reserved` is that list; check against it at
  configuration time rather than letting a start fail minutes later.
- Nothing refuses a model for being too large. `HardwareProbe` and
  `ModelFootprintCalculator` produce an advisory estimate; the fleet turns a
  predicted shortfall into `--fit on` so the server offloads instead of crashing.
  Every figure shown is labelled as an estimate when it is one.
- Aliases: repair what a person types, validate strictly what a file contains,
  and never generate a name that fails `FleetFile.ValidateAlias`.
- A windowed build has no console. Everything diagnostic also goes to
  `padra-launcher.log` next to the executable.

<!-- BEGIN owner-global-rules -->
## Owner-mandated global rules (binding — every change, request, prompt, scheduled run)
Full text: docs/GLOBAL-RULES.md. In short: (1) ripple-review every dependent
caller/DI/migration/view/test and keep build+tests green; (2) complete code only, no
stubs/placeholders; (3) research existing patterns first, then the complete/optimal
solution; (4) best UI/UX on the design system; (5) honest reporting; (6) no
demo/mock/sample — all operational; (7) i18n + RTL/LTR, no hardcoded strings.
<!-- END owner-global-rules -->
