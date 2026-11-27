"""APS for iOS process launcher.

Display variables are inspected before any GUI library is loaded.
This module must not import ``webview`` or ``gi``. The GTK window lives in
``aps_gui`` and is loaded only after a live display check succeeds.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import shutil
import signal
import socket
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = logging.getLogger("aps")
_MODE = "headless"


def configure_logging() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        stream=sys.stdout,
    )


def forced_headless(argv: list[str]) -> bool:
    return os.environ.get("APS_HEADLESS") == "1" or "--headless" in argv


def display_env_present() -> bool:
    return bool(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"))


def is_frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def resolve_mode(argv: list[str]) -> str:
    if forced_headless(argv):
        return "headless"
    if is_frozen():
        LOG.error("frozen build has no GTK/WebKit backend; mode=headless")
        return "headless"
    if display_env_present():
        return "gui"
    return "headless"


def maybe_reexec_under_xvfb(argv: list[str]) -> None:
    """Optional CI/test fallback. Production must not set APS_XVFB."""
    if os.environ.get("APS_XVFB") != "1":
        return
    if forced_headless(argv) or display_env_present() or os.environ.get("APS_XVFB_INNER") == "1":
        return
    xvfb = shutil.which("xvfb-run")
    if not xvfb:
        LOG.error("APS_XVFB=1 but xvfb-run is missing; staying headless")
        return
    LOG.info("mode=xvfb-fallback")
    os.environ["APS_XVFB_INNER"] = "1"
    os.execvp(xvfb, [xvfb, "-a", sys.executable, os.path.abspath(__file__), *argv[1:]])


def probe_cups() -> dict:
    sock = socket.socket()
    sock.settimeout(0.5)
    try:
        sock.connect(("127.0.0.1", 631))
    except OSError as exc:
        return {"reachable": False, "error": str(exc)}
    finally:
        sock.close()
    return {"reachable": True, "port": 631}


def probe_avahi() -> dict:
    browse = shutil.which("avahi-browse")
    if not browse:
        return {"available": False, "printers": [], "error": "avahi-browse is not installed"}
    try:
        proc = subprocess.run(
            [browse, "-rptk", "-t", "_ipp._tcp"],
            capture_output=True,
            text=True,
            timeout=4,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"available": False, "printers": [], "error": str(exc)}
    printers = []
    for line in proc.stdout.splitlines():
        parts = line.split(";")
        if len(parts) >= 6 and parts[0] == "=":
            printers.append({"name": parts[3], "type": parts[4], "domain": parts[5]})
    return {
        "available": proc.returncode == 0,
        "printers": printers,
        "error": proc.stderr.strip() or None,
    }


def status_payload() -> dict:
    return {"ok": True, "mode": _MODE, "cups": probe_cups(), "avahi": probe_avahi()}


def page_html() -> bytes:
    body = f"""<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>APS for iOS</title>
<style>
  body {{ font-family: sans-serif; margin: 2rem; color: #1d1d1f; background: #fff; }}
  h1 {{ font-size: 1.4rem; }}
  pre {{ background: #f5f7fa; padding: 1rem; white-space: pre-wrap; }}
</style>
</head>
<body>
<h1>APS for iOS</h1>
<p>Режим: {_MODE}. Окно есть только в GUI. Headless не открывает браузер.</p>
<pre id="status">загрузка статуса…</pre>
<script>
fetch("/status").then(r => r.json()).then(data => {{
  document.getElementById("status").textContent = JSON.stringify(data, null, 2);
}}).catch(err => {{
  document.getElementById("status").textContent = String(err);
}});
</script>
</body>
</html>
"""
    return body.encode("utf-8")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt: str, *args) -> None:
        LOG.info("http " + fmt, *args)

    def do_GET(self) -> None:  # noqa: N802
        path = self.path.split("?", 1)[0]
        if path == "/health":
            self._json({"ok": True, "mode": _MODE})
            return
        if path == "/status":
            self._json(status_payload())
            return
        if path == "/":
            body = page_html()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self.send_error(404)

    def _json(self, payload: dict) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def make_server(host: str, port: int) -> ThreadingHTTPServer:
    server = ThreadingHTTPServer((host, port), Handler)
    server.daemon_threads = True
    return server


def serve_blocking(server: ThreadingHTTPServer) -> None:
    def stop(_signum, _frame) -> None:
        LOG.info("stopping")
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    server.serve_forever()


def open_gui(url: str) -> bool:
    """Load GTK only here, and only after the caller chose GUI mode."""
    try:
        import importlib

        gui = importlib.import_module("aps_gui")
    except Exception as exc:
        LOG.error("GUI module unavailable: %s", exc)
        return False
    try:
        if not gui.display_alive():
            LOG.error("not starting pywebview")
            return False
        gui.open_window(url)
    except Exception as exc:
        LOG.error("pywebview failed: %s", exc)
        return False
    return True


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="APS for iOS launcher")
    parser.add_argument("--headless", action="store_true", help="never open a window")
    parser.add_argument("--bind", default=os.environ.get("APS_BIND", "127.0.0.1"))
    parser.add_argument("--port", type=int, default=int(os.environ.get("APS_PORT", "8631")))
    return parser.parse_args(argv[1:])


def main(argv: list[str] | None = None) -> int:
    global _MODE
    configure_logging()
    argv = list(sys.argv if argv is None else argv)
    args = parse_args(argv)
    if not 1 <= args.port <= 65535:
        LOG.error("invalid port")
        return 2
    maybe_reexec_under_xvfb(argv)
    _MODE = resolve_mode(argv)
    if args.bind == "0.0.0.0":
        LOG.warning("status server is reachable on the network and has no authentication")
    LOG.info("mode=%s bind=%s:%s", _MODE, args.bind, args.port)
    try:
        server = make_server(args.bind, args.port)
    except OSError as exc:
        LOG.error("cannot bind status server: %s", exc)
        return 1
    url = f"http://{args.bind if args.bind != '0.0.0.0' else '127.0.0.1'}:{args.port}/"
    if _MODE == "gui":
        thread = threading.Thread(target=server.serve_forever, name="aps-http", daemon=True)
        thread.start()
        if open_gui(url):
            server.shutdown()
            return 0
        LOG.info("mode=headless")
        _MODE = "headless"
        serve_blocking(server)
        return 0
    serve_blocking(server)
    return 0


if __name__ == "__main__":
    sys.exit(main())
