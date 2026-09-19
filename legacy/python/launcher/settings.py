"""Persistent user preferences for the launcher UI.

Stored next to the application as `launcher.settings.json`. Only the keys in
`DEFAULTS` are kept, and each one is coerced to the type of its default, so a
missing, truncated or hand-edited file can never break start-up.
"""

from __future__ import annotations

import json
import os
import tempfile
import threading
from pathlib import Path
from typing import Any, Dict, List

DEFAULTS: Dict[str, Any] = {
    "culture": "en-US",
    "mode": "exclusive",
    "projectFolder": "",
    "selectedAlias": "",
    "installTargets": ["codex", "qwen", "claude", "continue", "vscode"],
    "lastAgent": "",
}


def _coerce(value: Any, default: Any) -> Any:
    if isinstance(default, bool):
        return bool(value)
    if isinstance(default, list):
        if not isinstance(value, list):
            return list(default)
        return [str(item) for item in value if isinstance(item, (str, int, float))]
    if isinstance(default, int) and not isinstance(default, bool):
        try:
            return int(value)
        except (TypeError, ValueError):
            return default
    return str(value)


class Settings:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._lock = threading.Lock()
        self._data: Dict[str, Any] = {k: (list(v) if isinstance(v, list) else v)
                                      for k, v in DEFAULTS.items()}
        self.load()

    def load(self) -> None:
        if not self.path.exists():
            return
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (ValueError, OSError):
            return
        if not isinstance(raw, dict):
            return
        with self._lock:
            for key, default in DEFAULTS.items():
                if key in raw:
                    self._data[key] = _coerce(raw[key], default)

    def save(self) -> None:
        with self._lock:
            payload = json.dumps(self._data, ensure_ascii=False, indent=2) + "\n"
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            handle = tempfile.NamedTemporaryFile(
                "w", encoding="utf-8", delete=False, dir=str(self.path.parent),
                prefix=self.path.name + ".", suffix=".tmp",
            )
            try:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            finally:
                handle.close()
            os.replace(handle.name, self.path)
        except OSError:
            return

    def get(self, key: str) -> Any:
        with self._lock:
            value = self._data.get(key, DEFAULTS.get(key))
        return list(value) if isinstance(value, list) else value

    def set(self, key: str, value: Any) -> None:
        if key not in DEFAULTS:
            raise KeyError(key)
        with self._lock:
            self._data[key] = _coerce(value, DEFAULTS[key])
        self.save()

    def update(self, values: Dict[str, Any]) -> None:
        changed = False
        with self._lock:
            for key, value in values.items():
                if key in DEFAULTS and value is not None:
                    self._data[key] = _coerce(value, DEFAULTS[key])
                    changed = True
        if changed:
            self.save()

    def as_dict(self) -> Dict[str, Any]:
        with self._lock:
            return {k: (list(v) if isinstance(v, list) else v)
                    for k, v in self._data.items()}

    def targets(self) -> List[str]:
        return [str(t) for t in self.get("installTargets")]
