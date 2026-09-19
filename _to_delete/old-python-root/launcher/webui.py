"""Web UI routes for Padra Platform Launcher.

Serves the localized control panel and every endpoint it needs: fleet
management, local and catalog model discovery, LAN exposure, client
configuration install and agent launching. The panel is a same-origin single
page; every user-facing string comes from `launcher/locales/*.json` and is
applied in the browser, which also supplies bidirectional layout, Persian
digits and the Jalali calendar through `Intl`.
"""

from __future__ import annotations

import asyncio
import io
import json
import os
import shlex
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse, Response
from pydantic import BaseModel, Field

from launcher import discovery, fleetfile, gen_configs, netinfo
from launcher.fleetfile import FleetFileError
from launcher.settings import Settings

PACKAGE_DIR = Path(__file__).resolve().parent
STATIC_DIR = PACKAGE_DIR / "static"
LOCALES_DIR = PACKAGE_DIR / "locales"
LOOPBACK = {"127.0.0.1", "::1", "localhost"}
IS_WINDOWS = os.name == "nt"
CREATE_NO_WINDOW = 0x08000000 if IS_WINDOWS else 0

AGENTS = {
    "codex": {"command": "codex", "install_key": "dialog.missing.codex"},
    "qwen": {"command": "qwen", "install_key": "dialog.missing.qwen"},
    "claude": {"command": "claude", "install_key": "dialog.missing.claude"},
}

STATIC_TYPES = {
    ".html": "text/html; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".js": "application/javascript; charset=utf-8",
    ".svg": "image/svg+xml",
    ".json": "application/json; charset=utf-8",
}


# --------------------------------------------------------------------------- #
# Request bodies
# --------------------------------------------------------------------------- #
class LaunchRequest(BaseModel):
    agent: str
    alias: str = ""
    folder: str


class SettingsPatch(BaseModel):
    culture: Optional[str] = None
    mode: Optional[str] = None
    projectFolder: Optional[str] = None
    selectedAlias: Optional[str] = None
    installTargets: Optional[List[str]] = None


class AddModelRequest(BaseModel):
    repo: str
    quant: str = ""
    alias: str = ""
    contextLength: Optional[int] = Field(default=None, ge=0)
    makeDefault: bool = False
    reasoning: str = ""


class NetworkRequest(BaseModel):
    host: Optional[str] = None
    port: Optional[int] = Field(default=None, ge=1, le=65535)
    allowLan: Optional[bool] = None
    idleTimeout: Optional[int] = Field(default=None, ge=0)


class InstallRequest(BaseModel):
    targets: List[str] = Field(default_factory=lambda: list(gen_configs.TARGETS))
    scope: str = "local"


# --------------------------------------------------------------------------- #
# Locales
# --------------------------------------------------------------------------- #
def _locale_files() -> List[Path]:
    return sorted(LOCALES_DIR.glob("*.json"))


def _locale_meta() -> List[Dict[str, str]]:
    cultures: List[Dict[str, str]] = []
    for path in _locale_files():
        try:
            meta = json.loads(path.read_text(encoding="utf-8")).get("_meta", {})
        except (ValueError, OSError):
            continue
        cultures.append({
            "code": str(meta.get("code") or path.stem),
            "name": str(meta.get("name") or path.stem),
            "dir": "rtl" if str(meta.get("dir", "ltr")).lower() == "rtl" else "ltr",
        })
    return cultures


def _which(command: str) -> Optional[str]:
    from shutil import which

    found = which(command)
    if found:
        return found
    if IS_WINDOWS:
        for suffix in (".cmd", ".exe", ".bat", ".ps1"):
            found = which(command + suffix)
            if found:
                return found
    return None


# --------------------------------------------------------------------------- #
# Registration
# --------------------------------------------------------------------------- #
def register(app: FastAPI, *, fleet: Any, config_path: Path,
             gateway_key: Callable[[], str],
             request_shutdown: Callable[[], None]) -> None:
    from launcher import gateway as gateway_mod

    settings = Settings(config_path.parent / "launcher.settings.json")
    catalog = discovery.Catalog()
    cache: Dict[str, Any] = {"local": None, "local_at": 0.0}
    scan_lock = asyncio.Lock()

    # -- guard -------------------------------------------------------------- #
    def guard(request: Request) -> None:
        """Loopback callers are trusted; every other caller must present the key."""
        client = request.client.host if request.client else None
        if client in LOOPBACK:
            return
        key = gateway_key()
        if not key:
            raise HTTPException(
                status_code=403,
                detail={"error": {"message": "This launcher has no API key, so the "
                                             "control panel only answers locally."}},
            )
        header = request.headers.get("authorization", "")
        token = header.split(" ", 1)[1].strip() if header.lower().startswith("bearer ") else None
        if token is None:
            token = request.headers.get("x-api-key")
        if token != key:
            raise HTTPException(status_code=401,
                                detail={"error": {"message": "Invalid API key."}})

    def fail(status: int, message: str, **extra: Any) -> HTTPException:
        return HTTPException(status_code=status,
                             detail={"error": {"message": message, **extra}})

    # -- helpers ------------------------------------------------------------ #
    def gw() -> Dict[str, Any]:
        return fleet.gateway_cfg

    def base_url_for(scope: str) -> str:
        host, port = str(gw()["host"]), int(gw()["port"])
        if scope == "lan":
            return netinfo.preferred_lan_url(host, port)
        return gen_configs.default_base_url(fleet.cfg)

    def client_context(scope: str = "local") -> gen_configs.ClientContext:
        return gen_configs.context_from_config(
            fleet.cfg, base_url=base_url_for(scope), project_dir=config_path.parent
        )

    async def local_models(refresh: bool = False) -> List[Dict[str, Any]]:
        async with scan_lock:
            fresh_enough = (cache["local"] is not None
                            and time.time() - cache["local_at"] < 60)
            if fresh_enough and not refresh:
                return cache["local"]
            found = await asyncio.to_thread(
                discovery.scan_local_models, gw().get("extra_model_paths") or []
            )
            in_fleet = {
                (m["repo"].lower(), (m.get("quant") or "").lower())
                for m in fleet.cfg["models"]
            }
            payload = []
            for model in found:
                item = model.as_dict()
                item["inFleet"] = (model.repo.lower(), model.quant.lower()) in in_fleet
                payload.append(item)
            cache["local"] = payload
            cache["local_at"] = time.time()
            return payload

    def invalidate_local_cache() -> None:
        cache["local"] = None
        cache["local_at"] = 0.0

    async def persist_and_apply(cfg: Dict[str, Any]) -> None:
        normalised = fleetfile.normalise(cfg)
        await asyncio.to_thread(fleetfile.save, config_path, normalised)
        async with fleet.lock:
            stopped = await asyncio.to_thread(fleet.apply, normalised)
        for alias in stopped:
            gateway_mod.log(f"{alias}: stopped because its configuration changed")
        invalidate_local_cache()

    # -- shell -------------------------------------------------------------- #
    @app.get("/")
    async def index(request: Request) -> FileResponse:
        guard(request)
        return FileResponse(STATIC_DIR / "index.html",
                            media_type=STATIC_TYPES[".html"],
                            headers={"Cache-Control": "no-store"})

    @app.get("/ui/{filename}")
    async def static_file(filename: str, request: Request) -> FileResponse:
        guard(request)
        target = (STATIC_DIR / filename).resolve()
        if target.parent != STATIC_DIR or not target.is_file():
            raise fail(404, "Not found")
        return FileResponse(target,
                            media_type=STATIC_TYPES.get(target.suffix, "text/plain"),
                            headers={"Cache-Control": "no-store"})

    # -- bootstrap and localization ----------------------------------------- #
    @app.get("/api/bootstrap")
    async def bootstrap(request: Request) -> JSONResponse:
        guard(request)
        gateway = gw()
        return JSONResponse({
            "apiKey": gateway_key(),
            "cultures": _locale_meta(),
            "settings": settings.as_dict(),
            "mode": fleet.mode,
            "defaultModel": fleet.default_alias,
            "configPath": str(config_path),
            "platform": "windows" if IS_WINDOWS else sys.platform,
            "installTargets": list(gen_configs.TARGETS),
            "agents": sorted(AGENTS),
            "network": _network_payload(),
            "models": _fleet_payload(),
            "idleTimeout": int(gateway.get("idle_timeout") or 0),
        })

    @app.get("/api/locales/{code}")
    async def locale(code: str, request: Request) -> JSONResponse:
        guard(request)
        target = (LOCALES_DIR / f"{code}.json").resolve()
        if target.parent != LOCALES_DIR or not target.is_file():
            raise fail(404, f"Unknown culture {code}")
        return JSONResponse(json.loads(target.read_text(encoding="utf-8")))

    @app.get("/api/settings")
    async def read_settings(request: Request) -> JSONResponse:
        guard(request)
        return JSONResponse({"settings": settings.as_dict(), "mode": fleet.mode})

    @app.post("/api/settings")
    async def write_settings(patch: SettingsPatch, request: Request) -> JSONResponse:
        guard(request)
        if patch.culture:
            if patch.culture not in {c["code"] for c in _locale_meta()}:
                raise fail(400, "Unknown culture")
            settings.set("culture", patch.culture)
        if patch.mode:
            await fleet.set_mode(patch.mode)
            settings.set("mode", patch.mode)
        if patch.installTargets is not None:
            unknown = [t for t in patch.installTargets if t not in gen_configs.TARGETS]
            if unknown:
                raise fail(400, f"Unknown target(s): {', '.join(unknown)}")
            settings.set("installTargets", patch.installTargets)
        if patch.projectFolder is not None:
            settings.set("projectFolder", patch.projectFolder)
        if patch.selectedAlias is not None:
            settings.set("selectedAlias", patch.selectedAlias)
        return JSONResponse({"settings": settings.as_dict(), "mode": fleet.mode})

    # -- status and log ------------------------------------------------------ #
    def _fleet_payload() -> List[Dict[str, Any]]:
        return [
            {
                "alias": m["alias"],
                "repo": m["repo"],
                "quant": m.get("quant") or "",
                "port": m["port"],
                "contextLength": m.get("context_length"),
                "isDefault": m["alias"] == fleet.default_alias,
            }
            for m in fleet.cfg["models"]
        ]

    def _network_payload() -> Dict[str, Any]:
        gateway = gw()
        host, port = str(gateway["host"]), int(gateway["port"])
        payload = netinfo.endpoint_urls(host, port)
        payload["allowLan"] = bool(gateway.get("allow_lan"))
        payload["hostname"] = netinfo.hostname()
        payload["firewallCommand"] = netinfo.firewall_command(port)
        payload["canOpenFirewall"] = IS_WINDOWS
        return payload

    @app.get("/api/status")
    async def status(request: Request) -> JSONResponse:
        guard(request)
        return JSONResponse({
            "mode": fleet.mode,
            "defaultModel": fleet.default_alias,
            "backends": [b.status() for b in fleet.backends.values()],
            "models": _fleet_payload(),
            "network": _network_payload(),
        })

    @app.get("/api/log")
    async def read_log(request: Request, since: int = 0) -> JSONResponse:
        guard(request)
        entries = gateway_mod.log_entries_since(since)
        return JSONResponse({
            "entries": entries,
            "cursor": entries[-1]["seq"] if entries else since,
        })

    # -- fleet --------------------------------------------------------------- #
    @app.post("/api/models/{alias}/load")
    async def load_model(alias: str, request: Request) -> JSONResponse:
        guard(request)
        backend = fleet.resolve(alias, fallback=False)
        await fleet.ensure(backend)
        return JSONResponse(backend.status())

    @app.post("/api/models/{alias}/unload")
    async def unload_model(alias: str, request: Request) -> JSONResponse:
        guard(request)
        backend = fleet.resolve(alias, fallback=False)
        await fleet.unload(backend)
        return JSONResponse(backend.status())

    @app.post("/api/models/{alias}/activate")
    async def activate_model(alias: str, request: Request) -> JSONResponse:
        """Make a model the default and load it, freeing the others."""
        guard(request)
        backend = fleet.resolve(alias, fallback=False)
        await fleet.set_default(backend.alias)
        settings.set("selectedAlias", backend.alias)
        if fleet.mode == "exclusive":
            for other in fleet.backends.values():
                if other is not backend and other.is_running():
                    await fleet.unload(other)
        await fleet.ensure(backend)
        gateway_mod.log(f"{backend.alias} is now the active model")
        return JSONResponse(backend.status())

    @app.post("/api/models")
    async def add_model(payload: AddModelRequest, request: Request) -> JSONResponse:
        guard(request)
        cfg = json.loads(json.dumps(fleet.cfg))  # deep copy of plain data
        try:
            entry = fleetfile.add_model(
                cfg,
                repo=payload.repo,
                quant=payload.quant,
                alias=payload.alias,
                context_length=payload.contextLength,
                reasoning=payload.reasoning,
                make_default=payload.makeDefault,
            )
        except FleetFileError as exc:
            raise fail(400, str(exc)) from exc
        await persist_and_apply(cfg)
        gateway_mod.log(f"added `{entry['alias']}` -> {payload.repo}"
                        f"{':' + payload.quant if payload.quant else ''}")
        return JSONResponse({"ok": True, "model": entry, "models": _fleet_payload()})

    @app.delete("/api/models/{alias}")
    async def delete_model(alias: str, request: Request) -> JSONResponse:
        guard(request)
        backend = fleet.backends.get(alias)
        if backend is None:
            raise fail(404, f"Unknown model `{alias}`")
        if backend.is_running():
            await fleet.unload(backend)
        cfg = json.loads(json.dumps(fleet.cfg))
        try:
            fleetfile.remove_model(cfg, alias)
        except FleetFileError as exc:
            raise fail(400, str(exc)) from exc
        await persist_and_apply(cfg)
        gateway_mod.log(f"removed `{alias}` from the fleet")
        return JSONResponse({"ok": True, "models": _fleet_payload()})

    # -- discovery ------------------------------------------------------------ #
    @app.get("/api/models/local")
    async def list_local(request: Request, refresh: bool = False) -> JSONResponse:
        guard(request)
        models = await local_models(refresh=refresh)
        roots = [str(p) for p in discovery.huggingface_cache_roots() if p.exists()]
        roots += [str(p) for p in discovery.lmstudio_roots()]
        return JSONResponse({
            "models": models,
            "roots": roots,
            "totalBytes": sum(int(m["sizeBytes"]) for m in models),
            "scannedAt": cache["local_at"],
        })

    @app.get("/api/models/catalog")
    async def list_catalog(request: Request, search: str = "", repo: str = "",
                           refresh: bool = False) -> JSONResponse:
        guard(request)
        known = await local_models()
        local = [
            discovery.LocalModel(
                repo=str(m["repo"]), quant=str(m["quant"]), source=str(m["source"]),
                size_bytes=int(m["sizeBytes"]), file_count=int(m["fileCount"]),
                modified=float(m["modified"]), path=str(m["path"]),
                context_length=m.get("contextLength"),
            )
            for m in known
        ]
        try:
            if repo:
                record = await asyncio.to_thread(catalog.variants, repo, refresh=refresh)
                return JSONResponse({"repo": discovery.merge_downloaded(record, local)})
            repos = await asyncio.to_thread(catalog.repositories, search=search,
                                            refresh=refresh)
        except discovery.CatalogError as exc:
            raise fail(503, str(exc)) from exc
        downloaded = {m.repo.lower() for m in local}
        payload = []
        for record in repos:
            item = record.as_dict()
            item["hasLocal"] = record.repo.lower() in downloaded
            payload.append(item)
        return JSONResponse({"repos": payload})

    # -- network -------------------------------------------------------------- #
    @app.get("/api/network")
    async def read_network(request: Request) -> JSONResponse:
        guard(request)
        return JSONResponse(_network_payload())

    @app.post("/api/network")
    async def write_network(payload: NetworkRequest, request: Request) -> JSONResponse:
        guard(request)
        cfg = json.loads(json.dumps(fleet.cfg))
        gateway = cfg["gateway"]
        restart_needed = False

        if payload.allowLan is not None:
            gateway["allow_lan"] = bool(payload.allowLan)
            gateway["host"] = "0.0.0.0" if payload.allowLan else "127.0.0.1"
            restart_needed = True
        if payload.host:
            gateway["host"] = payload.host.strip()
            gateway["allow_lan"] = gateway["host"] == "0.0.0.0"
            restart_needed = True
        if payload.port:
            gateway["port"] = int(payload.port)
            restart_needed = True
        if payload.idleTimeout is not None:
            gateway["idle_timeout"] = int(payload.idleTimeout)

        if restart_needed and gateway["allow_lan"] and not gateway_key():
            raise fail(400, "Set a gateway API key before sharing on the network.")

        try:
            await persist_and_apply(cfg)
        except FleetFileError as exc:
            raise fail(400, str(exc)) from exc

        return JSONResponse({"ok": True, "restartRequired": restart_needed,
                             "network": _network_payload()})

    @app.post("/api/network/firewall")
    async def firewall(request: Request) -> JSONResponse:
        guard(request)
        port = int(gw()["port"])
        result = await asyncio.to_thread(netinfo.open_firewall, port)
        gateway_mod.log(
            f"firewall rule for port {port}: "
            + ("added" if result["ok"] else f"not added — run: {result['command']}")
        )
        return JSONResponse(result)

    # -- clients --------------------------------------------------------------- #
    @app.get("/api/clients")
    async def clients(request: Request) -> JSONResponse:
        guard(request)
        unsloth = discovery.unsloth_binary()
        return JSONResponse({
            "unsloth": {"installed": bool(unsloth), "path": unsloth or ""},
            "agents": [
                {"id": name, "installed": bool(_which(spec["command"])),
                 "path": _which(spec["command"]) or "", "helpKey": spec["install_key"]}
                for name, spec in sorted(AGENTS.items())
            ],
            "vscodeFolders": [str(p) for p in gen_configs.vscode_user_dirs()],
            "targets": list(gen_configs.TARGETS),
        })

    @app.get("/api/client-configs")
    async def client_configs(request: Request, scope: str = "local") -> JSONResponse:
        guard(request)
        ctx = client_context(scope)
        if not ctx.models:
            raise fail(400, "Add a model before generating client configuration.")
        return JSONResponse({
            "baseUrl": ctx.base_url,
            "files": gen_configs.generated_files(ctx),
        })

    @app.get("/api/client-bundle")
    async def client_bundle(request: Request, scope: str = "lan") -> Response:
        """A zip another machine can unpack to talk to this gateway."""
        guard(request)
        ctx = client_context(scope)
        if not ctx.models:
            raise fail(400, "Add a model before generating client configuration.")
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
            for relative, content in gen_configs.generated_files(ctx).items():
                archive.writestr(f"padra-client/{relative}", content)
        gateway_mod.log(f"client bundle generated for {ctx.base_url}")
        return Response(
            content=buffer.getvalue(),
            media_type="application/zip",
            headers={"Content-Disposition": 'attachment; filename="padra-client.zip"'},
        )

    @app.post("/api/install-configs")
    async def install_configs(payload: InstallRequest, request: Request) -> JSONResponse:
        guard(request)
        unknown = [t for t in payload.targets if t not in gen_configs.TARGETS]
        if unknown:
            raise fail(400, f"Unknown target(s): {', '.join(unknown)}")
        ctx = client_context(payload.scope)
        if not ctx.models:
            raise fail(400, "Add a model before installing client configuration.")

        out = config_path.parent / "generated"
        await asyncio.to_thread(gen_configs.write_generated, ctx, out, payload.targets)
        report = await asyncio.to_thread(gen_configs.install, ctx, payload.targets)
        for line in report.lines:
            gateway_mod.log(line)
        settings.set("installTargets", payload.targets)
        return JSONResponse({
            "ok": report.ok,
            "written": report.written,
            "failed": report.failed,
            "output": report.lines,
            "generated": str(out),
            "baseUrl": ctx.base_url,
        })

    # -- launching -------------------------------------------------------------- #
    @app.post("/api/launch-agent")
    async def launch_agent(payload: LaunchRequest, request: Request) -> JSONResponse:
        guard(request)
        spec = AGENTS.get(payload.agent)
        if spec is None:
            raise fail(400, f"Unknown agent `{payload.agent}`")

        alias = payload.alias or fleet.default_alias
        backend = fleet.resolve(alias, fallback=False)

        folder = Path(payload.folder).expanduser()
        if not folder.is_dir():
            raise fail(400, "folder", path=str(folder))
        if _which(spec["command"]) is None:
            raise fail(424, "missing", agent=payload.agent)

        ctx = client_context("local")
        # Codex resolves `--profile <alias>` from ~/.codex/<alias>.config.toml, so
        # the agent's own configuration has to exist before the terminal opens.
        # The installers are idempotent, so this is safe to repeat.
        report = await asyncio.to_thread(gen_configs.install, ctx, [payload.agent])
        for line in report.lines:
            gateway_mod.log(line)
        if not report.ok:
            raise fail(500, f"Could not write the {payload.agent} configuration.")

        env = _agent_environment(payload.agent, ctx, backend.alias)
        command = _agent_command(payload.agent, backend.alias)

        settings.update({"projectFolder": str(folder), "selectedAlias": backend.alias,
                         "lastAgent": payload.agent})

        try:
            started, script = await asyncio.to_thread(
                _spawn_terminal, payload.agent, backend.alias, folder, env, command
            )
        except OSError as exc:
            raise fail(500, str(exc)) from exc

        if not started:
            gateway_mod.log(f"no terminal emulator was found; run manually: {script}")
            return JSONResponse({"ok": False, "command": script})

        gateway_mod.log(f"launched {payload.agent} with {backend.alias} in {folder}")
        return JSONResponse({"ok": True, "agent": payload.agent,
                             "alias": backend.alias, "folder": str(folder)})

    # -- diagnostics and shutdown ------------------------------------------------ #
    @app.get("/api/diagnostics")
    async def diagnostics(request: Request) -> JSONResponse:
        guard(request)
        gateway = gw()
        roots = [
            {"path": str(root), "exists": root.exists()}
            for root in discovery.huggingface_cache_roots()
        ]
        return JSONResponse({
            "python": sys.version.split()[0],
            "platform": sys.platform,
            "configPath": str(config_path),
            "gatewayPort": int(gateway["port"]),
            "gatewayHost": str(gateway["host"]),
            "hasApiKey": bool(gateway_key()),
            "unslothPath": discovery.unsloth_binary() or "",
            "cacheRoots": roots,
            "backendPorts": [
                {"alias": m["alias"], "port": m["port"],
                 "free": netinfo.port_is_free("127.0.0.1", int(m["port"]))}
                for m in fleet.cfg["models"]
            ],
        })

    @app.post("/api/shutdown")
    async def shutdown(request: Request) -> JSONResponse:
        guard(request)
        request_shutdown()
        return JSONResponse({"ok": True})


# --------------------------------------------------------------------------- #
# Terminal launching
# --------------------------------------------------------------------------- #
def _agent_environment(agent: str, ctx: "gen_configs.ClientContext",
                       alias: str) -> Dict[str, str]:
    env: Dict[str, str] = {gen_configs.ENV_KEY_NAME: ctx.api_key}
    if agent == "claude":
        env.update(gen_configs.claude_env(ctx))
        env["ANTHROPIC_MODEL"] = alias
    else:
        env.update({
            "OPENAI_API_KEY": ctx.api_key,
            "OPENAI_BASE_URL": ctx.base_url,
            "OPENAI_MODEL": alias,
        })
    return env


def _agent_command(agent: str, alias: str) -> List[str]:
    if agent == "codex":
        return ["codex", "--profile", alias]
    if agent == "qwen":
        return ["qwen", "--model", alias]
    return ["claude", "--model", alias]


def _spawn_terminal(agent: str, alias: str, folder: Path, env: Dict[str, str],
                    command: List[str]) -> "tuple[bool, str]":
    """Open a terminal in `folder` with `env` set and `command` running.

    A script file is written instead of building one long shell string, so
    folder names and keys with spaces or quotes cannot break the command line.
    """
    title = f"{agent} — {alias}"
    scripts = Path(tempfile.gettempdir()) / "padra-launcher"
    scripts.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")

    if IS_WINDOWS:
        lines = ["@echo off", f"title {title}", f'cd /d "{folder}"']
        lines += [f'set "{name}={value}"' for name, value in env.items()]
        lines.append(subprocess.list2cmdline(command))
        script = scripts / f"{agent}-{stamp}.cmd"
        script.write_text("\r\n".join(lines) + "\r\n", encoding="utf-8")
        subprocess.Popen(["cmd", "/c", "start", title, "cmd", "/k", str(script)],
                         close_fds=True)
        return True, str(script)

    lines = ["#!/usr/bin/env bash", "set -e", f"cd {shlex.quote(str(folder))}"]
    lines += [f"export {name}={shlex.quote(value)}" for name, value in env.items()]
    lines.append(" ".join(shlex.quote(part) for part in command))
    script = scripts / f"{agent}-{stamp}.sh"
    script.write_text("\n".join(lines) + "\n", encoding="utf-8")
    script.chmod(0o700)

    from shutil import which

    terminals = [
        (["x-terminal-emulator", "-e"], True),
        (["gnome-terminal", "--"], False),
        (["konsole", "-e"], True),
        (["xfce4-terminal", "-e"], True),
        (["alacritty", "-e"], False),
        (["kitty"], False),
        (["xterm", "-e"], True),
    ]
    if sys.platform == "darwin":
        subprocess.Popen(["open", "-a", "Terminal", str(script)], close_fds=True)
        return True, str(script)
    for prefix, _needs_string in terminals:
        if which(prefix[0]):
            subprocess.Popen(prefix + ["bash", "-lc",
                                       f"{shlex.quote(str(script))}; exec bash"],
                             close_fds=True)
            return True, str(script)
    return False, str(script)
