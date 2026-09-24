# Padra Platform Launcher

Points Codex, Qwen Code and Claude Code — CLI and VS Code extension alike — at a
local Unsloth model, and puts them back when you are done. Send them straight to
Unsloth's own server, or through a gateway here that fronts every model on the
machine and swaps them on demand.

```
                     ┌── direct ──────────────────► unsloth run :8888
Codex ────┐          │                              (one model, no proxy)
Qwen ─────┼── the launcher writes their config
Claude ───┤          │                              ┌─► unsloth run :8801  (qwen3-coder)
VS Code ──┘          └── gateway :8900/v1 ──────────┼─► unsloth run :8802  (gemma-4)
                          (this or any LAN machine) └─► unsloth run :8803  (qwen3.5-4b)
```

Unsloth's server already answers the OpenAI Chat Completions, OpenAI Responses
and Anthropic Messages APIs on one port, so **direct** is the shortest path and
this launcher stays out of the request path entirely — it configures the agents
and nothing else. **Gateway** exists for what that server does not do: hold
several models and pick one per request.

Built on .NET 8: an ASP.NET Core gateway with a Blazor Server control panel, and
a WPF + WebView2 window that hosts it.

## Run it

**Windows** — build once, then double-click `dist\Padra Platform Launcher.exe`:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

The publish is self-contained: the target machine needs no .NET runtime. The
window starts the gateway, shows the panel, and on close asks the gateway to
unload every model so no VRAM is left occupied.

**Headless** (Windows, Linux, macOS):

```bash
./run-server.sh              # this computer only
./run-server.sh --lan        # serve the whole local network
```

or, from a publish folder, `Padra.Server --config models.yaml [--lan]`.

On the first run the launcher generates a strong API key into `models.yaml` and
imports the GGUF models it finds already downloaded on this machine.

## Prerequisites

```powershell
winget install --id Microsoft.DotNet.SDK.8            # to build
irm https://unsloth.ai/install.ps1 | iex              # Unsloth CLI
winget install --id OpenAI.Codex                      # Codex CLI      (optional)
npm install -g @qwen-code/qwen-code                   # Qwen Code CLI  (optional)
npm install -g @anthropic-ai/claude-code              # Claude Code    (optional)
```

macOS and Linux install Unsloth with `curl -fsSL https://unsloth.ai/install.sh | sh`.
The window also needs the WebView2 runtime, which ships with Windows 11 and
current Windows 10; without it the app offers to open the panel in your browser.

## Control panel

| Panel | What it does |
| --- | --- |
| **Models in the gateway** | Radio button picks the active model — it becomes the default and is loaded while the others are freed. Per-row Load / Unload / Remove, live state (idle, loaded, attached, failed). |
| **Model library → Downloaded** | Every GGUF already on this disk, with its real size, quantisation, context length read from the GGUF header, and where it came from. One click adds it to the gateway. "Add a model by repository" takes any repo/quant/alias typed by hand. |
| **Model library → Unsloth catalog** | Live search of the Unsloth organisation on Hugging Face: repositories, every quantisation variant, download size and context length, with the ones you already have marked. |
| **Hardware and offload** | This machine's memory and graphics cards, what each model is estimated to need, and whether it fits. Per-model GPU layers, shrink-to-fit, expert offload, cache precision, flash attention and threads, plus one button that tunes a model for this machine. |
| **Where the agents send their requests** | Direct to Unsloth's own server — on this computer or anywhere on the network — or through this gateway. Shows whether that server is answering, which key was found and where, every model it has with what is resident, and lets you pick the one the agents use and load or unload it on that server. Signs in to Unsloth Studio to create a key when none is known. |
| **Coding agents** | Choose which agents to configure, select the default model independently of the model currently loaded, write the local or LAN address, enable the explicit full-tool-access mode for supported clients, install the configuration, download a zip for another machine, or open a project folder in Codex / Qwen Code / Claude Code with the endpoint, key and model already set. All available chat-capable models remain selectable. "Restore original configuration" puts every agent back the way it was. |
| **Network** | Share on the LAN, change the port, set an idle timeout that frees VRAM automatically, see every URL other machines can use, and add the Windows firewall rule. |
| **Activity log** | Live gateway output. |

Language switch: English and فارسی, full RTL, Persian digits and Jalali dates.
Strings live in `src/Padra.Web/UiStrings.resx` and `UiStrings.fa-IR.resx` and are
resolved with `IStringLocalizer`; the culture comes from a cookie through
`UseRequestLocalization`.

## Serving the whole network

Turn on **Share with the local network** (or run with `--lan`, or set
`gateway.host: 0.0.0.0`). The panel then lists every URL other machines can use
and the exact firewall command; on Windows the button adds the rule itself.

A shared gateway always requires the API key — remote callers with no key or the
wrong key get `401`. Loopback callers keep working without one. A browser cannot
send an `Authorization` header on a normal navigation, so to open the panel from
another machine append the key once: `http://<host>:8900/?key=<gateway key>`. It
is stored in a strict same-site cookie for that browser.

To set up another machine, click **Download config bundle** with the scope set to
*Local network address*. The zip contains ready-to-use files for every agent plus
`env.sh` / `env.ps1`, all pointing at this computer's LAN URL.

## Choosing where the agents send their requests

**Where the agents send their requests** in the panel, or `gateway.endpointSource`
in `models.yaml`:

| | `unsloth` — direct | `gateway` — through this launcher |
| --- | --- | --- |
| Endpoint written into the agents | `http://127.0.0.1:8888` | `http://127.0.0.1:8900` |
| Key | Unsloth's own `sk-unsloth-…`, discovered | this launcher's, generated |
| Codex provider | `unsloth_api`, `env_key = UNSLOTH_STUDIO_AUTH_TOKEN` | `unsloth_gw`, `env_key = UNSLOTH_GATEWAY_KEY` |
| Model ids | Unsloth's own, e.g. `unsloth/gemma-4-26B-A4B-it-GGUF:UD-Q4_K_XL` | the short aliases in `models.yaml` |
| Models available at once | one — Unsloth keeps one resident | every model in the fleet, swapped per request |
| In the request path | nothing | this gateway |

Direct mode discovers the server rather than assuming it. The origin comes from
`gateway.unslothServerUrl`, else `UNSLOTH_STUDIO_URL`, else
`http://127.0.0.1:8888` — and it may be another machine entirely:
`192.168.30.227:8888` typed into the panel is an Unsloth Studio on the network.
`GET /api/health` — note the prefix — says whether anything is listening, which
separates "not running" from "running, wrong key". The key comes from
`gateway.unslothApiKey`, else `UNSLOTH_API_KEY` or `UNSLOTH_STUDIO_AUTH_TOKEN`,
else the cache `unsloth start` writes at
`~/.unsloth/studio/auth/agent_api_key.json` — Unsloth stores its keys hashed, so
that cache is the only place a usable one exists on disk. The model list is read
from that server's own `GET /v1/models`; nothing is invented.

### A key for a server that has none cached

A server on another machine has no key cached here, and Studio cannot show an
existing key again. The panel signs in instead: enter the Studio username and
password under **Create a key by signing in** and the launcher calls
`POST /api/auth/login` then `POST /api/auth/api-keys`, saves the key it is handed
as `gateway.unslothApiKey`, and forgets the password. The key is listed in Studio
as *Padra Platform Launcher*; revoke it there to cut the agents off.

### Picking and loading the model

Unsloth keeps one model resident at a time, and `GET /v1/models` lists every
model it has on disk with a `loaded` flag. The **Where the agents send their
requests** table shows that list; the radio button picks the one the agents are
written for (`gateway.unslothModel`), and **Load** asks the server to bring it
into memory through its own `POST /v1/load` — which is how a model is switched
on a machine where no `unsloth` command can be run from here. Progress comes
from `GET /v1/load-progress`; **Unload** is `POST /v1/unload`. Until the chosen
model is resident the panel says so, because the agents would otherwise be
configured for a model that cannot answer.

On this machine the same thing can be done from a terminal:

```bash
unsloth run --model unsloth/gemma-4-26B-A4B-it-GGUF --disable-tools -p 8888
```

`--disable-tools` is not optional there. Unsloth runs its own server-side tools
by default and they swallow a coding agent's tool calls, so the agent answers but
never edits a file.

Two things direct mode inherits from that server: authentication is required even
on loopback, and it accepts `Authorization: Bearer` only — which is why Claude
Code is given `ANTHROPIC_AUTH_TOKEN` and never `ANTHROPIC_API_KEY`, since the
latter is sent as `x-api-key` and is refused.

## Client configuration

The panel's **Install client configuration** button writes:

| Agent | File | What is written |
| --- | --- | --- |
| Codex CLI + `openai.chatgpt` VS Code extension | `~/.codex/config.toml` | a marker-delimited `[model_providers.unsloth_gw]` table with `base_url`, `env_key = UNSLOTH_GATEWAY_KEY`, `wire_api`, `requires_openai_auth = false`, plus root `model_provider` / `model` keys above every table |
| Codex, everywhere it runs | the per-user environment | `UNSLOTH_GATEWAY_KEY` — the Windows user environment block, or a marker-delimited block in `~/.profile` |
| Codex profiles | `~/.codex/<alias>.config.toml` | one file per model — `codex --profile <alias>` |
| Qwen Code CLI | `~/.qwen/settings.json` + `~/.qwen/.env` | one `modelProviders.openai[]` entry per model (merged beside any provider you already have), `security.auth.selectedType`, `model.name`, `env`; the `.env` block carries `OPENAI_API_KEY` / `OPENAI_BASE_URL` / `OPENAI_MODEL`, which beats the settings `env` section and reaches the VS Code companion |
| Qwen Code in VS Code | VS Code `settings.json` | `qwen-code.provider = api-key`, which stops the Qwen sign-in and uses the providers above |
| Claude Code CLI + `anthropic.claude-code` | `~/.claude/settings.json` | `env.ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_MODEL` and the haiku/sonnet/opus/fable aliases — every family, so subagent and background traffic stays local too |
| Claude Code in VS Code | VS Code `settings.json` | `claudeCode.environmentVariables`, `claudeCode.disableLoginPrompt` |
| Every CLI in the VS Code terminal | VS Code `settings.json` | `terminal.integrated.env.<platform>` — merged with whatever is already there |
| Copilot Chat (bring your own key) | `chatLanguageModels.json` next to it | one `customendpoint` provider whose `models[]` carries every alias |
| Continue | `~/.continue/config.yaml` | one `provider: openai` model per alias, with tool-use capability and the correct gateway API mode |
| Roo Code | `~/.padra/roo-code-settings.json` + VS Code `settings.json` | OpenAI-compatible provider profiles, auto-imported through `roo-cline.autoImportSettingsPath` |
| Workspace tasks | `generated/vscode/tasks.json` | start the gateway, check status, run each agent |

Everything is merged, not overwritten: unrelated keys survive, existing files are
copied to `.bak` first, and running it twice changes nothing.

Notes that cost time if you get them wrong:

- Codex reads the key from the environment variable named by `env_key`; it does
  **not** honour `OPENAI_BASE_URL` or `OPENAI_API_KEY`.
- The `openai.chatgpt` VS Code extension exposes **no** provider, model, endpoint
  or profile setting — its whole configuration is `~/.codex/config.toml`. It also
  spawns Codex from the extension host, so `terminal.integrated.env.*` never
  reaches it: the key has to be a real per-user environment variable. Those two
  facts are why installing writes the root `model_provider` / `model` keys and
  sets the variable for your user account. Turn **Make this the default for every
  agent** off to skip the root keys and add the provider only.
- `codex --oss` selects whatever `oss_provider` names in `config.toml`, which the
  install sets — so `--oss` reaches the local model rather than Ollama or LM
  Studio. `codex --profile <name>` works either way.
- Codex profiles are separate files. `[profiles.x]` inside `config.toml` stopped
  working in Codex 0.134.0.
- A root `model` key already in your `config.toml` is commented out with a
  `#padra-disabled#` marker rather than removed — two root keys of the same name
  make Codex refuse to parse the file — and the marker is taken off again by
  **Restore original configuration**.
- Qwen Code's VS Code companion (`qwenlm.qwen-code-vscode-ide-companion`) has no
  endpoint setting either; `qwen-code.provider = api-key` is what moves it off the
  Qwen sign-in and onto `~/.qwen/settings.json`.
- Claude Code speaks the **Anthropic Messages API**, not Chat Completions, so
  `ANTHROPIC_BASE_URL` is the gateway origin without `/v1`. It also needs real
  streaming, which the gateway passes through unbuffered.

### Putting it all back

**Restore original configuration** returns Codex, Qwen Code, Claude Code,
Continue and VS Code to what they were before this launcher first touched them.
The first time a file is written its pristine bytes are copied into
`~/.padra/client-state/`, and that copy is never replaced, so ten reinstalls
still revert to the file you started with. Files the launcher created are
deleted, folders it created and left empty are removed, and the per-user
environment variable is put back to its previous value or unset.

If that record is missing — an install from an older build, or a file you have
since moved — the restore falls back to removing exactly the keys and blocks this
launcher writes and leaves the rest of the file alone.

## Endpoints

| Route | Purpose |
| --- | --- |
| `GET /` | control panel |
| `HEAD /` | liveness probe (Claude Code sends this on startup) |
| `GET /v1/models` | alias list with load state and context length |
| `POST /v1/responses` | OpenAI Responses — Codex |
| `POST /v1/chat/completions` | OpenAI Chat Completions — Qwen Code, Continue, Copilot |
| `POST /v1/messages` | Anthropic Messages — Claude Code |
| `POST /v1/messages/count_tokens` | Anthropic token counting |
| `ANY /v1/*` | passthrough to the resolved backend |
| `GET /healthz` | health, mode, loaded models |
| `GET /admin/status` | per-backend detail |
| `POST /admin/load/{alias}`, `/admin/unload/{alias}` | load and free models |
| `POST /admin/mode/{mode}` | exclusive / concurrent at runtime |
| `POST /admin/reload` | re-read `models.yaml` |
| `POST /admin/shutdown` | stop every backend and exit |
| `GET /api/endpoint` | which server the agents are configured against, whether Unsloth's is answering, what it lists, what is resident, and any load in flight |
| `POST /api/endpoint` | switch between `gateway` and `unsloth`, set that server's address, key and model |
| `POST /api/endpoint/select` | `{ "model": "org/repo:QUANT" }` — the model the agents are written for in direct mode |
| `POST /api/endpoint/load`, `/api/endpoint/unload` | `{ "model": …, "wait": true }` — make a model resident on Unsloth's server, or free it, through its own API |
| `POST /api/endpoint/key` | `{ "username": …, "password": … }` — sign in to Unsloth Studio once and create a key for the agents |
| `GET /api/hardware` | this machine's memory and graphics cards, plus a size estimate per model |
| `POST /api/models/{alias}/runtime` | set GPU layers, fit, cache precision, expert offload, threads |
| `POST /api/models/{alias}/runtime/auto` | tune one model for this machine |
| `POST /api/uninstall-configs` | put every agent's configuration back as it was |
| `GET /api/client-state` | whether a restore is available, per agent |
| `GET/POST /api/*` | everything else the control panel uses (models, library, network, clients, launch, diagnostics) |

Replies carry `X-Unsloth-Backend` and `X-Unsloth-Served-Model`. Aliases resolve
loosely: `qwen3-coder`, `unsloth/Qwen3.6-35B-A3B-GGUF`, `Qwen3.6-35B-A3B-GGUF`
and `openai:qwen3-coder` all reach the same backend. A model id the fleet does
not know — `claude-sonnet-4-6`, for example — is served by the default model and
logged; set `gateway.unknownModel: error` to get a 404 instead.

## Keys

`gateway.apiKey` in `models.yaml` (or `GATEWAY_API_KEY`) is what clients send.
It is generated on first run. Each backend's own `sk-unsloth-…` key is captured
from `unsloth run` output and swapped in per request, so the backend key never
leaves this machine.

## Living alongside your own `unsloth run`

The gateway never fights a server it did not start. If a fleet model's port is
already answering, the gateway attaches to it and detaches on unload — it does
not kill it. If the port is occupied but unusable (wrong key, no models), loading
that model is refused with the reason; a second process is never started on an
occupied port. Crash cleanup is identity-checked: a recorded pid is only killed
when the process behind it was created at the recorded moment, so a recycled pid
or an `unsloth run` you started yourself is left alone. A model with requests
still streaming through it is never unloaded by the idle timer, and an
exclusive-mode swap waits for in-flight requests to finish before it frees VRAM.

## Model fleet

`models.yaml` is written by the control panel and is still hand-editable:

```yaml
models:
  - alias: qwen3-coder
    repo: unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF
    quant: UD-Q4_K_XL
    port: 8801
    contextLength: 262144
    reasoning: off        # on | off
    disableTools: true    # the coding agent runs its own tools
    extraArgs: []         # passed straight to `unsloth run`
    env: {}               # e.g. { CUDA_VISIBLE_DEVICES: "0" }
```

An alias may be up to 96 characters of lowercase letters, digits, dot, dash and
underscore. A name typed into the panel is repaired rather than refused —
`Qwen3.6 27B (Heretic)` becomes `qwen3.6-27b-heretic` — while `models.yaml`
edited by hand is validated strictly and told exactly which rule it broke.

## Models bigger than the machine

A model that does not fit in graphics memory still runs. The gateway sizes each
model against what this computer reports before it starts one, and when the
weights, the KV cache and the working buffers add up to more than the largest
card holds it asks the inference server to keep the remainder in system memory
instead of handing it an optimistic placement that crashes on load.

The **Hardware and offload** panel shows that arithmetic and lets you override
every part of it. **Tune for this machine** fills the block in for you; for a
Mixture-of-Experts checkpoint it moves as many expert layers into system RAM as
the shortfall needs.

```yaml
    runtime:
      gpuMemoryMode: manual   # auto | manual — auto lets Unsloth place the model
      gpuLayers: 24           # -ngl; -1 = all
      fit: on                 # auto | on | off — shrink and offload until it fits
      fitContext: 8192        # -fitc, the smallest context `fit` may fall back to
      cpuMoeLayers: 20        # -ncmoe; -1 keeps every expert in system RAM (-cmoe)
      kvCacheType: q8_0       # -ctk/-ctv: f32 f16 bf16 q8_0 q5_1 q5_0 iq4_nl q4_1 q4_0
      flashAttention: on      # auto | on | off
      threads: 12             # -t
      noKvOffload: false      # -nkvo
      swaFull: false          # --swa-full
      allowOversize: true     # start anyway, offloading, instead of refusing
```

Every key maps to a flag `unsloth run` forwards to its inference server, and the
runtime block is emitted before `extraArgs`, so a hand-written argument still
wins. Flags Unsloth manages itself — `--host`, `--port`, `--api-key`, `--model`,
`--parallel` and the rest — are refused when the file is read, with the name of
the flag, rather than becoming an opaque start failure minutes later.

The size shown is an estimate: the weights are the bytes on disk, the cache is
`blocks × kv heads × (key + value) × context × cache precision` read from the
GGUF header, and the working memory is a floor plus a share of the weights. When
a header does not describe the model's shape the panel says the cache figure is
estimated rather than quietly presenting a guess as a measurement.

Models are discovered in the Hugging Face hub cache — `$HF_HUB_CACHE`,
`$HF_HOME/hub`, `$XDG_CACHE_HOME/huggingface/hub`, then
`~/.cache/huggingface/hub` — and in the LM Studio folder. Add more with
`gateway.extraModelPaths`.

## Tests

```bash
dotnet test
```

212 tests cover fleet-file validation and rewriting, alias repair and length,
GGUF header parsing and cache scanning, catalog grouping, the offload flags and
the size estimate behind them, every generated client config (including that the
Codex block stays valid TOML when appended to an existing file, and that a
restore returns each file byte for byte), gateway routing and authentication
against a stub backend served over real HTTP, the whole control API, LAN URL
building, and translation-key parity in both languages, plus a guard that no
component code detaches from the Blazor dispatcher. Direct mode is exercised
against a stub that enforces the same Bearer-only rule and answers the same
`/v1/models`, `/v1/status`, `/v1/load`, `/v1/unload`, `/v1/load-progress` and
sign-in routes as Unsloth Studio's own server.

## Layout

```
models.yaml                          fleet definition — written by the panel, editable by hand
publish.ps1                          builds dist\Padra Platform Launcher.exe + Padra.Server.exe
run-server.ps1 / run-server.sh       headless gateway from source
src/Padra.Core/Configuration/        models.yaml load / validate / mutate / save, user settings
src/Padra.Core/Discovery/            local GGUF scan, GGUF header reader, Hugging Face catalog
src/Padra.Core/Networking/           LAN addresses, URLs, firewall
src/Padra.Core/Runtime/              backend processes, the fleet, Unsloth server discovery, hardware probe, size estimate, activity log, orphan cleanup
src/Padra.Core/Clients/              Codex / Qwen / Claude / Continue / VS Code config writer, install snapshots, restore
src/Padra.Web/Endpoints/             the /v1, /admin and /api surfaces
src/Padra.Web/Components/            the Blazor control panel
src/Padra.Web/UiStrings*.resx        every user-facing string
src/Padra.Desktop/                   WPF + WebView2 window and gateway supervisor (Windows only)
tests/Padra.Tests/                   xunit suite
legacy/python/                       the previous Python implementation, kept for reference
docs/GLOBAL-RULES.md                 owner-mandated rules, binding on every change
```

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `the unsloth command was not found` | install the Unsloth CLI, or set `gateway.unslothBin` to its full path |
| `alias … is N characters` | shorten it to 96 or fewer, or add the model from the panel and let it shorten the name |
| A model is too big for the graphics card | nothing to do — it starts with offload. Use **Tune for this machine** to pick the cache precision and expert placement deliberately |
| Codex in VS Code still signs in to ChatGPT | reinstall with **Make this the default for every agent** on, then restart VS Code so it picks up the new user environment variable |
| Qwen Code in VS Code asks for the Qwen sign-in | reinstall; it sets `qwen-code.provider` to `api-key` |
| An agent has to go back to how it was | **Restore original configuration** in the Coding agents panel |
| Direct mode says "Not answering" | nothing is listening on that address — start it with `unsloth run --model <repo> --disable-tools`, or on another machine start Unsloth Studio and turn on LAN access in its settings |
| Direct mode says "Key refused" | the key found is not one that server accepts; sign in from the panel to create one, or paste one in |
| Direct mode says "No key found" | Unsloth stores keys hashed; sign in from the panel with the Studio username and password, run `unsloth start claude` once so it caches a usable one, or paste one in |
| The chosen model is "On disk", not "Loaded" | press **Load** in the endpoint table; the agents cannot use a model the server has not brought into memory |
| A load says "did not finish in time" | raise `gateway.startTimeout`; a large model on a slow disk takes longer than the default 15 minutes |
| The agent answers but never edits a file | the server was started without `--disable-tools`, so Unsloth's own tools are eating the agent's tool calls |
| Codex shows the ChatGPT sign-in picker | use `codex --profile <alias>`, not `--oss` |
| 404 unknown model | `GET /v1/models` for the exact list |
| 503 backend_start_error | the message carries the last 30 lines of `unsloth run` output |
| Another machine cannot connect | share on the LAN, add the firewall rule, and send it the config bundle |
| The panel opens from another machine but is refused | append `?key=<gateway key>` once |
| Claude Code keeps asking to log in | reinstall the configs; the VS Code extension needs `claudeCode.environmentVariables` |
| The agent still asks before using tools | enable **Allow unrestricted tool access** in the Coding agents panel and reinstall the selected configurations; this opts Codex into `approval_policy = "never"` plus `sandbox_mode = "danger-full-access"`, Qwen Code into `tools.approvalMode = "yolo"` (Full Access), Claude Code into `bypassPermissions`, and VS Code chat tools into global auto-approval |
| The window says WebView2 is missing | install the runtime, or use the button to open the panel in your browser |
| Port already in use | change it in the Network panel, or stop the other server |
| Nothing on screen and no window | read `padra-launcher.log` next to the executable |
