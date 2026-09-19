"""Local model discovery: quant naming, GGUF headers and cache scanning."""

from __future__ import annotations

import struct
from pathlib import Path
from typing import Any, List, Tuple

import pytest

from launcher import discovery


# --------------------------------------------------------------------------- #
# GGUF writer used to build believable fixtures
# --------------------------------------------------------------------------- #
def _kv_string(key: str, value: str) -> bytes:
    return (struct.pack("<Q", len(key)) + key.encode() +
            struct.pack("<I", 8) +
            struct.pack("<Q", len(value)) + value.encode())


def _kv_uint32(key: str, value: int) -> bytes:
    return (struct.pack("<Q", len(key)) + key.encode() +
            struct.pack("<I", 4) + struct.pack("<I", value))


def _kv_array_uint8(key: str, count: int) -> bytes:
    return (struct.pack("<Q", len(key)) + key.encode() +
            struct.pack("<I", 9) + struct.pack("<I", 0) +
            struct.pack("<Q", count) + b"\x00" * count)


def write_gguf(path: Path, architecture: str, context_length: int,
               padding: int = 0) -> Path:
    payload = (_kv_string("general.architecture", architecture) +
               _kv_array_uint8("tokenizer.ggml.padding", 64) +
               _kv_uint32(f"{architecture}.context_length", context_length))
    header = b"GGUF" + struct.pack("<I", 3) + struct.pack("<Q", 0) + struct.pack("<Q", 3)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(header + payload + b"\x00" * padding)
    return path


# --------------------------------------------------------------------------- #
# Quantisation naming
# --------------------------------------------------------------------------- #
@pytest.mark.parametrize("relative, repo, expected", [
    ("Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf", "unsloth/Qwen3.6-35B-A3B-GGUF", "UD-Q4_K_XL"),
    ("Qwen3.6-35B-A3B-Q8_0.gguf", "unsloth/Qwen3.6-35B-A3B-GGUF", "Q8_0"),
    ("BF16/Qwen3.6-35B-A3B-BF16-00001-of-00002.gguf",
     "unsloth/Qwen3.6-35B-A3B-GGUF", "BF16"),
    ("Qwen3.5-4B-MXFP4_MOE.gguf", "unsloth/Qwen3.5-4B-GGUF", "MXFP4_MOE"),
    ("some-other-name-UD-IQ2_M.gguf", "unsloth/Whatever-GGUF", "UD-IQ2_M"),
])
def test_quant_is_derived_from_the_file_layout(relative: str, repo: str,
                                               expected: str) -> None:
    assert discovery.quant_from_relative_path(relative, repo) == expected


def test_repo_basename_strips_the_gguf_suffix() -> None:
    assert discovery.repo_basename("unsloth/Qwen3.6-35B-A3B-GGUF") == "Qwen3.6-35B-A3B"
    assert discovery.repo_basename("vendor/plain") == "plain"


# --------------------------------------------------------------------------- #
# GGUF metadata
# --------------------------------------------------------------------------- #
def test_gguf_context_length_is_read(tmp_path: Path) -> None:
    target = write_gguf(tmp_path / "model.gguf", "qwen35", 262144)
    meta = discovery.read_gguf_metadata(target)
    assert meta["architecture"] == "qwen35"
    assert meta["context_length"] == 262144


def test_unreadable_files_never_raise(tmp_path: Path) -> None:
    junk = tmp_path / "junk.gguf"
    junk.write_bytes(b"not a gguf file at all")
    assert discovery.read_gguf_metadata(junk) == {"architecture": None,
                                                  "context_length": None}
    assert discovery.read_gguf_metadata(tmp_path / "missing.gguf")["context_length"] is None


# --------------------------------------------------------------------------- #
# Cache scanning
# --------------------------------------------------------------------------- #
def build_hf_cache(root: Path, repo: str,
                   files: List[Tuple[str, int]]) -> Path:
    """Create a Hugging Face cache entry with snapshot symlinks into blobs."""
    folder = root / ("models--" + repo.replace("/", "--"))
    blobs = folder / "blobs"
    snapshot = folder / "snapshots" / "abc123"
    blobs.mkdir(parents=True, exist_ok=True)
    snapshot.mkdir(parents=True, exist_ok=True)
    for index, (name, size) in enumerate(files):
        blob = blobs / f"blob{index}"
        blob.write_bytes(b"\x00" * size)
        link = snapshot / name
        link.parent.mkdir(parents=True, exist_ok=True)
        link.symlink_to(blob)
    return folder


def test_scan_finds_models_and_sums_real_sizes(tmp_path: Path,
                                               monkeypatch: pytest.MonkeyPatch) -> None:
    cache = tmp_path / "hub"
    build_hf_cache(cache, "unsloth/Qwen3.5-4B-GGUF", [
        ("Qwen3.5-4B-UD-Q4_K_XL.gguf", 2048),
        ("Qwen3.5-4B-Q8_0.gguf", 4096),
        ("BF16/Qwen3.5-4B-BF16-00001-of-00002.gguf", 1024),
        ("BF16/Qwen3.5-4B-BF16-00002-of-00002.gguf", 1024),
        ("README.md", 10),
    ])
    monkeypatch.setattr(discovery, "huggingface_cache_roots", lambda: [cache])
    monkeypatch.setattr(discovery, "lmstudio_roots", lambda: [])

    models = discovery.scan_local_models(read_headers=False)
    by_quant = {m.quant: m for m in models}
    assert set(by_quant) == {"UD-Q4_K_XL", "Q8_0", "BF16"}
    assert by_quant["UD-Q4_K_XL"].size_bytes == 2048
    assert by_quant["BF16"].size_bytes == 2048
    assert by_quant["BF16"].file_count == 2
    assert by_quant["Q8_0"].repo == "unsloth/Qwen3.5-4B-GGUF"
    assert by_quant["Q8_0"].spec == "unsloth/Qwen3.5-4B-GGUF:Q8_0"
    assert all(m.source == "huggingface" for m in models)


def test_duplicate_snapshots_are_counted_once(tmp_path: Path,
                                              monkeypatch: pytest.MonkeyPatch) -> None:
    cache = tmp_path / "hub"
    folder = build_hf_cache(cache, "unsloth/X-GGUF", [("X-Q4_K_M.gguf", 5000)])
    second = folder / "snapshots" / "def456"
    second.mkdir(parents=True)
    (second / "X-Q4_K_M.gguf").symlink_to(folder / "blobs" / "blob0")

    monkeypatch.setattr(discovery, "huggingface_cache_roots", lambda: [cache])
    monkeypatch.setattr(discovery, "lmstudio_roots", lambda: [])
    models = discovery.scan_local_models(read_headers=False)
    assert len(models) == 1
    assert models[0].size_bytes == 5000


def test_context_length_comes_from_the_header(tmp_path: Path,
                                              monkeypatch: pytest.MonkeyPatch) -> None:
    cache = tmp_path / "hub"
    folder = cache / "models--unsloth--Tiny-GGUF" / "snapshots" / "s1"
    write_gguf(folder / "Tiny-Q4_K_M.gguf", "tiny", 8192, padding=256)
    monkeypatch.setattr(discovery, "huggingface_cache_roots", lambda: [cache])
    monkeypatch.setattr(discovery, "lmstudio_roots", lambda: [])

    models = discovery.scan_local_models()
    assert len(models) == 1
    assert models[0].context_length == 8192
    assert models[0].architecture == "tiny"
    assert models[0].as_dict()["downloaded"] is True


def test_extra_folders_are_scanned(tmp_path: Path,
                                   monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(discovery, "huggingface_cache_roots", lambda: [])
    monkeypatch.setattr(discovery, "lmstudio_roots", lambda: [])
    folder = tmp_path / "models" / "vendor" / "Some-GGUF"
    folder.mkdir(parents=True)
    (folder / "Some-Q4_K_M.gguf").write_bytes(b"\x00" * 1500)

    models = discovery.scan_local_models([str(tmp_path / "models")], read_headers=False)
    assert len(models) == 1
    assert models[0].repo == "vendor/Some-GGUF"
    assert models[0].quant == "Q4_K_M"
    assert models[0].source == "folder"


def test_cache_roots_follow_the_environment(monkeypatch: pytest.MonkeyPatch,
                                            tmp_path: Path) -> None:
    monkeypatch.setenv("HF_HUB_CACHE", str(tmp_path / "explicit"))
    monkeypatch.setenv("HF_HOME", str(tmp_path / "hf"))
    roots = discovery.huggingface_cache_roots()
    assert roots[0] == tmp_path / "explicit"
    assert tmp_path / "hf" / "hub" in roots


# --------------------------------------------------------------------------- #
# Catalog
# --------------------------------------------------------------------------- #
class _Response:
    def __init__(self, payload: Any, status: int = 200) -> None:
        self._payload = payload
        self.status_code = status

    def json(self) -> Any:
        return self._payload


def test_catalog_groups_variants_and_marks_downloads(monkeypatch: pytest.MonkeyPatch) -> None:
    pages = {
        "/api/models/unsloth/Demo-GGUF": {
            "lastModified": "2026-01-01", "downloads": 5, "likes": 1,
            "gguf": {"context_length": 32768},
        },
        "/api/models/unsloth/Demo-GGUF/tree/main": [
            {"type": "file", "path": "Demo-Q4_K_M.gguf", "size": 100},
            {"type": "file", "path": "Demo-Q8_0.gguf", "size": 300},
            {"type": "file", "path": "README.md", "size": 5},
            {"type": "directory", "path": "BF16"},
            {"type": "file", "path": "BF16/Demo-BF16-00001-of-00002.gguf",
             "lfs": {"size": 400}},
            {"type": "file", "path": "BF16/Demo-BF16-00002-of-00002.gguf",
             "lfs": {"size": 100}},
        ],
    }

    def fake_get(url: str, params=None, headers=None, timeout=None,
                 follow_redirects=None) -> _Response:
        for suffix, payload in pages.items():
            if url.endswith(suffix):
                return _Response(payload)
        raise AssertionError(f"unexpected url {url}")

    monkeypatch.setattr(discovery.httpx, "get", fake_get)
    catalog = discovery.Catalog()
    record = catalog.variants("unsloth/Demo-GGUF")
    sizes = {v.quant: v.size_bytes for v in record.variants}
    assert sizes == {"Q4_K_M": 100, "Q8_0": 300, "BF16": 500}
    assert all(v.context_length == 32768 for v in record.variants)

    local = [discovery.LocalModel(repo="unsloth/Demo-GGUF", quant="Q8_0",
                                  source="huggingface", size_bytes=299,
                                  file_count=1, modified=0.0, path="/tmp")]
    merged = discovery.merge_downloaded(record, local)
    states = {v["quant"]: v["downloaded"] for v in merged["variants"]}
    assert states == {"Q4_K_M": False, "Q8_0": True, "BF16": False}


def test_catalog_errors_are_wrapped(monkeypatch: pytest.MonkeyPatch) -> None:
    def boom(*args: Any, **kwargs: Any) -> _Response:
        return _Response({}, status=503)

    monkeypatch.setattr(discovery.httpx, "get", boom)
    with pytest.raises(discovery.CatalogError):
        discovery.Catalog().repositories(refresh=True)
