#!/bin/sh
# Build the headless folder distribution on Linux. Do not use --onefile.
# There is no GUI PyInstaller build: pywebview's GTK backend needs the
# distro python3-gi linked to system CPython.
set -eu
cd "$(dirname "$0")"
python3 -m pip install --user pyinstaller
python3 -m PyInstaller --noconfirm --clean pyinstaller-headless.spec
echo "Headless build: dist/aps-headless/"
echo "GUI stays on system Python: /usr/bin/python3 /opt/aps/aps_launcher.py"
