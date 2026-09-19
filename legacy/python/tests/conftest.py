"""Shared fixtures: an isolated home directory and a stub Unsloth backend."""

from __future__ import annotations

import socket
import sys
import threading
import time
from pathlib import Path
from typing import Any, Dict, Iterator

import pytest
import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


@pytest.fixture()
def home(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    """Redirect every per-user path used by the launcher into tmp_path."""
    fake_home = tmp_path / "home"
    fake_home.mkdir()
    monkeypatch.setenv("HOME", str(fake_home))
    monkeypatch.setenv("USERPROFILE", str(fake_home))
    monkeypatch.setenv("APPDATA", str(fake_home / "AppData" / "Roaming"))
    monkeypatch.setenv("XDG_CONFIG_HOME", str(fake_home / ".config"))
    monkeypatch.setenv("XDG_CACHE_HOME", str(fake_home / ".cache"))
    monkeypatch.delenv("CODEX_HOME", raising=False)
    monkeypatch.delenv("QWEN_HOME", raising=False)
    monkeypatch.delenv("HF_HUB_CACHE", raising=False)
    monkeypatch.delenv("HF_HOME", raising=False)
    monkeypatch.delenv("GATEWAY_API_KEY", raising=False)
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: fake_home))
    return fake_home


class StubBackend:
    """A minimal `unsloth run` stand-in: /v1/models plus a streaming endpoint."""

    def __init__(self, served_id: str = "stub/model:Q4") -> None:
        self.port = free_port()
        self.served_id = served_id
        self.received: list = []
        self._server: uvicorn.Server | None = None
        self._thread: threading.Thread | None = None

    def _app(self) -> FastAPI:
        app = FastAPI()

        @app.get("/v1/models")
        async def models() -> JSONResponse:
            return JSONResponse({"data": [{"id": self.served_id}]})

        @app.post("/v1/{path:path}")
        async def inference(path: str, request: Request):
            payload: Dict[str, Any] = await request.json()
            self.received.append({
                "path": path,
                "body": payload,
                "headers": dict(request.headers),
                "query": str(request.url.query),
            })

            async def stream():
                yield b'data: {"delta":"hi"}\n\n'
                yield b"data: [DONE]\n\n"

            return StreamingResponse(stream(), media_type="text/event-stream")

        return app

    def start(self) -> "StubBackend":
        config = uvicorn.Config(self._app(), host="127.0.0.1", port=self.port,
                                log_level="error", access_log=False)
        self._server = uvicorn.Server(config)
        self._thread = threading.Thread(target=self._server.run, daemon=True)
        self._thread.start()
        deadline = time.time() + 20
        while time.time() < deadline:
            if getattr(self._server, "started", False):
                return self
            time.sleep(0.05)
        raise RuntimeError("the stub backend did not start")

    def stop(self) -> None:
        if self._server is not None:
            self._server.should_exit = True
        if self._thread is not None:
            self._thread.join(timeout=15)


@pytest.fixture()
def stub_backend() -> Iterator[StubBackend]:
    backend = StubBackend().start()
    try:
        yield backend
    finally:
        backend.stop()
