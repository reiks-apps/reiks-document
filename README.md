# Reiks Document

A small document editor for Windows with its own open file format, the **Reiks document** (`.docu`).

A Reiks document holds the text and, next to it, the finished layout: the place of every line and every object on every page, in whole units. It also holds fingerprints of the content and of every font file the layout was made with. When a document is opened and the fingerprints match, its pages are shown exactly as stored. If a font differs, the program says so and lays the document out again by the rules of the format.

The format and every layout rule are written down in [FORMAT.md](FORMAT.md). What works and what does not is in [STATUS.md](STATUS.md).

Everything is local: the program has no network code and sends nothing anywhere. Its windows are kept out of screenshots and screen recordings unless you allow them under Preferences.

## What you need

- Windows 10 or 11
- The .NET 10 SDK
- The fonts Calibri, Calibri Light and Cambria Math (they come with Windows and Office)

No other packages are used.

## Build

```
cd D:\ReiksDocument
dotnet build -c Release
```

The program is then `bin\Release\net10.0-windows\ReiksDocument.exe`.

## Install

`python tools\make_setup.py` builds the setup file, `dist\ReiksDocumentSetup-<version>.exe`: one file that carries the program inside. Add `--no-build` to pack the program as it was last built.

Run the setup file to put Reiks Document on a PC, for the user who runs it:

- the program goes into `%LOCALAPPDATA%\Programs\Reiks Document`; no administrator rights are needed;
- it gets an entry in the Start menu and, if you tick it, a shortcut on the desktop;
- if you leave it ticked, `.docu` documents open with Reiks Document and show their icon;
- running a newer setup file replaces the program; documents and preferences stay.

To remove it, use Installed apps in the Settings of Windows. That takes away the program, the shortcuts and the file type; your documents stay, and your preferences too unless you tick that they should go.

The PC needs the .NET 10 Desktop Runtime. Where it is missing, Windows says so when the setup file is started, and offers it.

The setup file can be tried without touching Windows: `ReiksDocumentSetup-<version>.exe --trial-install <folder>` puts everything into that folder (what would go into the registry is written to `registry.txt` there), and `--trial-uninstall <folder>` takes it away again.

## Run

```
bin\Release\net10.0-windows\ReiksDocument.exe
bin\Release\net10.0-windows\ReiksDocument.exe samples\Sample.docu
```

`samples\Sample.docu` is a six-page document that uses every feature. It can be written again with:

```
bin\Release\net10.0-windows\ReiksDocument.exe --sample samples\Sample.docu
```

## Using it

| To do this | Do this |
|---|---|
| New, open, save, save under a new name | Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Shift+S. New opens an empty document in a window of its own; Open does the same for a file, unless the window is still empty |
| Save as PDF | Ctrl+Shift+P or the PDF button. The text of the PDF can be selected and searched |
| Print | Ctrl+P or the Print button opens the print box: a picture of every page, the printer, which pages (all, or typed like 1-3, 5), the number of copies, one side or both sides of the sheet (if the printer can), and the paper |
| Sources and citations | the Citation button opens the sources of the document. Add a source (authors, year, title, where it was published), choose it and press "Insert citation": the text gets (Smith, 2020) or, if numbers are chosen, [1]. "Insert list of references" puts the list below the paragraph the caret is in; it is written again by itself whenever a citation or a source changes |
| Header, footer, page numbers | the Header button: three texts each for the top and the bottom of the pages, each of one or several lines (Enter begins a new line); `{page}` and `{pages}` stand for the page number and the number of pages. A picture such as a logo can stand in each, at the left, in the middle or at the right. The first page can have its own header and footer, or none |
| Undo, redo | Ctrl+Z, Ctrl+Y, or the two arrow buttons |
| Cut, copy, paste | Ctrl+X, Ctrl+C, Ctrl+V, the three buttons in the third row, or a right click |
| Paste a picture | Ctrl+V also takes a picture: a screenshot, a picture copied in another program, or a PNG or JPEG file copied in the Explorer. Text comes first when the clipboard has both |
| Paragraph: spacing and indents | the button with the arrows beside the line spacing box, or a right click and Paragraph, opens a box for the selected paragraphs: room above and below in points, line spacing, and the indents on the left, of the first line (below 0 it stands out) and on the right. "As the style says" puts the values of the style back |
| Blocks that stay together | in the Paragraph box, "Begins a block" marks a paragraph as the start of something that belongs together, such as a task with its parts; the block goes on to the next paragraph marked like this. If less than half of a block fits at the bottom of a page, it starts on the next page; if most of it fits, it stays and the rest goes on. Word documents come with their numbered tasks marked |
| Lines and words at the ends of pages and lines | the Page button has two choices for the whole document. "No line of a paragraph alone…" keeps the first line of a paragraph from staying behind at the bottom of a page and the last from going on alone; it is on in new documents. "Divide words at the ends of lines" sets a hyphen inside long words where a line ends, by the rules of the language chosen; this fills the lines better, most of all in justified text and narrow columns. Spanish is built in; English (American spelling) and Swedish come as lists of patterns in the folder `hyph` beside the program, taken unchanged from the TeX hyphenation project, with their copyright and licence notices in `NOTICE.txt` there. Another language is offered when its list lies in that folder: `hyph-<id>.pat.txt` in the plain-text form of the TeX hyphenation patterns, with a line for it in `languages.txt` there |
| Word documents | Open also takes a Word document (.docx). It opens as a new document that is laid out anew by this program: the content and its formats are taken over, but lines and pages do not break where Word broke them. A message says what was placed differently or left out. Saving asks for a name and writes a Reiks document beside the Word file, which is never changed |
| Table of contents | give the titles of your sections the style Heading 1, 2 or 3, put the caret where the list should stand and press Contents. It lists the headings with their pages. It is written again when you press Contents again, and by itself before saving, printing and making a PDF | Hold Ctrl and click an entry to go to its heading |
| Links | select text and press Link (Ctrl+K), or press it with nothing selected to type the text and the address. A web address or an e-mail address; "www.example.org" becomes "https://www.example.org". Hold Ctrl and click a link to open it in your browser; a right click opens, changes or removes it. The program itself never goes online: it only hands the address to Windows |
| PDF | links work in the PDF, the headings are its bookmarks, and the entries of the table of contents lead to their pages
| Bold, italic, underline | Ctrl+B, Ctrl+I, Ctrl+U. The small arrow beside U opens the kinds of underline: single, double, thick, dotted, dashed, wavy |
| Capitals and small letters | the Aa button changes the selected text, or the word the caret is in: sentence case, lowercase, UPPERCASE, a capital for each word, or each letter switched |
| Code | the code button: letters of equal width, as for program text |
| Strike through, raised, lowered | the S, x² and x₂ buttons; Ctrl and a number types that number lowered (H, Ctrl+2, O gives H₂O), Ctrl+Shift and a number types it raised; Ctrl and + switches raising on and off for whatever you type or have selected, Ctrl and - does the same for lowering |
| Text colour, highlight | the A and ab buttons open a sheet of colours |
| Copy a format onto other text | put the caret in the text to copy from, press the paintbrush button, then select the text that should take it (or click a word). Ctrl+Shift+C and Ctrl+Shift+V do the same from the keyboard |
| Erase a format | the eraser button or Ctrl+Space: the selected text goes back to the look of its style; without a selection the whole paragraph does, and it loses its own alignment, line spacing and indent |
| Lists | the list button opens the kinds of list: four bullets; numbers as 1. 1) I. A. a) a. i.; and three kinds with several levels (1. 1.1. 1.1.1., 1. a. i., 1) a) i)). Tab at the start of a list paragraph moves it one level in, Shift+Tab one level out; the ← and → buttons do the same. Enter in an empty list paragraph goes one level out and at the outer level ends the list |
| Line spacing, indent | the box with 1.0 … 3.0, and the ← and → buttons (Ctrl+Shift+M / Ctrl+M), which move a paragraph by 1 cm, or by half an inch if inches are chosen under Preferences |
| Double-click a document in Explorer | Preferences, tab General, "Open .docu files with Reiks Document": Windows then opens such files with the program and shows them with the icon of a document. It holds for you only and can be undone there |
| Spelling | words the check does not know get a red wavy line; a right click on one offers other spellings, or keeps the word. The language is chosen under Preferences, tab Spelling, from those Windows can check on this PC |
| The page | the Page button: paper (A4, A5, A3, Letter, Legal), upright or on its side, and the four margins |
| Styles | the ¶ button beside the style box: font, size, colour, alignment, line spacing, space before and after, and the indents of every style; "New style…" adds one |
| Shortcuts | Preferences, tab Shortcuts: every command and every symbol of the equation box with its keys. Choose a line and press "Change…" to give it other keys, "No keys" to take them away, "Standard" to get the usual ones back |
| Preferences | the Preferences button, with five tabs. General: centimetres or inches. Appearance: light or dark colours for the program, the same as Windows, or a colour of your own (soft grey, green, blue, khaki, or any other); and the colour of the sheet on the screen (white, natural white, natural paper, soft grey, green, blue, or any other), which does not change print or PDF. Privacy: whether screenshots and screen recordings of the program are allowed. General also has the choice to keep a recovery copy |
| After a crash or a power cut | while a document has changes that are not saved, a recovery copy of it is written a few seconds after you stop typing and at least every half minute, into `%LOCALAPPDATA%\ReiksDocument\recovery`. Your own file changes only when you save. If the program did not close in the normal way, it offers the copy the next time it starts |
| Find and replace | Ctrl+F or the magnifier button; Enter goes to the next place, Esc closes |
| Left, centre, right, justify | Ctrl+L, Ctrl+E, Ctrl+R, Ctrl+J |
| New line inside a paragraph | Shift+Enter |
| Start a new page | the New page button or Ctrl+Enter (Backspace at the start of that paragraph removes it) |
| Move by word, to the start or end of the document | Ctrl+Left / Ctrl+Right, Ctrl+Home / Ctrl+End |
| Select a word, a paragraph, everything | double click, triple click, Ctrl+A |
| Zoom | Ctrl+mouse wheel, or the − and + buttons at the bottom right |
| Footnote | Ctrl+Alt+F or the Footnote button; Esc or a click returns to the text |
| Image | the Image button; click an image to select it, drag the square at its corner to resize it |
| How an image stands to the text | right-click the image and choose: in the line, on a line of its own, text above and below, text around it, behind the text, in front of the text. An image that is not in the line can be dragged to another place |
| Symbol | the Symbol button opens a table of symbols (signs for money, Greek letters, signs of mathematics, arrows, other signs, letters with accents); a click puts one into the text, and the ones used last are kept on the first tab |
| Equation | the Equation button. The box has two tabs for two ways of writing, and opens on the one used last. "In the equation": type into the drawn equation itself; press a shape (fraction, power, index, square root, sum, product, integral, brackets, absolute value) or type / ^ _ ( and fill in its parts, with Tab to go to the next part that is still empty and the arrow keys or the mouse to move. The symbols are in groups (Greek, capitals, operators, relations, sets and logic, more). In both tabs Alt and a letter types the Greek letter (Alt+A α, Alt+B β, Alt+D δ; with Shift the capital) and Alt and a number some common signs (Alt+1 ≤, Alt+2 ≥, Alt+3 ≠, Alt+8 ∞); the name and the keys of a symbol show when the mouse rests on its button. "In a line": type it in one line, for example `x^2`, `(a+b)/c`, `\sqrt(x+1)`, `\sum_(i=1)^n i^2`, `\alpha \le \pi` |
| Table | the Table button asks for rows and columns. Type in a cell; Tab goes to the next cell and adds a row behind the last one; Esc leaves the table. A right click inside the table adds or removes a row or a column, or removes the table; the buttons Row +, Column +, Row - and Column - appear while the caret is in a table and work on its cell. Drag the line between two columns to change their widths. With the caret on the table itself (click beside it), Backspace or Delete removes it |
| Box | the Box button puts in a frame for text or a picture: a table of one cell. Type into it, or put a picture, an equation or a chart into it with their buttons or with Paste; a right click changes its lines and its shading, and dragging its right edge makes it narrower. The same goes for every table cell: pictures, equations and charts can stand in them |
| Table: joining, shading, lines | a right click in a cell also joins it with the cell to its right or below it (and splits a joined cell again), shades the cell, its row, its column or the whole table, and sets which lines the table has (all, above and below the rows only, around the table only, none) and how thick they are. When the margins change, a table that filled the width of the text fills it again |
| Chart | the Chart button; one row for each category, one column for each series, the names of the series in the boxes at the top of the columns; the horizontal and the vertical axis can be given names, which stand along the axes (the vertical one turned) or at their ends. A pie has one row for each slice: its name and its value |
| Graph of a formula | the Chart button, kind "Graph of a formula": type for example `sin(x)/x` or `x^2 - 3x + 1`, and the stretch of x; several formulas go one in each line, and a line can begin with a name for its graph, as in `Height = 20x - 5x^2` |
| Change an equation or chart | double click it; for a chart a right click also offers it |
| Put an image, equation or chart on its own line | right-click it and choose "On a line of its own" |
| A line under a title, a frame around a paragraph | the Paragraph box, under Lines: None, Below, Above, Above and below, All around |
| Dark page (dark sheet, light text) | the small half-dark disc at the foot of the window, at the right; it changes the screen only, not the document, the PDF or the print |
| APA, Harvard, Chicago, MLA, IEEE, Vancouver | Citation, then choose under Reference system; the citations and the list of references are written anew in that system |
| Page numbers | Page, under Page numbers: where they stand and how they look. The Header button does the same with more freedom |
| Spelling language, words to check | the button at the foot of the window, at the left: click it to choose the language or to switch the check off; the two small arrows beside it go to the word to check before and after the caret |
| Type LaTeX | press the LaTeX button (Ctrl+Shift+E), type for example `\frac{1}{2}`, and press Enter or the button again: it becomes an equation. Or select LaTeX that stands in the text and press the button |
| Equations from LaTeX | with the LaTeX button on, paste text that holds LaTeX; or open a Word document that does: a line that is nothing but LaTeX, such as `V = \frac{4\pi r^{3}}{3}`, becomes an equation on a line of its own, and LaTeX between `$ … $` becomes an equation inside the line |

Paragraph style, font and size are chosen in the boxes of the second row. The style Normal leaves a gap after every paragraph, as Word does; the style No spacing leaves none, and Shift+Enter starts a new line without a gap. The line-spacing box sets the room between the lines inside a paragraph. Everything that is put into the document is in the third row. The number of words is shown at the bottom right.

## Checking

```
bin\Release\net10.0-windows\ReiksDocument.exe --selftest selftest-out
```

The self-test builds sample documents, lays each one out twice, saves it, opens it again and lays it out once more, and fails if any position differs. It also runs several hundred random edits and compares the layout after each with a layout from scratch, and it writes images of pages (`*.png`) so that the result can be looked at even though the window is normally kept out of captures. The result is in `selftest-out\selftest.log`; the exit code is 0 when every check passed.

A second check works from the other side: `tools\check_format.py` is a separate small program (Python 3, no packages) written from FORMAT.md alone. It lays a document out again by the written rules and compares every line, footnote, image, equation and chart with the layout stored in the file:

```
python tools\check_format.py samples\Sample.docu selftest-out\long.docu
```

It prints the number of lines and objects compared and how many differ, and ends with exit code 0 when none do.

A PDF can be made from the command line, and checked against the layout it was made from (the check needs the Python package PyMuPDF): it compares the place of every character in the PDF with the place the layout gives it.

```
bin\Release\net10.0-windows\ReiksDocument.exe --pdf samples\Sample.docu samples\Sample.pdf
python tools\check_pdf.py samples\Sample.docu samples\Sample.pdf
```

Printing can be tried without a printer: this writes the pages of a document to an XPS file through the same path as printing.

```
bin\Release\net10.0-windows\ReiksDocument.exe --printtest samples\Sample.docu out.xps
```

**Reading a Word document without the window.** `ReiksDocument.exe --import in.docx out.docu notes.txt` reads a Word document, writes it as a Reiks document, and writes what was simplified or left out to the third file (which can be left off).

**Trying the Word import on many files.** `python tools\import_check.py <out folder> <files or folders>` reads every Word document it finds and says for each result what is not tidy: lines or pictures outside the width of the text, pictures wider than their table cell, very narrow cells, empty pages; how the number of pages compares with Word's own; and which notes the import gave.

**Laying a document out again.** `ReiksDocument.exe --resave in.docu out.docu` opens a document, lays it out by the rules of this version and writes it to another file.

**Speed.** `ReiksDocument.exe --bench times.txt 6000` makes a long document of that many paragraphs (6000 are about 740 pages) and writes to the file how long the things take that happen while somebody types in it: a typed letter with and without the window, a new paragraph, undo, drawing a page, saving, opening, the recovery copy and the PDF. Nothing of the user's is touched; the window it opens for this stays off the screen.

## The source

| File | Holds |
|---|---|
| `src\Fonts.cs` | reading font files: names, metrics, glyphs, fingerprints |
| `src\Document.cs` | the document: paragraphs, runs, styles, images, footnotes |
| `src\Layout.cs` | the layout rules: lines, pages, footnotes |
| `src\Math.cs` | equations: structure, the typed line, layout |
| `src\Latex.cs` | mathematics written as LaTeX: read as the typed line, found in a text |
| `src\Chart.cs` | charts and the boxes of objects |
| `src\RksFile.cs` | reading and writing `.docu` files |
| `src\Editor.cs` | caret, selection, editing, undo |
| `src\PageRenderer.cs`, `src\PageView.cs` | drawing pages; mouse and keyboard |
| `src\MainWindow.cs`, `src\Dialogs.cs` | the window and the boxes for equations, charts, header and footer, preferences |
| `src\Settings.cs`, `src\Theme.cs` | what was chosen under Preferences; the light and dark colours; screenshots |
| `src\Plot.cs`, `src\Printing.cs`, `src\Pdf.cs` | graphs of formulas; printing; the PDF export |
| `src\Recovery.cs` | the recovery copies |
| `src\Shortcuts.cs`, `src\EquationPad.cs` | the keyboard shortcuts; the equation that is typed into |
| `src\Bench.cs` | the measuring of `--bench` |
| `src\Hyphenator.cs` | where words may be divided |
| `src\DocxImport.cs` | reading a Word document (.docx) |
| `src\Citations.cs` | sources, what a citation reads, the list of references |
| `src\Contents.cs` | the table of contents |
| `src\SampleDoc.cs`, `src\SelfTest.cs` | the sample document and the self-test |
| `tools\check_format.py` | the independent check of FORMAT.md |
| `tools\check_pdf.py` | the check of a PDF against the stored layout |
| `tools\import_check.py` | reads many Word documents and measures how tidy the results are |
| `tools\make_icon.py`, `assets\` | draws the icons of the program and of a document; the icons |
| `setup\`, `tools\make_setup.py` | the setup file: the program that installs and removes Reiks Document, and the script that builds it |

## License, name and format

- **The code** is open source under the Apache License 2.0 (`LICENSE`, `NOTICE`): anyone may read, copy, change and share it, also as part of their own programs, keeping the copyright notice and the license.
- **The `.docu` format** is free for everyone: any program may read and write `.docu` files, without asking. `FORMAT.md` describes it completely.
- **New versions of the format** are published only in this repository. A program that writes something no published version of `FORMAT.md` describes should not call those files `.docu` files.
- **The name "Reiks Document"** and its icons belong to Reiks Apps. Only versions published here carry them; a changed copy of the program needs another name and other icons.
