r"""Draws the icons of Reiks Document.

    python tools\make_icon.py            the two icons in use: the program (a white U on a blue tile)
                                         and a document (a sheet with a blue U)
    python tools\make_icon.py 3          design 3 of the list as the icon of the program
    python tools\make_icon.py sheet      all designs of the list side by side, to choose from

Writes assets\ReiksDocument.ico and assets\DocuFile.ico (16 to 256 pixels) with a .png of each (256 pixels);
"sheet" writes assets\icon-designs.png instead and leaves the icons alone. Needs the package Pillow.
Small sizes are drawn with fewer and thicker strokes, so that they stay clear.
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(HERE, "assets")
BLUE = ((58, 118, 232), (24, 56, 150))           # a tile, from top to bottom
TEAL = ((38, 166, 154), (0, 96, 100))
INK = ((66, 72, 90), (24, 26, 36))
WHITE, FOLD = (255, 255, 255), (206, 218, 240)
LINE, ORANGE = (132, 156, 204), (255, 166, 32)
NAMES = ["Sheet with a caret", "Letter R with a caret", "Pen nib", "Sheet and pencil", "Paragraph sign", "Typed line with a caret",
         "U with a caret", "Sheet with a U", "U as one line", "The word udoc", "Dot and u", "U in a sheet outline",
         "U made of text lines", "U with a folded corner", "u in a disc", "U ending in a pen nib", "u with a dark caret", "U on a light tile"]


def tile(s, colours):
    """A flat tile in one colour, with round corners: no shading."""
    out = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(out).rounded_rectangle((0, 0, s - 1, s - 1), radius=round(s * 0.22), fill=mid(colours) + (255,))
    return out


def font(name, px):
    try:
        return ImageFont.truetype(os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts", name), px)
    except OSError:
        return ImageFont.load_default()


def mid(colours):
    return tuple(round((colours[0][i] + colours[1][i]) / 2) for i in range(3))


def sheet_shape(d, s, small, colours, box=None):
    """A white sheet with a folded corner; gives back its box."""
    left, right, top, bottom = box or ((0.24, 0.76, 0.14, 0.86) if not small else (0.22, 0.78, 0.12, 0.88))
    x0, x1, y0, y1 = left * s, right * s, top * s, bottom * s
    f = (0.16 if not small else 0.18) * s
    d.rounded_rectangle((x0, y0, x1, y1), radius=round(s * 0.035), fill=WHITE)
    d.polygon([(x1 - f, y0 - 1), (x1 + 1, y0 - 1), (x1 + 1, y0 + f)], fill=mid(colours))
    d.polygon([(x1 - f, y0), (x1 - f, y0 + f), (x1, y0 + f)], fill=FOLD)
    return x0, x1, y0, y1


def text_lines(d, s, small, x0, x1, rows, caret=True, colour=LINE):
    thick = (0.05 if not small else 0.085) * s
    for i, r in enumerate(rows):
        last = i == len(rows) - 1
        end = x0 + (x1 - x0) * 0.52 if last and caret else x1
        y = r * s
        d.rounded_rectangle((x0, y - thick / 2, end, y + thick / 2), radius=thick / 2, fill=colour)
        if last and caret:
            w = thick * 0.85
            cx = end + thick * 0.9
            d.rounded_rectangle((cx, y - thick * 1.5, cx + w, y + thick * 1.5), radius=w / 2, fill=ORANGE)


def design1(s, small):
    im = tile(s, BLUE)
    d = ImageDraw.Draw(im)
    x0, x1, _, _ = sheet_shape(d, s, small, BLUE)
    pad = (x1 - x0) * 0.16
    text_lines(d, s, small, x0 + pad, x1 - pad, [0.40, 0.52, 0.64, 0.76] if not small else [0.44, 0.60, 0.76])
    return im


def design2(s, small):
    im = tile(s, BLUE)
    d = ImageDraw.Draw(im)
    f = font("georgiab.ttf", round(s * 0.68))
    box = d.textbbox((0, 0), "R", font=f)
    w, h = box[2] - box[0], box[3] - box[1]
    bar = s * (0.07 if not small else 0.09)
    x = (s - w - bar * 1.9) / 2 - box[0]
    y = (s - h) / 2 - box[1]
    d.text((x, y), "R", font=f, fill=WHITE)
    cx = x + box[2] + bar * 0.9
    d.rounded_rectangle((cx, y + box[1] - s * 0.03, cx + bar, y + box[3] + s * 0.03), radius=bar / 2, fill=ORANGE)
    return im


def design3(s, small):
    im = tile(s, TEAL)
    d = ImageDraw.Draw(im)
    p = lambda x, y: (x * s, y * s)
    d.polygon([p(0.37, 0.13), p(0.63, 0.13), p(0.72, 0.50), p(0.50, 0.88), p(0.28, 0.50)], fill=WHITE)
    d.rectangle((0.37 * s, 0.13 * s, 0.63 * s, 0.23 * s), fill=(214, 240, 236))
    back = mid(TEAL)
    slit = s * (0.022 if not small else 0.04)
    d.rectangle((0.5 * s - slit / 2, 0.47 * s, 0.5 * s + slit / 2, 0.80 * s), fill=back)
    r = s * (0.05 if not small else 0.065)
    d.ellipse((0.5 * s - r, 0.44 * s - r, 0.5 * s + r, 0.44 * s + r), fill=back)
    return im


def design4(s, small):
    im = tile(s, BLUE)
    d = ImageDraw.Draw(im)
    x0, x1, _, _ = sheet_shape(d, s, small, BLUE, (0.18, 0.66, 0.14, 0.86))
    pad = (x1 - x0) * 0.16
    text_lines(d, s, small, x0 + pad, x1 - pad, [0.40, 0.52, 0.64] if not small else [0.44, 0.62], caret=False)
    # The pencil is drawn upright and then turned.
    pen = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    q = ImageDraw.Draw(pen)
    w = s * (0.15 if not small else 0.19)
    cx, top, bottom = s / 2, s * 0.10, s * 0.74
    q.rounded_rectangle((cx - w / 2, top, cx + w / 2, top + w * 0.7), radius=w * 0.25, fill=(240, 120, 130))
    q.rectangle((cx - w / 2, top + w * 0.6, cx + w / 2, top + w * 0.95), fill=(200, 205, 215))
    q.rectangle((cx - w / 2, top + w * 0.95, cx + w / 2, bottom), fill=ORANGE)
    q.polygon([(cx - w / 2, bottom), (cx + w / 2, bottom), (cx, bottom + w * 1.15)], fill=(242, 220, 184))
    q.polygon([(cx - w * 0.17, bottom + w * 0.76), (cx + w * 0.17, bottom + w * 0.76), (cx, bottom + w * 1.15)], fill=(52, 56, 70))
    pen = pen.rotate(-38, resample=Image.BICUBIC, center=(s / 2, s / 2), translate=(s * 0.19, s * 0.07))
    im.alpha_composite(pen)
    return im


def design5(s, small):
    im = tile(s, INK)
    d = ImageDraw.Draw(im)
    f = font("georgiab.ttf", round(s * 0.74))
    box = d.textbbox((0, 0), "\u00b6", font=f)
    x = (s - (box[2] - box[0])) / 2 - box[0]
    y = (s - (box[3] - box[1])) / 2 - box[1]
    d.text((x, y), "\u00b6", font=f, fill=WHITE)
    bar = s * (0.06 if not small else 0.08)
    d.rounded_rectangle((s * 0.24, s * 0.84, s * 0.76, s * 0.84 + bar), radius=bar / 2, fill=ORANGE)
    return im


def design6(s, small):
    im = tile(s, INK)
    d = ImageDraw.Draw(im)
    thick = s * (0.07 if not small else 0.10)
    rows = [0.32, 0.50, 0.68] if not small else [0.34, 0.66]
    for i, r in enumerate(rows):
        last = i == len(rows) - 1
        end = s * (0.50 if last else 0.80)
        d.rounded_rectangle((s * 0.20, r * s - thick / 2, end, r * s + thick / 2), radius=thick / 2, fill=WHITE if not last else (200, 208, 226))
    y = rows[-1] * s
    cx = s * 0.58
    d.rounded_rectangle((cx, y - thick * 1.7, cx + thick * 0.8, y + thick * 1.7), radius=thick * 0.4, fill=ORANGE)
    for yy in (y - thick * 1.7, y + thick * 1.7 - thick * 0.5):
        d.rounded_rectangle((cx - thick * 0.7, yy, cx + thick * 1.5, yy + thick * 0.5), radius=thick * 0.25, fill=ORANGE)
    return im


def letter(d, s, text, name, height, colour, cx=0.5, cy=0.5):
    """Draws text with its ink centred at cx, cy (as parts of the size); gives back the box of the ink."""
    f = font(name, round(s * height))
    box = d.textbbox((0, 0), text, font=f)
    x = cx * s - (box[0] + box[2]) / 2
    y = cy * s - (box[1] + box[3]) / 2
    d.text((x, y), text, font=f, fill=colour)
    return x + box[0], y + box[1], x + box[2], y + box[3]


def design7(s, small):
    """A white U with the caret after it."""
    im = tile(s, BLUE)
    d = ImageDraw.Draw(im)
    bar = s * (0.07 if not small else 0.09)
    x0, y0, x1, y1 = letter(d, s, "U", "segoeuib.ttf", 0.72, WHITE, cx=0.45)
    d.rounded_rectangle((x1 + bar * 0.8, y0 - s * 0.02, x1 + bar * 1.8, y1 + s * 0.02), radius=bar / 2, fill=ORANGE)
    return im


def design8(s, small):
    """A sheet with a blue U on it."""
    im = tile(s, BLUE)
    d = ImageDraw.Draw(im)
    sheet_shape(d, s, small, BLUE)
    letter(d, s, "U", "segoeuib.ttf", 0.46 if not small else 0.52, mid(BLUE), cy=0.56)
    return im


def design9(s, small):
    """A U drawn as one thick line, its right arm ending in an orange caret."""
    im = tile(s, TEAL)
    d = ImageDraw.Draw(im)
    w = s * (0.13 if not small else 0.16)
    left, right, top, bottom = s * 0.27, s * 0.73, s * 0.20, s * 0.80
    r = (right - left) / 2
    d.rounded_rectangle((left, top, left + w, bottom - r), radius=w / 2, fill=WHITE)
    d.rectangle((left, top + w, left + w, bottom - r), fill=WHITE)
    d.rectangle((right - w, top + s * 0.20, right, bottom - r), fill=WHITE)
    d.pieslice((left, bottom - 2 * r, right, bottom), 0, 180, fill=WHITE)
    d.pieslice((left + w, bottom - 2 * r + w, right - w, bottom - w), 0, 180, fill=mid(TEAL))
    d.rectangle((left + w, bottom - r - 1, right - w, bottom - r + 1), fill=mid(TEAL))
    d.rounded_rectangle((right - w, top - s * 0.04, right, top + s * 0.17), radius=w / 2, fill=ORANGE)
    return im


def design10(s, small):
    """The word udoc with a line under it; at small sizes the u alone."""
    im = tile(s, INK)
    d = ImageDraw.Draw(im)
    if small:
        x0, y0, x1, y1 = letter(d, s, "u", "segoeuib.ttf", 0.80, WHITE, cy=0.44)
    else:
        x0, y0, x1, y1 = letter(d, s, "docu", "segoeuib.ttf", 0.34, WHITE, cy=0.46)
    bar = s * (0.055 if not small else 0.09)
    d.rounded_rectangle((x0, y1 + bar * 1.2, x1, y1 + bar * 2.2), radius=bar / 2, fill=ORANGE)
    return im


def design11(s, small):
    """An orange dot and a white u: the start of the file ending."""
    im = tile(s, INK)
    d = ImageDraw.Draw(im)
    x0, y0, x1, y1 = letter(d, s, "u", "segoeuib.ttf", 0.84, WHITE, cx=0.58, cy=0.47)
    r = s * (0.075 if not small else 0.09)
    cx = x0 - r * 1.9
    d.ellipse((cx - r, y1 - 2 * r, cx + r, y1), fill=ORANGE)
    return im


def design12(s, small):
    """A U inside the outline of a sheet."""
    im = tile(s, TEAL)
    d = ImageDraw.Draw(im)
    w = s * (0.055 if not small else 0.085)
    x0, x1, y0, y1 = 0.24 * s, 0.76 * s, 0.14 * s, 0.86 * s
    f = s * 0.17
    back = mid(TEAL)
    d.polygon([(x0, y0), (x1 - f, y0), (x1, y0 + f), (x1, y1), (x0, y1)], fill=WHITE)
    d.polygon([(x0 + w, y0 + w), (x1 - f - w * 0.4, y0 + w), (x1 - w, y0 + f + w * 0.4), (x1 - w, y1 - w), (x0 + w, y1 - w)], fill=back)
    letter(d, s, "U", "segoeuib.ttf", 0.40 if not small else 0.46, WHITE, cy=0.57)
    return im


PURPLE = ((112, 84, 214), (112, 84, 214))
AMBER = ((238, 148, 20), (238, 148, 20))
LIGHT = ((244, 246, 250), (244, 246, 250))


def design13(s, small):
    """A U made of lines of text."""
    im = tile(s, PURPLE)
    d = ImageDraw.Draw(im)
    n = 6 if not small else 4
    t = s * (0.062 if not small else 0.10)
    top, bottom = s * 0.22, s * 0.78
    for i in range(n):
        y = top + (bottom - top) * i / (n - 1)
        bar = lambda a, b, colour=WHITE: d.rounded_rectangle((a * s, y - t / 2, b * s, y + t / 2), radius=t / 2, fill=colour)
        if i < n - 2:
            bar(0.24, 0.42)
            bar(0.58, 0.76, ORANGE if i == 0 else WHITE)
        elif i == n - 2:
            bar(0.26, 0.74)
        else:
            bar(0.34, 0.66)
    return im


def design14(s, small):
    """A U whose right arm has the folded corner of a sheet."""
    im = tile(s, BLUE)
    d = ImageDraw.Draw(im)
    w = s * (0.15 if not small else 0.18)
    left, right, top, bottom = s * 0.25, s * 0.75, s * 0.18, s * 0.82
    r = (right - left) / 2
    back = mid(BLUE)
    d.rectangle((left, top, left + w, bottom - r), fill=WHITE)
    d.rectangle((right - w, top, right, bottom - r), fill=WHITE)
    d.pieslice((left, bottom - 2 * r, right, bottom), 0, 180, fill=WHITE)
    d.pieslice((left + w, bottom - 2 * r + w, right - w, bottom - w), 0, 180, fill=back)
    d.rectangle((left + w, bottom - r - 1, right - w, bottom - r + 1), fill=back)
    d.polygon([(right - w, top - 1), (right + 1, top - 1), (right + 1, top + w)], fill=back)
    d.polygon([(right - w, top), (right - w, top + w), (right, top + w)], fill=ORANGE)
    return im


def design15(s, small):
    """A white disc with the u in the colour of the tile."""
    im = tile(s, AMBER)
    d = ImageDraw.Draw(im)
    r = s * 0.36
    d.ellipse((s / 2 - r, s / 2 - r, s / 2 + r, s / 2 + r), fill=WHITE)
    letter(d, s, "u", "segoeuib.ttf", 0.62, mid(INK), cy=0.50)
    return im


def design16(s, small):
    """A U that ends in the point of a pen nib."""
    im = tile(s, PURPLE)
    d = ImageDraw.Draw(im)
    p = lambda x, y: (x * s, y * s)
    back = mid(PURPLE)
    d.polygon([p(0.23, 0.15), p(0.77, 0.15), p(0.77, 0.52), p(0.50, 0.89), p(0.23, 0.52)], fill=WHITE)
    d.polygon([p(0.41, 0.10), p(0.59, 0.10), p(0.59, 0.47), p(0.50, 0.59), p(0.41, 0.47)], fill=back)
    slit = s * (0.022 if not small else 0.04)
    d.rectangle((0.5 * s - slit / 2, 0.59 * s, 0.5 * s + slit / 2, 0.80 * s), fill=back)
    return im


def design17(s, small):
    """A small u with a dark caret, on an orange tile."""
    im = tile(s, AMBER)
    d = ImageDraw.Draw(im)
    bar = s * (0.075 if not small else 0.095)
    x0, y0, x1, y1 = letter(d, s, "u", "segoeuib.ttf", 0.86, WHITE, cx=0.44, cy=0.48)
    d.rounded_rectangle((x1 + bar * 0.7, y0 - s * 0.10, x1 + bar * 1.7, y1 + s * 0.04), radius=bar / 2, fill=mid(INK))
    return im


def design18(s, small):
    """A blue U over an orange line, on a light tile."""
    im = tile(s, LIGHT)
    d = ImageDraw.Draw(im)
    x0, y0, x1, y1 = letter(d, s, "U", "segoeuib.ttf", 0.64, mid(BLUE), cy=0.43)
    bar = s * (0.07 if not small else 0.10)
    d.rounded_rectangle((x0, y1 + bar * 0.9, x1, y1 + bar * 1.9), radius=bar / 2, fill=ORANGE)
    return im


DESIGNS = [design1, design2, design3, design4, design5, design6, design7, design8, design9, design10, design11, design12,
           design13, design14, design15, design16, design17, design18]


def program_icon(s, small):
    """The icon of the program: a white D on the blue tile."""
    im = tile(s, BLUE)
    letter(ImageDraw.Draw(im), s, "D", "segoeuib.ttf", 0.72 if not small else 0.78, WHITE)
    return im


def file_icon(s, small):
    """The icon of a document: a sheet with a folded corner and a blue D in its middle. No tile behind it."""
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    blue, edge = mid(BLUE), (150, 166, 198)
    x0, x1, y0, y1 = 0.17 * s, 0.83 * s, 0.04 * s, 0.96 * s
    f = (0.22 if not small else 0.26) * s
    w = max(1.0, s * (0.016 if not small else 0.035))
    shape = [(x0, y0), (x1 - f, y0), (x1, y0 + f), (x1, y1), (x0, y1)]
    d.polygon(shape, fill=edge)
    d.polygon([(x0 + w, y0 + w), (x1 - f - w * 0.4, y0 + w), (x1 - w, y0 + f + w * 0.4), (x1 - w, y1 - w), (x0 + w, y1 - w)], fill=WHITE)
    d.polygon([(x1 - f, y0), (x1 - f, y0 + f), (x1, y0 + f)], fill=edge)
    d.polygon([(x1 - f + w, y0 + w * 2.4), (x1 - f + w, y0 + f - w), (x1 - w * 2.4, y0 + f - w)], fill=FOLD)
    letter(d, s, "D", "segoeuib.ttf", 0.50 if not small else 0.56, blue, cy=0.60)
    return im


def draw(design, size):
    """One picture at the given size, drawn larger and made smaller for smooth edges.
    design: a number from the list of designs, or one of the two drawings in use."""
    s = size * 4 if size < 256 else 1024
    make = design if callable(design) else DESIGNS[design - 1]
    return make(s, size <= 32).resize((size, size), Image.LANCZOS)


def write_icon(design, name="ReiksDocument"):
    os.makedirs(OUT, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    pictures = [draw(design, n) for n in sizes]
    pictures[-1].save(os.path.join(OUT, name + ".png"))
    pictures[-1].save(os.path.join(OUT, name + ".ico"), format="ICO", sizes=[(n, n) for n in sizes], append_images=pictures[:-1])
    print(name, "written:", ", ".join(str(n) for n in sizes))


def write_sheet():
    """All designs side by side: large, and at 48, 32 and 16 pixels as the taskbar and title bars show them."""
    os.makedirs(OUT, exist_ok=True)
    cell, gap, across, tall = 200, 28, 6, 200 + 150
    rows = (len(DESIGNS) + across - 1) // across
    page = Image.new("RGBA", (gap + across * (cell + gap), rows * tall), (38, 40, 46, 255))
    d = ImageDraw.Draw(page)
    label, little = font("segoeuib.ttf", 22), font("segoeui.ttf", 15)
    for i in range(len(DESIGNS)):
        x, y = gap + (i % across) * (cell + gap), (i // across) * tall
        d.text((x, y + 14), str(i + 1), font=label, fill=WHITE)
        d.text((x + 34, y + 19), NAMES[i], font=little, fill=(190, 196, 210))
        big = draw(i + 1, cell)
        page.alpha_composite(big, (x, y + 50))
        at = x
        for n in (48, 32, 16):
            small = draw(i + 1, n)
            page.alpha_composite(small, (at, y + 50 + cell + 22 + (48 - n) // 2))
            at += n + 18
    page.save(os.path.join(OUT, "icon-designs.png"))
    print("written:", os.path.join(OUT, "icon-designs.png"))


if __name__ == "__main__":
    arg = sys.argv[1] if len(sys.argv) > 1 else ""
    if arg == "sheet":
        write_sheet()
    elif arg:
        write_icon(int(arg))
    else:
        write_icon(program_icon)
        write_icon(file_icon, "DocuFile")
