# APS for iOS on Linux

This directory is the Linux process model for APS. The Windows desktop app in `src/AirPrinter` is unchanged. The launcher serves a local status page, checks CUPS and Avahi, and opens a GTK window only when a display is alive.

## Modes

`APS_HEADLESS=1` or `--headless` forces headless. Otherwise `DISPLAY` or `WAYLAND_DISPLAY` selects GUI. With neither variable, the process stays headless. `APS_XVFB=1` is an explicit CI fallback that re-execs under `xvfb-run`; production units do not set it.

`aps_launcher.py` does not import `webview` or `gi`. Those imports live in `aps_gui.py` and run only after `Gtk.init_check()` and `Gdk.Display.get_default()` succeed. A dead display is logged, then the process keeps serving HTTP.

The status server binds `127.0.0.1:8631` unless `APS_BIND` / `APS_PORT` or `--bind` / `--port` say otherwise. Nothing in this tree calls a browser.

## Packages

Debian/Ubuntu GUI:

```bash
sh install-debian-gui.sh
```

That installs `python3-gi`, GTK 3, and WebKit2GTK 4.1 when the distro has it, otherwise 4.0. WebKit2GTK must be at least 2.22. Fedora equivalents are `python3-gobject gtk3 webkit2gtk4.1`. Arch equivalents are `python-gobject gtk3 webkit2gtk-4.1`.

Debian/Ubuntu headless:

```bash
sh install-debian-headless.sh
```

That installs CUPS, Avahi, and `libnss-mdns`. It does not install GTK.

Package names differ by distro and by Ubuntu release. Do not treat this list as universal.

## systemd

GUI is a user service: `systemd/user/aps.service`, wanted by `default.target`, after `graphical-session.target`. A user service does not inherit the desktop environment by itself. In the session, run:

```bash
systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XDG_RUNTIME_DIR
systemctl --user enable --now aps.service
```

`loginctl enable-linger` keeps the user manager running without a seat. It does not create a display. Do not use linger to pretend a GUI service has X11.

Headless is a system service: `systemd/system/aps.service`, user `aps`, `APS_HEADLESS=1`, after `network-online.target`, `cups.service`, and `avahi-daemon.service`.

```bash
sudo sh install-opt.sh
sudo systemctl enable --now aps.service
```

`install-opt.sh` copies the system unit and does not enable it. It never installs the GUI unit into `/etc/systemd/system`.

## Builds

Headless, on a Linux machine, folder layout only:

```bash
sh build-headless.sh
```

Equivalent command:

```bash
python3 -m PyInstaller --noconfirm --clean --onedir \
  --name aps-headless \
  --exclude-module aps_gui \
  --exclude-module webview \
  --exclude-module gi \
  --exclude-module PyGObject \
  aps_launcher.py
```

There is no GUI PyInstaller command. The frozen interpreter cannot import the distro `python3-gi` / WebKit stack, and this project does not vendor those libraries. GUI always starts through `/usr/bin/python3` and `/opt/aps/aps-launcher`. The frozen headless binary refuses the GUI path even if `DISPLAY` is set.

## Check

- GUI: a window opens on X11 and on Wayland after the user unit imports the display variables.
- GUI: unsetting `DISPLAY` and `WAYLAND_DISPLAY` logs `mode=headless` and does not start GTK.
- A display variable with a dead session fails `Gtk.init_check` or `Gdk.Display.get_default()` and stays on the HTTP server.
- Headless: `systemctl status aps.service` is active with no display, and `journalctl -u aps.service` shows `mode=headless`.
- `curl -s http://127.0.0.1:8631/status` reports CUPS on port 631 and `_ipp._tcp` names from `avahi-browse` when those services are up.
- Headless logs contain no browser launch. `xvfb-run` is absent unless `APS_XVFB=1` was set for a test.

## System libraries

GTK 3, PyGObject, and WebKit2GTK come from the distribution. They are not part of the headless folder build and cannot be packed into it reliably. A machine without those packages can run the headless service. A machine without them cannot open the GUI, and the launcher then logs the failure and serves HTTP instead of exiting.
