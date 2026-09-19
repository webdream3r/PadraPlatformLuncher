"""Model discovery for Padra Platform Launcher.

Two sources, both real:

* **Local** — every GGUF already downloaded on this machine. Unsloth stores
  models in the standard Hugging Face hub cache
  (``$HF_HUB_CACHE`` > ``$HF_HOME/hub`` > ``$XDG_CACHE_HOME/huggingface/hub`` >
  ``~/.cache/huggingface/hub``) and additionally detects models downloaded by
  LM Studio, so both layouts are scanned. Sizes are the real on-disk byte
  counts, de-duplicated by blob so a model is never counted twice.
* **Catalog** — the Unsloth organisation on Hugging Face, queried live through
  the public HTTP API for repositories, quantisation variants, file sizes and
  the context length recorded in the GGUF metadata.

Context length for a local model is read straight out of the GGUF header, so
the value is exact and available with no network connection.
"""

from __future__ import annotations

import os
import re
import struct
import time
from dataclasses import dataclass, field
from pathlib import Path
from shutil import which
from typing import Any, Dict, Iterable, Iterator, List, Optional, Sequence, Tuple

import httpx

HF_ENDPOINT = os.environ.get("HF_ENDPOINT", "https://huggingface.co").rstrip("/")
UNSLOTH_AUTHOR = "unsloth"

# Shard suffix used by llama.cpp for split GGUF files: name-00001-of-00003.gguf
SHARD_RE = re.compile(r"-\d{5}-of-\d{5}$")
# Quantisation tokens Unsloth publishes (UD- = Unsloth Dynamic).
QUANT_RE = re.compile(
    r"(?:UD-)?(?:I?Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?)$",
    re.IGNORECASE,
)

_GGUF_MAGIC = b"GGUF"
_GGUF_SCAN_LIMIT = 32 * 1024 * 1024  # stop parsing metadata after 32 MiB

# GGUF metadata value types.
_T_UINT8, _T_INT8, _T_UINT16, _T_INT16 = 0, 1, 2, 3
_T_UINT32, _T_INT32, _T_FLOAT32, _T_BOOL = 4, 5, 6, 7
_T_STRING, _T_ARRAY, _T_UINT64, _T_INT64, _T_FLOAT64 = 8, 9, 10, 11, 12

_FIXED_WIDTH = {
    _T_UINT8: ("B", 1), _T_INT8: ("b", 1),
    _T_UINT16: ("H", 2), _T_INT16: ("h", 2),
    _T_UINT32: ("I", 4), _T_INT32: ("i", 4), _T_FLOAT32: ("f", 4),
    _T_BOOL: ("?", 1),
    _T_UINT64: ("Q", 8), _T_INT64: ("q", 8), _T_FLOAT64: ("d", 8),
}


# --------------------------------------------------------------------------- #
# Data model
# --------------------------------------------------------------------------- #
@dataclass
class LocalModel:
    """One downloaded quantisation variant of one repository."""

    repo: str
    quant: str
    source: str                       # huggingface | lmstudio | folder
    size_bytes: int
    file_count: int
    modified: float
    path: str
    context_length: Optional[int] = None
    architecture: Optional[str] = None

    @property
    def spec(self) -> str:
        return f"{self.repo}:{self.quant}" if self.quant else self.repo

    def as_dict(self) -> Dict[str, Any]:
        return {
            "repo": self.repo,
            "quant": self.quant,
            "spec": self.spec,
            "source": self.source,
            "sizeBytes": self.size_bytes,
            "fileCount": self.file_count,
            "modified": self.modified,
            "path": self.path,
            "contextLength": self.context_length,
            "architecture": self.architecture,
            "downloaded": True,
        }


@dataclass
class CatalogVariant:
    repo: str
    quant: str
    size_bytes: int
    file_count: int
    context_length: Optional[int] = None

    def as_dict(self) -> Dict[str, Any]:
        return {
            "repo": self.repo,
            "quant": self.quant,
            "spec": f"{self.repo}:{self.quant}" if self.quant else self.repo,
            "sizeBytes": self.size_bytes,
            "fileCount": self.file_count,
            "contextLength": self.context_length,
            "downloaded": False,
        }


@dataclass
class CatalogRepo:
    repo: str
    downloads: int = 0
    likes: int = 0
    last_modified: str = ""
    context_length: Optional[int] = None
    variants: List[CatalogVariant] = field(default_factory=list)

    def as_dict(self) -> Dict[str, Any]:
        return {
            "repo": self.repo,
            "downloads": self.downloads,
            "likes": self.likes,
            "lastModified": self.last_modified,
            "contextLength": self.context_length,
            "variants": [v.as_dict() for v in self.variants],
        }


# --------------------------------------------------------------------------- #
# Cache roots
# --------------------------------------------------------------------------- #
def huggingface_cache_roots() -> List[Path]:
    """Every Hugging Face hub cache directory this machine may be using."""
    roots: List[Path] = []

    def add(candidate: Optional[Path]) -> None:
        if candidate is None:
            return
        resolved = candidate.expanduser()
        if resolved not in roots:
            roots.append(resolved)

    hub_cache = os.environ.get("HF_HUB_CACHE")
    if hub_cache:
        add(Path(hub_cache))
    hf_home = os.environ.get("HF_HOME")
    if hf_home:
        add(Path(hf_home) / "hub")
    xdg = os.environ.get("XDG_CACHE_HOME")
    if xdg:
        add(Path(xdg) / "huggingface" / "hub")
    add(Path.home() / ".cache" / "huggingface" / "hub")
    return roots


def lmstudio_roots() -> List[Path]:
    """LM Studio's model folder, which Unsloth Studio also picks up."""
    roots = [Path.home() / ".lmstudio" / "models", Path.home() / ".cache" / "lm-studio" / "models"]
    return [r for r in roots if r.exists()]


def unsloth_binary() -> Optional[str]:
    """Absolute path of the `unsloth` CLI, or None when it is not installed."""
    found = which("unsloth")
    if found:
        return found
    if os.name == "nt":
        for suffix in (".cmd", ".exe", ".bat"):
            found = which("unsloth" + suffix)
            if found:
                return found
    for candidate in (
        Path.home() / ".local" / "bin" / "unsloth",
        Path.home() / ".unsloth" / "bin" / "unsloth",
        Path.home() / ".local" / "share" / "unsloth" / "bin" / "unsloth",
    ):
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    return None


# --------------------------------------------------------------------------- #
# GGUF header
# --------------------------------------------------------------------------- #
class _GgufReader:
    """Sequential little/big-endian reader over the head of a GGUF file."""

    def __init__(self, handle: Any, byte_order: str) -> None:
        self._handle = handle
        self._order = byte_order
        self.consumed = 0

    def read(self, count: int) -> bytes:
        if self.consumed + count > _GGUF_SCAN_LIMIT:
            raise EOFError("gguf metadata exceeds the scan limit")
        chunk = self._handle.read(count)
        if len(chunk) != count:
            raise EOFError("truncated gguf metadata")
        self.consumed += count
        return chunk

    def scalar(self, fmt: str, size: int) -> Any:
        return struct.unpack(self._order + fmt, self.read(size))[0]

    def string(self) -> str:
        length = self.scalar("Q", 8)
        if length > 1 << 22:
            raise EOFError("implausible gguf string length")
        return self.read(int(length)).decode("utf-8", "replace")

    def value(self, value_type: int) -> Any:
        if value_type in _FIXED_WIDTH:
            fmt, size = _FIXED_WIDTH[value_type]
            return self.scalar(fmt, size)
        if value_type == _T_STRING:
            return self.string()
        if value_type == _T_ARRAY:
            item_type = self.scalar("I", 4)
            count = int(self.scalar("Q", 8))
            if item_type in _FIXED_WIDTH:
                _, size = _FIXED_WIDTH[item_type]
                self.read(count * size)          # skip the payload wholesale
                return []
            for _ in range(count):
                self.value(item_type)
            return []
        raise EOFError(f"unknown gguf value type {value_type}")


def read_gguf_metadata(path: Path) -> Dict[str, Any]:
    """Return ``{"architecture": str|None, "context_length": int|None}``.

    Never raises: an unreadable or non-GGUF file yields empty values.
    """
    result: Dict[str, Any] = {"architecture": None, "context_length": None}
    try:
        with path.open("rb") as handle:
            magic = handle.read(4)
            if magic != _GGUF_MAGIC:
                return result
            order = "<"
            version = struct.unpack(order + "I", handle.read(4))[0]
            if version > 0xFFFF:                 # byte-swapped file
                order = ">"
                version = struct.unpack(order + "I", struct.pack("<I", version))[0]
            if version < 2:
                return result

            reader = _GgufReader(handle, order)
            reader.scalar("Q", 8)                # tensor count
            kv_count = int(reader.scalar("Q", 8))

            architecture: Optional[str] = None
            pending: Dict[str, int] = {}
            for _ in range(kv_count):
                key = reader.string()
                value = reader.value(reader.scalar("I", 4))
                if key == "general.architecture" and isinstance(value, str):
                    architecture = value
                elif key.endswith(".context_length") and isinstance(value, int):
                    pending[key] = value
                if architecture and f"{architecture}.context_length" in pending:
                    break

            result["architecture"] = architecture
            if architecture and f"{architecture}.context_length" in pending:
                result["context_length"] = pending[f"{architecture}.context_length"]
            elif pending:
                result["context_length"] = next(iter(pending.values()))
    except (OSError, EOFError, struct.error, ValueError):
        return result
    return result


# --------------------------------------------------------------------------- #
# Quantisation naming
# --------------------------------------------------------------------------- #
def repo_basename(repo: str) -> str:
    """`unsloth/Qwen3.6-35B-A3B-GGUF` -> `Qwen3.6-35B-A3B`."""
    name = repo.split("/")[-1]
    for suffix in ("-GGUF", "-gguf", ".GGUF", ".gguf"):
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return name


def quant_from_relative_path(relative: str, repo: str) -> str:
    """Derive the `--model repo:QUANT` token from a file inside a repository."""
    normalised = relative.replace("\\", "/").strip("/")
    parts = normalised.split("/")
    stem = parts[-1]
    if stem.lower().endswith(".gguf"):
        stem = stem[: -len(".gguf")]
    stem = SHARD_RE.sub("", stem)

    if len(parts) > 1 and parts[-2]:
        # Sharded models live in a directory named after the quantisation.
        return parts[-2]

    base = repo_basename(repo)
    if stem.lower().startswith(base.lower() + "-"):
        candidate = stem[len(base) + 1:]
        if candidate:
            return candidate
    match = QUANT_RE.search(stem)
    if match:
        return match.group(0)
    return stem


# --------------------------------------------------------------------------- #
# Local scan
# --------------------------------------------------------------------------- #
def _unique_size(path: Path, seen: set) -> int:
    """Real size of `path`, counting each physical blob at most once.

    Hugging Face snapshots are symlinks into `blobs/`, so several snapshot
    entries can point at one file; resolving first keeps totals honest.
    """
    try:
        stat = path.stat()
    except OSError:
        return 0
    try:
        key = str(path.resolve())
    except OSError:
        key = str(path)
    if key in seen:
        return 0
    seen.add(key)
    return int(stat.st_size)


def _hf_repo_dirs(root: Path) -> Iterator[Tuple[str, Path]]:
    if not root.is_dir():
        return
    try:
        entries = sorted(root.iterdir())
    except OSError:
        return
    for entry in entries:
        if not entry.is_dir() or not entry.name.startswith("models--"):
            continue
        repo = entry.name[len("models--"):].replace("--", "/")
        yield repo, entry


def _scan_huggingface_root(root: Path, read_headers: bool) -> List[LocalModel]:
    models: List[LocalModel] = []
    for repo, repo_dir in _hf_repo_dirs(root):
        snapshots = repo_dir / "snapshots"
        if not snapshots.is_dir():
            continue
        grouped: Dict[str, List[Path]] = {}
        for snapshot in sorted(snapshots.iterdir()):
            if not snapshot.is_dir():
                continue
            for file in snapshot.rglob("*.gguf"):
                if not file.is_file():
                    continue
                relative = file.relative_to(snapshot).as_posix()
                grouped.setdefault(quant_from_relative_path(relative, repo), []).append(file)
        for quant, files in grouped.items():
            models.append(_build_local(repo, quant, files, "huggingface", read_headers))
    return models


def _scan_folder_root(root: Path, source: str, read_headers: bool) -> List[LocalModel]:
    """Scan a plain folder tree of GGUF files (LM Studio layout or user folder)."""
    if not root.is_dir():
        return []
    grouped: Dict[Tuple[str, str], List[Path]] = {}
    for file in root.rglob("*.gguf"):
        if not file.is_file():
            continue
        relative = file.relative_to(root)
        parts = relative.parts
        if len(parts) >= 3:
            repo = f"{parts[0]}/{parts[1]}"
            inner = Path(*parts[2:]).as_posix()
        elif len(parts) == 2:
            repo = parts[0]
            inner = parts[1]
        else:
            repo = root.name
            inner = parts[0]
        grouped.setdefault((repo, quant_from_relative_path(inner, repo)), []).append(file)
    return [
        _build_local(repo, quant, files, source, read_headers)
        for (repo, quant), files in grouped.items()
    ]


def _build_local(repo: str, quant: str, files: Sequence[Path], source: str,
                 read_headers: bool) -> LocalModel:
    seen: set = set()
    size = sum(_unique_size(f, seen) for f in files)
    modified = max((f.stat().st_mtime for f in files if f.exists()), default=0.0)
    header: Dict[str, Any] = {"architecture": None, "context_length": None}
    if read_headers and files:
        header = read_gguf_metadata(sorted(files)[0])
    return LocalModel(
        repo=repo,
        quant=quant,
        source=source,
        size_bytes=size,
        file_count=len(files),
        modified=modified,
        path=str(sorted(files)[0].parent) if files else "",
        context_length=header.get("context_length"),
        architecture=header.get("architecture"),
    )


def scan_local_models(extra_roots: Iterable[str] = (), *,
                      read_headers: bool = True) -> List[LocalModel]:
    """Every downloaded GGUF variant on this machine, largest first."""
    found: Dict[Tuple[str, str], LocalModel] = {}

    for root in huggingface_cache_roots():
        for model in _scan_huggingface_root(root, read_headers):
            found.setdefault((model.repo.lower(), model.quant.lower()), model)
    for root in lmstudio_roots():
        for model in _scan_folder_root(root, "lmstudio", read_headers):
            found.setdefault((model.repo.lower(), model.quant.lower()), model)
    for raw in extra_roots:
        root = Path(raw).expanduser()
        for model in _scan_folder_root(root, "folder", read_headers):
            found.setdefault((model.repo.lower(), model.quant.lower()), model)

    models = [m for m in found.values() if m.size_bytes > 0]
    models.sort(key=lambda m: (m.repo.lower(), -m.size_bytes))
    return models


# --------------------------------------------------------------------------- #
# Remote catalog
# --------------------------------------------------------------------------- #
class CatalogError(RuntimeError):
    """Raised when the Hugging Face API cannot be reached or answers badly."""


class Catalog:
    """Live view of the Unsloth GGUF repositories on Hugging Face."""

    def __init__(self, *, timeout: float = 20.0, ttl: float = 900.0) -> None:
        self._timeout = timeout
        self._ttl = ttl
        self._repos: Tuple[float, List[CatalogRepo]] = (0.0, [])
        self._variants: Dict[str, Tuple[float, CatalogRepo]] = {}

    # -- http ------------------------------------------------------------- #
    def _get(self, path: str, params: Optional[Dict[str, Any]] = None) -> Any:
        headers = {"Accept": "application/json"}
        token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGING_FACE_HUB_TOKEN")
        if token:
            headers["Authorization"] = f"Bearer {token}"
        try:
            response = httpx.get(f"{HF_ENDPOINT}{path}", params=params,
                                 headers=headers, timeout=self._timeout,
                                 follow_redirects=True)
        except httpx.HTTPError as exc:
            raise CatalogError(str(exc)) from exc
        if response.status_code != 200:
            raise CatalogError(f"HTTP {response.status_code} for {path}")
        try:
            return response.json()
        except ValueError as exc:
            raise CatalogError("the catalog response was not JSON") from exc

    # -- queries ----------------------------------------------------------- #
    def repositories(self, *, search: str = "", limit: int = 60,
                     refresh: bool = False) -> List[CatalogRepo]:
        query = search.strip()
        if not query and not refresh:
            cached_at, cached = self._repos
            if cached and time.time() - cached_at < self._ttl:
                return cached[:limit]

        params: Dict[str, Any] = {
            "author": UNSLOTH_AUTHOR,
            "filter": "gguf",
            "sort": "downloads",
            "direction": -1,
            "limit": max(1, min(limit, 200)),
        }
        if query:
            params["search"] = query
        payload = self._get("/api/models", params)
        if not isinstance(payload, list):
            raise CatalogError("unexpected catalog payload")

        repos = [
            CatalogRepo(
                repo=str(item.get("id") or ""),
                downloads=int(item.get("downloads") or 0),
                likes=int(item.get("likes") or 0),
                last_modified=str(item.get("lastModified") or ""),
            )
            for item in payload
            if item.get("id")
        ]
        if not query:
            self._repos = (time.time(), repos)
        return repos

    def variants(self, repo: str, *, refresh: bool = False) -> CatalogRepo:
        key = repo.lower()
        if not refresh:
            cached = self._variants.get(key)
            if cached and time.time() - cached[0] < self._ttl:
                return cached[1]

        meta = self._get(f"/api/models/{repo}")
        gguf_meta = meta.get("gguf") if isinstance(meta, dict) else None
        context_length = None
        if isinstance(gguf_meta, dict) and isinstance(gguf_meta.get("context_length"), int):
            context_length = int(gguf_meta["context_length"])

        tree = self._get(f"/api/models/{repo}/tree/main", {"recursive": 1})
        if not isinstance(tree, list):
            raise CatalogError("unexpected tree payload")

        grouped: Dict[str, List[int]] = {}
        for entry in tree:
            if entry.get("type") != "file":
                continue
            path = str(entry.get("path") or "")
            if not path.lower().endswith(".gguf"):
                continue
            size = entry.get("size")
            if not isinstance(size, int):
                size = int((entry.get("lfs") or {}).get("size") or 0)
            grouped.setdefault(quant_from_relative_path(path, repo), []).append(size)

        record = CatalogRepo(
            repo=repo,
            last_modified=str(meta.get("lastModified") or "") if isinstance(meta, dict) else "",
            downloads=int(meta.get("downloads") or 0) if isinstance(meta, dict) else 0,
            likes=int(meta.get("likes") or 0) if isinstance(meta, dict) else 0,
            context_length=context_length,
            variants=sorted(
                (
                    CatalogVariant(repo=repo, quant=quant, size_bytes=sum(sizes),
                                   file_count=len(sizes), context_length=context_length)
                    for quant, sizes in grouped.items()
                ),
                key=lambda v: v.size_bytes,
            ),
        )
        self._variants[key] = (time.time(), record)
        return record


# --------------------------------------------------------------------------- #
# Presentation helpers
# --------------------------------------------------------------------------- #
def merge_downloaded(catalog_repo: CatalogRepo,
                     local: Sequence[LocalModel]) -> Dict[str, Any]:
    """Catalog variants annotated with what is already on disk."""
    have = {
        (m.repo.lower(), m.quant.lower()): m
        for m in local
    }
    payload = catalog_repo.as_dict()
    for variant in payload["variants"]:
        match = have.get((catalog_repo.repo.lower(), str(variant["quant"]).lower()))
        if match is not None:
            variant["downloaded"] = True
            variant["sizeBytes"] = match.size_bytes
            variant["path"] = match.path
            if match.context_length:
                variant["contextLength"] = match.context_length
    return payload
