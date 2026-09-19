#!/usr/bin/env python3
"""Padra Platform Launcher — desktop shell.

Responsibilities:
  1. Ensure a virtual environment with the required packages exists.
  2. Re-execute itself inside that environment.
  3. Start and supervise the gateway process.
  4. Open the localized control panel in a native window (WebView2 on Windows),
     falling back to the default browser when no webview runtime is available.
  5. Ask the gateway to shut down cleanly when the window closes, so no
     `unsloth run` process — and no VRAM — is left behind.

Frozen builds re-invoke this same executable with `--run-gateway` /
`--run-genconfigs` instead of spawning a separate interpreter.
"""

from __future__ import annotations

import json
import os
import signal
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import webbrowser
from pathlib import Path
from typing import Any, Dict, List, Optional

FROZEN = bool(getattr(sys, "frozen", False))
APP_DIR = Path(sys.executable).resolve().parent if FROZEN else Path(__file__).resolve().parent
BUNDLE_DIR = Path(getattr(sys, "_MEIPASS", str(APP_DIR)))
CONFIG_PATH = APP_DIR / "models.yaml"
SETTINGS_PATH = APP_DIR / "launcher.settings.json"
VENV_DIR = APP_DIR / ".venv"
IS_WINDOWS = os.name == "nt"
CREATE_NO_WINDOW = 0x08000000 if IS_WINDOWS else 0
CREATE_NEW_PROCESS_GROUP = 0x00000200 if IS_WINDOWS else 0
READY_TIMEOUT = 120
REQUIRED_MODULES = ("fastapi", "uvicorn", "httpx", "yaml", "pydantic")


def venv_python(windowless: bool = False) -> Path:
    if IS_WINDOWS:
        return VENV_DIR / "Scripts" / ("pythonw.exe" if windowless else "python.exe")
    return VENV_DIR / "bin" / "python"


def venv_pip() -> Path:
    return VENV_DIR / ("Scripts/pip.exe" if IS_WINDOWS else "bin/pip")


def inside_venv() -> bool:
    try:
        current = Path(sys.executable).resolve()
    except OSError:
        return False
    for candidate in (venv_python(), venv_python(windowless=True)):
        try:
            if current == candidate.resolve():
                return True
        except OSError:
            continue
    return False


def say(message: str) -> None:
    print(f"[launcher] {message}", flush=True)


# --------------------------------------------------------------------------- #
# Window title comes from the same resource files as the rest of the UI.
# --------------------------------------------------------------------------- #
def window_title() -> str:
    culture = "en-US"
    if SETTINGS_PATH.exists():
        try:
            culture = json.loads(SETTINGS_PATH.read_text(encoding="utf-8")).get(
                "culture", culture)
        except (ValueError, OSError):
            pass
    for candidate in (culture, "en-US"):
        path = BUNDLE_DIR / "launcher" / "locales" / f"{candidate}.json"
        if path.is_file():
            try:
                title = json.loads(path.read_text(encoding="utf-8")).get("app.title")
            except (ValueError, OSError):
                continue
            if title:
                return str(title)
    return "Padra Platform Launcher"


def gateway_settings() -> Dict[str, Any]:
    host, port = "127.0.0.1", 8900
    if CONFIG_PATH.exists():
        try:
            import yaml  # available once the environment is prepared

            gateway = (yaml.safe_load(CONFIG_PATH.read_text(encoding="utf-8"))
                       or {}).get("gateway", {}) or {}
            configured = str(gateway.get("host", host))
            host = "127.0.0.1" if configured in ("0.0.0.0", "::", "*") else configured
            port = int(gateway.get("port", port))
        except Exception:  # noqa: BLE001 - fall back to the documented default
            pass
    if ":" in host and not host.startswith("["):
        host = f"[{host}]"
    return {"origin": f"http://{host}:{port}"}


# --------------------------------------------------------------------------- #
# Environment bootstrap
# --------------------------------------------------------------------------- #
def dependencies_present(python: Path) -> bool:
    if not python.exists():
        return False
    probe = ";".join(f"import {name}" for name in REQUIRED_MODULES)
    try:
        result = subprocess.run([str(python), "-c", probe], capture_output=True,
                                timeout=120, creationflags=CREATE_NO_WINDOW)
    except (OSError, subprocess.SubprocessError):
        return False
    return result.returncode == 0


def install_dependencies() -> None:
    say("installing dependencies")
    subprocess.run([str(venv_pip()), "install", "--upgrade", "pip", "-q"],
                   check=False, creationflags=CREATE_NO_WINDOW)
    subprocess.run([str(venv_pip()), "install", "-q", "-r",
                    str(APP_DIR / "requirements.txt")], check=True,
                   creationflags=CREATE_NO_WINDOW)
    say("dependencies installed")


def ensure_environment() -> None:
    if FROZEN or inside_venv():
        return

    console_python = venv_python()
    if not console_python.exists():
        say("creating the virtual environment (first run only)")
        subprocess.run([sys.executable, "-m", "venv", str(VENV_DIR)], check=True)
        install_dependencies()
    elif not dependencies_present(console_python):
        say("the virtual environment is incomplete — reinstalling dependencies")
        install_dependencies()

    target = venv_python(windowless=IS_WINDOWS)
    if not target.exists():
        target = console_python
    say("restarting inside the virtual environment")
    script = str(Path(__file__).resolve())
    os.execv(str(target), [str(target), script, *sys.argv[1:]])


# --------------------------------------------------------------------------- #
# Gateway supervision
# --------------------------------------------------------------------------- #
class Gateway:
    def __init__(self) -> None:
        self.proc: Optional[subprocess.Popen] = None
        self.origin = gateway_settings()["origin"]

    def command(self) -> List[str]:
        if FROZEN:
            return [sys.executable, "--run-gateway", "--config", str(CONFIG_PATH)]
        return [sys.executable, "-m", "launcher", "--config", str(CONFIG_PATH)]

    def start(self) -> None:
        kwargs: Dict[str, Any] = dict(cwd=str(APP_DIR))
        if IS_WINDOWS:
            kwargs["creationflags"] = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP
        else:
            kwargs["start_new_session"] = True
        say("starting the gateway")
        self.proc = subprocess.Popen(self.command(), **kwargs)

    def alive(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def wait_ready(self, timeout: int = READY_TIMEOUT) -> bool:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.proc and self.proc.poll() is not None:
                say(f"the gateway exited with code {self.proc.returncode}")
                return False
            try:
                with urllib.request.urlopen(f"{self.origin}/healthz", timeout=3) as response:
                    if response.status == 200:
                        return True
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(0.7)
        return False

    def request_shutdown(self) -> bool:
        """Ask the gateway to unload every model and exit by itself."""
        request = urllib.request.Request(f"{self.origin}/api/shutdown", data=b"{}",
                                         method="POST",
                                         headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status == 200
        except (urllib.error.URLError, OSError):
            return False

    def stop(self) -> None:
        if not self.alive():
            self.proc = None
            return
        say("stopping the gateway")
        if self.request_shutdown():
            try:
                self.proc.wait(timeout=90)
                self.proc = None
                return
            except subprocess.TimeoutExpired:
                say("the gateway did not exit after the shutdown request")

        try:
            if IS_WINDOWS:
                self.proc.send_signal(signal.CTRL_BREAK_EVENT)
            else:
                os.killpg(os.getpgid(self.proc.pid), signal.SIGTERM)
        except (OSError, ValueError):
            pass
        try:
            self.proc.wait(timeout=45)
        except subprocess.TimeoutExpired:
            say("forcing shutdown")
            if IS_WINDOWS:
                subprocess.run(["taskkill", "/PID", str(self.proc.pid), "/T", "/F"],
                               capture_output=True, creationflags=CREATE_NO_WINDOW)
            else:
                try:
                    os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
                except OSError:
                    pass
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                pass
        self.proc = None


# --------------------------------------------------------------------------- #
# Frozen-build child roles
# --------------------------------------------------------------------------- #
def run_child_role() -> Optional[int]:
    if len(sys.argv) < 2 or not sys.argv[1].startswith("--run-"):
        return None
    role = sys.argv[1][len("--run-"):]
    sys.argv = [sys.argv[0]] + sys.argv[2:]
    sys.path.insert(0, str(BUNDLE_DIR))
    if role == "gateway":
        from launcher import gateway

        return int(gateway.main() or 0)
    if role == "genconfigs":
        from launcher import gen_configs

        return int(gen_configs.main() or 0)
    print(f"unknown role: {role}", file=sys.stderr)
    return 2


# --------------------------------------------------------------------------- #
def open_window(url: str, gateway: Gateway) -> None:
    try:
        import webview  # provided by pywebview
    except ImportError:
        say("pywebview is unavailable; opening the control panel in your browser")
        webbrowser.open(url)
        try:
            while gateway.alive():
                time.sleep(1)
        except KeyboardInterrupt:
            pass
        return

    window = webview.create_window(window_title(), url, width=1180, height=860,
                                   min_size=(920, 640))

    def watchdog() -> None:
        while gateway.alive():
            time.sleep(1)
        try:
            window.destroy()
        except Exception:  # noqa: BLE001 - the window may already be gone
            pass

    threading.Thread(target=watchdog, name="gateway-watchdog", daemon=True).start()
    try:
        webview.start()
    except Exception as exc:  # noqa: BLE001 - no webview runtime on this machine
        say(f"the native window could not be created ({exc}); using the browser")
        webbrowser.open(url)
        while gateway.alive():
            time.sleep(1)


def main() -> int:
    role_exit = run_child_role()
    if role_exit is not None:
        return role_exit

    if not CONFIG_PATH.exists():
        bundled = BUNDLE_DIR / "models.yaml"
        if bundled.is_file():
            CONFIG_PATH.write_bytes(bundled.read_bytes())
            say(f"created {CONFIG_PATH}")
        else:
            say(f"models.yaml was not found next to {APP_DIR}")
            return 2

    ensure_environment()

    gateway = Gateway()
    gateway.start()
    try:
        if not gateway.wait_ready():
            say("the gateway did not become ready in time")
            return 1
        open_window(gateway.origin + "/", gateway)
    finally:
        gateway.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
