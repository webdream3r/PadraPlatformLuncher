"""Client configuration: generation, TOML safety and idempotent installs."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
import yaml

from launcher import fleetfile, gen_configs

tomllib = pytest.importorskip("tomllib", reason="TOML parsing needs Python 3.11+")


def make_context(base_url: str = "http://127.0.0.1:8900/v1") -> gen_configs.ClientContext:
    cfg = fleetfile.normalise({})
    cfg["gateway"]["api_key"] = "sk-padra-test-key"
    fleetfile.add_model(cfg, repo="unsloth/Qwen3.5-4B-GGUF", quant="UD-Q4_K_XL",
                        context_length=262144)
    fleetfile.add_model(cfg, repo="unsloth/gemma-4-26B-A4B-it-GGUF",
                        quant="UD-Q4_K_XL", context_length=131072)
    return gen_configs.context_from_config(cfg, base_url=base_url)


# --------------------------------------------------------------------------- #
# Codex
# --------------------------------------------------------------------------- #
def test_codex_block_is_valid_toml_and_has_no_root_keys() -> None:
    ctx = make_context()
    block = gen_configs.codex_provider_block(ctx)
    parsed = tomllib.loads(block)
    assert set(parsed) == {"model_providers"}
    provider = parsed["model_providers"][gen_configs.CODEX_PROVIDER_ID]
    assert provider["base_url"] == ctx.base_url
    assert provider["env_key"] == gen_configs.ENV_KEY_NAME
    assert provider["wire_api"] == "responses"
    assert provider["requires_openai_auth"] is False
    assert "oss_provider" not in block


def test_codex_block_appends_to_an_existing_table_without_corrupting_it() -> None:
    existing = 'model = "gpt-5.6"\n\n[tui]\nnotifications = true\n'
    merged = gen_configs.replace_managed_block(
        existing, gen_configs.codex_provider_block(make_context()))
    parsed = tomllib.loads(merged)
    assert parsed["model"] == "gpt-5.6"
    assert parsed["tui"] == {"notifications": True}
    assert gen_configs.CODEX_PROVIDER_ID in parsed["model_providers"]


def test_managed_block_is_replaced_not_duplicated() -> None:
    first = gen_configs.replace_managed_block("", "[model_providers.a]\nx = 1\n")
    second = gen_configs.replace_managed_block(first, "[model_providers.a]\nx = 2\n")
    assert second.count(gen_configs.BLOCK_BEGIN) == 1
    assert tomllib.loads(second)["model_providers"]["a"]["x"] == 2


def test_codex_profiles_are_separate_files_with_top_level_keys(home: Path) -> None:
    report = gen_configs.install(make_context(), ["codex"])
    assert report.ok

    profile = home / ".codex" / "qwen3.5-4b.config.toml"
    parsed = tomllib.loads(profile.read_text(encoding="utf-8"))
    assert parsed["model"] == "qwen3.5-4b"
    assert parsed["model_provider"] == gen_configs.CODEX_PROVIDER_ID
    assert parsed["model_context_window"] == 262144
    assert "profiles" not in parsed

    root = tomllib.loads((home / ".codex" / "config.toml").read_text(encoding="utf-8"))
    assert gen_configs.CODEX_PROVIDER_ID in root["model_providers"]


def test_reinstalling_codex_is_idempotent_and_prunes_stale_profiles(home: Path) -> None:
    ctx = make_context()
    gen_configs.install(ctx, ["codex"])
    stale = home / ".codex" / "gone.config.toml"
    stale.write_text(gen_configs.codex_profile(
        ctx, gen_configs.ModelEntry(alias="gone")), encoding="utf-8")

    report = gen_configs.install(ctx, ["codex"])
    assert report.ok
    assert not stale.exists()
    assert (home / ".codex" / "config.toml").read_text(encoding="utf-8").count(
        gen_configs.BLOCK_BEGIN) == 1
    assert any("already current" in line for line in report.lines)


def test_existing_codex_config_is_backed_up(home: Path) -> None:
    codex = home / ".codex"
    codex.mkdir()
    (codex / "config.toml").write_text('model = "gpt-5.6"\n', encoding="utf-8")
    gen_configs.install(make_context(), ["codex"])
    assert (codex / "config.toml.bak").read_text(encoding="utf-8") == 'model = "gpt-5.6"\n'


# --------------------------------------------------------------------------- #
# Qwen Code
# --------------------------------------------------------------------------- #
def test_qwen_settings_merge_keeps_unrelated_keys(home: Path) -> None:
    qwen = home / ".qwen"
    qwen.mkdir()
    (qwen / "settings.json").write_text(json.dumps({
        "ui": {"theme": "dark"},
        "modelProviders": {"anthropic": [{"id": "keep"}]},
        "security": {"auth": {"selectedType": "qwen-oauth"}},
    }), encoding="utf-8")

    gen_configs.install(make_context(), ["qwen"])
    data = json.loads((qwen / "settings.json").read_text(encoding="utf-8"))
    assert data["ui"] == {"theme": "dark"}
    assert data["modelProviders"]["anthropic"] == [{"id": "keep"}]
    assert [p["id"] for p in data["modelProviders"]["openai"]] == \
        ["qwen3.5-4b", "gemma-4-26b-a4b-it"]
    assert data["security"]["auth"]["selectedType"] == "openai"
    assert data["model"]["name"] == "qwen3.5-4b"
    assert data["env"]["OPENAI_BASE_URL"] == "http://127.0.0.1:8900/v1"
    assert (qwen / "settings.json.bak").exists()


def test_qwen_settings_survive_a_corrupt_file(home: Path) -> None:
    qwen = home / ".qwen"
    qwen.mkdir()
    (qwen / "settings.json").write_text("{ this is not json", encoding="utf-8")
    report = gen_configs.install(make_context(), ["qwen"])
    assert report.ok
    data = json.loads((qwen / "settings.json").read_text(encoding="utf-8"))
    assert data["security"]["auth"]["selectedType"] == "openai"


# --------------------------------------------------------------------------- #
# Claude Code
# --------------------------------------------------------------------------- #
def test_claude_settings_use_the_anthropic_surface(home: Path) -> None:
    ctx = make_context()
    gen_configs.install(ctx, ["claude"])
    data = json.loads((home / ".claude" / "settings.json").read_text(encoding="utf-8"))
    env = data["env"]
    assert env["ANTHROPIC_BASE_URL"] == "http://127.0.0.1:8900"   # no /v1 suffix
    assert env["ANTHROPIC_AUTH_TOKEN"] == "sk-padra-test-key"
    assert env["ANTHROPIC_MODEL"] == "qwen3.5-4b"
    assert env["ANTHROPIC_DEFAULT_SONNET_MODEL"] == "qwen3.5-4b"
    assert data["model"] == "qwen3.5-4b"
    assert "ANTHROPIC_API_KEY" not in env


def test_claude_settings_drop_a_conflicting_subscription_key(home: Path) -> None:
    claude = home / ".claude"
    claude.mkdir()
    (claude / "settings.json").write_text(json.dumps({
        "env": {"ANTHROPIC_API_KEY": "sk-ant-real", "MY_VAR": "keep"},
        "permissions": {"allow": ["Bash"]},
    }), encoding="utf-8")

    gen_configs.install(make_context(), ["claude"])
    data = json.loads((claude / "settings.json").read_text(encoding="utf-8"))
    assert "ANTHROPIC_API_KEY" not in data["env"]
    assert data["env"]["MY_VAR"] == "keep"
    assert data["permissions"] == {"allow": ["Bash"]}


# --------------------------------------------------------------------------- #
# Continue and VS Code
# --------------------------------------------------------------------------- #
def test_continue_config_is_valid_yaml(home: Path) -> None:
    gen_configs.install(make_context(), ["continue"])
    data = yaml.safe_load((home / ".continue" / "config.yaml").read_text(encoding="utf-8"))
    assert data["name"] == "padra-unsloth"
    assert data["schema"] == "v1"
    assert data["models"][0]["provider"] == "openai"
    assert data["models"][0]["apiBase"] == "http://127.0.0.1:8900/v1"
    assert data["models"][0]["roles"] == ["chat", "edit", "apply"]


def test_vscode_settings_and_copilot_endpoints(home: Path,
                                               monkeypatch: pytest.MonkeyPatch) -> None:
    user_dir = home / ".config" / "Code" / "User"
    user_dir.mkdir(parents=True)
    (user_dir / "settings.json").write_text(
        '{\n  // a comment survives parsing\n  "editor.fontSize": 13,\n}\n',
        encoding="utf-8")
    monkeypatch.setattr(gen_configs, "vscode_user_dirs", lambda: [user_dir])

    gen_configs.install(make_context(), ["vscode"])
    settings = json.loads((user_dir / "settings.json").read_text(encoding="utf-8"))
    assert settings["editor.fontSize"] == 13
    assert settings["claudeCode.disableLoginPrompt"] is True
    names = [item["name"] for item in settings["claudeCode.environmentVariables"]]
    assert "ANTHROPIC_BASE_URL" in names

    models = json.loads((user_dir / "chatLanguageModels.json").read_text(encoding="utf-8"))
    assert [m["id"] for m in models] == ["qwen3.5-4b", "gemma-4-26b-a4b-it"]
    assert models[0]["vendor"] == "customendpoint"
    assert models[0]["url"] == "http://127.0.0.1:8900/v1/chat/completions"
    assert models[0]["maxInputTokens"] == 262144


def test_vscode_reinstall_does_not_duplicate_endpoints(home: Path,
                                                       monkeypatch: pytest.MonkeyPatch) -> None:
    user_dir = home / ".config" / "Code" / "User"
    user_dir.mkdir(parents=True)
    (user_dir / "chatLanguageModels.json").write_text(
        json.dumps([{"vendor": "other", "id": "keep"}]), encoding="utf-8")
    monkeypatch.setattr(gen_configs, "vscode_user_dirs", lambda: [user_dir])

    gen_configs.install(make_context(), ["vscode"])
    gen_configs.install(make_context(), ["vscode"])
    models = json.loads((user_dir / "chatLanguageModels.json").read_text(encoding="utf-8"))
    assert [m["id"] for m in models] == ["keep", "qwen3.5-4b", "gemma-4-26b-a4b-it"]


def test_missing_vscode_folder_is_reported_not_fatal(home: Path,
                                                     monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(gen_configs, "vscode_user_dirs", lambda: [])
    report = gen_configs.install(make_context(), ["vscode"])
    assert report.ok
    assert any("VS Code" in line for line in report.lines)


# --------------------------------------------------------------------------- #
# Bundle and helpers
# --------------------------------------------------------------------------- #
def test_generated_bundle_covers_every_target(tmp_path: Path) -> None:
    ctx = make_context("http://192.168.1.20:8900/v1")
    written = gen_configs.write_generated(ctx, tmp_path)
    names = {path.relative_to(tmp_path).as_posix() for path in written}
    assert {"codex/config.toml", "qwen/settings.json", "claude/settings.json",
            "continue/config.yaml", "vscode/settings.json",
            "vscode/chatLanguageModels.json", "vscode/tasks.json",
            "env.sh", "env.ps1", "README.txt"} <= names
    assert "codex/qwen3.5-4b.config.toml" in names

    tasks = json.loads((tmp_path / "vscode" / "tasks.json").read_text(encoding="utf-8"))
    assert tasks["inputs"][0]["options"] == ["qwen3.5-4b", "gemma-4-26b-a4b-it"]
    assert "192.168.1.20" in (tmp_path / "env.sh").read_text(encoding="utf-8")


def test_jsonc_loader_handles_comments_and_trailing_commas() -> None:
    text = '''{
      // line comment
      "a": 1, /* block */
      "b": "http://x/y // not a comment",
      "c": [1, 2,],
    }'''
    assert gen_configs.load_jsonc(text) == {"a": 1,
                                            "b": "http://x/y // not a comment",
                                            "c": [1, 2]}
    assert gen_configs.load_jsonc("   ") == {}


def test_unknown_targets_are_rejected_by_the_cli() -> None:
    with pytest.raises(SystemExit):
        gen_configs.parse_targets("codex,nope")
    assert gen_configs.parse_targets("all") == list(gen_configs.TARGETS)
    assert gen_configs.parse_targets("codex, claude") == ["codex", "claude"]


def test_cli_writes_and_installs(home: Path, tmp_path: Path,
                                 monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(gen_configs, "vscode_user_dirs", lambda: [])
    config = tmp_path / "models.yaml"
    cfg = fleetfile.normalise({})
    cfg["gateway"]["api_key"] = "sk-padra-cli"
    fleetfile.add_model(cfg, repo="unsloth/Qwen3.5-4B-GGUF", quant="Q4_K_M")
    fleetfile.save(config, cfg)

    out = tmp_path / "generated"
    code = gen_configs.main(["--config", str(config), "--out", str(out), "--install"])
    assert code == 0
    assert (out / "claude" / "settings.json").exists()
    assert (home / ".claude" / "settings.json").exists()
