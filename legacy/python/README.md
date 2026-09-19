# Padra Platform Launcher

One local endpoint in front of every Unsloth model on your machine, plus a
localized control panel. Codex, Qwen Code, Claude Code and VS Code all point at
the same URL and pick a model per request — from this computer or from anywhere
on your network.

```
Codex ────┐                             ┌─► unsloth run :8801  (qwen3-coder)
Qwen ─────┤                             │
Claude ───┼──► gateway :8900/v1 ────────┼─► unsloth run :8802  (gemma-4)
VS Code ──┤    (this or any LAN machine)│
other PCs ┘                             └─► unsloth run :8803  (qwen3.5-4b)
```

## Run it

Windows — double-click **`Padra Platform Launcher.bat`**. The first launch
creates `.venv`, installs dependencies, starts the gateway and opens the control
panel in a native window. Closing the window asks the gateway to shut down, which
unloads every model and frees VRAM.

Headless: `.\start.ps1` (Windows) or `./start.sh` (macOS, Linux, WSL). Add
`--lan` to serve the whole network straight away.

On the first run the launcher generates a strong API key into `models.yaml` and
imports the GGUF models it finds already downloaded on this machine.

## Prerequisites

```powershell
winget install --id Python.Python.3.12          # tick "Add python.exe to PATH"
irm https://unsloth.ai/install.ps1 | iex        # Unsloth CLI
winget install --id OpenAI.Codex                # Codex CLI      (optional)
npm install -g @qwen-code/qwen-code             # Qwen Code CLI  (optional)
npm install -g @anthropic-ai/claude-code        # Claude Code    (optional)
```

macOS and Linux install Unsloth with `curl -fsSL https://unsloth.ai/install.sh | sh`.

## Control panel

| Panel | What it does |
| --- | --- |
| **Models in the gateway** | Radio button picks the active model — it becomes the default and is loaded while the others are freed. Per-row Load / Unload / Remove, live state (idle, loaded, attached, failed). |
| **Model library → Downloaded** | Every GGUF already on this disk, with its real size, quantisation, context length read from the GGUF header, and where it came from. One click adds it to the gateway. |
| **Model library → Unsloth catalog** | Live search of the Unsloth organisation on Hugging Face: repositories, every quantisation variant, download size and context length, with the ones you already have marked. |
| **Coding agents** | Choose which agents to configure and whether to write the local or LAN address, install the configuration, download a zip for another machine, or open a project folder in Codex / Qwen Code / Claude Code with the endpoint, key and model already set. |
| **Network** | Share on the LAN, change the port, set an idle timeout that frees VRAM automatically, see every URL other machines can use, and add the Windows firewall rule. |
| **Activity log** | Live gateway output. |

Language switch: English and فارسی, full RTL, Persian digits and Jalali dates.

## Serving the whole network

Turn on **Share with the local network** (or run with `--lan`, or set
`gateway.host: 0.0.0.0`). The panel then lists every URL other machines can use
and the exact firewall command; on Windows the button adds the rule itself.

A shared gateway always requires the API key — remote callers with no key or the
wrong key get `401`. Loopback callers keep working without one.

To set up another machine, click **Download config bundle** with the scope set to
*Local network address*. The zip contains ready-to-use files for every agent plus
`env.sh` / `env.ps1`, all pointing at this computer's LAN URL.

## Client configuration

`python -m launcher.gen_configs --install` (or the panel button) writes:

| Agent | File | What is written |
| --- | --- | --- |
| Codex CLI + `openai.chatgpt` VS Code extension | `~/.codex/config.toml` | a marker-delimited `[model_providers.unsloth_gw]` table with `base_url`, `env_key = UNSLOTH_GATEWAY_KEY`, `wire_api`, `requires_openai_auth = false` |
| Codex profiles | `~/.codex/<alias>.config.toml` | one file per model — `codex --profile <alias>` |
| Qwen Code | `~/.qwen/settings.json` | `modelProviders.openai[]`, `security.auth.selectedType`, `model.name`, `env` |
| Claude Code CLI + `anthropic.claude-code` | `~/.claude/settings.json` | `env.ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_MODEL` and the haiku/sonnet/opus aliases |
| Claude Code in VS Code | VS Code `settings.json` | `claudeCode.environmentVariables`, `claudeCode.disableLoginPrompt` |
| Copilot Chat (bring your own key) | `chatLanguageModels.json` next to it | one `customendpoint` entry per model |
| Continue | `~/.continue/config.yaml` | one `provider: openai` model per alias |
| Workspace tasks | `generated/vscode/tasks.json` | start the gateway, check status, run each agent |

Everything is merged, not overwritten: unrelated keys survive, existing files are
copied to `.bak` first, and running it twice changes nothing.

Notes that cost time if you get them wrong:

- Codex reads the key from the environment variable named by `env_key`; it does
  **not** honour `OPENAI_BASE_URL` or `OPENAI_API_KEY`.
- `codex --oss` only selects Ollama or LM Studio. Use `--profile <alias>`.
- Codex profiles are separate files. `[profiles.x]` inside `config.toml` stopped
  working in Codex 0.134.0.
- Claude Code speaks the **Anthropic Messages API**, not Chat Completions, so
  `ANTHROPIC_BASE_URL` is the gateway origin without `/v1`. It also needs real
  streaming, which the gateway passes through untouched.

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

Replies carry `X-Unsloth-Backend` and `X-Unsloth-Served-Model`. Aliases resolve
loosely: `qwen3-coder`, `unsloth/Qwen3.6-35B-A3B-GGUF`, `Qwen3.6-35B-A3B-GGUF`
and `openai:qwen3-coder` all reach the same backend. A model id the fleet does
not know — `claude-sonnet-4-6`, for example — is served by the default model and
logged; set `gateway.unknown_model: error` to get a 404 instead.

## Keys

`gateway.api_key` in `models.yaml` (or `GATEWAY_API_KEY`) is what clients send.
It is generated on first run. Each backend's own `sk-unsloth-…` key is captured
from `unsloth run` output and swapped in per request, so the backend key never
leaves this machine.

## Model fleet

`models.yaml` is written by the control panel and is still hand-editable:

```yaml
models:
  - alias: qwen3-coder
    repo: unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF
    quant: UD-Q4_K_XL
    port: 8801
    context_length: 262144
    reasoning: off        # on | off
    disable_tools: true   # the coding agent runs its own tools
    extra_args: []        # passed straight to `unsloth run`
    env: {}               # e.g. { CUDA_VISIBLE_DEVICES: "0" }
```

Models are discovered in the Hugging Face hub cache — `$HF_HUB_CACHE`,
`$HF_HOME/hub`, `$XDG_CACHE_HOME/huggingface/hub`, then
`~/.cache/huggingface/hub` — and in the LM Studio folder. Add more with
`gateway.extra_model_paths`.

## Tests

```bash
pip install -r requirements-dev.txt
python -m pytest tests -q
```

117 tests cover fleet-file validation and rewriting, GGUF header parsing and
cache scanning, catalog grouping, every generated client config (including that
the Codex block stays valid TOML when appended to an existing file), gateway
routing and authentication against a stub backend, the control-panel API, LAN
URL building and translation-key parity.

## Layout

```
models.yaml                 fleet definition — written by the panel, editable by hand
app.pyw                     desktop shell: bootstrap, supervise, open window
Padra Platform Launcher.bat Windows entry point
launcher/gateway.py         router, backend supervisor, orphan cleanup
launcher/fleetfile.py       models.yaml load / validate / mutate / save
launcher/discovery.py       local GGUF scan, GGUF headers, Hugging Face catalog
launcher/netinfo.py         LAN addresses, URLs, firewall
launcher/webui.py           control panel routes
launcher/gen_configs.py     Codex / Qwen / Claude / Continue / VS Code config writer
launcher/settings.py        persisted preferences
launcher/locales/*.json     every user-facing string
launcher/static/            control panel (HTML, CSS, JS)
tests/                      pytest suite
build-exe.ps1               optional single-file executable
docs/GLOBAL-RULES.md        owner-mandated rules, binding on every change
```

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `the unsloth command was not found` | install the Unsloth CLI, or set `gateway.unsloth_bin` to its full path |
| Codex shows the ChatGPT sign-in picker | use `codex --profile <alias>`, not `--oss` |
| 404 unknown model | `GET /v1/models` for the exact list |
| 503 backend_start_error | the message carries the last 30 lines of `unsloth run` output |
| Another machine cannot connect | share on the LAN, add the firewall rule, and send it the config bundle |
| Claude Code keeps asking to log in | reinstall the configs; the VS Code extension needs `claudeCode.environmentVariables` |
| Panel opens in a browser instead of a window | `pip install pywebview` inside `.venv` |
| Port already in use | change it in the Network panel, or stop the other server |
