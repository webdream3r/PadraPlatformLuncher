"""Console entry point: `python -m launcher --config models.yaml`.

Importing `launcher.gateway` here (instead of running it as `__main__`) keeps a
single module instance, so the log buffer shared with the web UI is the same
object the gateway writes to.
"""

from launcher.gateway import main

if __name__ == "__main__":
    raise SystemExit(main())
