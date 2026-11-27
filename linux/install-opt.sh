#!/bin/sh
# Copy the launcher into /opt/aps and install unit files.
# The GUI unit is installed only as a user unit. It is never enabled
# under the system manager.
set -eu
ROOT="$(cd "$(dirname "$0")" && pwd)"
sudo install -d -m 0755 /opt/aps
sudo install -m 0644 "$ROOT/aps_launcher.py" "$ROOT/aps_gui.py" /opt/aps/
sudo install -m 0755 "$ROOT/aps-launcher" /opt/aps/aps-launcher
if ! id aps >/dev/null 2>&1; then
  sudo useradd --system --home /opt/aps --shell /usr/sbin/nologin aps
fi
sudo chown -R aps:aps /opt/aps
sudo install -m 0644 "$ROOT/systemd/system/aps.service" /etc/systemd/system/aps.service
sudo systemctl daemon-reload
echo "System unit installed but not enabled."
echo "Headless: sudo systemctl enable --now aps.service"
if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
  USER_HOME="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
  UNIT_DIR="$USER_HOME/.config/systemd/user"
  sudo -u "$SUDO_USER" mkdir -p "$UNIT_DIR"
  sudo -u "$SUDO_USER" cp "$ROOT/systemd/user/aps.service" "$UNIT_DIR/aps.service"
  echo "User unit copied to $UNIT_DIR/aps.service"
  echo "From the desktop session, import the display and start the user unit:"
  echo "  systemctl --user import-environment DISPLAY WAYLAND_DISPLAY XDG_RUNTIME_DIR"
  echo "  systemctl --user enable --now aps.service"
fi
