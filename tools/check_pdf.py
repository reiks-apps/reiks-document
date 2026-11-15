#!/usr/bin/env python3
"""Check a PDF made by Reiks Document against the layout stored in the .docu file it was made from.

Usage: python check_pdf.py file.docu file.pdf [more pairs ...]

Needs the package PyMuPDF. For every page it compares the place of each character in the PDF
with the place the layout gives it:
  - every item of every line (body, footnotes and table cells),
  - every glyph of every equation,
  - the first character of list markers and of header and footer texts.
Characters whose place the layout does not state one by one are counted but not compared:
the rest of a marker or header text, footnote numbers, raised and lowered text, text in charts,
the hyphen of a word divided at the end of a line.
It also reports how many characters can be read out of the PDF.
Exit code 0 only if every compared character is within 0.01 point of its place.
"""
import json
import sys
import zipfile

import pymupdf

LIMIT = 0.01      # points
POINT = 12700     # EMU


def key(y):
    return round(y, 1)


def check(rks, pdf):
    layout = json.loads(zipfile.ZipFile(rks).read("layout.json"))
    content = json.loads(zipfile.ZipFile(rks).read("content.json"))
    # The items of each body paragraph that are citations: one item each, drawn as several characters.
    cites = {}
    for index, para in enumerate(content.get("body", [])):
        at = 0
        for run in para.get("runs", []):
            if "text" in run:
                at += len(run["text"].encode("utf-16-le")) // 2
            else:
                if "cite" in run:
                    cites.setdefault(index, set()).add(at)
                at += 1
    doc = pymupdf.open(pdf)
    compared = off = left_out = 0
    worst = 0.0
    if doc.page_count != len(layout["pages"]):
        print(f"{pdf}: {doc.page_count} pages, the layout has {len(layout['pages'])}")
        return False
    for number, page in enumerate(layout["pages"]):
        lines = list(page["lines"])
        for note in page.get("notes", {}).get("items", []):
            lines += note["lines"]
        for cell in page.get("cells", []):
            lines += cell["lines"]
        places = {}      # baseline -> x of everything that must stand exactly there
        starts = {}      # baseline -> left edge of each line on it: left of it stand markers and footnote numbers
        inside = []      # (baseline, left, right) of every citation: its first character is checked, the rest stand inside
        for line in lines:
            b = line["b"] / POINT
            x = line["x"]
            starts.setdefault(key(b), []).append(x / POINT)
            mine = cites.get(line["p"], ()) if "p" in line else ()
            for k, advance in enumerate(line["a"]):
                places.setdefault(key(b), []).append(x / POINT)
                if line.get("s", 0) + k in mine:
                    inside.append((b, x / POINT, (x + advance) / POINT))
                x += advance
            if line.get("hy") and line["a"]:
                # The hyphen of a divided word stands inside the room of the line's last item.
                inside.append((b, (x - line["a"][-1]) / POINT, x / POINT))
            if "mark" in line:
                places.setdefault(key(b), []).append(line["mark"]["x"] / POINT)
        charts = []
        in_cells = [obj for cell in page.get("cells", []) for obj in cell.get("objects", [])]
        for obj in page.get("objects", []) + in_cells:
            if obj["kind"] == "chart":
                charts.append((obj["x"] / POINT, obj["y"] / POINT, (obj["x"] + obj["w"]) / POINT, (obj["y"] + obj["h"]) / POINT))
            for piece in obj.get("draw", []):
                if piece[0] == "glyph":
                    places.setdefault(key(piece[2] / POINT), []).append(piece[1] / POINT)
        bands = set()
        for band in page.get("bands", []):
            places.setdefault(key(band["b"] / POINT), []).append(band["x"] / POINT)
            bands.add(key(band["b"] / POINT))

        for block in doc[number].get_text("rawdict")["blocks"]:
            for row in block.get("lines", []):
                for span in row["spans"]:
                    for ch in span["chars"]:
                        ox, oy = ch["origin"]
                        near = []
                        for k in (key(oy), key(oy + 0.1), key(oy - 0.1)):
                            near += places.get(k, [])
                        diff = min((abs(ox - x) for x in near), default=None)
                        if diff is not None and diff <= LIMIT:
                            compared += 1
                            worst = max(worst, diff)
                            continue
                        line_starts = starts.get(key(oy), []) + starts.get(key(oy + 0.1), []) + starts.get(key(oy - 0.1), [])
                        free = (ch["c"] == " "                                             # a space the reader made up for a gap, as between the parts of an equation
                                or diff is None                                            # no line on this baseline: raised or lowered text, a footnote number
                                or key(oy) in bands                                        # the rest of a header or footer text
                                or any(ox < s - LIMIT for s in line_starts)                # the rest of a list marker
                                or any(abs(oy - cb) <= 0.1 and x0 < ox < x1 for cb, x0, x1 in inside)  # the rest of a citation
                                or any(x0 <= ox <= x1 and y0 <= oy <= y1 for x0, y0, x1, y1 in charts))
                        if free:
                            left_out += 1
                        else:
                            compared += 1
                            off += 1
                            worst = max(worst, diff)
    text = sum(len(p.get_text()) for p in doc)
    print(f"{pdf}: pages {doc.page_count}, characters compared {compared}, off their place {off}, "
          f"largest offset {worst:.4f} pt, not compared {left_out}, readable characters {text}")
    return off == 0 and text > 0


def main(args):
    if len(args) < 2 or len(args) % 2:
        print(__doc__)
        return 2
    ok = True
    for i in range(0, len(args), 2):
        ok &= check(args[i], args[i + 1])
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
