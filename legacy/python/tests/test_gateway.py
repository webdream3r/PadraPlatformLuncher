"""Gateway routing, authentication and process bookkeeping."""

from __future__ import annotations

from pathlib import Path
from typing import Any, Dict, Iterator

import pytest
from fastapi.testclient import TestClient

from launcher import fleetfile, gateway
from tests.conftest import StubBackend

KEY = "sk-padra-test-key"


def write_config(tmp_path: Path, stub: StubBackend, **gateway_overrides: Any) -> Path:
    cfg = fleetfile.normalise({})
    cfg["gateway"]["api_key"] = KEY
    cfg["gateway"]["auto_import"] = False
    cfg["gateway"].update(gateway_overrides)
    fleetfile.add_model(cfg, repo="unsloth/Demo-GGUF", quant="Q4_K_M",
                        port=stub.port, context_length=4096)
    fleetfile.add_model(cfg, repo="unsloth/Other-GGUF", quant="Q8_0",
                        port=stub.port + 1)
    config_path = tmp_path / "models.yaml"
    fleetfile.save(config_path, cfg)
    return config_path


def make_client(config_path: Path, host: str = "127.0.0.1") -> TestClient:
    cfg = fleetfile.load(config_path)
    app = gateway.build_app(cfg, config_path)
    return TestClient(app, client=(host, 51234))


@pytest.fixture()
def client(tmp_path: Path, home: Path, stub_backend: StubBackend) -> Iterator[TestClient]:
    config_path = write_config(tmp_path, stub_backend)
    with make_client(config_path) as test_client:
        yield test_client


def auth() -> Dict[str, str]:
    return {"Authorization": f"Bearer {KEY}"}


# --------------------------------------------------------------------------- #
# Probes
# --------------------------------------------------------------------------- #
def test_healthz_needs_no_key(client: TestClient) -> None:
    response = client.get("/healthz")
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["default_model"] == "demo"
    assert sorted(body["models"]) == ["demo", "other"]


def test_head_root_answers_the_claude_code_probe(client: TestClient) -> None:
    assert client.head("/").status_code == 200


def test_models_endpoint_requires_the_key(client: TestClient) -> None:
    assert client.get("/v1/models").status_code == 401
    response = client.get("/v1/models", headers=auth())
    assert response.status_code == 200
    ids = [item["id"] for item in response.json()["data"]]
    assert ids == ["demo", "other"]
    assert response.json()["data"][0]["context_length"] == 4096


def test_a_wrong_key_is_refused(client: TestClient) -> None:
    response = client.get("/v1/models", headers={"Authorization": "Bearer nope"})
    assert response.status_code == 401
    assert response.json()["detail"]["error"]["type"] == "authentication_error"


def test_x_api_key_header_is_accepted(client: TestClient) -> None:
    assert client.get("/v1/models", headers={"x-api-key": KEY}).status_code == 200


# --------------------------------------------------------------------------- #
# Proxying
# --------------------------------------------------------------------------- #
def test_chat_completions_are_proxied_and_the_model_is_rewritten(
        client: TestClient, stub_backend: StubBackend) -> None:
    response = client.post("/v1/chat/completions", headers=auth(),
                           json={"model": "demo", "messages": [{"role": "user",
                                                                "content": "hi"}]})
    assert response.status_code == 200
    assert response.headers["X-Unsloth-Backend"] == "demo"
    assert b"[DONE]" in response.content

    received = stub_backend.received[-1]
    assert received["path"] == "chat/completions"
    assert received["body"]["model"] == stub_backend.served_id
    assert received["body"]["messages"][0]["content"] == "hi"


def test_anthropic_messages_are_proxied(client: TestClient,
                                        stub_backend: StubBackend) -> None:
    response = client.post("/v1/messages", headers={**auth(),
                                                    "anthropic-version": "2023-06-01"},
                           json={"model": "demo", "max_tokens": 16,
                                 "messages": [{"role": "user", "content": "."}]})
    assert response.status_code == 200
    received = stub_backend.received[-1]
    assert received["path"] == "messages"
    assert received["headers"]["anthropic-version"] == "2023-06-01"


def test_count_tokens_gets_a_model_injected(client: TestClient,
                                            stub_backend: StubBackend) -> None:
    response = client.post("/v1/messages/count_tokens", headers=auth(),
                           json={"messages": [{"role": "user", "content": "."}]})
    assert response.status_code == 200
    assert stub_backend.received[-1]["body"]["model"] == stub_backend.served_id


def test_query_parameters_survive_the_proxy(client: TestClient,
                                            stub_backend: StubBackend) -> None:
    client.post("/v1/responses?beta=true&trace=1", headers=auth(), json={"model": "demo"})
    assert stub_backend.received[-1]["query"] == "beta=true&trace=1"


def test_unknown_models_fall_back_to_the_default(client: TestClient,
                                                 stub_backend: StubBackend) -> None:
    response = client.post("/v1/chat/completions", headers=auth(),
                           json={"model": "claude-sonnet-4-6", "messages": []})
    assert response.status_code == 200
    assert response.headers["X-Unsloth-Backend"] == "demo"


def test_unknown_models_can_be_rejected_instead(tmp_path: Path, home: Path,
                                                stub_backend: StubBackend) -> None:
    config_path = write_config(tmp_path, stub_backend, unknown_model="error")
    with make_client(config_path) as client:
        response = client.post("/v1/chat/completions", headers=auth(),
                               json={"model": "ghost", "messages": []})
        assert response.status_code == 404
        assert response.json()["detail"]["error"]["available"] == ["demo", "other"]


def test_a_dead_backend_is_reported_as_unavailable(client: TestClient) -> None:
    response = client.post("/v1/chat/completions", headers=auth(),
                           json={"model": "other", "messages": []})
    assert response.status_code == 503
    assert response.json()["detail"]["error"]["type"] == "backend_start_error"


def test_repo_and_spec_names_resolve_to_the_alias(client: TestClient) -> None:
    for name in ("unsloth/Demo-GGUF", "unsloth/Demo-GGUF:Q4_K_M", "Demo-GGUF",
                 "openai:demo"):
        response = client.post("/v1/chat/completions", headers=auth(),
                               json={"model": name, "messages": []})
        assert response.headers["X-Unsloth-Backend"] == "demo", name


# --------------------------------------------------------------------------- #
# Admin
# --------------------------------------------------------------------------- #
def test_load_unload_and_status(client: TestClient) -> None:
    loaded = client.post("/admin/load/demo", headers=auth())
    assert loaded.status_code == 200
    assert loaded.json()["running"] is True
    assert loaded.json()["external"] is True

    status = client.get("/admin/status", headers=auth()).json()
    assert status["mode"] == "exclusive"
    assert [b["alias"] for b in status["backends"]] == ["demo", "other"]

    unloaded = client.post("/admin/unload/demo", headers=auth())
    assert unloaded.json()["running"] is False


def test_mode_switch_is_persisted(client: TestClient, tmp_path: Path) -> None:
    assert client.post("/admin/mode/concurrent", headers=auth()).json()["mode"] == "concurrent"
    assert fleetfile.load(tmp_path / "models.yaml")["gateway"]["mode"] == "concurrent"
    assert client.post("/admin/mode/sideways", headers=auth()).status_code == 400


def test_reload_picks_up_an_edited_file(client: TestClient, tmp_path: Path) -> None:
    config_path = tmp_path / "models.yaml"
    cfg = fleetfile.load(config_path)
    fleetfile.remove_model(cfg, "other")
    fleetfile.save(config_path, cfg)

    response = client.post("/admin/reload", headers=auth())
    assert response.status_code == 200
    assert response.json()["models"] == ["demo"]
    assert client.get("/healthz").json()["models"] == ["demo"]


def test_a_broken_file_does_not_break_a_running_gateway(client: TestClient,
                                                        tmp_path: Path) -> None:
    (tmp_path / "models.yaml").write_text("gateway: [1,2,3]\n", encoding="utf-8")
    response = client.post("/admin/reload", headers=auth())
    assert response.status_code == 400
    assert client.get("/healthz").json()["models"] == ["demo", "other"]


# --------------------------------------------------------------------------- #
# Remote callers
# --------------------------------------------------------------------------- #
def test_remote_callers_must_authenticate(tmp_path: Path, home: Path,
                                          stub_backend: StubBackend) -> None:
    config_path = write_config(tmp_path, stub_backend)
    with make_client(config_path, host="10.1.2.3") as remote:
        assert remote.get("/api/bootstrap").status_code == 401
        assert remote.get("/api/bootstrap", headers=auth()).status_code == 200


def test_without_a_key_remote_callers_are_refused(tmp_path: Path, home: Path,
                                                  stub_backend: StubBackend) -> None:
    cfg = fleetfile.normalise({})
    cfg["gateway"]["auto_import"] = False
    fleetfile.add_model(cfg, repo="unsloth/Demo-GGUF", quant="Q4_K_M", port=stub_backend.port)
    config_path = tmp_path / "models.yaml"
    fleetfile.save(config_path, cfg)

    with make_client(config_path, host="10.1.2.3") as remote:
        assert remote.get("/v1/models").status_code == 401
        assert remote.get("/api/bootstrap").status_code == 403
    with make_client(config_path) as local:
        assert local.get("/v1/models").status_code == 200


# --------------------------------------------------------------------------- #
# Backend bookkeeping
# --------------------------------------------------------------------------- #
def test_backend_command_covers_the_documented_flags() -> None:
    backend = gateway.Backend(
        {"alias": "a", "repo": "unsloth/X-GGUF", "quant": "Q4_K_M", "port": 8801,
         "context_length": 4096, "reasoning": "off", "disable_tools": True,
         "extra_args": ["--temp", "0.6"]},
        {"unsloth_bin": "unsloth", "start_timeout": 5},
    )
    command = backend.build_command()
    assert command[:3] == ["unsloth", "run", "--model"]
    assert command[3] == "unsloth/X-GGUF:Q4_K_M"
    assert "-H" in command and "-p" in command and "-c" in command
    assert "--disable-tools" in command
    assert command[command.index("--reasoning") + 1] == "off"
    assert "--yes" in command
    assert command[-2:] == ["--temp", "0.6"]


def test_flags_are_not_duplicated_when_given_explicitly() -> None:
    backend = gateway.Backend(
        {"alias": "a", "repo": "x", "port": 8801,
         "extra_args": ["--disable-tools", "--yes"], "disable_tools": True},
        {"unsloth_bin": "unsloth", "start_timeout": 5},
    )
    command = backend.build_command()
    assert command.count("--disable-tools") == 1
    assert command.count("--yes") == 1


def test_missing_unsloth_binary_produces_a_useful_error(tmp_path: Path) -> None:
    backend = gateway.Backend(
        {"alias": "a", "repo": "x", "port": 59999},
        {"unsloth_bin": "definitely-not-installed-binary", "start_timeout": 1},
    )
    with pytest.raises(RuntimeError, match="Install the Unsloth CLI"):
        backend.start_and_wait()


def test_orphan_registry_round_trip(tmp_path: Path) -> None:
    registry = gateway.OrphanRegistry(tmp_path / "backends.json")
    registry.add("demo", 4242, 8801)
    assert registry._read()[0]["pid"] == 4242
    registry.remove(4242)
    assert registry._read() == []


def test_auto_import_seeds_an_empty_fleet(tmp_path: Path,
                                          monkeypatch: pytest.MonkeyPatch) -> None:
    from launcher import discovery

    found = [
        discovery.LocalModel(repo="unsloth/A-GGUF", quant="Q4_K_M", source="huggingface",
                             size_bytes=10, file_count=1, modified=0.0, path="/tmp",
                             context_length=8192),
        discovery.LocalModel(repo="unsloth/B-GGUF", quant="Q8_0", source="lmstudio",
                             size_bytes=20, file_count=1, modified=0.0, path="/tmp"),
    ]
    monkeypatch.setattr(discovery, "scan_local_models", lambda *a, **k: found)

    cfg = fleetfile.normalise({})
    assert gateway.auto_import_models(cfg) is True
    assert [m["alias"] for m in cfg["models"]] == ["a", "b"]
    assert cfg["models"][0]["context_length"] == 8192
    assert cfg["gateway"]["default_model"] == "a"


# --------------------------------------------------------------------------- #
# Windowless builds (pythonw.exe, PyInstaller --windowed) have no console
# --------------------------------------------------------------------------- #
def test_log_config_avoids_stdout_when_there_is_no_console(
        monkeypatch: pytest.MonkeyPatch) -> None:
    import uvicorn
    from fastapi import FastAPI

    monkeypatch.setattr(gateway, "HAS_CONSOLE", False)
    monkeypatch.setattr("sys.stdout", None)
    config = gateway.uvicorn_log_config()
    assert config is not None and "null" in config["handlers"]
    # Building the config is what crashed before: it calls sys.stdout.isatty().
    uvicorn.Config(FastAPI(), host="127.0.0.1", port=9, log_config=config)


def test_log_falls_back_to_a_file_without_a_console(
        tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    target = tmp_path / "padra-launcher.log"
    monkeypatch.setattr(gateway, "HAS_CONSOLE", False)
    monkeypatch.setattr(gateway, "_LOG_FILE", target)
    gateway.log("windowless line")
    assert "windowless line" in target.read_text(encoding="utf-8")
