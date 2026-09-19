# Padra Platform Launcher

Local model gateway and desktop control panel. Runs a fleet of `unsloth run`
backends behind one OpenAI/Anthropic-compatible endpoint so OpenAI Codex, Qwen
Code, Claude Code and VS Code can select a model per request, from this machine
or from anywhere on the LAN.

## Stack

- Python 3.10+, FastAPI + Uvicorn (`launcher/gateway.py`, `launcher/webui.py`)
- Browser control panel served from `launcher/static/` (no build step)
- Desktop shell `app.pyw` (pywebview window, browser fallback)
- Resource files `launcher/locales/*.json` — the only place user-facing text lives
- `pytest` suite in `tests/`

## Commands

```bash
python -m launcher --config models.yaml        # headless gateway
python -m launcher --config models.yaml --lan  # serve the local network
python app.pyw                                 # gateway + control panel window
python -m launcher.gen_configs --install       # write every client config
python -m launcher.gen_configs --install --only codex,claude --url http://192.168.1.20:8900/v1
python -m pytest tests -q                      # full suite
```

## Module map

| Module | Owns |
| --- | --- |
| `launcher/fleetfile.py` | reading, validating, mutating and atomically rewriting `models.yaml` |
| `launcher/discovery.py` | local GGUF scan (Hugging Face cache, LM Studio), GGUF header parsing, Hugging Face catalog |
| `launcher/netinfo.py` | LAN addresses, endpoint URLs, port checks, firewall rule |
| `launcher/gateway.py` | `Backend`, `Fleet`, proxy routes, auth, orphan registry |
| `launcher/webui.py` | every `/api/*` route the control panel calls |
| `launcher/gen_configs.py` | Codex, Qwen Code, Claude Code, Continue, VS Code config generation and install |

## Conventions

- Every user-facing string is a key in **all** locale files. Never inline text in
  HTML, JS or Python. Add new keys to `en-US.json` and `fa-IR.json` together;
  `tests/test_i18n.py` fails otherwise.
- CSS uses logical properties only (`margin-inline`, `inset-inline`,
  `text-align: start`). No `left` / `right`. The panel must render correctly with
  `dir="rtl"` and `dir="ltr"`.
- Numbers, sizes and dates go through `Intl` with the active culture, which
  supplies Persian digits and the Jalali calendar for `fa-IR`.
- The gateway owns backend processes. Anything that starts a process must also
  stop it on shutdown; `OrphanRegistry` cleans up after a crash, and the desktop
  shell calls `POST /api/shutdown` before it terminates anything.
- `launcher/gateway.py` must be imported as a module (never run as `__main__`)
  so the log buffer shared with the web UI stays a single instance — that is why
  `launcher/__main__.py` exists.
- Client configuration is always merged, never overwritten: keep unrelated keys,
  back up to `.bak`, and stay idempotent. Codex text goes inside the
  `# >>> padra-platform-launcher >>>` markers, and only as a `[table]` — bare
  TOML keys appended to a file would be swallowed by the preceding section.
- Verified client facts, not guesses: Codex uses `env_key` (not `OPENAI_*`) and
  separate profile files; `--oss` is Ollama/LM Studio only; Claude Code speaks
  the Anthropic Messages API at `ANTHROPIC_BASE_URL` with real SSE streaming.

<!-- BEGIN owner-global-rules -->
## Owner-mandated global rules (binding — every change, request, prompt, scheduled run)
Full text: docs/GLOBAL-RULES.md. In short: (1) ripple-review every dependent
caller/DI/migration/view/test and keep build+tests green; (2) complete code only, no
stubs/placeholders; (3) research existing patterns first, then the complete/optimal
solution; (4) best UI/UX on the design system; (5) honest reporting; (6) no
demo/mock/sample — all operational; (7) i18n + RTL/LTR, no hardcoded strings.
<!-- END owner-global-rules -->
