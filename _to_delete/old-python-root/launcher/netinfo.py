"""LAN reachability helpers.

The launcher can serve the gateway to every machine on the local network. That
needs three things this module provides: the addresses this host is reachable
on, the URLs to hand to the other machines, and the firewall rule that lets
them through on Windows.
"""

from __future__ import annotations

import ipaddress
import os
import socket
import subprocess
from typing import Any, Dict, List, Optional

IS_WINDOWS = os.name == "nt"
CREATE_NO_WINDOW = 0x08000000 if IS_WINDOWS else 0
ANY_HOSTS = {"0.0.0.0", "::", "*"}
FIREWALL_RULE_NAME = "Padra Platform Launcher"


def _is_usable(address: str) -> bool:
    try:
        parsed = ipaddress.ip_address(address)
    except ValueError:
        return False
    return not (parsed.is_loopback or parsed.is_link_local or
                parsed.is_multicast or parsed.is_unspecified or parsed.is_reserved)


def primary_address() -> Optional[str]:
    """The address the OS would use to reach the LAN. No packets are sent."""
    for family, probe in ((socket.AF_INET, ("192.0.2.1", 9)),
                          (socket.AF_INET6, ("2001:db8::1", 9))):
        sock = socket.socket(family, socket.SOCK_DGRAM)
        try:
            sock.connect(probe)
            address = sock.getsockname()[0]
        except OSError:
            continue
        finally:
            sock.close()
        if _is_usable(address):
            return address
    return None


def local_addresses() -> List[Dict[str, Any]]:
    """Every non-loopback address of this host, primary interface first."""
    addresses: Dict[str, Dict[str, Any]] = {}
    preferred = primary_address()
    if preferred:
        addresses[preferred] = {
            "address": preferred,
            "family": "ipv6" if ":" in preferred else "ipv4",
            "primary": True,
        }

    hostname = socket.gethostname()
    for candidate in (hostname, f"{hostname}.local"):
        try:
            infos = socket.getaddrinfo(candidate, None, proto=socket.IPPROTO_TCP)
        except socket.gaierror:
            continue
        for family, _type, _proto, _canon, sockaddr in infos:
            address = str(sockaddr[0]).split("%", 1)[0]
            if not _is_usable(address) or address in addresses:
                continue
            addresses[address] = {
                "address": address,
                "family": "ipv6" if family == socket.AF_INET6 else "ipv4",
                "primary": False,
            }

    ordered = sorted(
        addresses.values(),
        key=lambda item: (not item["primary"], item["family"] != "ipv4", item["address"]),
    )
    return ordered


def hostname() -> str:
    return socket.gethostname()


def format_host(address: str) -> str:
    """Wrap IPv6 literals in brackets so they can go into a URL."""
    return f"[{address}]" if ":" in address and not address.startswith("[") else address


def endpoint_urls(host: str, port: int, *, path: str = "/v1") -> Dict[str, Any]:
    """Local and LAN URLs for a gateway bound to `host:port`."""
    shared = host in ANY_HOSTS
    local_url = f"http://127.0.0.1:{port}{path}"
    lan: List[Dict[str, str]] = []
    if shared:
        for entry in local_addresses():
            lan.append({
                "address": entry["address"],
                "family": entry["family"],
                "url": f"http://{format_host(entry['address'])}:{port}{path}",
                "primary": entry["primary"],
            })
        name = hostname()
        if name:
            lan.append({
                "address": name,
                "family": "hostname",
                "url": f"http://{name}:{port}{path}",
                "primary": False,
            })
    elif host not in {"127.0.0.1", "localhost", "::1"}:
        lan.append({
            "address": host,
            "family": "ipv6" if ":" in host else "ipv4",
            "url": f"http://{format_host(host)}:{port}{path}",
            "primary": True,
        })
    return {"shared": shared, "host": host, "port": port,
            "localUrl": local_url, "lan": lan}


def preferred_lan_url(host: str, port: int, *, path: str = "/v1") -> str:
    """Best URL to hand to another machine; falls back to the loopback URL."""
    info = endpoint_urls(host, port, path=path)
    for entry in info["lan"]:
        if entry["primary"] and entry["family"] == "ipv4":
            return entry["url"]
    for entry in info["lan"]:
        if entry["family"] == "ipv4":
            return entry["url"]
    if info["lan"]:
        return info["lan"][0]["url"]
    return info["localUrl"]


# --------------------------------------------------------------------------- #
# Firewall
# --------------------------------------------------------------------------- #
def firewall_command(port: int) -> str:
    """The command that opens `port` for LAN clients on this platform."""
    if IS_WINDOWS:
        return (
            f'netsh advfirewall firewall add rule name="{FIREWALL_RULE_NAME}" '
            f'dir=in action=allow protocol=TCP localport={port} profile=private'
        )
    if os.path.exists("/usr/sbin/ufw") or os.path.exists("/usr/bin/ufw"):
        return f"sudo ufw allow {port}/tcp"
    return (
        f"sudo firewall-cmd --add-port={port}/tcp --permanent && "
        f"sudo firewall-cmd --reload"
    )


def port_is_free(host: str, port: int) -> bool:
    """True when nothing is already listening on `host:port`."""
    bind_host = "" if host in ANY_HOSTS else host
    family = socket.AF_INET6 if ":" in bind_host else socket.AF_INET
    sock = socket.socket(family, socket.SOCK_STREAM)
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((bind_host, port))
    except OSError:
        return False
    finally:
        sock.close()
    return True


def open_firewall(port: int) -> Dict[str, Any]:
    """Add the inbound rule. On Windows this raises the UAC prompt."""
    command = firewall_command(port)
    if not IS_WINDOWS:
        return {"ok": False, "elevated": False, "command": command, "output": ""}

    powershell = (
        "Start-Process -FilePath 'netsh' -Verb RunAs -Wait -WindowStyle Hidden "
        "-ArgumentList 'advfirewall','firewall','add','rule',"
        f"'name={FIREWALL_RULE_NAME}','dir=in','action=allow','protocol=TCP',"
        f"'localport={port}','profile=private'"
    )
    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", powershell],
            capture_output=True, text=True, timeout=120,
            creationflags=CREATE_NO_WINDOW,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        return {"ok": False, "elevated": True, "command": command, "output": str(exc)}
    output = (result.stdout + result.stderr).strip()
    return {"ok": result.returncode == 0, "elevated": True,
            "command": command, "output": output}
