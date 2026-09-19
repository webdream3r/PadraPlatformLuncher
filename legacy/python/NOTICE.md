# Previous implementation (Python)

This is the Python/FastAPI version of the launcher, kept for reference after the
rebuild on .NET 8. It is no longer the supported entry point; nothing in the
current application reads from this folder.

It is preserved with the fix for the crash that windowed builds hit
(`uvicorn`'s default log formatter calls `sys.stdout.isatty()`, and a windowed
build has no console): see `launcher/gateway.py`, `uvicorn_log_config()`.

To run it anyway: `python app.pyw`, or `python -m launcher --config models.yaml`.
