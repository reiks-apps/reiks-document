"""Builds the setup file of Reiks Document: dist/ReiksDocumentSetup-<version>.exe.

    python tools/make_setup.py            build the program, then the setup file
    python tools/make_setup.py --no-build use the program as it was last built (when Reiks Document is open)

The setup file is one program that carries Reiks Document inside. The version is the one in ReiksDocument.csproj.
Everything is built with what is on this PC; nothing is downloaded.
"""
import pathlib
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "bin" / "Release" / "net10.0-windows"
FILES = ["ReiksDocument.exe", "ReiksDocument.dll", "ReiksDocument.deps.json", "ReiksDocument.runtimeconfig.json", "assets/DocuFile.ico"]


def run(*args):
    done = subprocess.run(args, cwd=ROOT, capture_output=True, text=True)
    if done.returncode != 0:
        sys.exit(done.stdout[-3000:] + done.stderr[-1000:])


def main():
    version = re.search(r"<Version>(.*?)</Version>", (ROOT / "ReiksDocument.csproj").read_text(encoding="utf-8")).group(1)
    if "--no-build" not in sys.argv:
        run("dotnet", "build", "ReiksDocument.csproj", "-c", "Release", "-nologo", "-v", "q")
    with zipfile.ZipFile(ROOT / "setup" / "payload.zip", "w", zipfile.ZIP_DEFLATED) as z:
        for name in FILES:
            z.write(OUT / name, name)
        # The lists of patterns for dividing words, with their licence notices.
        for item in sorted((OUT / "hyph").glob("*")):
            if item.is_file():
                z.write(item, "hyph/" + item.name)
    publish = ROOT / "setup" / "publish"
    run("dotnet", "publish", "setup/ReiksDocumentSetup.csproj", "-c", "Release", "-nologo", "-v", "q", "-p:Version=" + version, "-o", str(publish))
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    target = dist / ("ReiksDocumentSetup-" + version + ".exe")
    shutil.copyfile(publish / "ReiksDocumentSetup.exe", target)
    print(target, target.stat().st_size, "bytes")


if __name__ == "__main__":
    main()
