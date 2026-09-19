"""Reading, validating and rewriting `models.yaml`.

The control panel edits the fleet at runtime — adding a downloaded model,
removing one, changing the listening address — so the file has to be written
back safely. Writes are atomic and always carry the explanatory header, so a
file the launcher produced is still a file a human can edit by hand.
"""

from __future__ import annotations

import os
import re
import secrets
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import yaml

PLACEHOLDER_KEYS = {"", "sk-gateway-local-change-me", "changeme", "change-me"}
ALIAS_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$")
DEFAULT_PORT = 8900
FIRST_BACKEND_PORT = 8801
LAST_BACKEND_PORT = 8999

GATEWAY_DEFAULTS: Dict[str, Any] = {
    "host": "127.0.0.1",
    "port": DEFAULT_PORT,
    "api_key": "",
    "mode": "exclusive",
    "default_model": "",
    "preload": False,
    "request_timeout": 1800,
    "start_timeout": 900,
    "idle_timeout": 0,
    "unsloth_bin": "unsloth",
    "wire_api": "responses",
    "unknown_model": "default",
    "auto_import": True,
    "allow_lan": False,
    "extra_model_paths": [],
}

HEADER = """\
# Padra Platform Launcher — model fleet
#
# The control panel writes this file when you add, remove or reconfigure a
# model, and it stays hand-editable. Every entry under `models:` becomes a
# selectable model id at the gateway; the gateway starts `unsloth run` for it
# on demand and routes by the `model` field of each request.
#
# gateway.host      127.0.0.1 keeps the gateway private; 0.0.0.0 serves the LAN.
# gateway.mode      exclusive = one model in VRAM at a time, concurrent = all.
# gateway.idle_timeout  seconds of inactivity before a model is freed (0 = off).
# gateway.wire_api  Codex provider wire format written into ~/.codex/config.toml.
# gateway.unknown_model  `default` serves unlisted model ids with the default
#                   model (what Claude Code needs); `error` returns 404 instead.
#
# Per-model keys: alias, repo, quant, port, context_length, extra_args, env,
#                 host, api_key, served_id, reasoning, disable_tools.
"""


class FleetFileError(ValueError):
    """Raised when a fleet file cannot be used as-is."""


# --------------------------------------------------------------------------- #
# Load / save
# --------------------------------------------------------------------------- #
def load(path: Path) -> Dict[str, Any]:
    if not path.exists():
        raise FleetFileError(f"config not found: {path}")
    try:
        raw = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    except yaml.YAMLError as exc:
        raise FleetFileError(f"{path} is not valid YAML: {exc}") from exc
    if not isinstance(raw, dict):
        raise FleetFileError(f"config must be a YAML mapping: {path}")
    return normalise(raw)


def save(path: Path, cfg: Dict[str, Any]) -> None:
    payload = {
        "gateway": {k: v for k, v in (cfg.get("gateway") or {}).items()
                    if not k.startswith("__")},
        "models": [
            {k: v for k, v in entry.items() if v not in (None, "", [], {})}
            for entry in (cfg.get("models") or [])
        ],
    }
    body = yaml.safe_dump(payload, sort_keys=False, allow_unicode=True,
                          default_flow_style=False, width=100)
    path.parent.mkdir(parents=True, exist_ok=True)
    handle = tempfile.NamedTemporaryFile(
        "w", encoding="utf-8", delete=False, dir=str(path.parent),
        prefix=path.name + ".", suffix=".tmp",
    )
    try:
        handle.write(HEADER + "\n" + body)
        handle.flush()
        os.fsync(handle.fileno())
    finally:
        handle.close()
    os.replace(handle.name, path)


# --------------------------------------------------------------------------- #
# Validation
# --------------------------------------------------------------------------- #
def normalise(raw: Dict[str, Any]) -> Dict[str, Any]:
    """Fill defaults, coerce types and reject anything unusable."""
    gateway = dict(GATEWAY_DEFAULTS)
    incoming = raw.get("gateway")
    if incoming is not None and not isinstance(incoming, dict):
        raise FleetFileError("`gateway` must be a mapping")
    gateway.update(incoming or {})

    gateway["host"] = str(gateway["host"] or "127.0.0.1").strip()
    gateway["port"] = _as_port(gateway.get("port"), DEFAULT_PORT, "gateway.port")
    gateway["mode"] = str(gateway.get("mode") or "exclusive").strip().lower()
    if gateway["mode"] not in ("exclusive", "concurrent"):
        raise FleetFileError("gateway.mode must be `exclusive` or `concurrent`")
    gateway["wire_api"] = str(gateway.get("wire_api") or "responses").strip().lower()
    if gateway["wire_api"] not in ("responses", "chat"):
        raise FleetFileError("gateway.wire_api must be `responses` or `chat`")
    gateway["unknown_model"] = str(gateway.get("unknown_model") or "default").strip().lower()
    if gateway["unknown_model"] not in ("default", "error"):
        raise FleetFileError("gateway.unknown_model must be `default` or `error`")
    gateway["preload"] = bool(gateway.get("preload"))
    gateway["auto_import"] = bool(gateway.get("auto_import", True))
    gateway["allow_lan"] = bool(gateway.get("allow_lan")) or gateway["host"] == "0.0.0.0"
    gateway["request_timeout"] = _as_number(gateway.get("request_timeout"), 1800,
                                            "gateway.request_timeout")
    gateway["start_timeout"] = int(_as_number(gateway.get("start_timeout"), 900,
                                              "gateway.start_timeout"))
    gateway["idle_timeout"] = int(_as_number(gateway.get("idle_timeout"), 0,
                                             "gateway.idle_timeout"))
    gateway["unsloth_bin"] = str(gateway.get("unsloth_bin") or "unsloth")
    gateway["api_key"] = str(gateway.get("api_key") or "")
    paths = gateway.get("extra_model_paths") or []
    if isinstance(paths, str):
        paths = [paths]
    if not isinstance(paths, list):
        raise FleetFileError("gateway.extra_model_paths must be a list of folders")
    gateway["extra_model_paths"] = [str(p) for p in paths if str(p).strip()]

    entries = raw.get("models") or []
    if not isinstance(entries, list):
        raise FleetFileError("`models` must be a list")

    models: List[Dict[str, Any]] = []
    aliases: set = set()
    ports: Dict[int, str] = {}
    for index, entry in enumerate(entries):
        model = _normalise_model(entry, index)
        if model["alias"] in aliases:
            raise FleetFileError(f"duplicate alias `{model['alias']}`")
        if model["port"] in ports:
            raise FleetFileError(
                f"port {model['port']} is used by both `{ports[model['port']]}` "
                f"and `{model['alias']}`"
            )
        if model["port"] == gateway["port"]:
            raise FleetFileError(
                f"model `{model['alias']}` uses the gateway port {gateway['port']}"
            )
        aliases.add(model["alias"])
        ports[model["port"]] = model["alias"]
        models.append(model)

    default_model = str(gateway.get("default_model") or "").strip()
    if default_model and default_model not in aliases:
        raise FleetFileError(
            f"gateway.default_model `{default_model}` is not a defined alias"
        )
    if not default_model and models:
        default_model = models[0]["alias"]
    gateway["default_model"] = default_model

    return {"gateway": gateway, "models": models}


def _normalise_model(entry: Any, index: int) -> Dict[str, Any]:
    if not isinstance(entry, dict):
        raise FleetFileError(f"models[{index}] must be a mapping")
    for required in ("alias", "repo"):
        if not str(entry.get(required) or "").strip():
            raise FleetFileError(f"models[{index}] is missing `{required}`")

    alias = str(entry["alias"]).strip().lower()
    if not ALIAS_RE.match(alias):
        raise FleetFileError(
            f"alias `{alias}` must be lowercase letters, digits, dot, dash or underscore"
        )

    model: Dict[str, Any] = {
        "alias": alias,
        "repo": str(entry["repo"]).strip(),
        "quant": str(entry.get("quant") or "").strip(),
        "port": _as_port(entry.get("port"), FIRST_BACKEND_PORT + index,
                         f"models[{index}].port"),
        "host": str(entry.get("host") or "127.0.0.1").strip(),
    }
    context_length = entry.get("context_length")
    model["context_length"] = (
        int(_as_number(context_length, 0, f"models[{index}].context_length")) or None
    )

    extra_args = entry.get("extra_args") or []
    if isinstance(extra_args, str):
        extra_args = extra_args.split()
    if not isinstance(extra_args, list):
        raise FleetFileError(f"models[{index}].extra_args must be a list")
    model["extra_args"] = [str(a) for a in extra_args]

    env = entry.get("env") or {}
    if not isinstance(env, dict):
        raise FleetFileError(f"models[{index}].env must be a mapping")
    model["env"] = {str(k): str(v) for k, v in env.items()}

    model["disable_tools"] = bool(entry.get("disable_tools", True))
    reasoning = entry.get("reasoning")
    model["reasoning"] = str(reasoning).strip().lower() if reasoning is not None else ""
    if model["reasoning"] not in ("", "on", "off"):
        raise FleetFileError(f"models[{index}].reasoning must be `on` or `off`")

    for optional in ("api_key", "served_id"):
        value = str(entry.get(optional) or "").strip()
        if value:
            model[optional] = value
    return model


def _as_port(value: Any, fallback: int, label: str) -> int:
    if value in (None, ""):
        return fallback
    try:
        port = int(value)
    except (TypeError, ValueError) as exc:
        raise FleetFileError(f"{label} must be a whole number") from exc
    if not 1 <= port <= 65535:
        raise FleetFileError(f"{label} must be between 1 and 65535")
    return port


def _as_number(value: Any, fallback: float, label: str) -> float:
    if value in (None, ""):
        return fallback
    try:
        number = float(value)
    except (TypeError, ValueError) as exc:
        raise FleetFileError(f"{label} must be a number") from exc
    if number < 0:
        raise FleetFileError(f"{label} cannot be negative")
    return number


# --------------------------------------------------------------------------- #
# Mutations
# --------------------------------------------------------------------------- #
def ensure_api_key(cfg: Dict[str, Any]) -> Tuple[Dict[str, Any], bool]:
    """Replace a missing or placeholder key with a real random one."""
    gateway = cfg.setdefault("gateway", {})
    current = str(gateway.get("api_key") or "").strip()
    if current.lower() in PLACEHOLDER_KEYS:
        gateway["api_key"] = "sk-padra-" + secrets.token_urlsafe(32)
        return cfg, True
    return cfg, False


def used_ports(cfg: Dict[str, Any]) -> set:
    ports = {int((cfg.get("gateway") or {}).get("port") or DEFAULT_PORT)}
    ports.update(int(m["port"]) for m in cfg.get("models") or [])
    return ports


def next_free_port(cfg: Dict[str, Any]) -> int:
    taken = used_ports(cfg)
    for port in range(FIRST_BACKEND_PORT, LAST_BACKEND_PORT + 1):
        if port not in taken:
            return port
    raise FleetFileError(
        f"no free port between {FIRST_BACKEND_PORT} and {LAST_BACKEND_PORT}"
    )


def suggest_alias(repo: str, quant: str, cfg: Dict[str, Any]) -> str:
    """A short, unique, URL-safe alias derived from the repository name."""
    from launcher.discovery import repo_basename

    base = repo_basename(repo).lower()
    base = re.sub(r"[^a-z0-9._-]+", "-", base).strip("-._") or "model"
    taken = {m["alias"] for m in cfg.get("models") or []}
    if base not in taken:
        return base
    if quant:
        quant_slug = re.sub(r"[^a-z0-9._-]+", "-", quant.lower()).strip("-._")
        candidate = f"{base}-{quant_slug}"[:64]
        if candidate and candidate not in taken:
            return candidate
    for counter in range(2, 1000):
        candidate = f"{base}-{counter}"
        if candidate not in taken:
            return candidate
    raise FleetFileError(f"cannot derive a free alias for {repo}")


def add_model(cfg: Dict[str, Any], *, repo: str, quant: str = "",
              alias: str = "", context_length: Optional[int] = None,
              port: Optional[int] = None, reasoning: str = "",
              extra_args: Optional[List[str]] = None,
              make_default: bool = False) -> Dict[str, Any]:
    """Append a model and return the stored entry. Mutates `cfg` in place."""
    repo = repo.strip()
    if not repo:
        raise FleetFileError("a repository is required")
    quant = quant.strip()
    for existing in cfg.get("models") or []:
        if existing["repo"].lower() == repo.lower() and \
                existing.get("quant", "").lower() == quant.lower():
            raise FleetFileError(f"`{repo}:{quant}` is already in the fleet"
                                 if quant else f"`{repo}` is already in the fleet")

    entry = {
        "alias": (alias.strip().lower() or suggest_alias(repo, quant, cfg)),
        "repo": repo,
        "quant": quant,
        "port": port or next_free_port(cfg),
        "context_length": context_length or None,
        "extra_args": list(extra_args or []),
        "disable_tools": True,
        "reasoning": reasoning,
        "host": "127.0.0.1",
        "env": {},
    }
    cfg.setdefault("models", []).append(entry)
    normalised = normalise(cfg)
    cfg["models"] = normalised["models"]
    cfg["gateway"] = normalised["gateway"]
    if make_default or len(cfg["models"]) == 1:
        cfg["gateway"]["default_model"] = entry["alias"]
    return next(m for m in cfg["models"] if m["alias"] == entry["alias"])


def remove_model(cfg: Dict[str, Any], alias: str) -> Dict[str, Any]:
    alias = alias.strip().lower()
    models = cfg.get("models") or []
    remaining = [m for m in models if m["alias"] != alias]
    if len(remaining) == len(models):
        raise FleetFileError(f"unknown model `{alias}`")
    cfg["models"] = remaining
    gateway = cfg.setdefault("gateway", {})
    if gateway.get("default_model") == alias:
        gateway["default_model"] = remaining[0]["alias"] if remaining else ""
    return cfg


def set_default_model(cfg: Dict[str, Any], alias: str) -> Dict[str, Any]:
    alias = alias.strip().lower()
    if alias not in {m["alias"] for m in cfg.get("models") or []}:
        raise FleetFileError(f"unknown model `{alias}`")
    cfg.setdefault("gateway", {})["default_model"] = alias
    return cfg
