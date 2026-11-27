"""GTK window for APS. Import this module only after a display is selected.

pywebview is imported inside open_window(), after Gdk.Display.get_default()
returns a display. A headless process must never import this module.
"""

from __future__ import annotations

import logging

LOG = logging.getLogger("aps")


def display_alive() -> bool:
    try:
        import gi

        gi.require_version("Gtk", "3.0")
        from gi.repository import Gdk, Gtk
    except Exception as exc:
        LOG.error("GTK backend unavailable: %s", exc)
        return False
    try:
        result = Gtk.init_check()
        ok = result[0] if isinstance(result, tuple) else bool(result)
        if not ok:
            LOG.error("Gtk.init_check reported no display")
            return False
        if Gdk.Display.get_default() is None:
            LOG.error("Gdk.Display.get_default() is null")
            return False
    except Exception as exc:
        LOG.error("GTK display check failed: %s", exc)
        return False
    return True


def open_window(url: str) -> None:
    import webview

    webview.create_window("APS for iOS", url, width=1080, height=720)
    webview.start(gui="gtk")
