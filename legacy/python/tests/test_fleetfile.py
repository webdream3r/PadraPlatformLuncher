"""Validation, mutation and round-tripping of models.yaml."""

from __future__ import annotations

from pathlib import Path

import pytest
import yaml

from launcher import fleetfile
from launcher.fleetfile import FleetFileError


def base(**gateway) -> dict:
    return {"gateway": gateway, "models": []}


def test_defaults_are_applied_to_an_empty_file() -> None:
    cfg = fleetfile.normalise({})
    assert cfg["gateway"]["host"] == "127.0.0.1"
    assert cfg["gateway"]["port"] == 8900
    assert cfg["gateway"]["mode"] == "exclusive"
    assert cfg["gateway"]["default_model"] == ""
    assert cfg["models"] == []


def test_model_entry_is_normalised() -> None:
    cfg = fleetfile.normalise({
        "models": [{"alias": "Demo", "repo": "unsloth/X-GGUF", "quant": "UD-Q4_K_XL",
                    "port": "8801", "context_length": "4096",
                    "extra_args": "--temp 0.6"}]
    })
    model = cfg["models"][0]
    assert model["alias"] == "demo"
    assert model["port"] == 8801
    assert model["context_length"] == 4096
    assert model["extra_args"] == ["--temp", "0.6"]
    assert model["disable_tools"] is True
    assert cfg["gateway"]["default_model"] == "demo"


@pytest.mark.parametrize("raw, message", [
    ({"models": [{"repo": "x"}]}, "alias"),
    ({"models": [{"alias": "a", "repo": "x", "port": 70000}]}, "between"),
    ({"models": [{"alias": "A B", "repo": "x"}]}, "lowercase"),
    ({"gateway": {"mode": "turbo"}}, "mode"),
    ({"gateway": {"wire_api": "grpc"}}, "wire_api"),
    ({"gateway": {"default_model": "ghost"}}, "default_model"),
    ({"models": "nope"}, "list"),
])
def test_invalid_configurations_are_rejected(raw: dict, message: str) -> None:
    with pytest.raises(FleetFileError) as excinfo:
        fleetfile.normalise(raw)
    assert message in str(excinfo.value)


def test_duplicate_alias_and_port_are_rejected() -> None:
    with pytest.raises(FleetFileError, match="duplicate alias"):
        fleetfile.normalise({"models": [
            {"alias": "a", "repo": "x", "port": 8801},
            {"alias": "a", "repo": "y", "port": 8802},
        ]})
    with pytest.raises(FleetFileError, match="port 8801"):
        fleetfile.normalise({"models": [
            {"alias": "a", "repo": "x", "port": 8801},
            {"alias": "b", "repo": "y", "port": 8801},
        ]})


def test_model_cannot_take_the_gateway_port() -> None:
    with pytest.raises(FleetFileError, match="gateway port"):
        fleetfile.normalise({"gateway": {"port": 8900},
                             "models": [{"alias": "a", "repo": "x", "port": 8900}]})


def test_api_key_placeholder_is_replaced() -> None:
    cfg = fleetfile.normalise({"gateway": {"api_key": "sk-gateway-local-change-me"}})
    cfg, changed = fleetfile.ensure_api_key(cfg)
    assert changed is True
    assert cfg["gateway"]["api_key"].startswith("sk-padra-")
    assert len(cfg["gateway"]["api_key"]) > 30

    cfg, changed = fleetfile.ensure_api_key(cfg)
    assert changed is False


def test_add_remove_and_default_model() -> None:
    cfg = fleetfile.normalise({})
    first = fleetfile.add_model(cfg, repo="unsloth/Qwen3.5-4B-GGUF", quant="UD-Q4_K_XL",
                                context_length=262144)
    assert first["alias"] == "qwen3.5-4b"
    assert first["port"] == fleetfile.FIRST_BACKEND_PORT
    assert cfg["gateway"]["default_model"] == "qwen3.5-4b"

    second = fleetfile.add_model(cfg, repo="unsloth/Qwen3.5-4B-GGUF", quant="Q8_0")
    assert second["alias"] == "qwen3.5-4b-q8_0"
    assert second["port"] == fleetfile.FIRST_BACKEND_PORT + 1

    with pytest.raises(FleetFileError, match="already in the fleet"):
        fleetfile.add_model(cfg, repo="unsloth/Qwen3.5-4B-GGUF", quant="Q8_0")

    fleetfile.set_default_model(cfg, "qwen3.5-4b-q8_0")
    assert cfg["gateway"]["default_model"] == "qwen3.5-4b-q8_0"

    fleetfile.remove_model(cfg, "qwen3.5-4b-q8_0")
    assert [m["alias"] for m in cfg["models"]] == ["qwen3.5-4b"]
    assert cfg["gateway"]["default_model"] == "qwen3.5-4b"

    fleetfile.remove_model(cfg, "qwen3.5-4b")
    assert cfg["gateway"]["default_model"] == ""
    with pytest.raises(FleetFileError, match="unknown model"):
        fleetfile.remove_model(cfg, "qwen3.5-4b")


def test_save_is_atomic_and_round_trips(tmp_path: Path) -> None:
    target = tmp_path / "models.yaml"
    cfg = fleetfile.normalise({})
    fleetfile.add_model(cfg, repo="unsloth/gemma-4-26B-A4B-it-GGUF",
                        quant="UD-Q4_K_XL", context_length=131072)
    fleetfile.save(target, cfg)

    text = target.read_text(encoding="utf-8")
    assert text.startswith("# Padra Platform Launcher")
    assert yaml.safe_load(text)["models"][0]["repo"] == "unsloth/gemma-4-26B-A4B-it-GGUF"
    assert not list(tmp_path.glob("*.tmp"))

    reloaded = fleetfile.load(target)
    assert reloaded["models"][0]["alias"] == cfg["models"][0]["alias"]
    assert reloaded["gateway"]["default_model"] == cfg["gateway"]["default_model"]


def test_load_reports_a_missing_file(tmp_path: Path) -> None:
    with pytest.raises(FleetFileError, match="config not found"):
        fleetfile.load(tmp_path / "nope.yaml")


def test_next_free_port_skips_everything_in_use() -> None:
    cfg = fleetfile.normalise({"gateway": {"port": 8802}, "models": [
        {"alias": "a", "repo": "x", "port": 8801},
    ]})
    assert fleetfile.next_free_port(cfg) == 8803
