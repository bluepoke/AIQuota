# PyInstaller spec - builds main.py into the single-file aiquota_plugin.exe that
# manifest.json's CodePath/CodePathWin points at. Run from this folder:
#   pyinstaller main.spec
# The result lands in dist/aiquota_plugin.exe - copy it next to main.py (i.e. into
# this .sdPlugin folder) before installing the plugin.

a = Analysis(
    ["main.py"],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=[],
    noarchive=False,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="aiquota_plugin",
    console=False,
    onefile=True,
)
