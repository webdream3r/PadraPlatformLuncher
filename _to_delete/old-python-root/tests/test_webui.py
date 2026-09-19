"""Control-panel API: settings, fleet editing, discovery, network and clients."""

from __future__ import annotations

import json
import zipfile
from io import BytesIO
from pathlib import Path
from typing import Any, Iterator

import pytest
from fastapi.testclient import TestClient

from launcher import discovery, fleetfile, gateway, gen_configs
from tests.conftest import StubBackend

KEY = "sk-padra-panel-key"


@pytest.fixture()
def panel(tmp_path: Path, home: Path, stub_backend: StubBackend,
          monkeypatch: pytest.MonkeyPatch) -> Iterator[TestClient]:
    monkeypatch.setattr(gen_configs, "vscode_user_dirs", lambda: [])
    cfg = fleetfile.normalise({})
    cfg["gateway"]["api_key"] = KEY
    cfg["gateway"]["auto_import"] = False
    fleetfile.add_model(cfg, repo="unsloth/Demo-GGUF", quant="Q4_K_M",
                        port=stub_backend.port, context_length=4096)
    config_path = tmp_path / "models.yaml"
    fleetfile.save(config_path, cfg)

    app = gateway.build_app(fleetfile.load(config_path), config_path)
    with TestClient(app, client=("127.0.0.1", 51234)) as client:
        client.config_path = config_path  # type: ignore[attr-defined]
        yield client


# --------------------------------------------------------------------------- #
# Shell and localization
# --------------------------------------------------------------------------- #
def test_index_and_assets_are_served(panel: TestClient) -> None:
    index = panel.get("/")
    assert index.status_code == 200
    assert "text/html" in index.headers["content-type"]
    assert panel.get("/ui/app.js").status_code == 200
    assert panel.get("/ui/styles.css").status_code == 200


def test_path_traversal_is_refused(panel: TestClient) -> None:
    assert panel.get("/ui/..%2f..%2fmodels.yaml").status_code == 404
    assert panel.get("/api/locales/..%2f..%2fmodels").status_code == 404


def test_bootstrap_exposes_everything_the_panel_needs(panel: TestClient) -> None:
    data = panel.get("/api/bootstrap").json()
    assert data["apiKey"] == KEY
    assert {c["code"] for c in data["cultures"]} == {"en-US", "fa-IR"}
    assert data["models"][0]["alias"] == "demo"
    assert data["models"][0]["isDefault"] is True
    assert data["network"]["shared"] is False
    assert set(data["installTargets"]) == set(gen_configs.TARGETS)
    assert data["agents"] == ["claude", "codex", "qwen"]


def test_locales_can_be_fetched(panel: TestClient) -> None:
    body = panel.get("/api/locales/fa-IR").json()
    assert body["_meta"]["dir"] == "rtl"
    assert panel.get("/api/locales/xx-YY").status_code == 404


def test_settings_round_trip_and_validate(panel: TestClient) -> None:
    assert panel.post("/api/settings", json={"culture": "fa-IR"}).status_code == 200
    assert panel.get("/api/settings").json()["settings"]["culture"] == "fa-IR"
    assert panel.post("/api/settings", json={"culture": "xx"}).status_code == 400
    assert panel.post("/api/settings",
                      json={"installTargets": ["codex", "nope"]}).status_code == 400

    panel.post("/api/settings", json={"installTargets": ["codex", "claude"],
                                      "projectFolder": "/tmp/project"})
    settings = panel.get("/api/settings").json()["settings"]
    assert settings["installTargets"] == ["codex", "claude"]
    assert settings["projectFolder"] == "/tmp/project"


def test_mode_change_through_settings(panel: TestClient) -> None:
    assert panel.post("/api/settings", json={"mode": "concurrent"}).json()["mode"] == "concurrent"


# --------------------------------------------------------------------------- #
# Fleet editing
# --------------------------------------------------------------------------- #
def test_add_activate_and_remove_a_model(panel: TestClient) -> None:
    added = panel.post("/api/models", json={"repo": "unsloth/Gemma-GGUF",
                                            "quant": "UD-Q4_K_XL",
                                            "contextLength": 131072})
    assert added.status_code == 200
    assert added.json()["model"]["alias"] == "gemma"
    assert [m["alias"] for m in added.json()["models"]] == ["demo", "gemma"]

    saved = fleetfile.load(panel.config_path)  # type: ignore[attr-defined]
    assert saved["models"][1]["repo"] == "unsloth/Gemma-GGUF"

    activated = panel.post("/api/models/gemma/activate")
    # The stub only serves `demo`'s port, so activating `gemma` reports the
    # start failure rather than pretending it worked.
    assert activated.status_code == 503
    assert fleetfile.load(panel.config_path)["gateway"]["default_model"] == "gemma"  # type: ignore[attr-defined]

    removed = panel.request("DELETE", "/api/models/gemma")
    assert removed.status_code == 200
    assert [m["alias"] for m in removed.json()["models"]] == ["demo"]
    assert fleetfile.load(panel.config_path)["gateway"]["default_model"] == "demo"  # type: ignore[attr-defined]


def test_duplicates_and_unknown_models_are_rejected(panel: TestClient) -> None:
    duplicate = panel.post("/api/models", json={"repo": "unsloth/Demo-GGUF",
                                                "quant": "Q4_K_M"})
    assert duplicate.status_code == 400
    assert "already in the fleet" in duplicate.json()["detail"]["error"]["message"]
    assert panel.request("DELETE", "/api/models/ghost").status_code == 404
    assert panel.post("/api/models/ghost/load").status_code == 404


def test_load_and_unload_through_the_panel(panel: TestClient) -> None:
    assert panel.post("/api/models/demo/load").json()["running"] is True
    assert panel.post("/api/models/demo/unload").json()["running"] is False


def test_status_includes_the_fleet_and_the_network(panel: TestClient) -> None:
    data = panel.get("/api/status").json()
    assert data["defaultModel"] == "demo"
    assert data["models"][0]["port"] > 0
    assert data["network"]["localUrl"].endswith("/v1")


# --------------------------------------------------------------------------- #
# Discovery
# --------------------------------------------------------------------------- #
def test_local_models_are_listed_with_sizes(panel: TestClient,
                                            monkeypatch: pytest.MonkeyPatch) -> None:
    found = [
        discovery.LocalModel(repo="unsloth/Demo-GGUF", quant="Q4_K_M",
                             source="huggingface", size_bytes=2048, file_count=1,
                             modified=1.0, path="/models", context_length=4096),
        discovery.LocalModel(repo="unsloth/New-GGUF", quant="Q8_0", source="lmstudio",
                             size_bytes=4096, file_count=2, modified=2.0, path="/models"),
    ]
    monkeypatch.setattr(discovery, "scan_local_models", lambda *a, **k: found)

    data = panel.get("/api/models/local?refresh=true").json()
    assert data["totalBytes"] == 6144
    by_repo = {m["repo"]: m for m in data["models"]}
    assert by_repo["unsloth/Demo-GGUF"]["inFleet"] is True
    assert by_repo["unsloth/New-GGUF"]["inFleet"] is False
    assert by_repo["unsloth/New-GGUF"]["sizeBytes"] == 4096


def test_catalog_search_and_variants(panel: TestClient,
                                     monkeypatch: pytest.MonkeyPatch) -> None:
    class Response:
        def __init__(self, payload: Any) -> None:
            self._payload = payload
            self.status_code = 200

        def json(self) -> Any:
            return self._payload

    def fake_get(url: str, params=None, **kwargs: Any) -> Response:
        if url.endswith("/api/models"):
            return Response([{"id": "unsloth/Demo-GGUF", "downloads": 9, "likes": 1}])
        if url.endswith("/tree/main"):
            return Response([{"type": "file", "path": "Demo-Q4_K_M.gguf", "size": 128}])
        return Response({"gguf": {"context_length": 8192}, "lastModified": "2026"})

    monkeypatch.setattr(discovery.httpx, "get", fake_get)
    monkeypatch.setattr(discovery, "scan_local_models", lambda *a, **k: [])

    repos = panel.get("/api/models/catalog?search=demo").json()["repos"]
    assert repos[0]["repo"] == "unsloth/Demo-GGUF"
    assert repos[0]["hasLocal"] is False

    record = panel.get("/api/models/catalog?repo=unsloth/Demo-GGUF").json()["repo"]
    assert record["variants"][0]["quant"] == "Q4_K_M"
    assert record["variants"][0]["contextLength"] == 8192


def test_catalog_failures_are_surfaced(panel: TestClient,
                                       monkeypatch: pytest.MonkeyPatch) -> None:
    def boom(*args: Any, **kwargs: Any):
        raise discovery.httpx.ConnectError("offline")

    monkeypatch.setattr(discovery.httpx, "get", boom)
    monkeypatch.setattr(discovery, "scan_local_models", lambda *a, **k: [])
    response = panel.get("/api/models/catalog?search=x&refresh=true")
    assert response.status_code == 503


# --------------------------------------------------------------------------- #
# Network
# --------------------------------------------------------------------------- #
def test_network_can_be_shared_and_reverted(panel: TestClient) -> None:
    shared = panel.post("/api/network", json={"allowLan": True})
    assert shared.status_code == 200
    assert shared.json()["restartRequired"] is True
    assert shared.json()["network"]["shared"] is True

    saved = fleetfile.load(panel.config_path)  # type: ignore[attr-defined]
    assert saved["gateway"]["host"] == "0.0.0.0"

    reverted = panel.post("/api/network", json={"allowLan": False})
    assert reverted.json()["network"]["shared"] is False


def test_sharing_without_a_key_is_refused(tmp_path: Path, home: Path,
                                          stub_backend: StubBackend) -> None:
    cfg = fleetfile.normalise({})
    cfg["gateway"]["auto_import"] = False
    fleetfile.add_model(cfg, repo="unsloth/Demo-GGUF", quant="Q4_K_M",
                        port=stub_backend.port)
    config_path = tmp_path / "models.yaml"
    fleetfile.save(config_path, cfg)

    app = gateway.build_app(fleetfile.load(config_path), config_path)
    with TestClient(app, client=("127.0.0.1", 51234)) as client:
        response = client.post("/api/network", json={"allowLan": True})
        assert response.status_code == 400
        assert "API key" in response.json()["detail"]["error"]["message"]


def test_idle_timeout_is_saved(panel: TestClient) -> None:
    panel.post("/api/network", json={"idleTimeout": 600})
    assert fleetfile.load(panel.config_path)["gateway"]["idle_timeout"] == 600  # type: ignore[attr-defined]


def test_firewall_endpoint_reports_the_command(panel: TestClient) -> None:
    result = panel.post("/api/network/firewall").json()
    assert str(result["command"]).strip() != ""
    assert result["ok"] in (True, False)


# --------------------------------------------------------------------------- #
# Clients
# --------------------------------------------------------------------------- #
def test_client_availability_is_reported(panel: TestClient) -> None:
    data = panel.get("/api/clients").json()
    assert {a["id"] for a in data["agents"]} == {"codex", "qwen", "claude"}
    assert set(data["targets"]) == set(gen_configs.TARGETS)
    assert "installed" in data["unsloth"]


def test_install_configs_writes_real_files(panel: TestClient, home: Path,
                                           tmp_path: Path) -> None:
    response = panel.post("/api/install-configs",
                          json={"targets": ["codex", "claude"], "scope": "local"})
    assert response.status_code == 200
    body = response.json()
    assert body["ok"] is True
    assert (home / ".codex" / "config.toml").exists()
    assert (home / ".claude" / "settings.json").exists()
    assert (tmp_path / "generated" / "claude" / "settings.json").exists()

    settings = json.loads((home / ".claude" / "settings.json").read_text(encoding="utf-8"))
    assert settings["env"]["ANTHROPIC_BASE_URL"].startswith("http://127.0.0.1:")


def test_install_configs_validates_targets(panel: TestClient) -> None:
    assert panel.post("/api/install-configs", json={"targets": ["nope"]}).status_code == 400


def test_client_bundle_is_a_zip_with_every_config(panel: TestClient) -> None:
    response = panel.get("/api/client-bundle?scope=lan")
    assert response.status_code == 200
    assert response.headers["content-type"] == "application/zip"
    with zipfile.ZipFile(BytesIO(response.content)) as archive:
        names = set(archive.namelist())
        assert "padra-client/claude/settings.json" in names
        assert "padra-client/codex/config.toml" in names
        assert "padra-client/README.txt" in names


def test_launch_agent_validates_the_folder_and_the_binary(panel: TestClient,
                                                          tmp_path: Path) -> None:
    missing = panel.post("/api/launch-agent", json={"agent": "codex", "alias": "demo",
                                                    "folder": str(tmp_path / "nope")})
    assert missing.status_code == 400
    assert missing.json()["detail"]["error"]["message"] == "folder"

    absent = panel.post("/api/launch-agent", json={"agent": "codex", "alias": "demo",
                                                   "folder": str(tmp_path)})
    assert absent.status_code == 424
    assert absent.json()["detail"]["error"]["agent"] == "codex"

    unknown = panel.post("/api/launch-agent", json={"agent": "emacs", "alias": "demo",
                                                    "folder": str(tmp_path)})
    assert unknown.status_code == 400


def test_launch_agent_writes_a_script_with_the_right_environment(
        panel: TestClient, tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("launcher.webui._which", lambda command: "/usr/bin/" + command)
    captured: dict = {}

    def fake_spawn(agent, alias, folder, env, command):
        captured.update({"agent": agent, "alias": alias, "folder": folder,
                         "env": env, "command": command})
        return True, "/tmp/script.sh"

    monkeypatch.setattr("launcher.webui._spawn_terminal", fake_spawn)

    response = panel.post("/api/launch-agent", json={"agent": "claude", "alias": "demo",
                                                     "folder": str(tmp_path)})
    assert response.status_code == 200
    assert captured["command"] == ["claude", "--model", "demo"]
    assert captured["env"]["ANTHROPIC_MODEL"] == "demo"
    assert captured["env"]["ANTHROPIC_AUTH_TOKEN"] == KEY
    assert "ANTHROPIC_BASE_URL" in captured["env"]

    panel.post("/api/launch-agent", json={"agent": "qwen", "alias": "demo",
                                          "folder": str(tmp_path)})
    assert captured["command"] == ["qwen", "--model", "demo"]
    assert captured["env"]["OPENAI_BASE_URL"].endswith("/v1")
    assert panel.get("/api/settings").json()["settings"]["lastAgent"] == "qwen"


def test_spawn_terminal_builds_a_quoted_script(tmp_path: Path,
                                               monkeypatch: pytest.MonkeyPatch) -> None:
    from launcher import webui

    monkeypatch.setattr("shutil.which", lambda name: None)
    folder = tmp_path / "a folder with spaces"
    folder.mkdir()
    started, script = webui._spawn_terminal(
        "codex", "demo", folder, {"KEY": 'va"lue with spaces'}, ["codex", "--profile", "demo"]
    )
    body = Path(script).read_text(encoding="utf-8")
    assert started is False           # no terminal emulator in the test container
    assert "a folder with spaces" in body
    assert "codex --profile demo" in body
    assert "va\"lue with spaces" in body


# --------------------------------------------------------------------------- #
# Diagnostics
# --------------------------------------------------------------------------- #
def test_diagnostics_report_the_environment(panel: TestClient) -> None:
    data = panel.get("/api/diagnostics").json()
    assert data["hasApiKey"] is True
    assert data["gatewayHost"] == "127.0.0.1"
    assert data["backendPorts"][0]["alias"] == "demo"
    assert isinstance(data["cacheRoots"], list)


def test_log_endpoint_streams_new_entries(panel: TestClient) -> None:
    first = panel.get("/api/log?since=0").json()
    gateway.log("a test line")
    second = panel.get(f"/api/log?since={first['cursor']}").json()
    assert any("a test line" in entry["text"] for entry in second["entries"])
