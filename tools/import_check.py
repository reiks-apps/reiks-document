"""Imports Word documents and measures how tidy the results are.

    python tools/import_check.py <out folder> <file or folder> [more files or folders ...]

Every .docx, .dotx and .docm found is read with ReiksDocument.exe --import into the out folder (the Word files are
only read). For each result the stored layout is looked through for things that are not tidy:

  - lines that reach past the right edge of the text by more than a quarter of an inch
    (spaces at the end of a line hang over the edge on purpose, so a few of these are no fault),
  - pictures, equations and charts outside the width of the text, or wider than the table cell they stand in,
  - table cells past the right edge, very narrow cells, and lines of a cell that reach past it,
  - pages with nothing on them.

It also says how the number of pages compares with the number Word wrote into the file, which notes the import gave,
and which files could not be read. Exit code 0 if every file was read.
"""
import collections
import io
import json
import os
import pathlib
import re
import subprocess
import sys
import time
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
EXE = ROOT / "bin" / "Release" / "net10.0-windows" / "ReiksDocument.exe"


def find(arguments):
    files = []
    for a in arguments:
        if os.path.isdir(a):
            for folder, _, names in os.walk(a):
                files += [os.path.join(folder, n) for n in names if n.lower().endswith((".docx", ".dotx", ".docm")) and not n.startswith("~$")]
        else:
            files.append(a)
    return sorted(files)


def untidy(content, layout):
    page = content["page"]
    right, bottom = page["width"] - page["right"], page["height"] - page["bottom"]
    m = collections.Counter()
    for p in layout["pages"]:
        if not p.get("lines") and not p.get("cells") and not p.get("objects"):
            m["empty pages"] += 1
        for line in p.get("lines", []):
            if line["x"] + sum(line.get("a", [])) > right + 228600:
                m["lines past the right edge"] += 1
            if "row" not in line and line["y"] + line["h"] > bottom + 12700:
                m["lines below the text area"] += 1
        for o in p.get("objects", []):
            if o.get("wrap") not in ("behind", "front") and (o["x"] + o["w"] > right + 12700 or o["x"] < page["left"] - 12700):
                m["objects outside the text width"] += 1
        for cell in p.get("cells", []):
            if cell["x"] + cell["w"] > right + 12700:
                m["table cells past the right edge"] += 1
            if cell["w"] < 250000:
                m["very narrow cells"] += 1
            for line in cell.get("lines", []):
                if line["x"] + sum(line.get("a", [])) > cell["x"] + cell["w"] + 36000:
                    m["cell lines past the cell"] += 1
            for o in cell.get("objects", []):
                if o["x"] + o["w"] > cell["x"] + cell["w"] + 12700:
                    m["objects past their cell"] += 1
    return m


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    out = pathlib.Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    files = find(sys.argv[2:])
    notes, total, rows, failed = collections.Counter(), collections.Counter(), [], []
    for n, f in enumerate(files):
        docu, txt = out / f"{n}.docu", out / f"{n}.txt"
        for old in (docu, txt):
            if old.exists():
                old.unlink()
        started = time.time()
        try:
            code = subprocess.run([str(EXE), "--import", f, str(docu), str(txt)], timeout=300).returncode
        except subprocess.TimeoutExpired:
            code = "took too long"
        if code != 0 or not docu.exists():
            failed.append((n, code, f))
            continue
        for line in io.open(txt, encoding="utf-8").read().split("\n")[1:]:
            if line.strip():
                notes[re.sub(r"\d+", "n", line)[:110]] += 1
        with zipfile.ZipFile(docu) as z:
            content, layout = json.loads(z.read("content.json")), json.loads(z.read("layout.json"))
        found = untidy(content, layout)
        total.update(found)
        word_pages = None
        try:
            got = re.search(r"<Pages>(\d+)</Pages>", zipfile.ZipFile(f).read("docProps/app.xml").decode("utf8", "replace"))
            word_pages = int(got.group(1)) if got else None
        except (KeyError, zipfile.BadZipFile, OSError):
            pass
        rows.append((n, round(time.time() - started, 1), len(layout["pages"]), word_pages, dict(found), os.path.basename(f)))

    def plain(text):
        return text.encode("ascii", "replace").decode()

    print("files", len(files), "read", len(rows), "not read", len(failed))
    for n, code, f in failed:
        print("  NOT READ", n, code, plain(f)[-90:])
    print("untidy, in all:", dict(total))
    for r in sorted(rows, key=lambda r: -sum(r[4].values()))[:12]:
        if r[4]:
            print("  ", r[0], r[4], "pages", r[2], "Word", r[3], plain(r[5])[:50])
    both = [(r[2], r[3]) for r in rows if r[3]]
    print("pages against Word's own count, in", len(both), "files: same", sum(a == b for a, b in both), "more here", sum(a > b for a, b in both), "fewer here", sum(a < b for a, b in both))
    print("slowest:", [(r[0], r[1]) for r in sorted(rows, key=lambda r: -r[1])[:3]])
    for text, count in notes.most_common(30):
        print(count, plain(text))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
