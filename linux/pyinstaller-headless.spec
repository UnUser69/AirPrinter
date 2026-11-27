# -*- mode: python ; coding: utf-8 -*-
# Headless onedir build for PyInstaller 6.
# GTK, WebKit, and pywebview are excluded: a frozen interpreter cannot
# import the distro python3-gi package.

a = Analysis(
    ["aps_launcher.py"],
    pathex=["."],
    binaries=[],
    datas=[],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["aps_gui", "webview", "gi", "PyGObject"],
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    exclude_binaries=True,
    name="aps-headless",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    name="aps-headless",
)
