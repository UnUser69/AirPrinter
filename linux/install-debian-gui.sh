#!/bin/sh
# GUI packages for Debian/Ubuntu. Does not install a system service:
# a window must run inside the user graphical session.
set -eu
sudo apt update
sudo apt install -y \
  python3 \
  python3-pip \
  python3-gi \
  python3-gi-cairo \
  gir1.2-gtk-3.0
if apt-cache show gir1.2-webkit2-4.1 >/dev/null 2>&1; then
  sudo apt install -y gir1.2-webkit2-4.1
else
  sudo apt install -y gir1.2-webkit2-4.0
fi
python3 -m pip install --user -r "$(dirname "$0")/requirements-gui.txt"
echo "GUI packages installed."
echo "WebKit2GTK must be >= 2.22. Ubuntu 24.04 uses gir1.2-webkit2-4.1; older releases use gir1.2-webkit2-4.0."
echo "Enable the user unit, not a system unit: systemctl --user enable --now aps.service"
