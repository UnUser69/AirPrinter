#!/bin/sh
# Headless packages. GTK and WebKit are intentionally absent.
set -eu
sudo apt update
sudo apt install -y \
  python3 \
  cups \
  avahi-daemon \
  avahi-utils \
  libnss-mdns
sudo systemctl enable --now cups.service avahi-daemon.service
echo "Headless packages installed. GTK was not installed."
echo "Then: systemctl daemon-reload && systemctl enable --now aps.service"
