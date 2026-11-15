using System;
using System.Collections.Generic;
using System.Linq;

namespace ReiksDocument;

enum Align { Left, Center, Right, Justify }

/// <summary>Character properties as overrides: null means "take it from the style".</summary>
readonly record struct CharProps(string? Font = null, int? Size = null, bool? Bold = null, bool? Italic = null, bool? Underline = null, int? Color = null,
    bool? Strike = null, int? Highlight = null, int? Vert = null, string? Under = null, string? Link = null)
{
    public CharFormat ApplyTo(CharFormat f) =>
        new(Font ?? f.Font, Size ?? f.Size, Bold ?? f.Bold, Italic ?? f.Italic, Underline ?? f.Underline, Color ?? f.Color,
            Strike ?? f.Strike, Highlight ?? f.Highlight, Vert ?? f.Vert, Under ?? f.Under, Link ?? f.Link);
}

/// <summary>Fully resolved character format. Size is in half-points, Color is 0xRRGGBB.
/// Highlight is 0xRRGGBB or -1 for none. Vert: 0 normal, 1 raised (superscript), 2 lowered (subscript).
/// Link is the address the text leads to, empty for none.</summary>
readonly record struct CharFormat(string Font, int Size, bool Bold, bool Italic, bool Underline, int Color,
    bool Strike = false, int Highlight = -1, int Vert = 0, string Under = "single", string Link = "");

/// <summary>Something that stands in the text as one item: its run has the single character U+FFFC.</summary>
/// Block: it stands on a line of its own.
abstract record Inline(bool Block);

/// <summary>A picture. Media names the file in the document's media; sizes are in EMU.</summary>
/// Wrap says how it stands to the text: "inline" (in the line, like a letter), or it floats: "topbottom" (text above
/// and below only), "square" (text beside it), "behind" or "front" (the text takes no notice). Dx, Dy place a floating picture.
sealed record Picture(string Media, int Width, int Height, bool Block, string Wrap = "inline", int Dx = 0, int Dy = 0) : Inline(Block)
{
    public bool Floating => Wrap != "inline";
}

/// <summary>The reference mark of a footnote.</summary>
sealed record NoteRef(int Id) : Inline(false);

sealed class Note
{
    public int Id;
    public List<Paragraph> Paras = new();
}

sealed record Run(string Text, CharProps Props = default, string? CharStyle = null, Inline? Obj = null)
{
    public const char ObjectChar = (char)0xFFFC;
    public bool SameFormat(Run o) => Obj == null && o.Obj == null && Props == o.Props && CharStyle == o.CharStyle;
    /// <summary>The format alone: no text, no object.</summary>
    public Run Format => this with { Text = "", Obj = null };
    public static Run Object(Inline obj, Run fmt) => new(ObjectChar.ToString(), fmt.Props, fmt.CharStyle, obj);
}

readonly record struct Pos(int Para, int Off) : IComparable<Pos>
{
    public int CompareTo(Pos o) => Para != o.Para ? Para.CompareTo(o.Para) : Off.CompareTo(o.Off);
    public static bool operator <(Pos a, Pos b) => a.CompareTo(b) < 0;
    public static bool operator >(Pos a, Pos b) => a.CompareTo(b) > 0;
    public static bool operator <=(Pos a, Pos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Pos a, Pos b) => a.CompareTo(b) >= 0;
}

sealed class ParaStyle
{
    public string Id = "", Name = "";
    public CharFormat Char = new("Calibri", 22, false, false, false, 0);
    public Align Align;
    public int Before, After, Line = 1000, IndentLeft, IndentRight, IndentFirst;
    public bool KeepNext;
    public string Next = "Normal";
    /// <summary>Lines at the edges of the paragraphs of this style.</summary>
    public ParaBorders Borders;
    /// <summary>A copy that can be changed on its own (every field is a value or a string).</summary>
    public ParaStyle Clone() => (ParaStyle)MemberwiseClone();
}

sealed class CharStyle
{
    public string Id = "", Name = "";
    public CharProps Props;
}

/// <summary>The three texts of a header or a footer. {page} stands for the number of the page, {pages} for the number of pages.
/// A text can have several lines.</summary>
sealed record Band(string Left, string Centre, string Right, BandPicture? Picture = null)
{
    public static readonly Band Empty = new("", "", "");
    public bool IsEmpty => Left.Length == 0 && Centre.Length == 0 && Right.Length == 0 && Picture == null;
    /// <summary>The lines of one of the texts: they are separated by line feeds; the empty text has none.</summary>
    public static string[] LinesOf(string text) => text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
    /// <summary>How many lines the header or footer is high: as many as its longest text has, and at least one.</summary>
    public int Rows => Math.Max(1, Math.Max(LinesOf(Left).Length, Math.Max(LinesOf(Centre).Length, LinesOf(Right).Length)));
}

/// <summary>A picture in a header or footer, such as a logo: its size and the place it stands in (0 left, 1 middle, 2 right).
/// It stands in the line of the header or footer, before the text of its place (behind it at the right).</summary>
sealed record BandPicture(string Media, int Width, int Height, int Place);

/// <summary>The grid of a table: the widths of its columns and, row by row, the ids of its cells.
/// What a cell holds lives in Document.Cells, as the text of a footnote lives in Document.Notes.</summary>
sealed class TableGrid
{
    public int[] Widths = Array.Empty<int>();
    public List<int[]> Rows = new();
    public int Columns => Widths.Length;

    public int Width
    {
        get
        {
            long w = 0;
            foreach (int x in Widths) w += x;
            return (int)Math.Min(w, int.MaxValue);
        }
    }

    /// <summary>The shading of cells: the number of a cell and its colour, 0xRRGGBB. A cell that is not here has none.</summary>
    public Dictionary<int, int> Shades = new();
    /// <summary>Which lines the table has: "all", "horizontal" (above and below every row), "outer" (around the table) or "none".</summary>
    public string Lines = "all";
    /// <summary>How thick its lines are.</summary>
    public int LineWidth = DefaultLine;
    public const int DefaultLine = 9525, LeastLine = 3175, MostLine = 57150;

    public TableGrid Clone() => new()
    {
        Widths = (int[])Widths.Clone(), Rows = Rows.Select(r => (int[])r.Clone()).ToList(),
        Shades = new Dictionary<int, int>(Shades), Lines = Lines, LineWidth = LineWidth,
    };

    // A joined cell stands at several places of the grid: all of them hold its number, and they form a block.

    /// <summary>True at the first place of a cell, its top left; false where a joined cell only reaches.</summary>
    public bool Starts(int r, int c) => (c == 0 || Rows[r][c - 1] != Rows[r][c]) && (r == 0 || Rows[r - 1][c] != Rows[r][c]);

    /// <summary>How many columns and rows the cell that starts here fills.</summary>
    public (int Cols, int Rows) Span(int r, int c)
    {
        int id = Rows[r][c], cols = 1, rows = 1;
        while (c + cols < Columns && Rows[r][c + cols] == id) cols++;
        while (r + rows < Rows.Count && Rows[r + rows][c] == id) rows++;
        return (cols, rows);
    }

    /// <summary>The width of some columns together.</summary>
    public int SpanWidth(int c, int cols)
    {
        long w = 0;
        for (int k = 0; k < cols && c + k < Columns; k++) w += Widths[c + k];
        return (int)Math.Min(w, int.MaxValue);
    }
}

sealed class Cell
{
    public int Id;
    public List<Paragraph> Paras = new();
}

/// <summary>Page numbers as a choice of a place and a look. They are a line in the header or footer like any other
/// (with {page} and {pages}); this finds that line and puts it where it is wanted.</summary>
static class PageNumbers
{
    public static readonly string[] Places =
    {
        "None", "At the foot, in the middle", "At the foot, on the right", "At the foot, on the left",
        "At the top, on the right", "At the top, in the middle", "At the top, on the left",
    };

    public static readonly (string Name, string Text)[] Looks =
    {
        ("1", "{page}"), ("Page 1", "Page {page}"), ("1 of 4", "{page} of {pages}"), ("Page 1 of 4", "Page {page} of {pages}"),
    };

    /// <summary>For a place: whether it is in the footer, and which of the three texts (0 left, 1 middle, 2 right).</summary>
    static (bool Footer, int Slot) Where(int place) => place switch
    {
        1 => (true, 1), 2 => (true, 2), 3 => (true, 0), 4 => (false, 2), 5 => (false, 1), _ => (false, 0),
    };

    static string Slot(Band b, int slot) => slot == 0 ? b.Left : slot == 1 ? b.Centre : b.Right;

    static Band With(Band b, int slot, string text) => slot == 0 ? b with { Left = text } : slot == 1 ? b with { Centre = text } : b with { Right = text };

    static int LookOf(string line) => System.Array.FindIndex(Looks, l => l.Text == line.Trim());

    /// <summary>Where the page number stands and how it looks; place 0 if there is none.</summary>
    public static (int Place, int Look) Of(Band header, Band footer)
    {
        for (int place = 1; place < Places.Length; place++)
        {
            var (inFooter, slot) = Where(place);
            foreach (string line in Slot(inFooter ? footer : header, slot).Split('\n'))
                if (LookOf(line) is >= 0 and var look) return (place, look);
        }
        return (0, 0);
    }

    /// <summary>The header and footer with the page number at this place in this look, and nowhere else. Other text of the
    /// header and footer stays; where the place already holds text, the number gets a line of its own below it.</summary>
    public static (Band Header, Band Footer) Put(Band header, Band footer, int place, int look)
    {
        for (int slot = 0; slot < 3; slot++)
        {
            header = With(header, slot, string.Join("\n", Slot(header, slot).Split('\n').Where(l => LookOf(l) < 0)));
            footer = With(footer, slot, string.Join("\n", Slot(footer, slot).Split('\n').Where(l => LookOf(l) < 0)));
        }
        if (place <= 0 || place >= Places.Length) return (header, footer);
        var (inFooter, at) = Where(place);
        string text = Looks[System.Math.Clamp(look, 0, Looks.Length - 1)].Text, had = Slot(inFooter ? footer : header, at);
        string now = had.Trim().Length == 0 ? text : had + "\n" + text;
        return inFooter ? (header, With(footer, at, now)) : (With(header, at, now), footer);
    }
}

sealed class PageSetup
{
    public int Width = 7560000, Height = 10692000, Top = 914400, Bottom = 914400, Left = 914400, Right = 914400;
    public int ContentWidth => Math.Max(0, Width - Left - Right);
}

sealed class Paragraph
{
    public string Style = "Normal";
    public Align? Align;
    public bool PageBreakBefore;
    /// <summary>Part of the list of references, which is written again whenever a citation or a source changes.</summary>
    public bool Refs;
    /// <summary>Part of the table of contents, which is written again from the headings. In such a paragraph the last tab
    /// reaches so far that what follows it ends at the right edge, and is drawn as a row of dots.</summary>
    public bool Toc;
    /// <summary>The texts of the citations in this paragraph, as last worked out.</summary>
    internal string? CiteSig;
    /// <summary>Line spacing (thousandths) and left indent of this paragraph, when they differ from its style.</summary>
    public int? Line, IndentLeft;
    /// <summary>Room above and below, the indent on the right and that of the first line, where the paragraph has its own; null: as its style says.</summary>
    public int? Before, After, IndentRight, IndentFirst;
    /// <summary>The paragraph begins a block: it and what follows up to the next such paragraph belong together, as a task or an item
    /// with its parts does. A block of which less than half would fit at the bottom of a page starts on the next page.</summary>
    public bool Together;
    /// <summary>Lines at the edges of the paragraph, where it has its own (also: none, where its style has some); null: as its style says.</summary>
    public ParaBorders? Borders;
    /// <summary>The places inside words where a line may end with a hyphen, and the text they were found for.
    /// They count only while the text is still that text.</summary>
    public int[]? Hyphens;
    public string? HyphenText;

    public int[] ValidHyphens => Hyphens != null && HyphenText != null && (ReferenceEquals(HyphenText, Text) || HyphenText == Text) ? Hyphens : Array.Empty<int>();
    /// <summary>"bullet" or "number" for a paragraph of a list; null otherwise.</summary>
    public string? List;
    /// <summary>Set for a paragraph that is a table. Such a paragraph has no text of its own.</summary>
    public TableGrid? Table;
    public List<Run> Runs = new();
    /// <summary>Format of the paragraph mark (Text is always empty): used by a line without items.</summary>
    public Run Mark = new("");

    string? text;
    (int Off, Inline Obj)[]? objs;
    internal List<LineBox>? Lines;
    internal Span[]? Spans;
    /// <summary>Measurements for line breaking; the lines across the whole width; the place in a list (-1 bullet, n number).</summary>
    internal ParaData? Data;
    internal List<LineBox>? FullLines;
    internal int ListNum;
    /// <summary>The bullet or number of a list paragraph, as worked out from the paragraphs before it.</summary>
    internal string? ListMark;
    /// <summary>How deep a list paragraph stands: 0 for the outer level, up to 8.</summary>
    public int ListLevel;
    /// <summary>For a paragraph in a table cell: the stretch its lines were broken for.</summary>
    internal int CellX, CellW;
    /// <summary>The footnote numbers the lines were made with, and the number of the mark before the first line.</summary>
    internal int[]? NoteNums;
    internal int MarkNum;

    static readonly (int, Inline)[] NoObjects = Array.Empty<(int, Inline)>();

    /// <summary>The objects of the paragraph with their offsets, in order.</summary>
    public (int Off, Inline Obj)[] Objects
    {
        get
        {
            if (objs != null) return objs;
            List<(int, Inline)>? list = null;
            int pos = 0;
            foreach (var r in Runs)
            {
                if (r.Obj != null) (list ??= new()).Add((pos, r.Obj));
                pos += r.Text.Length;
            }
            return objs = list?.ToArray() ?? NoObjects;
        }
    }

    public Inline? ObjectAt(int off)
    {
        foreach (var (o, obj) in Objects) if (o == off) return obj;
        return null;
    }

    public string Text => text ??= string.Concat(Runs.Select(r => r.Text));
    public int Length => Text.Length;

    /// <summary>Takes over style, alignment, spacing, indent and mark of another paragraph.</summary>
    public void TakeFormat(Paragraph o)
    {
        Style = o.Style;
        Align = o.Align;
        Line = o.Line;
        IndentLeft = o.IndentLeft;
        (Before, After, IndentRight, IndentFirst) = (o.Before, o.After, o.IndentRight, o.IndentFirst);
        List = o.List;
        ListLevel = o.ListLevel;
        Mark = o.Mark;
    }

    public void Touch() { text = null; objs = null; Lines = null; FullLines = null; Data = null; Spans = null; NoteNums = null; }

    public Paragraph Clone() => new()
    {
        Style = Style, Align = Align, PageBreakBefore = PageBreakBefore, Line = Line, IndentLeft = IndentLeft, Before = Before, After = After, IndentRight = IndentRight, IndentFirst = IndentFirst, List = List, ListLevel = ListLevel, ListMark = ListMark, Refs = Refs, Toc = Toc, CiteSig = CiteSig,
        FullLines = FullLines, Data = Data, ListNum = ListNum, Table = Table?.Clone(), CellX = CellX, CellW = CellW,
        Runs = new List<Run>(Runs), Mark = Mark, text = text, objs = objs, Lines = Lines, Spans = Spans,
        NoteNums = NoteNums, MarkNum = MarkNum, Hyphens = Hyphens, HyphenText = HyphenText, Together = Together, Borders = Borders,
    };

    /// <summary>Makes a run boundary at off; returns the index of the run that starts there.</summary>
    int SplitAt(int off)
    {
        int pos = 0;
        for (int i = 0; i < Runs.Count; i++)
        {
            var r = Runs[i];
            if (off <= pos) return i;
            if (off < pos + r.Text.Length)
            {
                Runs[i] = r with { Text = r.Text[..(off - pos)] };
                Runs.Insert(i + 1, r with { Text = r.Text[(off - pos)..] });
                return i + 1;
            }
            pos += r.Text.Length;
        }
        return Runs.Count;
    }

    public void Normalize()
    {
        for (int i = Runs.Count - 1; i >= 0; i--)
            if (Runs[i].Text.Length == 0) Runs.RemoveAt(i);
        for (int i = Runs.Count - 1; i > 0; i--)
            if (Runs[i].SameFormat(Runs[i - 1]))
            {
                Runs[i - 1] = Runs[i - 1] with { Text = Runs[i - 1].Text + Runs[i].Text };
                Runs.RemoveAt(i);
            }
        Touch();
    }

    /// <summary>The format text typed at off would take (Text is empty).</summary>
    public Run FormatAt(int off)
    {
        if (Runs.Count == 0) return Mark;
        int pos = 0;
        foreach (var r in Runs)
        {
            pos += r.Text.Length;
            if (off <= pos) return r.Format;
        }
        return Runs[^1].Format;
    }

    /// <summary>The format of the character at off (Text is empty).</summary>
    public Run FormatOfChar(int off)
    {
        int pos = 0;
        foreach (var r in Runs)
        {
            pos += r.Text.Length;
            if (off < pos) return r.Format;
        }
        return FormatAt(off);
    }

    public void Insert(int off, string s, Run fmt)
    {
        if (s.Length == 0) return;
        int i = SplitAt(off);
        Runs.Insert(i, fmt with { Text = s });
        Normalize();
    }

    public void InsertRuns(int off, IEnumerable<Run> rs)
    {
        int i = SplitAt(off);
        Runs.InsertRange(i, rs);
        Normalize();
    }

    public void Delete(int off, int count)
    {
        if (count <= 0) return;
        int a = SplitAt(off), b = SplitAt(off + count);
        if (a == 0 && b == Runs.Count && b > a) Mark = Runs[a].Format;
        Runs.RemoveRange(a, b - a);
        Normalize();
    }

    public List<Run> Slice(int start, int end)
    {
        var res = new List<Run>();
        int pos = 0;
        foreach (var r in Runs)
        {
            int a = Math.Max(start, pos), b = Math.Min(end, pos + r.Text.Length);
            if (a < b) res.Add(r with { Text = r.Text[(a - pos)..(b - pos)] });
            pos += r.Text.Length;
        }
        return res;
    }

    /// <summary>Puts other characters, one for one, in the place of a stretch of text; objects and formats stay as they are.</summary>
    public void Rewrite(int start, string fresh)
    {
        if (fresh.Length == 0) return;
        int a = SplitAt(start), b = SplitAt(start + fresh.Length), at = 0;
        for (int i = a; i < b; i++)
        {
            int len = Runs[i].Text.Length;
            if (Runs[i].Obj == null && at + len <= fresh.Length) Runs[i] = Runs[i] with { Text = fresh.Substring(at, len) };
            at += len;
        }
        Normalize();
    }

    public void Apply(int start, int end, Func<Run, Run> f)
    {
        if (end <= start) return;
        int a = SplitAt(start), b = SplitAt(end);
        for (int i = a; i < b; i++) Runs[i] = f(Runs[i]) with { Text = Runs[i].Text };
        Normalize();
    }

    /// <summary>Cuts the paragraph at off; returns the second part as a new paragraph.</summary>
    public Paragraph Split(int off)
    {
        int i = SplitAt(off);
        var tail = new Paragraph { Style = Style, Align = Align, Line = Line, IndentLeft = IndentLeft, Before = Before, After = After, IndentRight = IndentRight, IndentFirst = IndentFirst, List = List, Mark = Mark, Runs = Runs.GetRange(i, Runs.Count - i) };
        Runs.RemoveRange(i, Runs.Count - i);
        if (tail.Runs.Count == 0 && Runs.Count > 0) tail.Mark = Runs[^1].Format;
        if (Runs.Count == 0 && tail.Runs.Count > 0) Mark = tail.Runs[0].Format;
        Normalize();
        tail.Normalize();
        return tail;
    }

    public void Append(Paragraph o)
    {
        Runs.AddRange(o.Runs);
        Normalize();
    }
}

sealed class Document
{
    /// <summary>The character that breaks a line without ending the paragraph.</summary>
    public const char LineBreak = (char)0x2028;

    public PageSetup Page = new();
    public List<ParaStyle> ParaStyles = new();
    public List<CharStyle> CharStyles = new();
    public List<Paragraph> Body = new();
    /// <summary>Header and footer of the first page, used in place of those of the other pages when BandsSkipFirst is set.
    /// Both are empty in most documents: then the first page has neither.</summary>
    public Band FirstHeader = Band.Empty, FirstFooter = Band.Empty;
    public Band HeaderOf(int page) => page == 0 && BandsSkipFirst ? FirstHeader : Header;
    public Band FooterOf(int page) => page == 0 && BandsSkipFirst ? FirstFooter : Footer;
    /// <summary>Whether any page has a header (footer = false) or a footer.</summary>
    public bool HasBand(bool footer) => !(footer ? Footer : Header).IsEmpty || BandsSkipFirst && !(footer ? FirstFooter : FirstHeader).IsEmpty;
    /// <summary>Header and footer of every page; with BandsSkipFirst the first page has its own (FirstHeader, FirstFooter).</summary>
    public Band Header = Band.Empty, Footer = Band.Empty;
    public bool BandsSkipFirst;
    /// <summary>The works the document refers to, and how citations of them read: by author and year, or by number.</summary>
    public List<Source> Sources = new();
    public string CiteStyle = Citations.ByName;
    /// <summary>No line of a paragraph stands alone at the bottom or at the top of a page.</summary>
    public bool WidowControl;
    /// <summary>The room between paragraphs gives a little at the ends of pages (FORMAT.md §6, Giving room).</summary>
    public bool AdaptSpacing;
    /// <summary>The language whose rules divide words at the ends of lines; empty: words are not divided.</summary>
    public string Hyphenation = "";
    /// <summary>Footnotes by id. A note is part of the document only while the body refers to it.</summary>
    public Dictionary<int, Note> Notes = new();
    /// <summary>Picture files by name (SHA-256 of the bytes, lower-case hex, plus .png or .jpg).</summary>
    public Dictionary<string, byte[]> Media = new(StringComparer.Ordinal);

    /// <summary>What the cells of the tables hold, by id. A cell is part of the document only while a table refers to it.</summary>
    public Dictionary<int, Cell> Cells = new();

    public Cell NewCell()
    {
        var c = new Cell { Id = Cells.Count == 0 ? 1 : Cells.Keys.Max() + 1 };
        c.Paras.Add(new Paragraph { Style = ParaStyles[0].Id });
        return Cells[c.Id] = c;
    }

    public Cell CloneCell(int id)
    {
        var c = new Cell { Id = Cells.Count == 0 ? 1 : Cells.Keys.Max() + 1 };
        foreach (var p in Cells[id].Paras)
        {
            var copy = p.Clone();
            copy.Touch();
            c.Paras.Add(copy);
        }
        return Cells[c.Id] = c;
    }

    /// <summary>The cells of all tables of the body, in order.</summary>
    public IEnumerable<Cell> CellsInOrder()
    {
        foreach (var p in Body)
            if (p.Table != null)
                foreach (var row in p.Table.Rows)
                    foreach (int id in row)
                        if (Cells.TryGetValue(id, out var c)) yield return c;
    }

    static ParaStyle FootnoteStyle() =>
        new() { Id = "Footnote", Name = "Footnote", Char = new("Calibri", 20, false, false, false, 0), Line = 1000, Next = "Footnote" };

    int NextNoteId => Notes.Count == 0 ? 1 : Notes.Keys.Max() + 1;

    public Note NewNote()
    {
        if (FindPara("Footnote") == null) ParaStyles.Add(FootnoteStyle());
        var n = new Note { Id = NextNoteId };
        n.Paras.Add(new Paragraph { Style = "Footnote" });
        return Notes[n.Id] = n;
    }

    public Note CloneNote(int id)
    {
        var n = new Note { Id = NextNoteId };
        foreach (var p in Notes[id].Paras)
        {
            var c = p.Clone();
            c.Touch();
            n.Paras.Add(c);
        }
        return Notes[n.Id] = n;
    }

    /// <summary>The footnotes the body refers to, in the order of their first reference.</summary>
    public List<Note> NotesInOrder()
    {
        var seen = new HashSet<int>();
        var list = new List<Note>();
        foreach (var p in Body)
            foreach (var (_, o) in p.Objects)
                if (o is NoteRef nr && seen.Add(nr.Id) && Notes.TryGetValue(nr.Id, out var note)) list.Add(note);
        return list;
    }

    public HashSet<int> ReferencedNotes()
    {
        var set = new HashSet<int>();
        foreach (var p in Body)
            foreach (var (_, o) in p.Objects)
                if (o is NoteRef nr) set.Add(nr.Id);
        return set;
    }

    public ParaStyle StyleOf(Paragraph p) => FindPara(p.Style) ?? ParaStyles[0];

    // What a paragraph has of its own goes before what its style says.
    public int BeforeOf(Paragraph p) => p.Before ?? StyleOf(p).Before;
    public int AfterOf(Paragraph p) => p.After ?? StyleOf(p).After;
    public ParaBorders BordersOf(Paragraph p) => p.Borders ?? StyleOf(p).Borders;

    public ParaStyle? FindPara(string id)
    {
        foreach (var s in ParaStyles) if (s.Id == id) return s;
        return null;
    }

    public CharStyle? FindChar(string? id)
    {
        if (id == null) return null;
        foreach (var s in CharStyles) if (s.Id == id) return s;
        return null;
    }

    public CharFormat Resolve(ParaStyle st, Run r)
    {
        var f = st.Char;
        if (FindChar(r.CharStyle) is { } cs) f = cs.Props.ApplyTo(f);
        f = r.Props.ApplyTo(f);
        return f with
        {
            Size = Math.Clamp(f.Size, 2, 3276), Color = f.Color & 0xFFFFFF,
            Highlight = f.Highlight < 0 ? -1 : f.Highlight & 0xFFFFFF, Vert = f.Vert is 1 or 2 ? f.Vert : 0,
        };
    }

    public static Document NewDefault()
    {
        var d = new Document { WidowControl = true, AdaptSpacing = true };
        const int pt = U.PerPoint;
        d.ParaStyles.Add(new ParaStyle { Id = "Normal", Name = "Normal", Char = new("Calibri", 22, false, false, false, 0), After = 8 * pt, Line = 1080, Next = "Normal" });
        d.ParaStyles.Add(new ParaStyle { Id = "NoSpacing", Name = "No spacing", Char = new("Calibri", 22, false, false, false, 0), Line = 1000, Next = "NoSpacing" });
        d.ParaStyles.Add(new ParaStyle { Id = "Title", Name = "Title", Char = new("Calibri Light", 56, false, false, false, 0), After = 4 * pt, Line = 1000, KeepNext = true });
        d.ParaStyles.Add(new ParaStyle { Id = "Heading1", Name = "Heading 1", Char = new("Calibri Light", 32, false, false, false, 0x2F5496), Before = 12 * pt, After = 2 * pt, Line = 1080, KeepNext = true });
        d.ParaStyles.Add(new ParaStyle { Id = "Heading2", Name = "Heading 2", Char = new("Calibri Light", 26, false, false, false, 0x2F5496), Before = 8 * pt, After = 2 * pt, Line = 1080, KeepNext = true });
        d.ParaStyles.Add(new ParaStyle { Id = "Heading3", Name = "Heading 3", Char = new("Calibri Light", 24, false, false, false, 0x1F3763), Before = 6 * pt, After = 2 * pt, Line = 1080, KeepNext = true });
        d.ParaStyles.Add(FootnoteStyle());
        d.ParaStyles.Add(new ParaStyle { Id = "Header", Name = "Header", Char = new("Calibri", 20, false, false, false, 0x595959), Line = 1000, Next = "Header" });
        d.ParaStyles.Add(new ParaStyle { Id = "Footer", Name = "Footer", Char = new("Calibri", 20, false, false, false, 0x595959), Line = 1000, Next = "Footer" });
        d.CharStyles.Add(new CharStyle { Id = "Emphasis", Name = "Emphasis", Props = new(Italic: true) });
        d.CharStyles.Add(new CharStyle { Id = "Strong", Name = "Strong", Props = new(Bold: true) });
        d.CharStyles.Add(new CharStyle { Id = "Code", Name = "Code", Props = new(Font: "Consolas") });
        d.Body.Add(new Paragraph());
        return d;
    }
}

/// <summary>The spacing and the indents of a paragraph: room above and below, line spacing in thousandths, and the three indents.</summary>
readonly record struct ParaLook(int Before, int After, int Line, int Left, int First, int Right, bool Together = false, ParaBorders Borders = default);

/// <summary>Lines drawn at the edges of a paragraph: how thick each one is (0: none), how far those above and below keep
/// from the text (Gap) and those at the sides (SideGap), and their colour. Lengths in EMU.</summary>
readonly record struct ParaBorders(int Top = 0, int Bottom = 0, int Left = 0, int Right = 0, int Gap = 0, int SideGap = 0, int Color = 0)
{
    public const int Thinnest = 3175, Thickest = 76200, MostGap = 393700;
    /// <summary>What a line gets that is made here: three quarters of a point thick, one point from the text above and below, four at the sides.</summary>
    public const int UsualWidth = 9525, UsualGap = 12700, UsualSideGap = 50800;
    public bool None => Top == 0 && Bottom == 0 && Left == 0 && Right == 0;
    /// <summary>The room the line above takes above the first line of text, and the line below under the last.</summary>
    public int RoomAbove => Top > 0 ? Top + Gap : 0;
    public int RoomBelow => Bottom > 0 ? Bottom + Gap : 0;

    /// <summary>Within the limits of the format. With no line at all, nothing else is kept.</summary>
    public ParaBorders Checked()
    {
        static int W(int v) => v <= 0 ? 0 : Math.Clamp(v, Thinnest, Thickest);
        var b = new ParaBorders(W(Top), W(Bottom), W(Left), W(Right), Math.Clamp(Gap, 0, MostGap), Math.Clamp(SideGap, 0, MostGap), Color & 0xFFFFFF);
        return b.None ? default : b;
    }
}
