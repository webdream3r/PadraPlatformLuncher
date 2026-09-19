"""Every user-facing string is a key, and every key exists in every language."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Dict, Set

import pytest

ROOT = Path(__file__).resolve().parents[1]
LOCALES = ROOT / "launcher" / "locales"
STATIC = ROOT / "launcher" / "static"

# Keys the panel builds at runtime from data, e.g. t(`source.${model.source}`).
DYNAMIC_PREFIXES = ("unit.", "dialog.missing.", "source.", "client.", "mode.",
                    "state.", "empty.", "btn.", "log.", "notice.", "error.")


def load(code: str) -> Dict[str, str]:
    return json.loads((LOCALES / f"{code}.json").read_text(encoding="utf-8"))


def cultures() -> list:
    return sorted(path.stem for path in LOCALES.glob("*.json"))


def test_at_least_english_and_persian_are_shipped() -> None:
    assert set(cultures()) >= {"en-US", "fa-IR"}


def test_every_locale_has_the_same_keys() -> None:
    reference = load("en-US")
    reference_keys = set(reference) - {"_meta"}
    for code in cultures():
        keys = set(load(code)) - {"_meta"}
        assert keys == reference_keys, (
            f"{code} differs: missing {sorted(reference_keys - keys)}, "
            f"extra {sorted(keys - reference_keys)}"
        )


def test_locale_metadata_is_complete() -> None:
    for code in cultures():
        meta = load(code)["_meta"]
        assert meta["code"] == code
        assert meta["dir"] in ("ltr", "rtl")
        assert meta["name"].strip()


def test_placeholders_match_across_languages() -> None:
    reference = load("en-US")
    pattern = re.compile(r"\{(\w+)\}")
    for code in cultures():
        if code == "en-US":
            continue
        translated = load(code)
        for key, value in reference.items():
            if key == "_meta":
                continue
            assert set(pattern.findall(value)) == set(pattern.findall(translated[key])), key


def html_keys() -> Set[str]:
    text = (STATIC / "index.html").read_text(encoding="utf-8")
    found: Set[str] = set()
    for attribute in ("data-i18n", "data-i18n-placeholder", "data-i18n-aria-label"):
        found |= set(re.findall(rf'{attribute}="([^"]+)"', text))
    return found


def js_keys() -> Set[str]:
    text = (STATIC / "app.js").read_text(encoding="utf-8")
    return set(re.findall(r'\bt\(\s*"([^"]+)"', text))


def test_every_key_used_by_the_panel_exists() -> None:
    known = set(load("en-US"))
    missing = sorted((html_keys() | js_keys()) - known)
    assert not missing, f"keys used by the panel but not translated: {missing}"


def test_dynamic_key_families_are_populated() -> None:
    known = set(load("en-US"))
    for prefix in DYNAMIC_PREFIXES:
        assert any(key.startswith(prefix) for key in known), prefix


def test_no_hardcoded_text_in_the_markup() -> None:
    """Visible elements carry a key rather than literal text."""
    text = (STATIC / "index.html").read_text(encoding="utf-8")
    for tag in ("h1", "h2", "h3", "legend", "caption"):
        for body in re.findall(rf"<{tag}\b[^>]*>(.*?)</{tag}>", text, re.DOTALL):
            stripped = re.sub(r"<[^>]+>", "", body).strip()
            if not stripped:
                continue
            # Only the document title keeps an English fallback for no-JS load.
            assert stripped == "Padra Platform Launcher", stripped


def test_css_uses_logical_properties_only() -> None:
    css = (STATIC / "styles.css").read_text(encoding="utf-8")
    forbidden = re.findall(
        r"(?<![-\w])(margin|padding|border)-(left|right)\s*:", css)
    assert not forbidden, forbidden
    assert not re.findall(r"(?<![-\w])(left|right)\s*:\s*\d", css)
    assert "text-align: left" not in css and "text-align: right" not in css


@pytest.mark.parametrize("code", ["en-US", "fa-IR"])
def test_messages_are_not_empty(code: str) -> None:
    for key, value in load(code).items():
        if key == "_meta":
            continue
        assert isinstance(value, str) and value.strip(), key
