#!/usr/bin/env python3
"""
Write the client configuration for every supported coding agent.

    python -m launcher.gen_configs                       # write ./generated/**
    python -m launcher.gen_configs --install             # also install it
    python -m launcher.gen_configs --install --only codex,claude
    python -m launcher.gen_configs --url http://192.168.1.20:8900/v1

Produces, under `generated/`:

    codex/config.toml            provider block for ~/.codex/config.toml
    codex/<alias>.config.toml    one Codex profile per model
    qwen/settings.json           Qwen Code provider list and default model
    claude/settings.json         Claude Code env block (Anthropic Messages API)
    continue/config.yaml         Continue extension models
    vscode/settings.json         Claude Code extension keys for VS Code
    vscode/chatLanguageModels.json  Copilot Chat "bring your own key" endpoints
    vscode/tasks.json            workspace tasks for the gateway and the agents
    env.sh / env.ps1             shell exports for every CLI

`--install` merges the same content into the real locations, always keeping a
`.bak` copy of anything it replaces.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence

from launcher import fleetfile

CODEX_PROVIDER_ID = "unsloth_gw"
ENV_KEY_NAME = "UNSLOTH_GATEWAY_KEY"
BLOCK_BEGIN = "# >>> padra-platform-launcher >>>"
BLOCK_END = "# <<< padra-platform-launcher <<<"
DEFAULT_CONTEXT = 131072
TARGETS = ("codex", "qwen", "claude", "continue", "vscode")

IS_WINDOWS = os.name == "nt"


# --------------------------------------------------------------------------- #
# Context
# --------------------------------------------------------------------------- #
@dataclass
class ModelEntry:
    alias: str
    repo: str = ""
    quant: str = ""
    context_length: int = DEFAULT_CONTEXT

    @property
    def spec(self) -> str:
        return f"{self.repo}:{self.quant}" if self.quant else self.repo


@dataclass
class ClientContext:
    """Everything the generators need, independent of where it came from."""

    base_url: str
    api_key: str
    models: List[ModelEntry]
    default_alias: str
    wire_api: str = "responses"
    project_dir: Path = field(default_factory=Path.cwd)

    @property
    def anthropic_base(self) -> str:
        """Claude Code appends `/v1/...` itself, so it wants the bare origin."""
        return self.base_url[: -len("/v1")] if self.base_url.endswith("/v1") else self.base_url

    @property
    def max_context(self) -> int:
        return max((m.context_length for m in self.models), default=DEFAULT_CONTEXT)

    def model(self, alias: str) -> Optional[ModelEntry]:
        for entry in self.models:
            if entry.alias == alias:
                return entry
        return None


def context_from_config(cfg: Dict[str, Any], *, base_url: str = "",
                        project_dir: Optional[Path] = None) -> ClientContext:
    gateway = cfg.get("gateway") or {}
    models = [
        ModelEntry(
            alias=str(m["alias"]),
            repo=str(m.get("repo") or ""),
            quant=str(m.get("quant") or ""),
            context_length=int(m.get("context_length") or DEFAULT_CONTEXT),
        )
        for m in cfg.get("models") or []
    ]
    default_alias = str(gateway.get("default_model") or (models[0].alias if models else ""))
    return ClientContext(
        base_url=base_url or default_base_url(cfg),
        api_key=str(os.environ.get("GATEWAY_API_KEY") or gateway.get("api_key") or ""),
        models=models,
        default_alias=default_alias,
        wire_api=str(gateway.get("wire_api") or "responses"),
        project_dir=project_dir or Path.cwd(),
    )


def default_base_url(cfg: Dict[str, Any]) -> str:
    gateway = cfg.get("gateway") or {}
    host = str(gateway.get("host") or "127.0.0.1")
    if host in ("0.0.0.0", "::", "*"):
        host = "127.0.0.1"
    if ":" in host and not host.startswith("["):
        host = f"[{host}]"
    return f"http://{host}:{int(gateway.get('port') or 8900)}/v1"


# --------------------------------------------------------------------------- #
# Text helpers
# --------------------------------------------------------------------------- #
_COMMENT_RE = re.compile(
    r'"(?:\\.|[^"\\])*"|/\*.*?\*/|//[^\n\r]*', re.DOTALL
)
_TRAILING_COMMA_RE = re.compile(r",(\s*[}\]])")


def load_jsonc(text: str) -> Any:
    """Parse JSON that may carry comments and trailing commas (VS Code style)."""
    def strip(match: "re.Match[str]") -> str:
        token = match.group(0)
        return token if token.startswith('"') else " "

    cleaned = _TRAILING_COMMA_RE.sub(r"\1", _COMMENT_RE.sub(strip, text))
    if not cleaned.strip():
        return {}
    return json.loads(cleaned)


def toml_string(value: str) -> str:
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def replace_managed_block(existing: str, block: str) -> str:
    """Insert or refresh our marker-delimited block, leaving the rest intact."""
    body = f"{BLOCK_BEGIN}\n{block.strip()}\n{BLOCK_END}\n"
    if BLOCK_BEGIN in existing and BLOCK_END in existing:
        head, _, rest = existing.partition(BLOCK_BEGIN)
        _, _, tail = rest.partition(BLOCK_END)
        return head.rstrip("\n") + ("\n\n" if head.strip() else "") + body + tail.lstrip("\n")
    separator = "\n\n" if existing.strip() else ""
    return existing.rstrip("\n") + separator + body


def backup(path: Path) -> Optional[Path]:
    if not path.exists():
        return None
    target = path.with_suffix(path.suffix + ".bak")
    shutil.copy2(path, target)
    return target


def write_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def write_json(path: Path, data: Any) -> None:
    write_text(path, json.dumps(data, indent=2, ensure_ascii=False) + "\n")


# --------------------------------------------------------------------------- #
# Codex
# --------------------------------------------------------------------------- #
def codex_provider_block(ctx: ClientContext) -> str:
    """The `[model_providers.…]` table for `~/.codex/config.toml`.

    Only a table is emitted: in TOML every key after a table header belongs to
    that table, so appending bare keys to an existing file would silently move
    them into whatever section came last.
    """
    return (
        f"[model_providers.{CODEX_PROVIDER_ID}]\n"
        f"name                 = {toml_string('Padra Platform Launcher (Unsloth)')}\n"
        f"base_url             = {toml_string(ctx.base_url)}\n"
        f"env_key              = {toml_string(ENV_KEY_NAME)}\n"
        f"wire_api             = {toml_string(ctx.wire_api)}\n"
        f"requires_openai_auth = false\n"
        f"stream_idle_timeout_ms = 600000\n"
    )


def codex_root_config(ctx: ClientContext) -> str:
    header = (
        "# Add this to ~/.codex/config.toml\n"
        "#   Windows: %USERPROFILE%\\.codex\\config.toml\n"
        "# Select it per run with `codex --profile <alias>`, or make it the\n"
        "# default by adding these two keys ABOVE every [table] in that file:\n"
        f"#   model_provider = {toml_string(CODEX_PROVIDER_ID)}\n"
        f"#   model          = {toml_string(ctx.default_alias or 'your-alias')}\n"
        f"# Do not use `codex --oss`: that flag only selects Ollama or LM Studio.\n\n"
    )
    return header + codex_provider_block(ctx)


def codex_profile(ctx: ClientContext, model: ModelEntry) -> str:
    """A Codex profile file: `~/.codex/<alias>.config.toml`.

    Profiles are separate files whose keys are top-level; `[profiles.x]` tables
    inside config.toml stopped working in Codex 0.134.0.
    """
    return (
        f"# {model.alias} — run with:  codex --profile {model.alias}\n"
        f"model_provider       = {toml_string(CODEX_PROVIDER_ID)}\n"
        f"model                = {toml_string(model.alias)}\n"
        f"model_context_window = {model.context_length}\n"
    )


def install_codex(ctx: ClientContext, report: "Report") -> None:
    codex_home = Path(os.environ.get("CODEX_HOME") or (Path.home() / ".codex"))
    root = codex_home / "config.toml"
    existing = root.read_text(encoding="utf-8") if root.exists() else ""
    updated = replace_managed_block(existing, codex_provider_block(ctx))
    if updated != existing:
        report.backed_up(backup(root))
        write_text(root, updated)
        report.wrote(root)
    else:
        report.unchanged(root)

    keep = {f"{m.alias}.config.toml" for m in ctx.models}
    for model in ctx.models:
        target = codex_home / f"{model.alias}.config.toml"
        content = codex_profile(ctx, model)
        if not target.exists() or target.read_text(encoding="utf-8") != content:
            report.backed_up(backup(target))
            write_text(target, content)
            report.wrote(target)
        else:
            report.unchanged(target)

    # Remove profiles this launcher wrote for models that no longer exist.
    if codex_home.is_dir():
        for stale in codex_home.glob("*.config.toml"):
            if stale.name in keep:
                continue
            try:
                head = stale.read_text(encoding="utf-8")
            except OSError:
                continue
            if f'model_provider       = "{CODEX_PROVIDER_ID}"' in head:
                report.backed_up(backup(stale))
                stale.unlink()
                report.removed(stale)


# --------------------------------------------------------------------------- #
# Qwen Code
# --------------------------------------------------------------------------- #
def qwen_settings(ctx: ClientContext) -> Dict[str, Any]:
    return {
        "modelProviders": {
            "openai": [
                {
                    "id": m.alias,
                    "name": f"{m.alias} — Padra (Unsloth)",
                    "description": m.spec or m.alias,
                    "baseUrl": ctx.base_url,
                    "envKey": ENV_KEY_NAME,
                }
                for m in ctx.models
            ]
        },
        "env": {ENV_KEY_NAME: ctx.api_key, "OPENAI_API_KEY": ctx.api_key,
                "OPENAI_BASE_URL": ctx.base_url},
        "security": {"auth": {"selectedType": "openai"}},
        "model": {"name": ctx.default_alias},
    }


def install_qwen(ctx: ClientContext, report: "Report") -> None:
    target = Path(os.environ.get("QWEN_HOME") or (Path.home() / ".qwen")) / "settings.json"
    fresh = qwen_settings(ctx)
    merged: Dict[str, Any] = {}
    if target.exists():
        report.backed_up(backup(target))
        try:
            loaded = load_jsonc(target.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                merged = loaded
            else:
                report.note(f"{target} was not a JSON object — it was replaced")
        except ValueError:
            report.note(f"{target} was not valid JSON — it was replaced")

    providers = merged.setdefault("modelProviders", {})
    if not isinstance(providers, dict):
        providers = {}
        merged["modelProviders"] = providers
    providers["openai"] = fresh["modelProviders"]["openai"]

    env = merged.setdefault("env", {})
    if not isinstance(env, dict):
        env = {}
        merged["env"] = env
    env.update(fresh["env"])

    security = merged.setdefault("security", {})
    if not isinstance(security, dict):
        security = {}
        merged["security"] = security
    auth = security.setdefault("auth", {})
    if not isinstance(auth, dict):
        auth = {}
        security["auth"] = auth
    auth["selectedType"] = "openai"

    model = merged.setdefault("model", {})
    if not isinstance(model, dict):
        model = {}
        merged["model"] = model
    if ctx.default_alias:
        model["name"] = ctx.default_alias

    write_json(target, merged)
    report.wrote(target)


# --------------------------------------------------------------------------- #
# Claude Code
# --------------------------------------------------------------------------- #
def claude_env(ctx: ClientContext) -> Dict[str, str]:
    """Claude Code talks the Anthropic Messages API to `ANTHROPIC_BASE_URL`."""
    alias = ctx.default_alias or (ctx.models[0].alias if ctx.models else "")
    env = {
        "ANTHROPIC_BASE_URL": ctx.anthropic_base,
        "ANTHROPIC_AUTH_TOKEN": ctx.api_key,
        "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1",
        "API_TIMEOUT_MS": "600000",
    }
    if alias:
        env["ANTHROPIC_MODEL"] = alias
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = alias
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = alias
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = alias
    return env


def claude_settings(ctx: ClientContext) -> Dict[str, Any]:
    settings: Dict[str, Any] = {
        "$schema": "https://json.schemastore.org/claude-code-settings.json",
        "env": claude_env(ctx),
    }
    if ctx.default_alias:
        settings["model"] = ctx.default_alias
    return settings


def install_claude(ctx: ClientContext, report: "Report") -> None:
    target = Path.home() / ".claude" / "settings.json"
    merged: Dict[str, Any] = {}
    if target.exists():
        report.backed_up(backup(target))
        try:
            loaded = load_jsonc(target.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                merged = loaded
            else:
                report.note(f"{target} was not a JSON object — it was replaced")
        except ValueError:
            report.note(f"{target} was not valid JSON — it was replaced")

    env = merged.setdefault("env", {})
    if not isinstance(env, dict):
        env = {}
        merged["env"] = env
    env.update(claude_env(ctx))
    # A subscription key would win over the gateway token, so clear it.
    env.pop("ANTHROPIC_API_KEY", None)
    if ctx.default_alias:
        merged["model"] = ctx.default_alias

    write_json(target, merged)
    report.wrote(target)


# --------------------------------------------------------------------------- #
# Continue
# --------------------------------------------------------------------------- #
def continue_config(ctx: ClientContext) -> str:
    lines = [
        "# ~/.continue/config.yaml — Continue extension for VS Code and JetBrains",
        "name: padra-unsloth",
        "version: 0.0.1",
        "schema: v1",
        "models:",
    ]
    if not ctx.models:
        lines.append("  []")
    for model in ctx.models:
        lines += [
            f"  - name: {model.alias}",
            "    provider: openai",
            f"    model: {model.alias}",
            f"    apiBase: {ctx.base_url}",
            f"    apiKey: {ctx.api_key}",
            "    defaultCompletionOptions:",
            f"      contextLength: {model.context_length}",
            "    roles: [chat, edit, apply]",
        ]
    return "\n".join(lines) + "\n"


def install_continue(ctx: ClientContext, report: "Report") -> None:
    target = Path.home() / ".continue" / "config.yaml"
    content = continue_config(ctx)
    if target.exists() and target.read_text(encoding="utf-8") == content:
        report.unchanged(target)
        return
    report.backed_up(backup(target))
    write_text(target, content)
    report.wrote(target)


# --------------------------------------------------------------------------- #
# VS Code
# --------------------------------------------------------------------------- #
def vscode_user_dirs() -> List[Path]:
    """Every VS Code family user-settings folder present on this machine."""
    if IS_WINDOWS:
        base = Path(os.environ.get("APPDATA") or (Path.home() / "AppData" / "Roaming"))
        candidates = [base / "Code" / "User", base / "Code - Insiders" / "User",
                      base / "VSCodium" / "User"]
    elif sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support"
        candidates = [base / "Code" / "User", base / "Code - Insiders" / "User",
                      base / "VSCodium" / "User"]
    else:
        base = Path(os.environ.get("XDG_CONFIG_HOME") or (Path.home() / ".config"))
        candidates = [base / "Code" / "User", base / "Code - Insiders" / "User",
                      base / "VSCodium" / "User"]
    return [c for c in candidates if c.is_dir()]


def vscode_settings(ctx: ClientContext) -> Dict[str, Any]:
    """Keys the Claude Code extension reads before it spawns the CLI."""
    return {
        "claudeCode.environmentVariables": [
            {"name": name, "value": value} for name, value in claude_env(ctx).items()
        ],
        "claudeCode.disableLoginPrompt": True,
    }


def copilot_models(ctx: ClientContext) -> List[Dict[str, Any]]:
    """`chatLanguageModels.json` entries for Copilot Chat's custom endpoints."""
    return [
        {
            "vendor": "customendpoint",
            "name": f"{m.alias} (Padra)",
            "id": m.alias,
            "model": m.alias,
            "apiType": "Chat Completions",
            "url": f"{ctx.base_url}/chat/completions",
            "apiKey": ctx.api_key,
            "toolCalling": True,
            "maxInputTokens": m.context_length,
            "maxOutputTokens": 8192,
        }
        for m in ctx.models
    ]


def vscode_tasks(ctx: ClientContext) -> Dict[str, Any]:
    project = str(ctx.project_dir)
    default_alias = ctx.default_alias or "model"
    return {
        "version": "2.0.0",
        "tasks": [
            {
                "label": "Padra: start the gateway",
                "type": "shell",
                "command": "${command:python.interpreterPath} -m launcher --config models.yaml",
                "options": {"cwd": project},
                "isBackground": True,
                "problemMatcher": [],
                "presentation": {"panel": "dedicated", "reveal": "always"},
            },
            {
                "label": "Padra: gateway status",
                "type": "shell",
                "command": "curl -s -H \"Authorization: Bearer ${env:" + ENV_KEY_NAME
                           + "}\" " + ctx.base_url.rsplit("/v1", 1)[0] + "/admin/status",
                "problemMatcher": [],
            },
            {
                "label": "Padra: Codex against a local model",
                "type": "shell",
                "command": "codex --profile ${input:padraModel}",
                "problemMatcher": [],
            },
            {
                "label": "Padra: Qwen Code against a local model",
                "type": "shell",
                "command": "qwen --model ${input:padraModel}",
                "problemMatcher": [],
            },
            {
                "label": "Padra: Claude Code against a local model",
                "type": "shell",
                "command": "claude --model ${input:padraModel}",
                "problemMatcher": [],
            },
        ],
        "inputs": [
            {
                "id": "padraModel",
                "type": "pickString" if ctx.models else "promptString",
                "description": "Model alias served by the gateway",
                "default": default_alias,
                **({"options": [m.alias for m in ctx.models]} if ctx.models else {}),
            }
        ],
    }


def install_vscode(ctx: ClientContext, report: "Report") -> None:
    user_dirs = vscode_user_dirs()
    if not user_dirs:
        report.note("no VS Code user folder was found on this machine")
        return
    for user_dir in user_dirs:
        settings_path = user_dir / "settings.json"
        merged: Dict[str, Any] = {}
        if settings_path.exists():
            report.backed_up(backup(settings_path))
            try:
                loaded = load_jsonc(settings_path.read_text(encoding="utf-8"))
                if isinstance(loaded, dict):
                    merged = loaded
                else:
                    report.note(f"{settings_path} was not a JSON object — it was replaced")
            except ValueError:
                report.note(f"{settings_path} was not valid JSON — it was replaced")
        merged.update(vscode_settings(ctx))
        write_json(settings_path, merged)
        report.wrote(settings_path)

        models_path = user_dir / "chatLanguageModels.json"
        existing: List[Dict[str, Any]] = []
        if models_path.exists():
            report.backed_up(backup(models_path))
            try:
                loaded = load_jsonc(models_path.read_text(encoding="utf-8"))
                if isinstance(loaded, list):
                    existing = [e for e in loaded if isinstance(e, dict)]
            except ValueError:
                report.note(f"{models_path} was not valid JSON — it was replaced")
        ours = {m["id"] for m in copilot_models(ctx)}
        kept = [
            e for e in existing
            if not (e.get("vendor") == "customendpoint" and
                    (e.get("id") in ours or str(e.get("name", "")).endswith("(Padra)")))
        ]
        write_json(models_path, kept + copilot_models(ctx))
        report.wrote(models_path)


# --------------------------------------------------------------------------- #
# Shell environment
# --------------------------------------------------------------------------- #
def env_sh(ctx: ClientContext) -> str:
    env = claude_env(ctx)
    return f"""\
# source this file:  . ./env.sh
export {ENV_KEY_NAME}="{ctx.api_key}"

# Qwen Code and any OpenAI-compatible client
export OPENAI_API_KEY="{ctx.api_key}"
export OPENAI_BASE_URL="{ctx.base_url}"
export OPENAI_MODEL="{ctx.default_alias}"

# Claude Code (Anthropic Messages API)
export ANTHROPIC_BASE_URL="{env['ANTHROPIC_BASE_URL']}"
export ANTHROPIC_AUTH_TOKEN="{ctx.api_key}"
export ANTHROPIC_MODEL="{ctx.default_alias}"

# Codex reads {ENV_KEY_NAME} through env_key in ~/.codex/config.toml
"""


def env_ps1(ctx: ClientContext) -> str:
    env = claude_env(ctx)
    return f"""\
# dot-source this file:  . .\\env.ps1
$env:{ENV_KEY_NAME} = "{ctx.api_key}"
$env:OPENAI_API_KEY = "{ctx.api_key}"
$env:OPENAI_BASE_URL = "{ctx.base_url}"
$env:OPENAI_MODEL = "{ctx.default_alias}"
$env:ANTHROPIC_BASE_URL = "{env['ANTHROPIC_BASE_URL']}"
$env:ANTHROPIC_AUTH_TOKEN = "{ctx.api_key}"
$env:ANTHROPIC_MODEL = "{ctx.default_alias}"
"""


# --------------------------------------------------------------------------- #
# Generation
# --------------------------------------------------------------------------- #
def generated_files(ctx: ClientContext,
                    targets: Sequence[str] = TARGETS) -> Dict[str, str]:
    """Relative path -> file content for everything `targets` covers."""
    files: Dict[str, str] = {}
    chosen = set(targets)

    if "codex" in chosen:
        files["codex/config.toml"] = codex_root_config(ctx)
        for model in ctx.models:
            files[f"codex/{model.alias}.config.toml"] = codex_profile(ctx, model)
    if "qwen" in chosen:
        files["qwen/settings.json"] = json.dumps(qwen_settings(ctx), indent=2,
                                                 ensure_ascii=False) + "\n"
    if "claude" in chosen:
        files["claude/settings.json"] = json.dumps(claude_settings(ctx), indent=2,
                                                   ensure_ascii=False) + "\n"
    if "continue" in chosen:
        files["continue/config.yaml"] = continue_config(ctx)
    if "vscode" in chosen:
        files["vscode/settings.json"] = json.dumps(vscode_settings(ctx), indent=2,
                                                   ensure_ascii=False) + "\n"
        files["vscode/chatLanguageModels.json"] = json.dumps(
            copilot_models(ctx), indent=2, ensure_ascii=False) + "\n"
        files["vscode/tasks.json"] = json.dumps(vscode_tasks(ctx), indent=2,
                                                ensure_ascii=False) + "\n"
    files["env.sh"] = env_sh(ctx)
    files["env.ps1"] = env_ps1(ctx)
    files["README.txt"] = bundle_readme(ctx, sorted(chosen))
    return files


def bundle_readme(ctx: ClientContext, targets: Sequence[str]) -> str:
    aliases = ", ".join(m.alias for m in ctx.models) or "(none configured yet)"
    return f"""\
Padra Platform Launcher — client configuration
==============================================

Endpoint : {ctx.base_url}
Key      : {ctx.api_key}
Models   : {aliases}
Included : {', '.join(targets)}

Codex        copy codex/config.toml into ~/.codex/config.toml and each
             <alias>.config.toml into ~/.codex/, then run
             `codex --profile <alias>`. Export {ENV_KEY_NAME} first.
Qwen Code    copy qwen/settings.json to ~/.qwen/settings.json, then run
             `qwen --model <alias>`.
Claude Code  copy claude/settings.json to ~/.claude/settings.json, then run
             `claude`. It uses the Anthropic Messages API of this gateway.
Continue     copy continue/config.yaml to ~/.continue/config.yaml.
VS Code      merge vscode/settings.json into your user settings.json and
             vscode/chatLanguageModels.json into the file of the same name in
             the same folder; tasks.json belongs in .vscode/ of a project.
Shell        `. ./env.sh` (bash/zsh) or `. .\\env.ps1` (PowerShell).

Every machine on the network uses the same key. Keep it private.
"""


def write_generated(ctx: ClientContext, out: Path,
                    targets: Sequence[str] = TARGETS) -> List[Path]:
    written: List[Path] = []
    for relative, content in generated_files(ctx, targets).items():
        target = out / relative
        write_text(target, content)
        written.append(target)
    return written


# --------------------------------------------------------------------------- #
# Install
# --------------------------------------------------------------------------- #
class Report:
    """Collects exactly what the installer touched, for honest reporting."""

    def __init__(self) -> None:
        self.lines: List[str] = []
        self.written: List[str] = []
        self.failed: List[str] = []

    def wrote(self, path: Path) -> None:
        self.written.append(str(path))
        self.lines.append(f"wrote {path}")

    def unchanged(self, path: Path) -> None:
        self.lines.append(f"already current {path}")

    def removed(self, path: Path) -> None:
        self.lines.append(f"removed {path}")

    def backed_up(self, path: Optional[Path]) -> None:
        if path is not None:
            self.lines.append(f"backed up {path}")

    def note(self, message: str) -> None:
        self.lines.append(message)

    def failure(self, target: str, error: BaseException) -> None:
        self.failed.append(target)
        self.lines.append(f"{target} failed: {error}")

    @property
    def ok(self) -> bool:
        return not self.failed


INSTALLERS: Dict[str, Callable[[ClientContext, Report], None]] = {
    "codex": install_codex,
    "qwen": install_qwen,
    "claude": install_claude,
    "continue": install_continue,
    "vscode": install_vscode,
}


def install(ctx: ClientContext, targets: Iterable[str] = TARGETS) -> Report:
    report = Report()
    for target in targets:
        installer = INSTALLERS.get(target)
        if installer is None:
            report.note(f"unknown target `{target}` — skipped")
            continue
        try:
            installer(ctx, report)
        except OSError as exc:
            report.failure(target, exc)
    return report


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def parse_targets(raw: str) -> List[str]:
    if not raw or raw.strip().lower() == "all":
        return list(TARGETS)
    chosen = [part.strip().lower() for part in raw.split(",") if part.strip()]
    unknown = [c for c in chosen if c not in TARGETS]
    if unknown:
        raise SystemExit(f"unknown target(s): {', '.join(unknown)}. "
                         f"Choose from: {', '.join(TARGETS)}")
    return chosen


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Write coding-agent configuration")
    parser.add_argument("--config", default="models.yaml", type=Path)
    parser.add_argument("--out", default=Path("generated"), type=Path)
    parser.add_argument("--install", action="store_true",
                        help="also write into the real client folders")
    parser.add_argument("--only", default="all",
                        help=f"comma separated: {', '.join(TARGETS)}")
    parser.add_argument("--url", default="",
                        help="endpoint to write into the configs "
                             "(default: the address in models.yaml)")
    args = parser.parse_args(argv)

    targets = parse_targets(args.only)
    try:
        cfg = fleetfile.load(args.config)
    except fleetfile.FleetFileError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    ctx = context_from_config(cfg, base_url=args.url.strip(),
                              project_dir=args.config.resolve().parent)
    if not ctx.models:
        print("models.yaml has no models yet — add one in the control panel first",
              file=sys.stderr)
        return 2

    for path in write_generated(ctx, args.out, targets):
        print(f"  {path}")
    print(f"generated -> {args.out.resolve()}  (endpoint {ctx.base_url})")

    if args.install:
        print("installing:")
        report = install(ctx, targets)
        for line in report.lines:
            print(f"  {line}")
        if not report.ok:
            return 1
        print("done. Open a new terminal so the new environment is picked up.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
