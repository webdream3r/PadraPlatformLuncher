#!/usr/bin/env python3
"""
Padra Platform Launcher — gateway
=================================

One OpenAI/Anthropic-compatible endpoint in front of several `unsloth run`
backends. Clients pick a model with the normal `model` field; the gateway
loads or swaps the matching Unsloth backend and proxies the request.

Exposed on http://<host>:<port>:

    HEAD /                       liveness probe (Claude Code sends this)
    GET  /v1/models              aggregated alias list
    POST /v1/responses           OpenAI Responses  (Codex)
    POST /v1/chat/completions    OpenAI Chat Completions (Qwen Code, Continue)
    POST /v1/messages            Anthropic Messages (Claude Code)
    POST /v1/messages/count_tokens
    ANY  /v1/*                   transparent passthrough
    GET  /healthz                gateway + backend state
    GET  /admin/status           detailed backend state
    POST /admin/load/{alias}     force-load a backend
    POST /admin/unload/{alias}   stop a backend
    POST /admin/mode/{mode}      exclusive / concurrent
    POST /admin/reload           re-read models.yaml
    POST /admin/shutdown         stop every backend and exit

Run:  python -m launcher --config models.yaml
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import signal
import subprocess
import sys
import threading
import time
from collections import deque
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Deque, Dict, List, Optional, Tuple

import httpx
import uvicorn
from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response, StreamingResponse
from starlette.background import BackgroundTask

from launcher import fleetfile, netinfo
from launcher.fleetfile import FleetFileError

IS_WINDOWS = os.name == "nt"
CREATE_NO_WINDOW = 0x08000000 if IS_WINDOWS else 0
CREATE_NEW_PROCESS_GROUP = 0x00000200 if IS_WINDOWS else 0
UNSLOTH_KEY_RE = re.compile(r"sk-unsloth-[A-Za-z0-9._\-]{8,}")
LOG_TAIL = 200
LOOPBACK_HOSTS = {"127.0.0.1", "::1", "localhost"}

HOP_REQUEST_HEADERS = {
    "host", "content-length", "connection", "keep-alive", "accept-encoding",
    "transfer-encoding", "authorization", "x-api-key", "proxy-authorization",
    "upgrade", "te", "trailer", "expect",
}
HOP_RESPONSE_HEADERS = {
    "content-length", "content-encoding", "transfer-encoding", "connection",
    "keep-alive", "upgrade", "trailer",
}

# Paths (relative to /v1/) where a `model` field is always meaningful, so the
# gateway fills it in when a client leaves it out.
INFERENCE_PATHS = {
    "chat/completions", "completions", "responses", "messages",
    "messages/count_tokens",
}
# Model names the Anthropic clients send before they are told otherwise.
ANTHROPIC_FAMILY_RE = re.compile(
    r"^(claude[-.].*|sonnet|opus|haiku|fable)$", re.IGNORECASE
)

LOG_BUFFER: Deque[Dict[str, Any]] = deque(maxlen=500)
_LOG_SEQ = [0]
_LOG_LOCK = threading.Lock()
# A windowed build (pythonw.exe, PyInstaller --windowed) has no console at all:
# `sys.stdout` is None. Everything that writes output has to cope with that.
HAS_CONSOLE = sys.stdout is not None
_LOG_FILE: Optional[Path] = None


def use_log_file(path: Path) -> None:
    """Mirror the log to a file. Used when there is no console to print to."""
    global _LOG_FILE
    _LOG_FILE = path


def log(msg: str) -> None:
    line = f"[gateway {time.strftime('%H:%M:%S')}] {msg}"
    with _LOG_LOCK:
        _LOG_SEQ[0] += 1
        LOG_BUFFER.append({"seq": _LOG_SEQ[0], "at": time.time(), "text": line})
        target = _LOG_FILE
    if HAS_CONSOLE:
        print(line, flush=True)
    if target is not None:
        try:
            with target.open("a", encoding="utf-8") as handle:
                handle.write(f"{time.strftime('%Y-%m-%d')} {line}\n")
        except OSError:
            pass


def uvicorn_log_config() -> Optional[Dict[str, Any]]:
    """Uvicorn's default formatter calls `sys.stdout.isatty()`, which raises in a
    windowed build. Give it a stream-free configuration there instead."""
    if HAS_CONSOLE:
        return uvicorn.config.LOGGING_CONFIG
    return {
        "version": 1,
        "disable_existing_loggers": False,
        "handlers": {"null": {"class": "logging.NullHandler"}},
        "loggers": {
            "uvicorn": {"handlers": ["null"], "level": "WARNING", "propagate": False},
            "uvicorn.error": {"handlers": ["null"], "level": "WARNING", "propagate": False},
            "uvicorn.access": {"handlers": ["null"], "level": "WARNING", "propagate": False},
        },
    }


def log_entries_since(seq: int) -> List[Dict[str, Any]]:
    with _LOG_LOCK:
        return [entry for entry in LOG_BUFFER if entry["seq"] > seq]


# --------------------------------------------------------------------------- #
# Orphan registry — a crashed gateway must not leave models in VRAM
# --------------------------------------------------------------------------- #
class OrphanRegistry:
    """Records spawned backend pids so a later run can clean up after a crash."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._lock = threading.Lock()

    def _read(self) -> List[Dict[str, Any]]:
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        return [e for e in data if isinstance(e, dict)] if isinstance(data, list) else []

    def _write(self, entries: List[Dict[str, Any]]) -> None:
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps(entries, indent=2), encoding="utf-8")
        except OSError as exc:
            log(f"could not update {self.path.name}: {exc}")

    def add(self, alias: str, pid: int, port: int) -> None:
        with self._lock:
            entries = [e for e in self._read() if e.get("pid") != pid]
            entries.append({"alias": alias, "pid": pid, "port": port,
                            "started": time.time()})
            self._write(entries)

    def remove(self, pid: int) -> None:
        with self._lock:
            self._write([e for e in self._read() if e.get("pid") != pid])

    def sweep(self, known_ports: set) -> None:
        """Kill backends left behind by a previous run of this launcher."""
        with self._lock:
            entries = self._read()
            if not entries:
                return
            survivors: List[Dict[str, Any]] = []
            for entry in entries:
                pid, port = entry.get("pid"), entry.get("port")
                if not isinstance(pid, int) or port not in known_ports:
                    continue
                if not _pid_alive(pid):
                    continue
                if netinfo.port_is_free("127.0.0.1", int(port)):
                    continue  # the pid was recycled by an unrelated process
                log(f"cleaning up an orphaned backend from a previous run "
                    f"(pid {pid}, port {port})")
                _terminate_tree(pid)
                if _pid_alive(pid):
                    survivors.append(entry)
            self._write(survivors)


def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if IS_WINDOWS:
        try:
            result = subprocess.run(
                ["tasklist", "/FI", f"PID eq {pid}", "/NH"],
                capture_output=True, text=True, timeout=15,
                creationflags=CREATE_NO_WINDOW,
            )
        except (OSError, subprocess.SubprocessError):
            return False
        return str(pid) in result.stdout
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _terminate_tree(pid: int) -> None:
    try:
        if IS_WINDOWS:
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],
                           capture_output=True, timeout=60,
                           creationflags=CREATE_NO_WINDOW)
        else:
            try:
                os.killpg(os.getpgid(pid), signal.SIGTERM)
            except ProcessLookupError:
                return
            for _ in range(30):
                if not _pid_alive(pid):
                    return
                time.sleep(0.5)
            os.killpg(os.getpgid(pid), signal.SIGKILL)
    except (OSError, subprocess.SubprocessError) as exc:
        log(f"could not stop pid {pid}: {exc}")


# --------------------------------------------------------------------------- #
# Backend
# --------------------------------------------------------------------------- #
class Backend:
    """A single `unsloth run` process serving exactly one model."""

    def __init__(self, spec: Dict[str, Any], defaults: Dict[str, Any],
                 registry: Optional[OrphanRegistry] = None) -> None:
        self.alias: str = spec["alias"]
        self.repo: str = spec["repo"]
        self.quant: str = spec.get("quant") or ""
        self.host: str = spec.get("host") or "127.0.0.1"
        self.port: int = int(spec["port"])
        self.context_length: Optional[int] = spec.get("context_length")
        self.extra_args: List[str] = list(spec.get("extra_args") or [])
        self.disable_tools: bool = bool(spec.get("disable_tools", True))
        self.reasoning: str = str(spec.get("reasoning") or "")
        self.env_extra: Dict[str, str] = {
            str(k): str(v) for k, v in (spec.get("env") or {}).items()
        }

        self.unsloth_bin: str = defaults["unsloth_bin"]
        self.start_timeout: int = int(defaults["start_timeout"])

        # Configured key wins; otherwise scraped from `unsloth run` stdout.
        self.configured_key: Optional[str] = spec.get("api_key") or os.environ.get(
            f"UNSLOTH_API_KEY_{self.alias.upper().replace('-', '_')}"
        ) or os.environ.get("UNSLOTH_API_KEY")
        self.api_key: Optional[str] = self.configured_key

        self.served_id: str = spec.get("served_id") or self.model_spec

        self.proc: Optional[subprocess.Popen] = None
        self.log_tail: Deque[str] = deque(maxlen=LOG_TAIL)
        self._reader: Optional[threading.Thread] = None
        self._registry = registry
        self.last_used: float = 0.0
        self.last_error: str = ""
        self.external: bool = False  # attached to an already-running server

    # -- identity ---------------------------------------------------------- #
    def matches(self, spec: Dict[str, Any]) -> bool:
        """True when a reloaded entry describes the same running process."""
        return (
            spec.get("repo") == self.repo
            and (spec.get("quant") or "") == self.quant
            and int(spec.get("port") or 0) == self.port
            and (spec.get("host") or "127.0.0.1") == self.host
            and list(spec.get("extra_args") or []) == self.extra_args
            and bool(spec.get("disable_tools", True)) == self.disable_tools
            and str(spec.get("reasoning") or "") == self.reasoning
            and spec.get("context_length") == self.context_length
        )

    @property
    def model_spec(self) -> str:
        return f"{self.repo}:{self.quant}" if self.quant else self.repo

    @property
    def base_url(self) -> str:
        return f"http://{netinfo.format_host(self.host)}:{self.port}"

    def is_running(self) -> bool:
        if self.external:
            return True
        return self.proc is not None and self.proc.poll() is None

    # -- lifecycle --------------------------------------------------------- #
    def build_command(self) -> List[str]:
        cmd = [
            self.unsloth_bin, "run",
            "--model", self.model_spec,
            "-H", self.host,
            "-p", str(self.port),
        ]
        if self.context_length:
            cmd += ["-c", str(self.context_length)]
        if self.disable_tools and "--disable-tools" not in self.extra_args:
            cmd.append("--disable-tools")
        if self.reasoning and "--reasoning" not in self.extra_args:
            cmd += ["--reasoning", self.reasoning]
        # Never block on the interactive confirmation shown for non-local binds.
        if "-y" not in self.extra_args and "--yes" not in self.extra_args:
            cmd.append("--yes")
        cmd += self.extra_args
        return cmd

    def _pump_output(self, proc: subprocess.Popen) -> None:
        stream = proc.stdout
        if stream is None:
            return
        try:
            for line in stream:
                line = line.rstrip("\n")
                self.log_tail.append(line)
                if not self.api_key:
                    found = UNSLOTH_KEY_RE.search(line)
                    if found:
                        self.api_key = found.group(0)
                        log(f"{self.alias}: captured the backend API key")
        except (ValueError, OSError):
            return
        finally:
            try:
                stream.close()
            except OSError:
                pass

    def _probe(self, timeout: float = 5.0) -> Tuple[bool, Optional[List[str]]]:
        """`(reachable, served ids)`. Ids are None when the answer was rejected."""
        headers = {}
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
            headers["x-api-key"] = self.api_key
        try:
            response = httpx.get(f"{self.base_url}/v1/models", headers=headers,
                                 timeout=timeout)
        except httpx.HTTPError:
            return False, None
        if response.status_code in (401, 403):
            return True, None
        if response.status_code != 200:
            return True, None
        try:
            data = response.json().get("data") or []
        except ValueError:
            return True, None
        return True, [str(m.get("id")) for m in data if m.get("id")]

    def start_and_wait(self) -> None:
        """Blocking. Attach to an existing server or spawn one, then wait."""
        if self.is_running():
            return
        self.last_error = ""

        reachable, ids = self._probe(timeout=2.5)
        if reachable and ids:
            self.external = True
            self._adopt_served_id(ids)
            log(f"{self.alias}: attached to the server already on :{self.port} "
                f"(served id: {self.served_id})")
            return
        if reachable and not self.api_key:
            raise RuntimeError(
                f"{self.alias}: port {self.port} is already in use by a server that "
                f"needs an API key. Set `api_key` for this model in models.yaml, or "
                f"free the port."
            )

        if not self._binary_available():
            raise RuntimeError(
                f"the `{self.unsloth_bin}` command was not found. Install the Unsloth "
                f"CLI first — Windows: irm https://unsloth.ai/install.ps1 | iex, "
                f"macOS/Linux: curl -fsSL https://unsloth.ai/install.sh | sh"
            )

        cmd = self.build_command()
        log(f"{self.alias}: starting -> {' '.join(cmd)}")

        env = os.environ.copy()
        env.update(self.env_extra)

        popen_kwargs: Dict[str, Any] = dict(
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            text=True,
            bufsize=1,
            env=env,
        )
        if IS_WINDOWS:
            popen_kwargs["creationflags"] = CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW
        else:
            popen_kwargs["start_new_session"] = True

        try:
            self.proc = subprocess.Popen(cmd, **popen_kwargs)
        except OSError as exc:
            raise RuntimeError(f"{self.alias}: could not start `{self.unsloth_bin}`: {exc}") from exc

        if self._registry is not None:
            self._registry.add(self.alias, self.proc.pid, self.port)
        self._reader = threading.Thread(
            target=self._pump_output, args=(self.proc,),
            name=f"log-{self.alias}", daemon=True,
        )
        self._reader.start()

        deadline = time.time() + self.start_timeout
        saw_server = False
        while time.time() < deadline:
            if self.proc.poll() is not None:
                self._forget()
                raise RuntimeError(
                    f"{self.alias}: `unsloth run` exited with code "
                    f"{self.proc.returncode}\n" + self._tail()
                )
            reachable, ids = self._probe()
            saw_server = saw_server or reachable
            if ids:
                self._adopt_served_id(ids)
                log(f"{self.alias}: ready on :{self.port} (served id: {self.served_id})")
                return
            time.sleep(1.5)

        self.stop()
        detail = (
            "the backend answered but rejected the API key"
            if saw_server and not self.api_key else
            f"it did not answer on {self.base_url}"
        )
        raise RuntimeError(
            f"{self.alias}: not ready after {self.start_timeout}s — {detail}.\n"
            + self._tail()
        )

    def _binary_available(self) -> bool:
        candidate = Path(self.unsloth_bin)
        if candidate.is_file():
            return True
        from shutil import which
        if which(self.unsloth_bin):
            return True
        return IS_WINDOWS and any(
            which(self.unsloth_bin + suffix) for suffix in (".cmd", ".exe", ".bat")
        )

    def _tail(self, lines: int = 30) -> str:
        return "\n".join(list(self.log_tail)[-lines:])

    def _adopt_served_id(self, ids: List[str]) -> None:
        if not ids:
            return
        for candidate in (self.served_id, self.model_spec, self.repo,
                          self.repo.split("/")[-1]):
            if candidate in ids:
                self.served_id = candidate
                return
        self.served_id = ids[0]

    def _forget(self) -> None:
        if self.proc is not None and self._registry is not None:
            self._registry.remove(self.proc.pid)
        self.proc = None
        self.api_key = self.configured_key

    def stop(self) -> None:
        if self.external:
            log(f"{self.alias}: detaching (the server was not started here)")
            self.external = False
            return
        if self.proc is None:
            return
        if self.proc.poll() is not None:
            self._forget()
            return

        pid = self.proc.pid
        log(f"{self.alias}: stopping pid {pid}")
        _terminate_tree(pid)
        try:
            self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            log(f"{self.alias}: pid {pid} did not exit; killing")
            try:
                self.proc.kill()
                self.proc.wait(timeout=10)
            except (OSError, subprocess.TimeoutExpired):
                pass
        self._forget()

    def status(self) -> Dict[str, Any]:
        return {
            "alias": self.alias,
            "repo": self.repo,
            "quant": self.quant,
            "model_spec": self.model_spec,
            "served_id": self.served_id,
            "base_url": self.base_url,
            "port": self.port,
            "running": self.is_running(),
            "external": self.external,
            "pid": self.proc.pid if self.proc and self.proc.poll() is None else None,
            "have_api_key": bool(self.api_key),
            "context_length": self.context_length,
            "last_used": self.last_used or None,
            "last_error": self.last_error,
        }


# --------------------------------------------------------------------------- #
# Fleet
# --------------------------------------------------------------------------- #
class Fleet:
    """Owns every backend process and the configuration behind them."""

    def __init__(self, cfg: Dict[str, Any], config_path: Path) -> None:
        self.config_path = config_path
        self.registry = OrphanRegistry(config_path.parent / ".padra-backends.json")
        self.backends: Dict[str, Backend] = {}
        self.lock = asyncio.Lock()
        self._unknown_models: set = set()
        self.cfg: Dict[str, Any] = {}
        self.apply(cfg)
        self.registry.sweep({b.port for b in self.backends.values()})

    # -- configuration ------------------------------------------------------ #
    def apply(self, cfg: Dict[str, Any]) -> List[str]:
        """Adopt a validated config. Returns aliases whose process was stopped."""
        gateway = cfg["gateway"]
        defaults = {
            "unsloth_bin": os.environ.get("UNSLOTH_BIN", gateway["unsloth_bin"]),
            "start_timeout": gateway["start_timeout"],
        }

        stopped: List[str] = []
        rebuilt: Dict[str, Backend] = {}
        for spec in cfg["models"]:
            existing = self.backends.get(spec["alias"])
            if existing is not None and existing.matches(spec):
                existing.start_timeout = int(defaults["start_timeout"])
                existing.unsloth_bin = defaults["unsloth_bin"]
                rebuilt[spec["alias"]] = existing
                continue
            if existing is not None and existing.is_running():
                existing.stop()
                stopped.append(existing.alias)
            rebuilt[spec["alias"]] = Backend(spec, defaults, self.registry)

        for alias, backend in self.backends.items():
            if alias not in rebuilt and backend.is_running():
                backend.stop()
                stopped.append(alias)

        self.backends = rebuilt
        self.cfg = cfg
        self._unknown_models.clear()
        return stopped

    @property
    def gateway_cfg(self) -> Dict[str, Any]:
        return self.cfg["gateway"]

    @property
    def mode(self) -> str:
        return str(self.gateway_cfg["mode"])

    @property
    def default_alias(self) -> str:
        return str(self.gateway_cfg.get("default_model") or "")

    @property
    def api_key(self) -> str:
        return str(self.gateway_cfg.get("api_key") or "")

    def persist(self) -> None:
        fleetfile.save(self.config_path, self.cfg)

    def reload(self) -> List[str]:
        cfg = fleetfile.load(self.config_path)
        cfg, changed = fleetfile.ensure_api_key(cfg)
        stopped = self.apply(cfg)
        if changed:
            self.persist()
        log(f"configuration reloaded from {self.config_path.name}")
        return stopped

    async def set_mode(self, mode: str) -> str:
        mode = str(mode).lower()
        if mode not in ("exclusive", "concurrent"):
            raise HTTPException(
                status_code=400,
                detail={"error": {"message": "mode must be `exclusive` or `concurrent`",
                                  "type": "invalid_request_error"}},
            )
        self.gateway_cfg["mode"] = mode
        if mode == "exclusive":
            async with self.lock:
                running = sorted(
                    (b for b in self.backends.values() if b.is_running()),
                    key=lambda b: b.last_used, reverse=True,
                )
                for extra in running[1:]:
                    await asyncio.to_thread(extra.stop)
        await asyncio.to_thread(self.persist)
        log(f"mode set to {mode}")
        return mode

    async def set_default(self, alias: str) -> str:
        backend = self.resolve(alias, fallback=False)
        fleetfile.set_default_model(self.cfg, backend.alias)
        await asyncio.to_thread(self.persist)
        log(f"default model set to {backend.alias}")
        return backend.alias

    # -- resolution -------------------------------------------------------- #
    def resolve(self, requested: Optional[str], *, fallback: bool = True) -> Backend:
        if not self.backends:
            raise HTTPException(
                status_code=503,
                detail={"error": {
                    "message": "No models are configured. Add one in the control "
                               "panel or in models.yaml.",
                    "type": "no_models_configured",
                }},
            )
        if not requested:
            return self.default_backend()

        name = str(requested).strip()
        if name in self.backends:
            return self.backends[name]

        lowered = name.lower()
        for backend in self.backends.values():
            candidates = {
                backend.alias.lower(), backend.served_id.lower(), backend.repo.lower(),
                backend.model_spec.lower(), backend.repo.split("/")[-1].lower(),
            }
            if lowered in candidates:
                return backend

        # Codex and Qwen sometimes prefix with a provider, e.g. "openai:alias".
        if ":" in name:
            tail = name.rsplit(":", 1)[-1]
            if tail in self.backends:
                return self.backends[tail]
            for backend in self.backends.values():
                if backend.model_spec.lower() == lowered:
                    return backend

        allow = fallback and (
            str(self.gateway_cfg.get("unknown_model", "default")).lower() == "default"
            or bool(ANTHROPIC_FAMILY_RE.match(name))
        )
        if allow:
            backend = self.default_backend()
            if name not in self._unknown_models:
                self._unknown_models.add(name)
                log(f"model `{name}` is not in the fleet — serving it with "
                    f"`{backend.alias}`")
            return backend

        raise HTTPException(
            status_code=404,
            detail={
                "error": {
                    "message": f"Unknown model `{requested}`.",
                    "type": "invalid_request_error",
                    "param": "model",
                    "available": sorted(self.backends),
                }
            },
        )

    def default_backend(self) -> Backend:
        alias = self.default_alias
        if alias in self.backends:
            return self.backends[alias]
        return next(iter(self.backends.values()))

    # -- lifecycle --------------------------------------------------------- #
    async def ensure(self, backend: Backend) -> Backend:
        if backend.is_running():
            backend.last_used = time.time()
            return backend

        async with self.lock:
            if backend.is_running():
                backend.last_used = time.time()
                return backend

            if self.mode == "exclusive":
                for other in self.backends.values():
                    if other is not backend and other.is_running():
                        await asyncio.to_thread(other.stop)

            try:
                await asyncio.to_thread(backend.start_and_wait)
            except Exception as exc:  # surface load failures to the client
                backend.last_error = str(exc)
                log(f"{backend.alias}: {exc}")
                raise HTTPException(
                    status_code=503,
                    detail={"error": {"message": str(exc),
                                      "type": "backend_start_error"}},
                ) from exc

            backend.last_error = ""
            backend.last_used = time.time()
            return backend

    async def unload(self, backend: Backend) -> None:
        async with self.lock:
            await asyncio.to_thread(backend.stop)

    async def shutdown(self) -> None:
        async with self.lock:
            for backend in self.backends.values():
                if backend.is_running():
                    await asyncio.to_thread(backend.stop)

    async def reap_idle(self, idle_timeout: int) -> None:
        """Free VRAM held by models nobody has used for `idle_timeout` seconds."""
        if idle_timeout <= 0:
            return
        cutoff = time.time() - idle_timeout
        for backend in list(self.backends.values()):
            if (backend.is_running() and not backend.external
                    and backend.last_used and backend.last_used < cutoff):
                log(f"{backend.alias}: idle for {idle_timeout}s — unloading")
                await self.unload(backend)


# --------------------------------------------------------------------------- #
# App
# --------------------------------------------------------------------------- #
def build_app(cfg: Dict[str, Any], config_path: Path) -> FastAPI:
    fleet = Fleet(cfg, config_path)
    state: Dict[str, Any] = {"stop": None}

    def gateway_key() -> str:
        return os.environ.get("GATEWAY_API_KEY") or fleet.api_key

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        gw = fleet.gateway_cfg
        state["client"] = httpx.AsyncClient(
            timeout=httpx.Timeout(float(gw["request_timeout"]), connect=15.0, pool=None),
            limits=httpx.Limits(max_connections=64, max_keepalive_connections=16),
            follow_redirects=False,
        )
        log(f"mode={fleet.mode} default={fleet.default_alias or '—'} "
            f"models={', '.join(sorted(fleet.backends)) or '—'}")
        if not gateway_key():
            log("WARNING: no gateway API key is set — LAN clients will be refused")
        if gw["allow_lan"] or gw["host"] == "0.0.0.0":
            log(f"LAN endpoint: {netinfo.preferred_lan_url(gw['host'], int(gw['port']))}")

        reaper = asyncio.create_task(_idle_loop(fleet))
        if gw["preload"] and fleet.backends:
            try:
                await fleet.ensure(fleet.default_backend())
            except HTTPException as exc:
                log(f"preload failed: {exc.detail}")
        try:
            yield
        finally:
            reaper.cancel()
            try:
                await reaper
            except asyncio.CancelledError:
                pass
            await state["client"].aclose()
            await fleet.shutdown()

    app = FastAPI(title="padra-platform-launcher", lifespan=lifespan,
                  docs_url=None, redoc_url=None)
    app.state.runtime = state
    app.add_middleware(
        CORSMiddleware,
        allow_origin_regex=".*",
        allow_credentials=False,
        allow_methods=["*"],
        allow_headers=["*"],
        expose_headers=["X-Unsloth-Backend", "X-Unsloth-Served-Model"],
    )

    from launcher import webui  # local import keeps this module importable alone
    webui.register(app, fleet=fleet, config_path=config_path,
                   gateway_key=gateway_key, request_shutdown=lambda: _request_stop(state))

    # -- auth -------------------------------------------------------------- #
    def presented_token(request: Request) -> Optional[str]:
        auth = request.headers.get("authorization", "")
        if auth.lower().startswith("bearer "):
            return auth.split(" ", 1)[1].strip()
        header_key = request.headers.get("x-api-key")
        return header_key.strip() if header_key else None

    def check_auth(request: Request) -> None:
        key = gateway_key()
        client = request.client.host if request.client else None
        if not key:
            if client in LOOPBACK_HOSTS:
                return
            raise HTTPException(
                status_code=401,
                detail={"error": {
                    "message": "This gateway has no API key configured, so only "
                               "local clients are accepted.",
                    "type": "authentication_error"}},
            )
        if presented_token(request) != key:
            raise HTTPException(
                status_code=401,
                detail={"error": {"message": "Invalid API key.",
                                  "type": "authentication_error"}},
            )

    app.state.check_auth = check_auth

    # -- probes ------------------------------------------------------------- #
    @app.head("/")
    async def head_root() -> Response:
        return Response(status_code=200)

    @app.get("/healthz")
    async def healthz() -> JSONResponse:
        return JSONResponse({
            "status": "ok",
            "mode": fleet.mode,
            "default_model": fleet.default_alias,
            "models": sorted(fleet.backends),
            "loaded": [b.alias for b in fleet.backends.values() if b.is_running()],
        })

    # -- admin -------------------------------------------------------------- #
    @app.get("/admin/status")
    async def admin_status(request: Request) -> JSONResponse:
        check_auth(request)
        return JSONResponse({
            "mode": fleet.mode,
            "default_model": fleet.default_alias,
            "backends": [b.status() for b in fleet.backends.values()],
        })

    @app.post("/admin/load/{alias}")
    async def admin_load(alias: str, request: Request) -> JSONResponse:
        check_auth(request)
        backend = fleet.resolve(alias, fallback=False)
        await fleet.ensure(backend)
        return JSONResponse(backend.status())

    @app.post("/admin/unload/{alias}")
    async def admin_unload(alias: str, request: Request) -> JSONResponse:
        check_auth(request)
        backend = fleet.resolve(alias, fallback=False)
        await fleet.unload(backend)
        return JSONResponse(backend.status())

    @app.post("/admin/mode/{mode}")
    async def admin_mode(mode: str, request: Request) -> JSONResponse:
        check_auth(request)
        return JSONResponse({"mode": await fleet.set_mode(mode)})

    @app.post("/admin/reload")
    async def admin_reload(request: Request) -> JSONResponse:
        check_auth(request)
        try:
            async with fleet.lock:
                stopped = await asyncio.to_thread(fleet.reload)
        except FleetFileError as exc:
            raise HTTPException(status_code=400,
                                detail={"error": {"message": str(exc)}}) from exc
        return JSONResponse({"ok": True, "stopped": stopped,
                             "models": sorted(fleet.backends)})

    @app.post("/admin/shutdown")
    async def admin_shutdown(request: Request) -> JSONResponse:
        check_auth(request)
        _request_stop(state)
        return JSONResponse({"ok": True})

    @app.get("/v1/models")
    async def list_models(request: Request) -> JSONResponse:
        check_auth(request)
        now = int(time.time())
        return JSONResponse({
            "object": "list",
            "data": [
                {
                    "id": b.alias,
                    "object": "model",
                    "created": now,
                    "owned_by": "unsloth",
                    "root": b.model_spec,
                    "display_name": b.alias,
                    "type": "model",
                    "context_length": b.context_length,
                    "max_input_tokens": b.context_length,
                    "loaded": b.is_running(),
                }
                for b in fleet.backends.values()
            ],
        })

    # -- proxy ------------------------------------------------------------- #
    @app.api_route("/v1/{path:path}",
                   methods=["GET", "POST", "PUT", "PATCH", "DELETE"])
    async def proxy(path: str, request: Request):
        check_auth(request)

        raw = await request.body()
        body: Optional[Dict[str, Any]] = None
        if raw:
            try:
                parsed = json.loads(raw)
                if isinstance(parsed, dict):
                    body = parsed
            except ValueError:
                body = None

        route = path.strip("/")
        requested_model = body.get("model") if body else None
        backend = fleet.resolve(requested_model)
        await fleet.ensure(backend)

        if body is not None and ("model" in body or route in INFERENCE_PATHS):
            body["model"] = backend.served_id
            raw = json.dumps(body).encode("utf-8")

        headers = {
            k: v for k, v in request.headers.items()
            if k.lower() not in HOP_REQUEST_HEADERS
        }
        if backend.api_key:
            headers["Authorization"] = f"Bearer {backend.api_key}"
            headers["x-api-key"] = backend.api_key
        if raw:
            headers["Content-Type"] = request.headers.get("content-type",
                                                          "application/json")
            headers["Content-Length"] = str(len(raw))

        client: httpx.AsyncClient = state["client"]
        upstream_request = client.build_request(
            request.method,
            f"{backend.base_url}/v1/{path}",
            content=raw or None,
            headers=headers,
            params=str(request.url.query) or None,
        )

        try:
            upstream = await client.send(upstream_request, stream=True)
        except httpx.HTTPError as exc:
            raise HTTPException(
                status_code=502,
                detail={"error": {
                    "message": f"Backend `{backend.alias}` is unreachable at "
                               f"{backend.base_url}: {exc}",
                    "type": "upstream_error",
                }},
            ) from exc

        out_headers = {
            k: v for k, v in upstream.headers.items()
            if k.lower() not in HOP_RESPONSE_HEADERS
        }
        out_headers["X-Unsloth-Backend"] = backend.alias
        out_headers["X-Unsloth-Served-Model"] = backend.served_id

        return StreamingResponse(
            upstream.aiter_raw(),
            status_code=upstream.status_code,
            headers=out_headers,
            media_type=upstream.headers.get("content-type"),
            background=BackgroundTask(upstream.aclose),
        )

    return app


async def _idle_loop(fleet: Fleet) -> None:
    while True:
        await asyncio.sleep(30)
        try:
            await fleet.reap_idle(int(fleet.gateway_cfg.get("idle_timeout") or 0))
        except asyncio.CancelledError:
            raise
        except Exception as exc:  # noqa: BLE001 - the reaper must never die
            log(f"idle sweep failed: {exc}")


def _request_stop(state: Dict[str, Any]) -> None:
    """Ask the server to exit. Only `main()` installs a stopper, so an embedded
    app (tests, another host process) is never signalled out from under its
    owner."""
    log("shutdown requested")
    server = state.get("stop")
    if callable(server):
        server()
        return
    log("no server owns this app — the shutdown request was ignored")


# --------------------------------------------------------------------------- #
# Entrypoint
# --------------------------------------------------------------------------- #
def _fatal(message: str) -> None:
    """Report a start-up failure on whatever channel this build actually has."""
    log(message)
    if sys.stderr is not None:
        print(f"[gateway] {message}", file=sys.stderr, flush=True)


def load_config(path: Path) -> Dict[str, Any]:
    cfg = fleetfile.load(path)
    cfg, changed = fleetfile.ensure_api_key(cfg)
    if changed:
        log(f"a new gateway API key was generated and saved to {path.name}")
    if not cfg["models"] and cfg["gateway"].get("auto_import", True):
        changed = auto_import_models(cfg) or changed
    if changed:
        fleetfile.save(path, cfg)
    return cfg


def auto_import_models(cfg: Dict[str, Any], limit: int = 12) -> bool:
    """Seed an empty fleet from the GGUF models already on this machine."""
    from launcher import discovery

    try:
        found = discovery.scan_local_models(
            cfg["gateway"].get("extra_model_paths") or []
        )
    except OSError as exc:
        log(f"could not scan for downloaded models: {exc}")
        return False
    if not found:
        log("no downloaded models were found — add one from the control panel")
        return False

    added = 0
    for model in found[:limit]:
        try:
            entry = fleetfile.add_model(
                cfg, repo=model.repo, quant=model.quant,
                context_length=model.context_length,
            )
        except FleetFileError as exc:
            log(f"skipped {model.spec}: {exc}")
            continue
        added += 1
        log(f"imported `{entry['alias']}` from {model.source} ({model.spec})")
    return added > 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Padra Platform Launcher gateway")
    parser.add_argument("--config", default="models.yaml", type=Path)
    parser.add_argument("--host", default=None)
    parser.add_argument("--port", default=None, type=int)
    parser.add_argument("--mode", default=None, choices=["exclusive", "concurrent"])
    parser.add_argument("--preload", action="store_true")
    parser.add_argument("--lan", action="store_true",
                        help="serve every machine on the local network")
    args = parser.parse_args(argv)

    config_path = args.config.resolve()
    if not HAS_CONSOLE:
        use_log_file(config_path.parent / "padra-launcher.log")
    try:
        cfg = load_config(config_path)
    except FleetFileError as exc:
        _fatal(f"{exc}")
        return 2

    gw = cfg["gateway"]
    if args.mode:
        gw["mode"] = args.mode
    if args.preload:
        gw["preload"] = True
    if args.lan:
        gw["host"] = "0.0.0.0"
        gw["allow_lan"] = True
    if args.host:
        gw["host"] = args.host
        gw["allow_lan"] = gw["allow_lan"] or args.host == "0.0.0.0"
    if args.port:
        gw["port"] = int(args.port)

    host, port = str(gw["host"]), int(gw["port"])
    if not netinfo.port_is_free(host, port):
        _fatal(f"port {port} is already in use on {host}")
        return 3

    app = build_app(cfg, config_path)
    config = uvicorn.Config(app, host=host, port=port, log_level="warning",
                            access_log=False, timeout_graceful_shutdown=20,
                            log_config=uvicorn_log_config())
    server = uvicorn.Server(config)
    app.state.runtime["stop"] = lambda: setattr(server, "should_exit", True)

    log(f"listening on http://{netinfo.format_host(host)}:{port}/v1")
    server.run()
    return 0
