# The Reiks document format (.docu)

Format version 26, layout rules version 26.

A Reiks document is a file with the extension `.docu`. It holds a document twice: its **content** (text, styles, tables, pictures, equations, charts, footnotes) and its **finished layout** (every line and object on every page at exact coordinates). It also holds a **manifest** that says which fonts and which content the layout was made from, so a program can tell whether the stored layout is still valid.

This text describes the file and every layout rule. A program that follows the rules with the same font files gets the same positions, to the unit.

## 1. Units and arithmetic

- All lengths and coordinates are whole numbers of **EMU**: 1 inch = 914 400 EMU, 1 point = 12 700 EMU, 1 mm = 36 000 EMU.
- Page coordinates start at the top left corner of the page; x grows to the right, y grows downward.
- Font sizes are whole numbers of **half-points** (22 = 11 pt). The size in EMU is `S = halfPoints × 6350`.
- All calculation is in integers (64-bit where products are large). There is one rounding rule:
  `round(a, b) = floor((2·a + b) / (2·b))` for a ≥ 0, b > 0 — a/b rounded to the nearest whole number, halves up.
- `floor(a / b)` is used where stated.
- Text offsets count **UTF-16 code units**. Each code unit is one *item* of a line. A picture, an equation, a chart or a footnote reference is one item and counts as one code unit (U+FFFC).

## 2. The file

A zip archive with these entries; the JSON entries are UTF-8 without a byte order mark:

| Entry | Holds |
|---|---|
| `manifest.json` | versions, fingerprints, fonts |
| `content.json` | page setup, styles, paragraphs, footnotes |
| `layout.json` | pages with their lines, objects and footnotes, all with positions |
| `media/<name>` | one entry per picture file |

Entry timestamps are not used (the writer sets 1980-01-01). A reader must ignore entries it does not know.

### 2.1 manifest.json

```json
{
  "format": "docu",
  "formatVersion": 23,
  "layoutRules": 23,
  "content": "<SHA-256 of the bytes of content.json, lower-case hex>",
  "layout": "<SHA-256 of the bytes of layout.json, lower-case hex>",
  "fonts": [
    { "family": "Calibri", "bold": false, "italic": false,
      "usedFamily": "Calibri", "file": "calibri.ttf", "face": 0,
      "sha256": "<SHA-256 of the whole font file, lower-case hex>" }
  ]
}
```

`fonts` has one entry for every combination of family, bold and italic that the layout uses: the format of every run of the body, of the table cells and of the footnotes the body refers to; the `mark` format of every paragraph that has no runs or ends with U+2028 (a table paragraph itself has no text and counts for nothing; the paragraphs in its cells count like any others); the character format of the paragraph style of the first paragraph of each of those footnotes (for its number); `Cambria Math`, not bold, not italic, if the body has an equation; and the character format of the style `Header` if at least one of the three header texts is not empty or the header has a picture (where the picture stands goes by `asc` and `desc` of that format, §6), of `Footer` likewise for the footer (of the first style if there is no style of that name). If `skipFirstPage` is set, the header and the footer of `first` count in the same way: `Header` is used if the header of the other pages or that of the first page has a text or a picture, `Footer` likewise. `family`, `bold`, `italic` are what the content asks for; `usedFamily`, `file` (name only, for information) and `face` (index of the face inside the file; 0 unless the file is a collection) say which face stood for it; `sha256` is the fingerprint of that font file. Fonts are named and fingerprinted, not embedded.

### 2.2 content.json

```json
{
  "page": { "width": 7560000, "height": 10692000, "top": 914400, "bottom": 914400, "left": 914400, "right": 914400 },
  "paragraphStyles": [
    { "id": "Normal", "name": "Normal", "font": "Calibri", "size": 22, "bold": false, "italic": false,
      "underline": false, "color": "000000", "align": "left", "before": 0, "after": 101600, "line": 1080,
      "indentLeft": 0, "indentRight": 0, "indentFirst": 0, "keepNext": false, "next": "Normal" }
  ],
  "characterStyles": [ { "id": "Emphasis", "name": "Emphasis", "italic": true } ],
  "body": [
    { "style": "Normal", "align": "justify", "pageBreakBefore": true,
      "mark": { "size": 28 },
      "runs": [
        { "text": "Plain " }, { "text": "bold", "bold": true }, { "text": "code", "style": "Code" },
        { "note": 1 },
        { "picture": { "media": "<sha256>.png", "width": 3048000, "height": 1905000, "block": true } },
        { "equation": { "source": "x^2", "tree": [ { "base": { "c": "𝑥" }, "sup": [ { "c": "2" } ] } ] } },
        { "chart": { "type": "bar", "title": "Sales", "width": 4320000, "height": 2700000,
                     "categories": [ "A", "B" ], "series": [ { "name": "North", "values": [ 12.5, -3 ] } ] } }
      ] }
  ],
  "notes": [
    { "id": 1, "paragraphs": [ { "style": "Footnote", "runs": [ { "text": "The text of the note." } ] } ] }
  ]
}
```

- `page`: page size and the four margins. The *text area* is the page minus the margins; its width is `W = width − left − right`.
- A **paragraph style** gives a complete character format (`font`, `size`, `bold`, `italic`, `underline`, `color` as RRGGBB, and optionally `strike`, `highlight` as RRGGBB, `vertical` as `super` or `sub`) and the paragraph properties: `align` (`left`, `center`, `right`, `justify`), space `before` and `after`, `line` (line spacing in thousandths: 1000 = single), three indents, `keepNext`, and `next` (the style an editor gives a new paragraph made at the end of this one). The first style in the list is used for a paragraph whose style is not found.
- A **character style** gives only the character properties it changes.
- A **paragraph** names its style; `align` (optional) replaces the style's alignment; `line` and `indentLeft` (optional) replace the style's line spacing and left indent, in the body and in footnotes alike (an `indentLeft` of 0 is a value of its own, not the same as leaving it out; one below 0 counts as 0, except in a list paragraph, where the paragraph's own value can be as low as −228 600 and anything lower counts as −228 600: the number of such a list stands at the left edge of the text. The `indentLeft` of a style below 0 always counts as 0; a `line` outside 100…10000 is brought into that range, also in a style); `pageBreakBefore` (optional) starts it on a new page; `list` (optional, one of the kinds of §5.5) makes it a paragraph of a list, and `level` (optional, a whole number from 0 to 8; 0 when missing or anything else; only read together with `list`) says how deep it stands (§5.5; neither has an effect in a footnote). A `list` that is not one of the kinds is ignored.
  - **`borders`** (optional) on a paragraph, and on a paragraph style: the lines at its edges, an object
    `{ "top": 12700, "bottom": 12700, "left": 9525, "right": 9525, "gap": 12700, "sideGap": 50800, "color": "4F81BD" }`.
    `top`, `bottom`, `left` and `right` are the thickness of the line at that edge in EMU, from 3175 to 76 200; one that is left out or is 0 or less means no line there, a larger or smaller value counts as the nearest limit. `gap` is the room between the text and the lines above and below, `sideGap` that between the text and the lines at the sides, each from 0 to 393 700 (outside that: the nearest limit; left out: 0). `color` is that of all four (left out: `000000`). An object without any line, such as `{}`, says that the paragraph has no lines. A paragraph without `borders` has those of its style; a paragraph with `borders` has exactly its own, and nothing of its style's. In what follows, *the borders of a paragraph* are these, `top` … `right` being 0 where there is no line. A table paragraph (one with `table`) has none, whatever it or its style says. A `borders` that is not an object counts as left out, and a value in it that is not a whole number as 0. Paragraphs in table cells and in footnotes have them like those of the body. The lines take room in the first and the last line of the paragraph (§5.3) and are drawn as §7 says.
  - **`together`** (optional, `true`) on a paragraph of the body that is not a table says that a *block* begins with it: this paragraph and the ones after it, up to but not including the next paragraph that has `together` or `pageBreakBefore`, or to the end of the body. A block of which less than half would fit at the bottom of a page starts on the next page (§6). Elsewhere `together` has no effect; on a table paragraph it is as if it were not there, so a table neither begins a block nor ends one by it.
  - A paragraph can also have `before`, `after`, `indentRight` and `indentFirst` of its own (each optional; `before`, `after` and `indentRight` below 0 count as 0). They replace the style's values of the same names for this paragraph: wherever §5 and §6 use the `before`, `after`, `indentRight` or `indentFirst` of a paragraph's style, the paragraph's own value is used if it has one. This holds in the body, in footnotes and in table cells, and for a table paragraph's `before` and `after`.
  - **`hyphens`** (optional) lists the places where a word of the paragraph may be divided at the end of a line: an array of item indexes. Each must be larger than the one before it (the first larger than 0) and smaller than the number of items; one that is not is left out. A line may end before such an item, with a hyphen (§5.2). The list is made by an editor from the rules of a language and written here, so that the layout never depends on those rules; a reader takes the list as it is. It holds for paragraphs of the body, of footnotes and of table cells alike; in a table paragraph (which has no items) it has no effect.
- At the top level, beside `page` and `body`: **`widowControl`** (optional, `true` or `false`; `false` when missing) says that no line of a paragraph is left alone at the bottom or the top of a page (§6). **`hyphenation`** (optional, a string such as `es`) names the language whose rules the editor used for the `hyphens` lists; it tells an editor how to go on and has no effect on the layout. **`adaptSpacing`** (optional, `true` or `false`; `false` when missing) lets the room between paragraphs give a little at the ends of pages (§6, *Giving room*).
- A paragraph with **`table`** is a table and has no `runs`: `{ "style": …, "align": …, "table": { "columns": [ w, … ], "rows": [ [ cell, … ], … ] } }`. `columns` are the widths of the columns; every row has one entry per column; a cell is an array of at least one paragraph, written like the paragraphs of the body. Its runs can be `text`, `picture`, `equation` and `chart`; a picture always stands in the line there (`wrap`, `dx` and `dy` have no effect in a cell, `block` has); a `note` or `cite` run in a cell is left out (`pageBreakBefore`, `keepNext` and `list` have no effect in a cell). A table has at least one row and one column; a paragraph whose `table` has none is an ordinary paragraph. The table paragraph's `align`, else its style's, places the table (§6). Tables stand in the body only.
  - **Joined and shaded cells.** In place of the array, a cell can be an object: `{ "paras": [ paragraph, … ], "cols": n, "rows": m, "shade": "RRGGBB" }`, each of `cols`, `rows` and `shade` optional. Such a cell *starts* at its place in the grid and fills a block of `cols` columns and `rows` rows from there, to the right and downward (each 1 when missing). The other places of the block are *covered*; in the file they hold `null`. `shade` is the colour of the cell's background.
  - The grid is read row by row from the top, and in each row from left to right. An entry at a covered place is not looked at, whatever it is. At a place that is not covered: `cols` is brought into the range 1 … (number of columns − its column), then made smaller until no place of the cell's first row is covered by an earlier cell; `rows` is brought into the range 1 … (number of rows − its row). `cols` or `rows` that is not a whole number counts as 1. Only the cell's first row needs this look at covered places: the places below it are then free as well. An entry that is `null`, or missing because the row is too short, or an array without paragraphs, or an object without `paras` or with none in it, is a cell with one empty paragraph. A cell has a number of columns `cols`, a number of rows `rows`, and *starts* at one row and one column; a cell that is not joined has 1 and 1.
  - The table can have `"lines"`: `all` (also when missing or anything else), `horizontal`, `outer` or `none`, and `"lineWidth"`: the thickness of its lines, brought into the range 3175 … 57150; 9525 when missing. Both only say what is drawn (§7).
- A **run** is one of six kinds, each with the character properties it changes and, optionally, a character style (`style`):
  - `text`: a string;
  - `picture`: one item. `media` names the entry `media/<name>`; `width` and `height` are the size at which it is shown; `block` (optional) puts it on a line of its own. `wrap` (optional: `topbottom`, `square`, `behind` or `front`) makes it a *floating* picture, placed by `dx` and `dy` (§6); a floating picture takes no notice of `block`;
  - `equation`: one item. `tree` is the structure that is laid out (§8); `source` is the line the user typed, kept for editing; `block` (optional) puts it on a line of its own and lays it out in display style;
  - `chart`: one item. `type` is `bar`, `line`, `pie` or `plot`; `title` may be empty; `xTitle` and `yTitle` (each optional; missing is the same as empty) are the names of the horizontal and the vertical axis, which a pie does not have; `axisNames` (optional) says where they stand: `middle` (also when missing or anything else) or `ends`; `width` and `height` are its size; `categories` are the labels; each of `series` has a `name` and one value per category; `block` (optional) puts it on a line of its own. Values are decimal numbers with at most three digits after the point, between −10⁹ and 10⁹; all arithmetic uses them as whole thousandths (§9): the value times 1000, rounded to the nearest whole number, halves away from zero. A `plot` is the graph of formulas: it has no categories, but `formula` (what the user typed, kept for editing), `from` and `to` (the stretch of x), and each series holds the values of one formula at equal steps from `from` to `to`, with `null` where the formula has no value;
  - `note`: one item, the reference mark of the footnote with that id.
  - `cite`: one item, a citation of the source with that id (a string). It shows the text of §4.5. Only in the body; elsewhere it is left out.
- The **character format** of a run is found in this order, later ones winning: the paragraph style, the run's character style, the run's own properties. A size outside 2…3276 half-points is brought into that range. The character properties are `font`, `size`, `bold`, `italic`, `underline`, `underlineStyle` (`single`, `double`, `thick`, `dotted`, `dashed` or `wave`; anything else is as if it were not there; in a style, `single` when missing; it has an effect only where `underline` is true), `color`, `strike` (struck through), `highlight` (RRGGBB, or `none`) and `vertical` (`super` for raised text, `sub` for lowered text, or `normal`), and `link` (a string of 1 to 2000 characters: the address the text leads to, such as `https://example.org` or `mailto:name@example.org`; anything else is as if it were not there). Where neither a style nor the run gives them, text is not struck through, has no highlight, is `normal` and has no link. A link changes nothing in the layout and is not drawn; that its text is blue and underlined is said by `color` and `underline` like for any text. It is one of the properties in which two formats can differ (§7), and tells a reader where the text leads.
- `mark` (optional) holds properties and a character style like a run, without text. It is the format of the end of the paragraph, used by lines that have no items (§5.3).
- Inside run text: U+0009 is a tab, U+2028 ends the line without ending the paragraph. Other control characters and U+FFFC do not occur.
- **`sources`** (optional): the works the document refers to, each `{ "id": "…", "authors": [ "…", … ], "year": "…", "title": "…", "in": "…", "url": "…" }`. `id` is a string that is not empty; a source whose `id` was used by one before it is left out. An author is written family name first, as in `Smith, Anna`; of every text the spaces at its ends do not count, and an author that is then empty is left out. A missing text is empty.
- **`citations`** (optional): a string that names the reference system of the document: `author-year` (APA), `harvard`, `chicago`, `mla`, `ieee`, `vancouver` or `numbered`. Missing, or any other value, counts as `author-year`. It decides how a citation reads (§4.5).
- A paragraph of the body with **`refs`** `true` belongs to the list of references. This changes nothing in its layout; it only tells an editor which paragraphs to write again when the sources or the citations change.
- A paragraph of the body with **`toc`** `true` belongs to the table of contents, which an editor writes again from the headings. It is an ordinary paragraph but for one thing: the last tab of its text is a *leader tab*, which reaches so far that what follows it ends at the right edge of the line (§5.2) and is drawn as a row of dots (§7). Outside the body, and in a table paragraph, `toc` has no effect.
- **`headerFooter`** (optional): `{ "header": { "left": "…", "centre": "…", "right": "…" }, "footer": { … }, "skipFirstPage": false }`. Each of the six texts may be empty and may have up to eight lines, separated by U+000A; it holds no other control characters. In a text, `{page}` stands for the number of the page and `{pages}` for the number of pages (§6). `header` and `footer` may each also hold one picture, such as a logo: `"picture": { "media": "<name>", "width": …, "height": …, "place": "left" }`. `media` names the entry `media/<name>` as for a picture in the text; `width` and `height` (each from 1 to 100 000 000) are the size at which it is shown; `place` is `left`, `centre` or `right` (anything else, or none, counts as `left`). A `picture` without a `media` name is as if it were not there. If `skipFirstPage` is `true`, `headerFooter` may also hold `"first": { "header": { … }, "footer": { … } }`: the header and footer of the first page, each written like the others (three texts and, optionally, a picture). A `first` that is missing means both are empty. If `skipFirstPage` is not `true`, `first` is not written and counts for nothing.
- **`notes`**: the footnotes, in the order of their references. Each has an `id` (a whole number above 0) and its paragraphs, written like body paragraphs. Footnote paragraphs hold text runs only; `pageBreakBefore` and `keepNext` have no effect in them. Every `note` run in the body refers to a different note. A reader removes a `note` run whose id is not above 0 or was already used by an earlier run, and gives a reference whose note is missing a note with one empty paragraph of the style `Footnote`.

### 2.3 media

A picture file is stored unchanged as `media/<name>`, where `<name>` is the SHA-256 of its bytes in lower-case hex followed by `.png` or `.jpg`. Because `content.json` names pictures by fingerprint, its own fingerprint covers them. A reader shows a placeholder of the same size for a picture that is missing or whose bytes do not have the fingerprint in its name; positions do not depend on the picture file.

### 2.4 layout.json

```json
{ "pages": [ {
  "lines": [
    { "p": 2, "s": 0, "n": 102, "x": 914400, "y": 1844973, "h": 184175, "b": 1994223, "a": [32060, 32060, 73397, 69509, ...] }
  ],
  "objects": [ { "kind": "picture", "p": 7, "i": 0, "x": 914400, "y": 3500000, "w": 3048000, "h": 1905000, "b": 5405000 } ],
  "notes": { "y": 9050000, "items": [ { "id": 1, "number": 1, "lines": [ ... ] } ] }
} ] }
```

Pages in order. On each page:

**`lines`** — the body lines from top to bottom:

| Field | Meaning |
|---|---|
| `p` | index of the paragraph in `body` |
| `s`, `n` | offset of the first item in the paragraph text, and number of items |
| `x` | left edge of the first item |
| `y` | top of the line |
| `h` | height of the line |
| `b` | y of the baseline |
| `a` | the advance of each of the `n` items, as placed (justification included) |
| `mark` | only on the first line of a list paragraph: `{ "x": …, "text": "…" }`, the left edge of its bullet or number and what it reads (§5.5) |

Item *k* of the line stands at `x + a[0] + … + a[k−1]`. Every item of every paragraph belongs to exactly one line; a paragraph without text has one line with `n` = 0.

A line that ends inside a word has `"hy": true` (§5.2). A table paragraph has one entry per row in place of lines: `s` = 0, `n` = 0, `a` empty, `x` the left edge of the table, `y` and `h` the top and the height of the row, `b` = `y + h`, and the extra field `row` with the number of the row (from 0).

**`cells`** (present when the page has table cells) — for each cell on the page, in the order of the rows and columns they start at (row by row, and in each row from left to right; a joined cell is there once): `p` the paragraph of its table, `r` the row and `c` the column it starts at, its rectangle `x`, `y`, `w`, `h`, and `lines`: the lines of its paragraphs, with the same fields as a body line and `p` the index of the paragraph inside the cell. A cell that holds pictures, equations or charts also has **`objects`**: one entry for each, in the order of the cell's paragraphs and, in a paragraph, of its items, with the same fields as the `objects` of the page (below) and `p` the index of the paragraph inside the cell.

**`objects`** (present when the page has pictures, equations or charts) — for each: its `kind` (`picture`, `equation`, `chart`), paragraph `p`, offset `i`, the rectangle of its box (left `x`, top `y`, width `w`, height `h`) and the baseline `b` it stands on. With `W`, `H`, `D` the box of §5.4: `w = W`, `h = H + D`, `y = b − H`. These follow from the lines (§5.4, §7) and are written out so that a reader needs no arithmetic to place an object. A floating picture is listed too, with the extra field `wrap`, its rectangle as placed by §6, `b` = `y + h` and no `draw`; the floating pictures of a page come first, in the order in which they were placed, then the objects standing in lines.

An equation or chart also has **`draw`**: everything it is made of, in page coordinates, in drawing order. Each entry is an array that starts with its kind and ends with its colour (`RRGGBB`):

| Entry | Meaning |
|---|---|
| `["glyph", x, y, size, "c", colour]` | one character of the equation font at that size, its origin (on its baseline) at x, y |
| `["text", x, y, size, "string", colour]` | text in the font of the object's run at that size, starting at x on the baseline y |
| `["textup", x, y, size, "string", colour]` | the same text turned a quarter turn against the clock around the point x, y: it starts there and runs upward, so that what would stand `d` to the right of x on the baseline stands at x, y − `d`, and the tops of the letters point to the left |
| `["rect", x, y, w, h, colour]` | a filled rectangle; x, y is its top left corner |
| `["line", thickness, x0, y0, x1, y1, …, colour]` | a line of that thickness through the points |
| `["disc", cx, cy, r, colour]` | a filled circle |
| `["slice", cx, cy, r, from, to, total, colour]` | the part of a filled circle between `from/total` and `to/total` of a full turn, clockwise from 12 o'clock |

**`bands`** (present when the page has a header or footer text) — each line of a text as it stands on this page: `{ "band": "header" or "footer", "x": …, "b": …, "text": "…" }`, with `x` its left edge, `b` its baseline and `text` what the line reads on this page (§6). A page without such texts has no `bands`.

**`bandPictures`** (present when the page has a picture of the header or footer) — each such picture as it stands on this page: `{ "band": "header" or "footer", "media": "<name>", "x": …, "y": …, "w": …, "h": … }`, with `x`, `y` its top left corner and `w`, `h` the `width` and `height` of the picture in `content.json` (§6). The picture of the header comes before that of the footer. A page without such pictures has no `bandPictures`.

**`notes`** (present when the page has footnotes) — `y` is the top of the separating rule; `items` are the footnotes of the page in order, each with its `id`, its `number` and its `lines`. A footnote line has the same fields as a body line, with `p` the index of the paragraph inside the note.

## 3. Opening a file

The stored layout is shown as it is, without laying out, when all of these hold:

1. `formatVersion` is one the program reads (otherwise the file is refused). Earlier versions are the current one without what was added later (§10).
2. `layout.json` is present and `layoutRules` equals the program's layout rules version.
3. The SHA-256 of `content.json` equals `content`, and that of `layout.json` equals `layout`.
4. For every family/bold/italic combination the layout uses, the manifest has an entry, and the font file found on this PC for it (§4.1) has the same `sha256` and `face`.
5. The lines fit the content (paragraph indexes in range and in order; lines of a paragraph contiguous and covering it; `a` has `n` numbers; every referenced footnote present once with its number; every floating picture listed belongs to a floating picture item; every cell of every table present once).

Otherwise the program says which condition failed (for a font: which font), lays the document out again by the rules below, and works with that layout. Saving writes the new layout and fingerprints.

## 4. Fonts

### 4.1 Which face

Fonts are TrueType/OpenType files (`.ttf`, `.otf`, `.ttc`, `.otc`) in three places, read in this order; a place that does not exist is passed over:

1. the Windows fonts folder (`%WINDIR%\Fonts`), the files directly in it;
2. the user's fonts folder (`%LOCALAPPDATA%\Microsoft\Windows\Fonts`), the files directly in it;
3. the folder where Office keeps the fonts of its documents (`%LOCALAPPDATA%\Microsoft\FontCache\4\CloudFonts`), the files in it and in all folders inside it, at any depth.

Within a place the files are read in the order of their full paths, compared without regard to case: character by character after changing each to its capital, the `\` between folder names being a character like any other. For each face:

- family = name record 1 of the `name` table (Windows platform, US English first; else any Windows; else Unicode platform; else Macintosh);
- bold = bit 5, italic = bit 0 of `fsSelection` in `OS/2` (if there is no `OS/2` table of at least 64 bytes: bits 0 and 1 of `macStyle` in `head`).

Family names are compared without regard to case. The first face found for a family/bold/italic combination is used for it. If a family has no face with the wanted combination, the first that exists of these is used: same bold without italic; same italic without bold; regular; any face of the family. If the family is not installed, the family `Segoe UI` stands for it. The layout always uses the metrics of the face actually used; a renderer may thicken or slant the glyphs to imitate a missing bold or italic, which does not change any position.

### 4.2 Tables used

| Value | From |
|---|---|
| `upem` | `head.unitsPerEm` |
| `ascender` | `hhea.ascender`, at least 0 |
| `descender` | −`hhea.descender`, at least 0 |
| `lineGap` | `hhea.lineGap`, at least 0 |
| advance of glyph *g* | `hmtx` advance of glyph min(*g*, `hhea.numberOfHMetrics` − 1) |
| glyph of a character | `cmap` |
| `underlinePosition`, `underlineThickness` | `post`; if the thickness is ≤ 0 it is floor(`upem`/20), at least 1; if the position is ≥ 0 it is −floor(`upem`/10) |
| `strikePosition`, `strikeSize` | `yStrikeoutPosition`, `yStrikeoutSize` in `OS/2` (0 without that table); if the size is ≤ 0 it is the underline thickness; if the position is ≤ 0 it is floor(`upem`/4) |

`cmap`: only subtables of format 4 or 12 are used; the first that exists of platform 3 encoding 10, platform 3 encoding 1, platform 0 (format 12 before format 4), platform 3 encoding 0. A character the subtable does not have is glyph 0. With a platform 3 encoding 0 subtable, a character below U+0100 that is not found is looked up again at U+F000 + its value.

There is no shaping: no kerning, no ligatures, no reordering. One character gives one glyph.

### 4.3 Sizes of a format

For a character format with face *F* and size `S` (EMU):

- advance of a character at a size `z` = `round(advance of its glyph × z, upem)`
- `asc` = `round((ascender + lineGap) × S, upem)`
- `desc` = `round(descender × S, upem)`

The characters of a format are measured and drawn at its **drawing size** `Sd`: `Sd = S`, except for raised and lowered text (`vertical` `super` or `sub`), where `Sd = floor(2·S / 3)`. Raised text stands `floor(S / 3)` above the baseline, lowered text `floor(S / 6)` below it. `asc` and `desc` are those of size `S` in every case. Objects and footnote references in a raised or lowered run are not changed by it: a footnote number there still takes its digit size and its raise from `S` (§4.4).

### 4.4 Footnote numbers

Footnotes are numbered 1, 2, 3 … in the order of their references in the body. A number is written in decimal digits, drawn smaller and raised:

- digit size `Sr = floor(2·S / 3)`; raise `floor(S / 3)` above the baseline;
- **width of a number** in a format = the sum of the advances of its digits at size `Sr`.

### 4.5 Citations

The text of a citation item is worked out from the source it names.

- A source that is not among `sources`: `[?]`.
- With `citations` `numbered`, `ieee` or `vancouver`: `[n]`, with `n` in decimal digits. Going through the paragraphs of the body in order and through their items in order, the first source cited that is among `sources` has the number 1, the next new one 2, and so on.
- In all other systems the text is made of `A` and `Y`. `Y` is the source's `year`, or `n.d.` if it is empty. The family name of an author is what stands before the first comma, or the whole author if there is no comma, without spaces at its ends; `F1`, `F2`, `F3` are the family names of the first three authors. Without authors, `A` is the source's `title` in every system.
  - `author-year` (also when `citations` is missing or names no system): `(A, Y)`, that is `(`, `A`, a comma, a space, `Y`, `)`. `A` is: with one author `F1`; with two `F1 & F2` (space, ampersand, space); with three or more `F1 et al.` (space, `et`, space, `al.`).
  - `harvard`: `(A Y)`, that is `(`, `A`, a space, `Y`, `)`. `A` is: with one author `F1`; with two `F1 and F2` (space, `and`, space); with three or more `F1 et al.`.
  - `chicago`: `(A Y)` as for `harvard`, but with exactly three authors `A` is `F1, F2, and F3` (comma and space, then comma, space, `and`, space); with four or more `F1 et al.`.
  - `mla`: `(A)`, with `A` as for `harvard`; the year is not part of it.

## 5. Lines

A line is broken for a *stretch*: `x0`, measured from the left edge of the text area, and `width`. Normally the stretch is the whole text area (`x0` = 0, `width` = `W`); only beside a floating picture is it narrower (§6). Apart from footnote numbers, list numbers and such narrower stretches, the lines of a paragraph do not depend on where the paragraph falls.

### 5.1 Items and their natural advance

| Item | Advance |
|---|---|
| U+2028 (line break) | 0 |
| U+0009 (tab) | decided on the line, §5.2 |
| a picture standing in the line, an equation or a chart | the width of its box (§5.4) |
| a floating picture | 0 |
| a footnote reference | the width of its number (§4.4) in the format of its run |
| a citation | the sum of the advances of the characters of its text (§4.5) at the drawing size `Sd` of the format of its run (§4.3); the text is never broken |
| a low surrogate that follows a high surrogate | 0 |
| a high surrogate followed by a low surrogate | advance of the character the pair encodes at the drawing size, in the format of the high surrogate |
| any other code unit | advance of the character with that value at the drawing size `Sd` of its format (§4.3) |

### 5.2 Breaking

Let `n` be the number of items, `left`, `right`, `first` the style's `indentLeft`, `indentRight`, `indentFirst`; where the paragraph has an `indentLeft`, `indentRight` or `indentFirst` of its own, that is taken instead. For the first paragraph of a footnote, `mark` is the width of the note's number plus the advance of U+0020 at size `S`, both in the character format of the paragraph's style; otherwise `mark` = 0. For a list paragraph see §5.5. The line that starts at item `s` (the paragraph's first line is the one with `s` = 0) is built like this:

```
indent = left + (first + mark if this is the paragraph's first line, else 0)      for a list paragraph: §5.5
avail  = max(0, width − indent − right)
w = 0; best = none
for i = s, s+1, … , n−1:
    c = item i
    if c is U+2028:  the line ends after item i; it is a hard end; stop
    if c is a box (a picture standing in the line, an equation or a chart) with block:
        if i > s: the line ends before item i
        else:     the advance of item i is its width; the line ends after item i
        it is a hard end; stop
    if c is a box and i > s:  best = i
    a = 457200 − (w mod 457200)   if c is a tab
        natural advance of item i  otherwise
    if the paragraph has toc, and item i is the last tab among all items of the paragraph:
        rest = the sum of the natural advances of the items i+1 … n−1
        if avail − w − rest > a:  a = avail − w − rest
    if i is one of the paragraph's hyphens and i > s:
        d = the advance of U+002D at the drawing size, in the format of item i−1
        if w + d ≤ avail:  hbest = i, and hd = d
    if c is not U+0020 and a > 0 and i > s and w + a > avail:
        if hbest is set and (best is not set or hbest > best):
            the line ends before item hbest; the advance of item hbest−1 on this line grows by hd; the line has `hy`; stop
        the line ends before item `best` if best is set, else before item i; stop
    the advance of item i on this line is a;  w = w + a
    if c is U+0020, U+002D, a tab or a box, and i+1 < n, and item i+1 is not U+0020:  best = i + 1
if the loop was not stopped: the line ends after item n−1
```

A floating picture is not a box: its item has no width, no height and gives no place to end a line. The next line starts where this one ended. Lines are made until all items are used; after a U+2028 at the very end of the paragraph one more line with no items follows. A paragraph without items has one line with no items.

The grown advance of the last item of a line with `hy` is its advance in everything that follows (the measured width, the alignment, the `a` of `layout.json`); the room it adds is where the hyphen is drawn (§7).

So: a line can end after a space, a hyphen-minus or a tab, inside a word at one of the paragraph's `hyphens` if the hyphen still fits, and before or after a picture, equation or chart; spaces never push anything to the next line (they hang past the right edge); a word wider than the line is cut where it stops fitting; tab stops lie every 457 200 EMU from the start of the line; a picture, equation or chart with `block` stands alone on its line; a footnote reference stays with the item before it.

### 5.3 Position and size of a line

Let the line hold items `s … e−1`.

- **Measured width** `m`: the sum of the advances of the line's items, leaving out a line break at the end and the spaces (U+0020) directly before the end (or before that line break).
- **Alignment** (the paragraph's `align`, else the style's): `shift` is
  - `left`: 0
  - `center`: floor((avail − m) / 2) if avail > m, else 0
  - `right`: avail − m if avail > m, else 0
  - `justify`: 0. In addition, unless the line has a hard end or is the paragraph's last line or avail ≤ m: let `t` be the end of the measured part, `from` the item after the last tab among items `s … t−1` (or `s`), `k` the number of spaces among items `from … t−1`, and `extra = avail − m`. If k > 0, the j-th of those spaces (j = 0 … k−1) gets `floor(extra·(j+1) / k) − floor(extra·j / k)` added to its advance.
- `x` of the line = page `left` + `x0` + `indent` + `shift`.
- **Height**: `asc` and `desc` of the line are the largest `asc` and the largest `desc` among the formats of its items (every item counts: spaces, tabs, the line break, and objects, floating pictures and footnote references with the format of their run). A line with no items takes them from the paragraph's `mark` format (the paragraph style with `mark` applied). `tall` is the largest height and `deep` the largest depth among the boxes on the line (0 without boxes), and `d = max(desc, deep)`.
  `h = max(round((asc + desc) × line, 1000) + d − desc, tall + d)` with the paragraph's own `line` if it has one, else the style's.
- **Baseline**: `h − d` below the top of the line.
- **Room for the borders** (§2.2), added after `h` and the baseline are found as above, wherever the line is broken (in the body, beside a floating picture, in a table cell, in a footnote). With `top`, `bottom` and `gap` of the paragraph's borders: if the line is the first of its paragraph (`s` = 0) and `top` > 0, then `h` and the baseline both grow by `top + gap`; if the line is the last of its paragraph (`e` is the number of items of the paragraph; an empty paragraph's only line is both first and last) and `bottom` > 0, then `h` grows by `bottom + gap`. Everything that uses the height of a line uses this one.

### 5.4 Boxes

A picture, an equation and a chart each have a box: a width, a height above the baseline and a depth below it.

| Object | Width | Height | Depth |
|---|---|---|---|
| picture | its `width` | its `height` | 0 |
| equation | of its top row (§8) | of its top row | of its top row |
| chart | its `width` | its `height` | 0 |

An equation is laid out, and the text of a chart measured, with the size `S` of the format of the object's run; a chart also uses that format's font.

### 5.5 Lists

A paragraph of the body with `list` is a list paragraph; a table is never one. Its `level` is `L` (0 to 8).

The kinds are of two sorts. **Bullets**: `bullet` (U+2022), `circle` (U+25E6), `square` (U+25AA), `dash` (U+2013). **Numbers**: `number`, `number-paren`, `roman-upper`, `alpha-upper`, `alpha-paren`, `alpha`, `roman`, `decimal`, `outline`, `outline-paren`.

**Counting.** There are nine counts `c[0]` … `c[8]`, all 0 at the start, and each count remembers the kind that counted it last (none at the start). Setting a count to 0 always makes it forget that kind too. Going through the paragraphs of the body in order:

- a paragraph that is not a list paragraph sets all nine to 0;
- a list paragraph at level `L` first sets `c[k]` to 0 for every `k` > `L`. Then a bullet sets `c[L]` to 0. A number sets `c[L]` to 0 if `c[L]` remembers a kind other than the paragraph's own; then it adds 1 to `c[L]`, and `c[L]` remembers the paragraph's kind.

**The marker** of a bullet is its character. The marker of a number, with `n = c[L]` after the step above:

| Kind | Marker |
|---|---|
| `number` | `n` in decimal digits, then a full stop |
| `number-paren` | `n` in decimal digits, then `)` |
| `roman-upper`, `roman` | `n` as a Roman numeral in capitals, or in small letters, then a full stop |
| `alpha-upper`, `alpha` | `n` in letters, in capitals or small, then a full stop |
| `alpha-paren` | `n` in small letters, then `)` |
| `decimal` | for `k` = 0 … `L`: `max(1, c[k])` in decimal digits and a full stop after each, as in `1.2.1.` |
| `outline` | `n` in decimal digits if `L mod 3` = 0, in small letters if 1, as a small Roman numeral if 2; then a full stop |
| `outline-paren` | as `outline`, with `)` in place of the full stop |

Letters: 1 is `a`, 26 is `z`, 27 is `aa`, 28 is `ab`, and so on (each letter stands for 1 to 26). Roman numerals are the usual ones for 1 to 3999 (4 is IV, 9 is IX, 40 is XL, 90 is XC, 400 is CD, 900 is CM); a larger number is written in decimal digits.

- The marker has the font, the size `S` and the colour of the format of the paragraph's first item (of the paragraph's `mark` format if it has no items); it is never raised, lowered, underlined, struck through or highlighted. `mw` is the sum of the advances of its characters at size `S` plus the advance of U+0020 at size `S`.
- In §5.2 the `left` of a list paragraph is the one defined there plus `L` × 457 200. With that `left`: `indent = left + 457200`, and on the paragraph's first line `indent = left + 457200 + max(0, 228600 + mw − 457200)`. The style's `indentFirst` is not used.
- The marker stands on the baseline of the paragraph's first line, its left edge at page `left` + `x0` + `left` + 228 600 (the second `left` is that of the paragraph, with the levels). This is the `x` of `mark` in `layout.json`; its `text` is the marker.

## 6. Pages

Paragraphs of the body are placed in order. `top` and `bottom` are the top and bottom edges of the text area. `y` starts at `top` on the first page. A page is *empty* while no line has been placed on it. "New page" means: start the next page, `y = top`.

**Footnote area.** The height of a footnote is the sum of the heights of its lines plus the `after` of each of its paragraphs. A page with footnotes keeps an area at the bottom of the text area: `area` = 152 400 + the heights of its footnotes; without footnotes `area` = 0. The footnotes *of a line* are those whose reference is an item of that line.

**Floating pictures.** A picture with `wrap` is placed on a page as a rectangle of its `width` and `height`, with its left edge at page `left` + `dx`; the steps below give its page and its top `py`. With `topbottom` and `square` the lines keep clear of it; with `behind` and `front` the lines take no notice of it. `room(t, h)` says where a line with top `t` and height `h` can stand on the current page:

```
fl = 0; fr = W; down = t; beside = none
for each picture placed on this page with wrap topbottom or square, in the order in which they were placed:
    its band runs from py − 127000 to py + height + 127000
    if t ≥ the end of the band or t + h ≤ the start of the band: next picture
    if topbottom:  down = max(down, end of the band); next picture
    pl = (its left edge − page left) − 127000;  pr = (its left edge − page left) + width + 127000
    if pl ≥ fr or pr ≤ fl: next picture
    beside = min(beside, end of the band)
    if min(fr, pl) − fl ≥ fr − max(fl, pr):  fr = min(fr, pl)      (text goes on the wider side, on the left if both are equal)
    else:                                    fl = max(fl, pr)
if down > t:                                    free = down
else if (fl > 0 or fr < W) and fr − fl < 914400: free = beside
else:                                           free = t
```

For each paragraph, with its style's `before`, `after`, `keepNext`:

1. If the page is not empty:
   - if the paragraph has `pageBreakBefore`: new page;
   - else if the paragraph has `together`: take its block (§2.2) without the paragraphs at its end that have no items and are not tables (the first paragraph always stays). `need` = for each paragraph of what is left, in order: its `before`, the heights of all its lines broken for the whole width (for a table: of its rows), and its `after`, except that the `after` of the last one is not counted. `room` = `bottom − area − y` (the page's area as it is now). If `need > room` and `2 × room < need`: new page, unless the page gives room (*Giving room*, step 1). If there is no new page, the next test is made as well;
   - else if `keepNext` is set and another paragraph follows: let `need` = `before` + the heights of all lines of this paragraph + `after`, plus — unless the following paragraph has `pageBreakBefore` — that paragraph's `before` and the height of its first line (with `widowControl`, if that paragraph is not a table: the heights of its first `keep₀` lines, as many as it has, where `keep₀` = 3 if it has three lines, 2 if it has two or more than three, else 1), all lines taken as broken for the whole width. If `y + need > bottom − area` (the page's area as it is now): new page, unless the page gives room (*Giving room*, step 1).
2. The lines are made and placed one at a time, starting with `s` = 0, until every item is in a line (and the empty line after a U+2028 at the very end is placed):
   - a. `trial` = the line from item `s` broken for the whole width (`x0` = 0, `width` = `W`).
   - b. `gap` = `before` if `s` = 0 and the page is not empty, else 0; `t = y + gap`.
   - c. For each floating picture whose item is in `trial` and which has not been placed yet, in the order of the items: with `topbottom` or `square`, `py = t + max(0, dy)`, and if the page is not empty and `py + height > bottom`: new page, and go back to b (pictures placed before stay where they are). With `behind` or `front`, `py = t + dy`. The picture is placed on this page.
   - d. Repeat: work out `room(t, height of trial)`. If `free > t`: `t = free` and repeat. If `fl = 0` and `fr = W`: the line is `trial`. Otherwise the line is the one from item `s` broken for the stretch `x0 = fl`, `width = fr − fl`; it is taken if its measured width is not above its `avail` (it holds no more items than `trial`, so it is no taller); if it is not taken: `t = beside` and repeat.
   - e. `area'` = the page's area with the footnotes of the line added. If the page is not empty and `t + h > bottom − area'`: the page may give room first (*Giving room*, step 2e); then new page, and go back to b.
     With `widowControl` the test asks for more room when the line is `trial` (it was taken at the whole width). Let `F` be the lines the paragraph has when every one of them is broken for the whole width, and `n` their number. If `trial` starts at the same item as a line of `F`, let `k` be the place of that line (from 0): `keep` = 3 if `k` = 0 and `n` = 3; otherwise 2 if `k` = 0 and `n` ≥ 2, or if `k` = `n − 2`; otherwise 1. The heights of the next `keep − 1` lines of `F` are added to `h` in the test, and the footnotes of those lines that are not yet on a page are added to `area'` (each footnote once). Nothing else changes: those lines are placed in their own turn, and the test is not made on an empty page.
   - f. The top of the line is `t`; then `y = t + h`; the footnotes of the line now belong to this page; `s` = the item after the line.
3. `y = y + after`.

So a line stays on a page only if its footnotes fit there too; space before a paragraph is dropped at the top of a page; a line that is too tall, or whose footnotes are too long, is still placed, as the first line of a page; with `widowControl` the first line of a paragraph does not stay behind alone at the bottom of a page and the last does not go on alone to the next, as far as the lines are those of the whole width; and a footnote is never split between pages. A floating picture stands `dy` below the place where the line holding its item would begin, lines move down past a `topbottom` picture, and beside a `square` picture they use the wider side if it is at least one inch wide.

**The last paragraph.** In a document with `adaptSpacing`, the last paragraph of the body is a *trailing empty paragraph* if the body has more than one paragraph and that last one is not a table and has no items, no `list` of one of the kinds of §5.5, and no lines at its edges (its own `borders`, or else its style's). It never begins a new page: step 1 is left out for it (also `pageBreakBefore`), and where in step 2e its line would not fit (`t + h > bottom − area`, with the page's area as it is), its top becomes max(`top`, `bottom − area − h`) and it is placed on the page all the same. It shows nothing, and may reach into the line above.

**Giving room.** Only in a document with `adaptSpacing`. A page *may give* if it has not given yet (a page gives at most once). `low` is the largest end of a band (as in `room`: `py` + `height` + 127 000) of the pictures with `topbottom` or `square` placed on the page so far, and 0 if there are none. The *unit of a picture* is the unit of the page whose line holds its item; a picture whose item is in no unit of the page (its line went on to the next page) has none. The *units* of a page are its lines and table rows in the order they were placed. A unit is *blank* if its paragraph is not a table and has no items, the line has no list marker, and the paragraph has no lines at its edges (its own `borders`, or else its style's); the other units are *solid*. The *hole* of a page is `bottom` − (the page's area as it is now) − (the top of its last unit + that unit's height). All numbers are whole numbers; floor rounds down.

- *Gaps.* Among the first `u` units of a page (all of them unless said otherwise), every two solid units `A` and `B` that belong to different paragraphs and have only blank units between them have a gap: `size` = max(0, top of `B` − (top of `A` + height of `A`)). `usual` = of the sizes that are above 0, sorted from the smallest, the one at place floor(count / 2), counting from 0; 0 if there are none. Each gap gets `least` = `size` − floor(`size` × `g` / 1000), where `g` = 400 if `size > usual`, else 200; and `most` = `size` if `size > 2 × usual`, else `size` + floor(`size` × `m` / 1000), where `m` = 250 if `2 × size < usual`, else 400. Then, for each picture with `topbottom` or `square` placed on the page, with `E` = the end of its band and `p` = the place of its unit in the list of units (0 if it has none): a gap whose `A` is at place `p` or later and whose `B` has a top less than `E` keeps its size (`least` = `most` = `size`); a gap whose `A` is at place `p` or later and has a top less than `E`, while the top of its `B` is `E` or more, gets `least` = max(`least`, `size` − (top of `B` − `E`)); and if the picture has a unit, the last gap whose `B` is at place `p` or before it gets `least` = max(`least`, `size` − max(0, `py` − (top of `A` + height of `A`))). The extra gap of *Pulling in* is never "at place `p` or before it". Where several pictures limit one gap, its `least` is the largest of their limits (and `size` where one of them holds it), whatever their order. So what was placed beside or below a picture after its item moves together with it, nothing moves up into its band, and the picture does not come up past the paragraph above it.
- *Shrinking by `n`* is possible if `n > 0` and the sum of `size − least` over the gaps is at least `n`. Then `L` is the largest whole number ≥ 0 for which the sum of `size − max(least, min(size, L))` is at least `n`, and every gap gets `new` = max(`least`, min(`size`, `L`)).
- *Stretching by `n`*: `c` = the number of gaps with `most > size`, and `n'` = `n − c + 1`. If `n' ≤ 0`, it is possible and every gap gets `new` = `size`. Otherwise it is possible if the sum of `most − size` is at least `n'`; then `L` is the smallest whole number ≥ 0 for which the sum of `min(most, max(size, L)) − size` is at least `n'`, and every gap gets `new` = min(`most`, max(`size`, `L`)).
- *Moving.* When the gaps among the first `u` units get their `new` sizes, those units move, from the first on, with a shift `d` that starts at 0. At the unit `B` of a gap, `d` grows by `new − size`. A blank unit between the `A` and `B` of a gap gets the top: the new top of `A` + the height of `A` + floor(`r` × `new` / `size`), where `r` = its old top − (the old top of `A` + the height of `A`); the last term is 0 when `size` = 0. Blank units keep their heights, so with less room one may reach into the unit below it. Every other unit moves down by `d` as it is at that unit (up, when `d` is below 0). A table row that moves takes along the cells that start in it (their `y`) and all lines of their paragraphs. A picture placed on the page whose unit is among the first `u` units moves by as much as its unit; a picture without a unit does not move. The units after the first `u` do not move.
- *Sections.* A paragraph *begins a section* if it is the first one, has `together` or `pageBreakBefore`, or its style has `keepNext`. The *end of the section* of paragraph `i` is found by `e = i`; while there is a paragraph `e + 1`, it does not have `pageBreakBefore`, and it does not begin a section or the style of `e` has `keepNext`: `e = e + 1`.
- *Pulling in* the body from paragraph `i`, line `k` of its lines `F` (the lines it has when each is broken for the whole width, as in step 2e), up to paragraph `e`: not possible if `y` is less than `low`, or one of the paragraphs `i` … `e` is a table or has a picture with `wrap`. Otherwise `at = y`, and for each paragraph `j` from `i` to `e` that is not a trailing empty paragraph (*The last paragraph*): if `j > i`, `at` += the `after` of paragraph `j − 1`; if `j > i` or `k = 0`, `at` += the `before` of `j`; then `at` += the height of each line of the `F` of `j`, from line `k` on for `j = i`, from line 0 on otherwise. `need` = `at` − (`bottom` − `area'`), where `area'` is the page's area with the footnotes of all those lines added that are not on a page yet (each once). The gaps are those of the page, and, when `k` = 0, one more: between the last solid unit `A` of the page (the last of all its units that is solid), if there is one, and the first line of paragraph `i`, with `size` = max(0, `y` + the `before` of `i` − (top of `A` + height of `A`)), and the top of its `B` taken as `y` + the `before` of `i`; it counts for `usual` like the others, and the blank units after `A` are between its `A` and `B`. It is done if `need > 0` and shrinking these gaps by `need` is possible: the units of the page move, `y` changes by the sum of `new − size` over the gaps, and the page has given.
- *Pushing* paragraph `i` over: `a = i`; while paragraph `a − 1` is not a table, its style has `keepNext`, and its first line was placed on this page: `a = a − 1`. It is not possible unless the first line of `a` was placed on this page, is not its first unit and has its top at least at `low`, none of the paragraphs `a` … `i` has a picture with `wrap`, and the heights of all lines of the `F` of `a` … `i`, with the `after` of each of them but the last and the `before` of each but the first, add up to no more than `bottom − top`. The units placed before the first line of `a` *stay*; `u` is their number, and `n` = `bottom` − (the page's area as it was when the first line of `a` was placed, before its footnotes) − the larger of: the top of the last unit that stays + its height, and `py` + `height` of each picture with `topbottom` or `square` whose unit stays. If stretching the gaps among the units that stay by `n` is possible: they move, the units from the first line of `a` on leave the page, the footnotes that came onto the page with them leave it too (they are not on a page any more), and the page has given.
- *Step 1.* Where the test of `together` or of `keepNext` for paragraph `i` would make a new page, and the page may give and its hole is at least the height of its last unit: pulling in from `i`, line 0, up to the end of `i`'s section. If that is done, this test makes no new page.
- *Step 2e.* Where the line would go to a new page and the page may give: `opens` = `s = 0` and paragraph `i` begins a section; `whole` = a line of `F` begins at item `s` (so `trial` is that line; `k` is its place in `F`), and the line taken in d is `trial` itself, not one broken again beside a picture; `byHand` = `s > 0` and the character of the paragraph's text at place `s − 1` (U+FFFC when the item there is an object) is U+2028.
  1. If `whole`, and not `opens` or the hole is at least the height of the last unit, these pieces are tried in turn, each from `i`, line `k`, until one is pulled in:
     - up to the end `e` of `i`'s section;
     - if paragraph `i` has a `list` that is one of the kinds of §5.5, and `s > 0` or the page's last unit belongs to paragraph `i − 1` and that has the same `list`: up to `l`, where `l` = `i`, and while `l < e` and paragraph `l + 1` has the same `list` as `i`: `l = l + 1`. "The same `list`" means the same kind; `level` does not matter. This piece is tried only if `l < e`;
     - if `e > i`, and either `s > 0` and not `byHand`, or `s = 0` and paragraph `i` is *only objects* (its text has at least one U+FFFC, the character of an object item, and every other character is U+0009, U+0020, U+00A0 or U+2028; a paragraph of spaces and tabs alone is not): up to `i`. Not tried when the piece before was tried with `l = i`.

     When one is pulled in: no new page; go back to b.
  2. If `whole`, `s > 0` and not `byHand`: pushing `i` over. When it is done: new page, and the placing goes on with paragraph `a`, from step 2 with `s` = 0.
  3. If not `opens` and the hole is at least the height of the last unit: `n` = `bottom` − the page's area − the larger of: the top of the last unit + its height, and `py` + `height` of each picture with `topbottom` or `square` that has a unit. If stretching the gaps of the page by `n` is possible, they move and the page has given. Then new page.

So with `adaptSpacing` only the room between paragraphs changes, never the lines, the margins or the room taken by the footnotes: a gap gives a fifth of itself at most (two fifths if it is larger than is usual on its page; empty paragraphs in it come closer together), the largest first, so that what the end of a page cut comes back; and grows by two fifths at most (a quarter if it is less than half the usual, nothing if it is more than twice the usual), the smallest first, so that a paragraph that would be cut begins whole on the next page with its headings, or so that a page whose text goes on is filled down to its lower edge. A page that ends where a section ends is not stretched. What stands beside a picture that the text keeps clear of moves together with it, the picture moves with the line that holds it, and nothing moves up into its band. The empty paragraph that ends a document never makes a page of its own.

**Placing the footnotes.** When a page is complete and has footnotes: the area's top is `bottom − area`. The separating rule has its top 101 600 below the area's top. The first footnote line has its top 152 400 below the area's top; the lines of the page's footnotes follow one another in order, and after each footnote paragraph its `after` is added. The lines of a footnote are broken by §5 like any paragraph (text area width `W`).

**Tables.** For a table paragraph, with `wₖ` the widths of its columns and `TW` their sum:

- The left edge of the table is page `left` + `shift`, where `room = W − TW` and `shift` = 0 if `room` ≤ 0 or the alignment is `left` or `justify`, floor(`room` / 2) for `center`, `room` for `right`.
- Column `c` begins at `cx` = the left edge of the table + `w₀ + … + w_{c−1}`. The width `cw` of a cell is the sum of the widths of the columns it fills (`w_c` for a cell that is not joined). Every paragraph of a cell that starts in column `c` is broken by §5 for the stretch `x0` = (`cx` − page `left`) + 72 000, `width` = max(0, `cw` − 144 000). All of §5 holds for it with the properties of its own style and its own `align`, `line` and `indentLeft`; it has no footnote number and no list marker.
- A picture, an equation or a chart in a paragraph of a cell is a box in its line as in the body (§5.1, §5.3, §5.4): it has its width as advance, makes its line as tall as it needs, and with `block` stands on a line of its own. Nothing makes it smaller: one that is wider than the cell reaches past the cell's edge.
- The content height of a cell is the sum of the heights of all lines of its paragraphs, plus the `before` of each of its paragraphs but the first, plus the `after` of each but the last (`before` and `after` of each paragraph's own style). A content height of more than 1 073 741 823 is taken as 1 073 741 823.
- The height of a row is first 72 000, or, if larger, the largest content height + 72 000 among the cells that start in the row and fill one row only.
- Then the cells that fill more than one row are gone through in the order of the rows and columns they start at. For each: `need` = its content height + 72 000, `have` = the sum of the heights of its rows as they are by then; if `need > have`, the last of its rows grows by `need − have`, but to no more than 1 073 741 823.
- A row is *tied* to the row below it if a cell fills places in both. A *block* of rows is a row that the row above is not tied to, together with the rows below that are tied to it one after the other; the height of a block is the sum of the heights of its rows. A table without joined cells has blocks of one row.

In the steps above the rows stand for the lines: in step 1 "the heights of all lines" of a table are the heights of its rows, and the "first line" of a following table is its first row. Step 2 is replaced by this, for each block of rows in order:

- a. `gap` = `before` (of the style of the table paragraph) if the block begins with the first row and the page is not empty, else 0; `t = y + gap`.
- b. Repeat: work out `room(t, height of the block)`. If `free > t`: `t = free` and repeat. If `fl` is not 0 or `fr` is not `W`: `t = beside` and repeat. (A block needs the whole width.)
- c. If the page is not empty and `t` + the height of the block `> bottom − area`: new page, and go back to a. (Here and in b, a block higher than 1 073 741 823 is taken as 1 073 741 823 high.)
- d. The rows of the block stand one below the other: the top of the first is `t`, the top of each next one the top of the row above + that row's height; then `y` = the top of the last row + its height. A cell that starts in a row with top `rt` and in column `c` has the rectangle `x = cx`, `y = rt`, `w = cw`, `h` = the sum of the heights of the rows it fills, but at most 1 073 741 823. The first line of a cell has its top 36 000 below `rt`; its lines follow one another, with the `before` of each paragraph but the first added before its first line and the `after` of each paragraph added behind its last.

Then step 3 as for any paragraph. So a row is never split between two pages, the rows a joined cell fills stay on one page, and a block keeps clear of floating pictures by moving below them.

**Header and footer.** When all pages are made, each page `k` (counted from 1) gets its texts: first those of the header, then those of the footer, each in the order `left`, `centre`, `right`. If `skipFirstPage` is set, page 1 takes the header and footer of `first` in place of `header` and `footer` (and has neither if there is no `first`); all that follows is the same for it.

- A text is made of lines: the stored string cut at every U+000A. The empty string has no lines; any other string has one line more than it has U+000A, and a line may be empty. Only the first eight lines count.
- In each line every `{pages}` is replaced by the number of pages (all pages, also a first page that has no header or footer) and then every `{page}` by `k`, both in decimal digits. Anything else in braces stays as it is.
- The format is the character format of the paragraph style with the id `Header`, for the footer `Footer`, or of the first style if there is no such style. Only its font (the face for the style's family, bold and italic, §4.1), its size `S` and its colour are used; `asc` and `desc` are those of §4.3.
- The header (or footer) of a page is `n` lines high: `n` is the largest number of lines among its three texts, and at least 1. Its lines are `P` = `asc` + `desc` apart. Its top `t` is 457 200 below the top of the page for the header; for the footer it is page height − 457 200 − `n` × `P`, so that a footer grows upwards.
- Line `j` of a text (counted from 0) has its baseline at `t` + `j` × `P` + `asc`. An empty line is left out, but counts for the lines after it. (With one line this is 457 200 + `asc` below the top of the page for the header and 457 200 + `desc` above the bottom of the page for the footer.)
- The width `wj` of a line is the sum of the advances of its characters at size `S`; a surrogate pair is one character.
- In a place without a picture each line is placed by itself: `x` = page `left` for `left`; page `left` + floor((W − `wj`) / 2) for `centre`; page `left` + W − `wj` for `right`. A line wider than the text area is placed by the same formulas.

A header or footer with a `picture` puts it on the same pages as its texts (also when all its texts are empty). The picture, `pw` wide and `ph` high, stands in its `place` together with the text of that place, side by side, and the two are placed as one:

- `w` is the largest `wj` of the lines of that text that are not empty; the text *has lines to show* if at least one of its lines is not empty.
- `g` = `pw` if the text of that place has no lines to show on this page; otherwise `pw` + 114 300 + `w`.
- `s` = page `left` for `left`; page `left` + floor((W − `g`) / 2) for `centre`; page `left` + W − `g` for `right`.
- For `left` and `centre` the picture's `x` is `s`, and every line of the text has `x` = `s` + `pw` + 114 300: the lines begin together. For `right` line `j` has `x` = `s` + `w` − `wj` (the lines end together), and the picture's `x` is `s` + `g` − `pw`: the picture stands at the outer end.
- The picture's `y` = `t` + floor((`n` × `P` − `ph`) / 2): its middle is the middle of all the lines. floor rounds down also when its argument is negative (a picture higher than the lines).
- The texts of the other two places are placed as without a picture.

In `bands` the lines to show stand one entry each: first those of the header, then those of the footer; within each first the lines of `left` from the first to the last, then those of `centre`, then those of `right`. The pictures stand in `bandPictures`, the header's first.

Header and footer stand in the margins and move nothing in the text area.

## 7. Drawing

- One EMU is 1/9525 of a pixel at 96 dpi and 100 % zoom.
- Each text item that is not a tab, a line break or a second half of a surrogate pair draws its glyph at the item's left edge, at the drawing size `Sd` and in the colour of its format, with its origin on the baseline — for raised text `floor(S / 3)` above it, for lowered text `floor(S / 6)` below it.
- A **stretch** in the three rules that follow is a run of neighbouring items of a line whose character formats are equal in every property.
- **Highlight**: for each stretch of items of one highlighted format on a line, a filled rectangle in the highlight colour from the left edge of its first item to the right edge of its last item, from `asc` above the line's baseline to `desc` below it (both at size `S`, also for raised and lowered text). Highlights are drawn before everything else of the line.
- **Strike-through**: for each stretch of items of one struck-through format on a line, a filled rectangle in the text colour from the left edge of its first item to the right edge of its last item, with top = baseline − `raise` − `round(strikePosition × Sd, upem)` and height = max(1, `round(strikeSize × Sd, upem)`), where `raise` is `floor(S / 3)` for raised text, −`floor(S / 6)` for lowered text, else 0.
- **Picture** standing in a line: fills the rectangle whose left edge is the item's left edge, whose bottom is the baseline, with the picture's `width` and `height`.
- **Floating picture**: fills its rectangle (§6). On each page the pictures with `wrap` `behind` are drawn first, then the lines of the body, then the table cells, then the other floating pictures, each group in the order in which the pictures were placed.
- **Borders of a paragraph** (§2.2), for a paragraph that has any: filled rectangles in their `color`, drawn before the items of the line they belong to. Let the *text area* of the paragraph begin at `A` and be `Wd` wide: page `left` and the width between the margins for the body and for footnotes; for a paragraph in a table cell, where the lines of the cell begin (page `left` + `x0` of §6) and the width they are broken for. Let `il`, `ir` and `if` be the left, right and first-line indent that hold for the paragraph, `il` and `ir` counting as 0 if below 0. The left edge of the text is `tl = il + level × 457 200` for a list paragraph (level as in §5.5) and `tl = max(0, il + min(0, if))` otherwise; `xl = A + tl`, less `sideGap + left` if `left` > 0; `xr = A + Wd − ir`, plus `sideGap + right` if `right` > 0. For every line of the paragraph, with top `y` and height `h` (§5.3, the room for the borders included):
  - if it is the paragraph's first line and `top` > 0: a rectangle at (`xl`, `y`), `xr − xl` wide and `top` high;
  - if it is the paragraph's last line and `bottom` > 0: a rectangle at (`xl`, `y + h − bottom`), `xr − xl` wide and `bottom` high;
  - if `left` > 0: a rectangle at (`xl`, `y`), `left` wide and `h + t` high; if `right` > 0: one at (`xr − right`, `y`), `right` wide and `h + t` high. `t` is 0, except for the last line of a paragraph of the body when the next line on the same page is the first line of the next paragraph of the body, that paragraph is not a table, has the same `left`, `right`, `sideGap` and `color`, and the same `tl` and `ir`: then `t` is the distance from the bottom of this line to the top of that one, so that the lines at the sides run on through the room between the two paragraphs.
- **Shading**: on each page, after the lines of the body and before any table cell is drawn, each cell on the page with a `shade` gets a filled rectangle in that colour at (`x`, `y`) of its rectangle, `w + ox` wide and `h + oy` high: `ox` = 3175 if there is a column to the right of the cell, else 0; `oy` = 3175 if there is a row below the cell, else 0. (Reaching a little into the next cell keeps two shaded cells from showing a seam.)
- **Table cell**: its lines like any lines; then up to four black rectangles, each centred on an edge of the cell's rectangle `x`, `y`, `w`, `h`. With `T` the table's `lineWidth` and `H = floor(T / 2)` they are, each given as left, top, width, height: top (`x − H`, `y − H`, `w + T`, `T`), bottom (`x − H`, `y + h − H`, `w + T`, `T`), left (`x − H`, `y − H`, `T`, `h + T`), right (`x + w − H`, `y − H`, `T`, `h + T`). Which of them are drawn is said by the table's `lines`: `all`: the four; `horizontal`: top and bottom; `outer`: top if the cell starts in the first row, bottom if it fills the last row, left if it starts in the first column, right if it fills the last column; `none`: none.
- **Header and footer**: the characters of each text at size `S` in the colour of its format, starting at its `x` on its baseline (§6). A picture of the header or footer fills the rectangle `x`, `y`, `w`, `h` of its entry in `bandPictures`; these pictures are drawn after the texts of header and footer and before everything else on the page.
- **List marker**: its characters at size `S`, starting at the `x` of `mark` on the baseline of the line (§5.5).
- **Equation, chart**: the pieces of §8 and §9, placed from the item's left edge and the line's baseline, in the colour of the run's format where no colour is given.
- **Footnote reference**: the digits of its number at size `Sr`, in the font and colour of its run's format, starting at the item's left edge, on a baseline `floor(S / 3)` above the line's baseline.
- **Footnote mark**: before the first line of a footnote, the digits of its number drawn the same way in the character format of that paragraph's style, starting at `x` of that line − `mark` (§5.2).
- **Separating rule**: a black rectangle from the left edge of the text area, floor(W / 3) wide and 9525 high, with its top at the `y` of `notes`.
- **Underline**: for each stretch of items of one underlined format on a line, from `x` = the left edge of its first item, `w` wide to the right edge of its last item, in the text colour, with `top` = baseline + `round(−underlinePosition × S, upem)` and `t` = max(1, `round(underlineThickness × S, upem)`). By `underlineStyle`:
  - `single`: a filled rectangle at (`x`, `top`), `w` wide and `t` high;
  - `double`: that rectangle, and a second one of the same size at (`x`, `top + 2t`);
  - `thick`: one rectangle at (`x`, `top`), `w` wide and `2t` high;
  - `dotted`: rectangles `t` high at `top`, the first at `x`, each next one `2t` further right, as long as it starts before `x + w`; each is `t` wide, the last one cut off at `x + w`;
  - `dashed`: the same with rectangles `6t` wide, each next one `9t` further right;
  - `wave`: with `n = floor(w / 2t)`: if `n` < 1, as `single`; otherwise one line `t` thick through the points (`x + 2t·k`, `top + floor(t / 2)` for even `k`, `top + floor(t / 2) + 2t` for odd `k`), `k` = 0 … `n`.
- **Citation**: its text (§4.5) is drawn like text of the format of its run, starting at the left edge of the item.
- **Hyphen**: a line with `hy` draws U+002D behind its last item, in the format of that item and on its baseline (raised or lowered like it): with `d` the advance of U+002D at the drawing size of that format, its left edge is the right edge of the item − `d`. Highlight, underline and strike-through of the item reach under it, because they go to the item's right edge.
- **Leader**: the leader tab of a `toc` paragraph (§5.2), with its left edge `x` measured from the left edge of the page and its advance `a` on its line, draws filled squares in the text colour of its format. With `t` as for an underline of that format: each square is `t` wide and `t` high with its bottom on the baseline; there is one at every left edge `X` that is a whole multiple of `4t` with `X ≥ x + 2t` and `X + t ≤ x + a − 2t`.

## 8. Equations

### 8.1 Structure

`tree` is a *row*: a JSON array of nodes. A node is one of:

| Node | Meaning |
|---|---|
| `{ "c": "x", "k": "bin" }` | one character. `k` is its class: `ord` (may be left out), `bin`, `rel`, `punct` or `op` |
| `{ "fn": "sin" }` | an upright name; its class is `op` |
| `{ "frac": [ row, row ] }` | numerator and denominator |
| `{ "sqrt": row }` | a square root |
| `{ "base": node or row, "sub": row, "sup": row }` | a base with an index and/or an exponent |
| `{ "open": "(", "close": ")", "body": row }` | brackets that grow with what they hold; `close` may be missing |

### 8.2 Font, sizes, boxes

- The font is the family `Cambria Math`, regular (found by §4.1 and listed in the manifest). Colour and size `S` come from the format of the equation's run.
- Every part has a **level**: the top row has level 0. The size at a level is `s₀ = S`, `s₁ = floor(7·S / 10)`, `s₂ = floor(S / 2)`.
- **Display style** holds for the top row of an equation with `block`; the rules below say where it is passed on.
- Every part has a **box**: width `W`, height `H` above its baseline, depth `D` below it. Positions inside a box are given as `x` from its left edge and `up` above its baseline.
- **Glyph box** of a character at size `s`: `W = round(advance × s, upem)`; with `yMin`, `yMax` the glyph's bounding box in the `glyf` table (0, 0 for a glyph without outline; −descender, ascender if the font has no `glyf` table): `H = round(yMax × s, upem)` if yMax > 0, else 0; `D = round(−yMin × s, upem)` if yMin < 0, else 0.
- With `mLo`, `mHi` the `yMin`, `yMax` of the glyph of U+2212 (minus sign):
  `axis(s) = round((mLo + mHi) × s, 2·upem)`, `t(s) = max(1, round((mHi − mLo) × s, upem))`. If the font has no such glyph or mHi ≤ mLo: `axis(s) = floor(s / 4)`, `t(s) = max(1, floor(s / 16))`.
- `e(s) = floor(s / 18)`.

In the rules below `s` is the size at the part's own level, and `a`, `t`, `e` are `axis(s)`, `t(s)`, `e(s)`.

**Order in `draw`** (§2.4): a part first lists what it draws itself (a glyph, a bar, a root sign), then its inner parts in this order — row: its items from left to right; fraction: numerator, denominator; root: body; index and exponent: base, exponent, index; brackets: opening bracket, body, closing bracket.

### 8.3 Row

The **class** of an item: a character has its `k`; a name is `op`; a base with index or exponent has the class of its base; everything else is `ord`. Going from left to right, a `bin` item becomes `ord` if it is the first item, or the item before it (after this adjustment) is `bin`, `rel`, `punct` or `op`, or it is the last item, or the item after it is `rel` or `punct`.

The items stand side by side on the baseline, with the row's level and display style. At level 0, before every item but the first there is a space of `n × e`, where `n` follows from the classes of the item before (left) and this item (right) — the first line that applies:

| Classes | n |
|---|---|
| either is `bin` | 4 |
| both are `rel` | 0 |
| one is `rel`: left is `punct` | 3 |
| one is `rel`: right is `punct` | 0 |
| one is `rel`, otherwise | 5 |
| left is `punct` | 3 |
| left is `op`, right is `ord` or `op` | 3 |
| left is `ord`, right is `op` | 3 |
| anything else | 0 |

`W` is the sum of the widths and spaces; `H` and `D` are the largest of the items.

### 8.4 Character and name

- A character of class `ord`, `bin`, `rel` or `punct`: its glyph at size `s`; the box is the glyph box.
- A character of class `op`: its glyph at size `s' = floor(3·s / 2)` in display style, else `s`. With `h`, `d` the height and depth of the glyph box at `s'`, the glyph is raised by `shift = a − floor((h − d) / 2)`. `W` is the width of the glyph box; `H = max(0, h + shift)`; `D = max(0, d − shift)`.
- A name: its characters, upright, side by side at size `s`; `H` and `D` are the largest of their glyph boxes.

### 8.5 Fraction

Numerator and denominator are rows at level `L` if the fraction is in display style, else at level `min(L + 1, 2)`; neither is in display style. With `Wn, Hn, Dn` and `Wd, Hd, Dd` their boxes:

- `g = 3·t` in display style, else `t + floor(t / 2)`;
- `inner = max(Wn, Wd) + 2·e`; `W = inner + 2·e`;
- the bar is a rectangle from `x = e`, `inner` wide, from `up = bar` to `up = bar + t`, where `bar = a − floor(t / 2)`;
- the numerator stands at `x = e + floor((inner − Wn) / 2)`, `up = bar + t + g + Dn`;
- the denominator stands at `x = e + floor((inner − Wd) / 2)`, `up = bar − g − Hd`;
- `H` = the numerator's `up` + `Hn`; `D = max(0, Dd − the denominator's up)`.

### 8.6 Square root

The body is a row at the same level and display style, with box `Wb, Hb, Db`.

- `g = 2·t` in display style, else `t + floor(t / 2)`;
- the body stands at `x = 10·e` on the baseline; `W = 11·e + Wb`; `H = Hb + g + t`; `D = Db`;
- the sign is a line of thickness `t` through these points (`x`, `up`), with `yb = −Db`, `yc = Hb + g + floor(t / 2)`, `hs = yc − yb`:
  (`e`, `yb + floor(2·hs / 5)`), (`3·e`, `yb + floor(hs / 2)`), (`6·e`, `yb`), (`9·e`, `yc`), (`W`, `yc`).

### 8.7 Index and exponent

The base is laid out by the rule for its own kind (a character of class `op` by §8.4, a row by §8.3, and so on) at level `L` with the display style it is given; index and exponent are rows at level `min(L + 1, 2)`, not in display style. `Wb, Hb, Db` is the box of the base, `Wp, Hp, Dp` of the exponent, `Wq, Hq, Dq` of the index. An absent exponent or index is not placed and has width 0.

**Above and below** — in display style, when the base is the character U+2211, U+220F, U+22C3 or U+22C2, or the name `lim`, `max` or `min`:
`W = max(Wb, Wp, Wq)`; each of the three is centred, at `x = floor((W − its width) / 2)`; the exponent stands at `up = Hb + 3·e + Dp`, and then `H` = that + `Hp` (else `H = Hb`); the index stands `Db + 3·e + Hq` below the baseline, and then `D` = that + `Dq` (else `D = Db`).

**At the side** — otherwise: the base at `x = 0`; exponent and index at `x = Wb`;
- the exponent is raised by `u = max(floor(2·s / 5), Hb − floor(3·s / 10))`;
- the index is lowered by `v = max(floor(s / 5), Db + floor(s / 10))`;
- if both exist and `(u − Dp) − (Hq − v) < 3·t`, `v` grows by the missing amount;
- `W = Wb + max(Wp, Wq) + e`; `H = max(Hb, u + Hp)` with an exponent, else `Hb`; `D = max(Db, v + Dq)` with an index, else `Db`.

### 8.8 Brackets

The body is a row at the same level and display style, with box `Wb, Hb, Db`. For the opening bracket, and for the closing one if there is one: let `w, h, d` be its glyph box at size `s`.

- If `Hb ≤ h` and `Db ≤ d`: the glyph at size `s` on the baseline.
- Otherwise: `need = 2 · max(Hb − a, Db + a)`; the glyph is drawn at size `s' = ceil(s · need / max(1, h + d))`, at least `s` and at most `6·s`; with `w, h, d` now its glyph box at `s'`, it is raised by `shift = a − floor((h − d) / 2)`, and its box is `w`, `max(0, h + shift)`, `max(0, d − shift)`.

Opening bracket, body and closing bracket stand side by side; `H` and `D` are the largest of the three.

### 8.9 The typed line

`source` is turned into `tree` by the editor. Only `tree` decides the layout; this section describes the editor of Reiks Document in short and is not needed to read or lay out a document:

- Letters a–z and A–Z become the mathematical italic letters (U+1D44E…, U+1D434…; h is U+210E), Greek α–ω become U+1D6FC…, `-` becomes U+2212, `*` becomes U+2217. The names sin, cos, tan, cot, sec, csc, arcsin, arccos, arctan, sinh, cosh, tanh, log, ln, exp, lim, max, min, det, gcd, abs stay upright.
- `\name` gives a symbol (`\alpha` … `\omega`, `\infty`, `\pm`, `\times`, `\cdot`, `\le`, `\ge`, `\ne`, `\approx`, `\to`, `\partial`, `\sum`, `\prod`, `\int` and others); a space after the name only ends the name. An unknown name is written upright.
- Text between two `"` is an upright name with exactly these characters.
- Classes: `bin` for + − ± ∓ × ÷ ⋅ ∗ ∪ ∩ ∘ ⊕ ⊗ ∧ ∨; `rel` for = < > : ≤ ≥ ≠ ≈ ≡ ∼ ∈ ∉ ⊂ ⊆ → ← ↔ ⇒ ⇔ ∝ ≪ ≫; `punct` for , and ;; `op` for ∑ ∏ ∫ ∬ ∭ ∮ ⋃ ⋂; `ord` for everything else.
- An *element* is a number, a letter or other `ord`/`op` character, a name, a bracket group, or a root.
- `^` and `_` give the element before them an exponent or index: an optional sign and the elements that follow up to a space (a space after a `\name` counts here too).
- `/` makes a fraction of the elements directly before it and directly after it; `a/b/c` is `(a/b)/c`.
- `\sqrt` takes the next element.
- `(`, `[` and `{` open brackets that grow. Round brackets around a whole numerator, denominator, index, exponent or root body are not kept.

## 9. Charts

A chart is a rectangle `W` × `H` (its `width` and `height`) standing on the baseline. The positions below are from its top left corner, y downward. `f` is the font of the chart's run and `S` its size; `Sc = floor(4·S / 5)`. `asc(z)` and `desc(z)` are §4.3 at size `z`; `ac = asc(Sc)`, `dc = desc(Sc)`, `lc = ac + dc`. The width of a text is the sum of the advances of its characters (§4.3). `P` = 76 200. Text is in the colour of the run's format. A value missing in a series counts as 0; only the first `n` values count, `n` being the number of categories.

**Values as text**: the whole part in decimal digits; if there is a fraction, a point and its three digits without zeros at the end; a negative value has U+2212 in front.

**Colours** (RRGGBB), used in turn and again from the start: 0072B2, E69F00, 009E73, D55E00, CC79A7, 56B4E9, F0E442, 999999. Bars and lines take the colour of the number of their series, pie slices that of their category.

Drawn in this order:

1. **Frame**: four rectangles in colour D0D0D0, in this order: top (0, 0, `W` wide, 9525 high), bottom (0, `H − 9525`, `W` wide, 9525 high), left (0, 0, 9525 wide, `H` high), right (`W − 9525`, 0, 9525 wide, `H` high).
2. **Title**, if not empty: at size `S`, at `x = max(0, floor((W − its width) / 2))`, baseline `P + asc(S)`. Then `y0 = P + asc(S) + desc(S) + 38100`; without a title `y0 = P`.
3. **Legend**: its entries are the categories for a pie; for the other kinds the names of the series if there are at least two; otherwise there is no legend and `y1 = H − P`. With a legend: `top = H − P − lc`, `y1 = top − 38100`, `q = floor(7·Sc / 10)`, `gap = floor(Sc / 3)`. An entry is `q + gap +` the width of its text wide; `total` is the sum of the entries plus `Sc` between neighbours. The first entry starts at `x = max(P, floor((W − total) / 2))`, each next one `Sc` after the end of the one before. An entry is a square `q` × `q` at (`x`, `top + ac − q`) in its colour, then its text at size `Sc` at `x + q + gap`, baseline `top + ac`; the entries are drawn one after another.
4. **Names of the axes** (`xTitle` for the horizontal one, `yTitle` for the vertical one), for every kind but the pie. `yc = 0`. In this order:
   - If `xTitle` is not empty: `xb = y1 − dc`, and `y1` shrinks by `lc + floor(Sc / 4)`.
   - If `yTitle` is not empty and `axisNames` is `ends`: its text is drawn now, at size `Sc` at `x = P`, baseline `y0 + ac`; then `y0` grows by `lc + floor(Sc / 4)`.
   - If `yTitle` is not empty and `axisNames` is `middle`: `yc = lc + floor(Sc / 4)`.
   - The rest is drawn further down, at the place where the rules say "the names of the axes", when `left`, `top`, `pw` and `ph` are known. First the name of the horizontal axis, if not empty: at size `Sc`, baseline `xb`, at `x = left + floor((pw − its width) / 2)` for `middle` and at `x = left + pw − its width` for `ends`. Then, for `middle` only and if not empty, the name of the vertical axis as turned text (`textup`, §2.4) at size `Sc`, starting at `x = P + ac`, `y = top + ph − floor((ph − its width) / 2)`. Both divisions round down also when the result is negative.
5. Without categories or without series nothing more is drawn (a graph of formulas has no categories and goes on).

**Pie.** `vᵢ = max(0, value i of the first series)`, `total` their sum, `r = floor(min(W − 2·P, y1 − y0) / 2)`. If `total` > 0 and `r` > 0: for each `vᵢ` > 0 a slice of the circle with centre (`floor(W / 2)`, `floor((y0 + y1) / 2)`) and radius `r`, from `(v₀ + … + vᵢ₋₁) / total` to `(v₀ + … + vᵢ) / total` of a full turn, clockwise from 12 o'clock. A program may draw each slice with a hair-thin outline in the slice's own colour, so that no seam shows between neighbours.

**Bars and lines.**

- Axis: `lo` = the smallest of 0 and all values, `hi` = the largest of 0 and all values; if they are equal, `hi = lo + 1000`. `step` is the first of 1, 2, 5, 10, 20, 50, 100, … (thousandths) for which `(aHi − aLo) / step ≤ 6`, where `aLo` is the largest multiple of `step` not above `lo` and `aHi` the smallest multiple not below `hi`. The ticks are `aLo`, `aLo + step`, …, `aHi`.
- `left = P + yc +` the width of the widest tick text at `Sc` `+ floor(Sc / 3)`; `right = W − P`; `top = y0 + floor(lc / 2)`; `bottom = y1 − lc − floor(Sc / 4)`; `pw = right − left`; `ph = bottom − top`. If `pw` or `ph` is not above 0 nothing more is drawn. Then the names of the axes (step 4).
- `Y(v) = bottom − floor((v − aLo) × ph / (aHi − aLo))`.
- For each tick `v`, from `aLo` upward, first its line and then its text: a rectangle at (`left`, `Y(v) − 4762`), `pw` wide and 9525 high, colour D9D9D9 (808080 for the tick 0); its text at size `Sc`, ending at `left − floor(Sc / 3)`, baseline `Y(v) + floor((ac − dc) / 2)`.
- `bw = floor(pw / n)`. Then all category labels in order: the label of category `i` at size `Sc` at `x = left + i·bw + floor((bw − its width) / 2)`, baseline `bottom + floor(Sc / 4) + ac`.
- **Bars** (`m` series): `mg = floor(bw / 8)`, `sw = floor((bw − 2·mg) / m)`. The bar of series `j` in category `i` is a rectangle from `x = left + i·bw + mg + j·sw`, `sw` wide, between `Y(0)` and `Y(value)`; a bar without height is not drawn. The bars are drawn series by series, and within a series category by category.
- **Lines**: series by series; for each series, if `n` > 1 a line 19 050 thick through the points (`left + i·bw + floor(bw / 2)`, `Y(value)`), then a filled circle of radius 28 575 at each point, in the order of the categories.

**Graph of formulas** (`plot`). Each series holds `N + 1` values `v₀ … v_N` (`N` ≥ 1), or `null` where there is none; `from` and `to` become thousandths like the values; the series need not be equally long (`N` is taken for each series by itself); tick texts are written by the rule for values as text. A reader never works out the formula: it draws the stored values. (The editor of Reiks Document makes them at 201 points and leaves out a value that is not a number or is beyond ±10⁹.) Nothing more is drawn if no series has a value or `to` ≤ `from`. The legend rule above holds (the names of the series, if there are at least two).

- `vLo`, `vHi` = the smallest and the largest of all values (0 is not added); if they are equal, `vHi = vLo + 1000`. `yStep`, `yLo`, `yHi` are the `step`, `aLo`, `aHi` of the axis rule above for `vLo`, `vHi`.
- `xStep` is the first of 1, 2, 5, 10, 20, 50, … for which `floor((to − from) / xStep) ≤ 8`.
- `left = P + yc +` the width of the widest y tick text at `Sc` `+ floor(Sc / 3)`; `top = y0 + floor(lc / 2)`; `bottom = y1 − lc − floor(Sc / 4)`; `pw = W − P − left`; `ph = bottom − top`. If `pw` or `ph` is not above 0 nothing more is drawn. Then the names of the axes (step 4).
- `Y(v) = bottom − floor((v − yLo) × ph / (yHi − yLo))`; `X(v) = left + floor((v − from) × pw / (to − from))`.
- For each y tick `v` from `yLo` to `yHi`: line and text exactly as for bars (rectangle at (`left`, `Y(v) − 4762`), `pw` wide, 9525 high; text ending at `left − floor(Sc / 3)`, baseline `Y(v) + floor((ac − dc) / 2)`).
- Then for each x tick `v`, from the smallest multiple of `xStep` not below `from`, in steps of `xStep` while `v ≤ to`: a rectangle at (`X(v) − 4762`, `top`), 9525 wide and `ph` high, colour D9D9D9 (808080 for the tick 0), then its text at size `Sc` at `x = X(v) − floor(its width / 2)`, baseline `bottom + floor(Sc / 4) + ac`.
- Then the curves, series by series in the colour of the series: point `k` is (`left + floor(k × pw / N)`, `Y(v_k)`); every run of two or more neighbouring points that have values is one line 19 050 thick.

## 10. Versions

- `formatVersion` changes when the file structure changes in a way an older reader cannot ignore.
- `layoutRules` changes whenever any rule in §4–§9 changes, so that a layout made by other rules is never taken for a valid one.

| formatVersion | layoutRules | Added |
|---|---|---|
| 1 | 1 | text, styles, pages |
| 2 | 2 | pictures, footnotes |
| 3 | 3 | equations, charts; a line can have a depth from the objects on it |
| 4 | 4 | struck-through, highlighted, raised and lowered text; line spacing and left indent of a single paragraph |
| 5 | 5 | lists; floating pictures; graphs of formulas |
| 6 | 6 | header and footer with page numbers |
| 7 | 7 | tables |
| 8 | 8 | titles for the axes of a chart |
| 9 | 9 | the names of the axes stand in the middle (the vertical one turned) or at the ends; turned text |
| 10 | 10 | more kinds of lists, and levels |
| 11 | 11 | sources and citations; kinds of underline |
| 12 | 11 | the format calls itself `udoc` and its files end in `.udoc` (until version 17) |
| 13 | 12 | the table of contents: `toc` paragraphs and their leader tab |
| 14 | 12 | links: the character property `link` |
| 15 | 13 | tables: joined cells, shading, which lines a table has and how thick |
| 16 | 14 | room above and below, right indent and first-line indent of a single paragraph |
| 17 | 15 | words divided at the ends of lines (`hyphens`, `hy`); no line alone at the bottom or top of a page (`widowControl`) |
| 18 | 15 | the format calls itself `docu` and its files end in `.docu` |
| 19 | 16 | pictures, equations and charts inside table cells |
| 20 | 17 | blocks that start on a new page when less than half of them fits (`together`); a list can stand at the left edge of the text (`indentLeft` down to −228 600) |
| 21 | 18 | a picture in the header and in the footer (`picture`, `bandPictures`) |
| 22 | 19 | a header and footer of its own for the first page (`first`) |
| 23 | 20 | texts of several lines in header and footer |
| 23 | 21 | fonts are also looked for in the folder where Office keeps the fonts of its documents (§4.1) |
| 24 | 22 | lines at the edges of a paragraph (`borders`) |
| 25 | 23 | reference systems: `citations` can be `harvard`, `chicago`, `mla`, `ieee` or `vancouver` (§4.5) |
| 26 | 24 | the room between paragraphs gives a little at the ends of pages (`adaptSpacing`, §6 *Giving room*) |
| 26 | 25 | with `adaptSpacing`: pictures move with the line that holds them and what stands beside them moves along, so room gives above pictures too; the empty paragraph that ends a document never makes a page of its own (§6 *The last paragraph*) |
| 26 | 26 | with `adaptSpacing`: a picture that the text keeps clear of does not come up past the paragraph above its line (§6 *Giving room*, *Gaps*) |

This description is that of format version 18. Before it, the `format` of the manifest was `udoc` and the files ended in `.udoc` (versions 12 to 17), and before that `rks`, in the first files `rwd`, with files ending in `.rks` and `.rwd` (up to version 11). A reader of this version does not have to read those: Reiks Document opens files whose `format` is `docu` and whose `formatVersion` is 18 or later, and says of the others that they come from an earlier version.
