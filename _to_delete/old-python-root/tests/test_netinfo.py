"""LAN address discovery, URL building and firewall helpers."""

from __future__ import annotations

import socket

import pytest

from launcher import netinfo


def test_loopback_binding_is_not_shared() -> None:
    info = netinfo.endpoint_urls("127.0.0.1", 8900)
    assert info["shared"] is False
    assert info["localUrl"] == "http://127.0.0.1:8900/v1"
    assert info["lan"] == []


def test_wildcard_binding_lists_every_address(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(netinfo, "local_addresses", lambda: [
        {"address": "192.168.1.20", "family": "ipv4", "primary": True},
        {"address": "fd00::5", "family": "ipv6", "primary": False},
    ])
    monkeypatch.setattr(netinfo, "hostname", lambda: "workstation")

    info = netinfo.endpoint_urls("0.0.0.0", 8900)
    assert info["shared"] is True
    urls = [entry["url"] for entry in info["lan"]]
    assert "http://192.168.1.20:8900/v1" in urls
    assert "http://[fd00::5]:8900/v1" in urls      # IPv6 literals are bracketed
    assert "http://workstation:8900/v1" in urls
    assert netinfo.preferred_lan_url("0.0.0.0", 8900) == "http://192.168.1.20:8900/v1"


def test_a_fixed_lan_address_is_reported_as_shared_enough() -> None:
    info = netinfo.endpoint_urls("192.168.1.20", 8900)
    assert info["shared"] is False
    assert info["lan"][0]["url"] == "http://192.168.1.20:8900/v1"
    assert netinfo.preferred_lan_url("192.168.1.20", 8900) == "http://192.168.1.20:8900/v1"


def test_preferred_url_falls_back_to_loopback(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(netinfo, "local_addresses", lambda: [])
    monkeypatch.setattr(netinfo, "hostname", lambda: "")
    assert netinfo.preferred_lan_url("0.0.0.0", 8900) == "http://127.0.0.1:8900/v1"


def test_addresses_exclude_loopback_and_link_local(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(netinfo, "primary_address", lambda: None)
    monkeypatch.setattr(socket, "gethostname", lambda: "host")

    def fake_getaddrinfo(name, *args, **kwargs):
        return [
            (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("127.0.0.1", 0)),
            (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("169.254.1.1", 0)),
            (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("10.0.0.7", 0)),
            (socket.AF_INET6, socket.SOCK_STREAM, 6, "", ("fe80::1%eth0", 0, 0, 2)),
        ]

    monkeypatch.setattr(socket, "getaddrinfo", fake_getaddrinfo)
    addresses = [entry["address"] for entry in netinfo.local_addresses()]
    assert addresses == ["10.0.0.7"]


def test_port_is_free_detects_a_listener() -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        sock.listen(1)
        port = sock.getsockname()[1]
        assert netinfo.port_is_free("127.0.0.1", port) is False
    assert netinfo.port_is_free("127.0.0.1", port) is True


def test_firewall_command_names_the_port() -> None:
    command = netinfo.firewall_command(8900)
    assert "8900" in command
    assert command.strip() != ""


def test_format_host_brackets_ipv6_once() -> None:
    assert netinfo.format_host("10.0.0.1") == "10.0.0.1"
    assert netinfo.format_host("fd00::1") == "[fd00::1]"
    assert netinfo.format_host("[fd00::1]") == "[fd00::1]"
