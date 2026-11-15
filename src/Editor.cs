using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReiksDocument;

/// <summary>Caret, selection, editing operations and undo for one document. No window code here.</summary>
sealed class Editor
{
    sealed class UndoRec
    {
        public int Index;
        public Paragraph[] Old = Array.Empty<Paragraph>(), New = Array.Empty<Paragraph>();
        public Pos CaretBefore, AnchorBefore, CaretAfter, AnchorAfter;
        public bool Typing;
        public int Story;
        /// <summary>The paragraph styles before and after, for a change to the styles; null when they did not change.</summary>
        public ParaStyle[]? StylesBefore, StylesAfter;
    }

    public readonly Document Doc;
    public readonly LayoutEngine Layout;
    public Pos Caret, Anchor;
    /// <summary>Where the caret is: 0 = the body, else the id of a footnote.</summary>
    public int Story;
    public bool CaretAtLineEnd;
    public bool Dirty;
    /// <summary>Format chosen for the next typed text while nothing is selected.</summary>
    public Run? InputFormat;

    /// <summary>The character style of text that is LaTeX not yet made into an equation.</summary>
    public const string LatexStyle = "Latex";

    /// <summary>While on, what is typed is LaTeX: it stands in the LaTeX character style until Enter, or the end of the mode,
    /// makes equations of it.</summary>
    public bool LatexMode { get; private set; }

    /// <summary>Where an equation made from LaTeX stands: 1 in the line (what holds unless something else is chosen), 2 on a
    /// line of its own, 0 as it comes (LaTeX that is all its line holds on a line of its own, other LaTeX in the line).
    /// Chosen in the menu beside the LaTeX button; it holds for every document until it is chosen anew.</summary>
    public static int LatexForm = 1;

    void EnsureLatexStyle()
    {
        if (Doc.FindChar(LatexStyle) == null) Doc.CharStyles.Add(new CharStyle { Id = LatexStyle, Name = "LaTeX", Props = new(Font: "Consolas", Color: 0x2E75B6) });
    }

    /// <summary>The LaTeX button. With a selection: the selected text is taken as LaTeX and made into equations, and the mode
    /// stays as it is. Without: switches the mode; switching it off makes equations of the LaTeX in the paragraph of the caret.</summary>
    public void ToggleLatex()
    {
        if (HasSelection)
        {
            Pos s = SelStart, e = SelEnd;
            EnsureLatexStyle();
            SetCharStyle(LatexStyle);
            for (int k = e.Para; k >= s.Para; k--) FinishLatex(k);
            return;
        }
        LatexMode = !LatexMode;
        if (!LatexMode) FinishLatex(Caret.Para);
        // The window shows the mode on its button: it is told, also when nothing in the text changed.
        SelectionChanged?.Invoke();
    }

    /// <summary>Makes equations of the LaTeX in a paragraph: of every stretch of text in the LaTeX style that is understood.
    /// A stretch that is all the paragraph holds becomes an equation on a line of its own (in the text of the document), any
    /// other one an equation inside the line. What is not understood stays as it is, in its style. One step for undo.
    /// Returns how many equations were made.</summary>
    public int FinishLatex(int para)
    {
        if (para < 0 || para >= Body.Count) return 0;
        var p = Body[para];
        if (p.Table != null || !p.Runs.Any(r => r.Obj == null && r.CharStyle == LatexStyle && r.Text.Length > 0)) return 0;
        var pieces = new List<(int From, int To)>();
        int at = 0;
        foreach (var run in p.Runs)
        {
            int len = run.Text.Length;
            if (run.Obj == null && run.CharStyle == LatexStyle && len > 0)
            {
                if (pieces.Count > 0 && pieces[^1].To == at) pieces[^1] = (pieces[^1].From, at + len);
                else pieces.Add((at, at + len));
            }
            at += len;
        }
        string text = p.Text;
        bool alone = pieces.Count == 1 && text[..pieces[0].From].Trim().Length == 0 && text[pieces[0].To..].Trim().Length == 0;
        bool block = Story == 0 && (LatexForm == 2 || LatexForm == 0 && alone);
        var found = pieces.Select(x => (x.From, x.To, Lines: Latex.ToLines(text[x.From..x.To]))).Where(x => x.Lines != null).ToList();
        if (found.Count == 0) return 0;

        var rec = Begin(para, para);
        var runs = new List<Run>();
        int pos = 0, made = 0, length = 0, caret = Caret.Para == para ? Caret.Off : 0, moved = caret;
        foreach (var (from, to, lines) in found)
        {
            var kept = p.Slice(pos, from);
            runs.AddRange(kept);
            length += from - pos;
            // The equation takes the format the text had, without the look of the LaTeX style.
            var first = p.Slice(from, from + 1);
            var fmt = new Run("", first.Count > 0 ? first[0].Props : default);
            int count = 0;
            for (int k = 0; k < lines!.Count; k++)
            {
                if (k > 0 && !block)
                {
                    runs.Add(fmt with { Text = Document.LineBreak.ToString() });
                    count++;
                }
                runs.Add(Run.Object(Equation.From(lines[k], block), fmt));
                count++;
                made++;
            }
            length += count;
            if (caret >= to) moved += count - (to - from);
            else if (caret > from) moved = length;
            pos = to;
        }
        runs.AddRange(p.Slice(pos, text.Length));
        p.Runs = runs;
        p.Touch();
        p.Normalize();
        Commit(rec, 1, new Pos(para, Math.Clamp(moved, 0, p.Length)));
        return made;
    }
    public event Action? Changed, SelectionChanged;

    readonly Stack<UndoRec> undo = new(), redo = new();
    int? preferX;
    static (string Text, List<Paragraph> Frag, Document Doc)? clip;

    public Editor(Document doc, LayoutEngine layout) { Doc = doc; Layout = layout; }

    List<Paragraph> Body => Layout.Paras(Story);
    public Pos SelStart => Caret < Anchor ? Caret : Anchor;
    public Pos SelEnd => Caret < Anchor ? Anchor : Caret;
    public bool HasSelection => Caret != Anchor;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public Pos DocEnd => new(Body.Count - 1, Body[^1].Length);

    public Pos Clamp(Pos p)
    {
        int para = Math.Clamp(p.Para, 0, Body.Count - 1);
        return new Pos(para, Math.Clamp(p.Off, 0, Body[para].Length));
    }

    // ---- undo ----

    UndoRec Begin(int a, int b) => new()
    {
        Story = Story, Index = a, Old = Body.GetRange(a, b - a + 1).Select(p => p.Clone()).ToArray(),
        CaretBefore = Caret, AnchorBefore = Anchor,
    };

    void Commit(UndoRec r, int newCount, Pos caret, Pos? anchor = null, bool typing = false)
    {
        r.New = Body.GetRange(r.Index, newCount).Select(p => p.Clone()).ToArray();
        Caret = caret;
        Anchor = anchor ?? caret;
        r.CaretAfter = Caret;
        r.AnchorAfter = Anchor;
        r.Typing = typing;
        if (typing && undo.Count > 0 && undo.Peek() is { Typing: true } last && last.Index == r.Index
            && last.New.Length == 1 && r.Old.Length == 1 && last.CaretAfter == r.CaretBefore && r.CaretBefore == r.AnchorBefore)
        {
            last.New = r.New;
            last.CaretAfter = r.CaretAfter;
            last.AnchorAfter = r.AnchorAfter;
        }
        else undo.Push(r);
        redo.Clear();
        After();
    }

    void After()
    {
        Dirty = true;
        InputFormat = null;
        preferX = null;
        CaretAtLineEnd = false;
        Layout.Update();
        if (Story < 0 && Layout.FindCell(-Story) == null)
        {
            // The cell the caret was in is no longer part of a table.
            Story = 0;
            Caret = Anchor = new Pos(0, 0);
        }
        if (Story != 0 && Body[0].Lines == null)
        {
            // The footnote the caret was in is no longer part of the document.
            Story = 0;
            Caret = Anchor = new Pos(0, 0);
        }
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var r = undo.Pop();
        Story = r.Story;
        Body.RemoveRange(r.Index, r.New.Length);
        Body.InsertRange(r.Index, r.Old.Select(p => p.Clone()));
        if (r.StylesBefore != null) PutStyles(r.StylesBefore);
        Caret = r.CaretBefore;
        Anchor = r.AnchorBefore;
        r.Typing = false;
        redo.Push(r);
        MendLinked();
        After();
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        var r = redo.Pop();
        Story = r.Story;
        Body.RemoveRange(r.Index, r.Old.Length);
        Body.InsertRange(r.Index, r.New.Select(p => p.Clone()));
        if (r.StylesAfter != null) PutStyles(r.StylesAfter);
        Caret = r.CaretAfter;
        Anchor = r.AnchorAfter;
        undo.Push(r);
        MendLinked();
        After();
    }

    // ---- changes to the paragraphs (no undo bookkeeping here) ----

    /// <summary>Deletes from s to e. Returns how many paragraphs now stand where the paragraphs s.Para..e.Para stood:
    /// 1, or 2 when the range ended in front of a table, which stays a paragraph of its own.</summary>
    int DeleteRange(Pos s, Pos e)
    {
        var first = Body[s.Para];
        if (s.Para == e.Para) { first.Delete(s.Off, e.Off - s.Off); return 1; }
        var last = Body[e.Para];
        if (first.Table != null)
        {
            // From a table on: the table goes, and what is left of the last paragraph takes its place.
            last.Delete(0, e.Off);
            Body.RemoveRange(s.Para, e.Para - s.Para);
            return 1;
        }
        first.Delete(s.Off, first.Length - s.Off);
        if (last.Table != null)
        {
            Body.RemoveRange(s.Para + 1, e.Para - s.Para - 1);
            return 2;
        }
        last.Delete(0, e.Off);
        if (first.Length == 0 && s.Off == 0) { first.TakeFormat(last); }
        first.Append(last);
        Body.RemoveRange(s.Para + 1, e.Para - s.Para);
        return 1;
    }

    /// <summary>Deletes the selection and makes sure text can go in at s: in front of a table an empty paragraph is made.
    /// Returns how many paragraphs now stand where the paragraphs s.Para..e.Para stood.</summary>
    int Clear(Pos s, Pos e)
    {
        int left = s != e ? DeleteRange(s, e) : 1;
        if (Body[s.Para].Table != null)
        {
            Body.Insert(s.Para, new Paragraph { Style = Doc.ParaStyles[0].Id });
            left++;
        }
        return left;
    }

    void RemoveTable(int para)
    {
        var rec = Begin(para, para);
        Body.RemoveAt(para);
        int count = 0;
        if (Body.Count == 0)
        {
            Body.Add(new Paragraph { Style = Doc.ParaStyles[0].Id });
            count = 1;
        }
        Commit(rec, count, Clamp(new Pos(para, 0)));
    }

    /// <summary>Inserts text; '\n' starts a new paragraph. Returns the position after it.</summary>
    Pos InsertText(Pos at, string text, Run fmt)
    {
        var segs = text.Split('\n');
        var p = Body[at.Para];
        int para = at.Para, off = at.Off;
        p.Insert(off, segs[0], fmt);
        off += segs[0].Length;
        for (int i = 1; i < segs.Length; i++)
        {
            var tail = p.Split(off);
            Body.Insert(++para, tail);
            p = tail;
            p.Insert(0, segs[i], fmt);
            off = segs[i].Length;
        }
        return new Pos(para, off);
    }

    /// <summary>A copy of a fragment that may go in at the caret: in a footnote without objects,
    /// in the body with a new footnote for every reference whose footnote is already in use.</summary>
    List<Paragraph> Prepare(List<Paragraph> frag)
    {
        var used = Story == 0 ? Doc.ReferencedNotes() : null;
        var res = new List<Paragraph>();
        foreach (var p in frag)
        {
            if (p.Table != null)
            {
                // A copied table gets cells of its own. Tables go into the body only.
                if (Story != 0) continue;
                var grid = p.Table.Clone();
                // A joined cell stays one cell, and a shaded one keeps its shading.
                var fresh = new Dictionary<int, int>();
                foreach (var row in grid.Rows)
                    for (int k = 0; k < row.Length; k++)
                    {
                        if (!Doc.Cells.ContainsKey(row[k])) continue;
                        if (!fresh.TryGetValue(row[k], out int copy)) fresh[row[k]] = copy = Doc.CloneCell(row[k]).Id;
                        row[k] = copy;
                    }
                grid.Shades = grid.Shades.Where(sh => fresh.ContainsKey(sh.Key)).ToDictionary(sh => fresh[sh.Key], sh => sh.Value);
                var tp = p.Clone();
                tp.Table = grid;
                tp.Touch();
                res.Add(tp);
                continue;
            }
            var c = p.Clone();
            c.Runs = new List<Run>();
            foreach (var r in p.Runs)
            {
                if (r.Obj == null) c.Runs.Add(r);
                else if (used == null) continue;
                else if (r.Obj is NoteRef nr)
                {
                    if (!Doc.Notes.ContainsKey(nr.Id)) continue;
                    int id = nr.Id;
                    if (!used.Add(id)) used.Add(id = Doc.CloneNote(id).Id);
                    c.Runs.Add(r with { Obj = new NoteRef(id) });
                }
                else c.Runs.Add(r);
            }
            if (used == null) c.PageBreakBefore = false;
            c.Normalize();
            res.Add(c);
        }
        // A table is never the first or the last piece: text pieces are joined to the text around, a table cannot be.
        if (res.Count == 0 || res[0].Table != null) res.Insert(0, new Paragraph { Style = Doc.ParaStyles[0].Id });
        if (res[^1].Table != null) res.Add(new Paragraph { Style = Doc.ParaStyles[0].Id });
        return res;
    }

    Pos InsertFragment(Pos at, List<Paragraph> frag)
    {
        frag = Prepare(frag);
        var p = Body[at.Para];
        if (frag.Count == 1)
        {
            int len = frag[0].Length;
            p.InsertRuns(at.Off, frag[0].Runs);
            return new Pos(at.Para, at.Off + len);
        }
        var tail = p.Split(at.Off);
        if (p.Length == 0) p.TakeFormat(frag[0]);
        p.InsertRuns(p.Length, frag[0].Runs);
        int para = at.Para;
        for (int i = 1; i < frag.Count - 1; i++) Body.Insert(++para, frag[i].Clone());
        var lastFrag = frag[^1];
        int lastLen = lastFrag.Length;
        if (tail.Length == 0) { tail.TakeFormat(lastFrag); }
        tail.InsertRuns(0, lastFrag.Runs);
        Body.Insert(++para, tail);
        return new Pos(para, lastLen);
    }

    static string CleanText(string s)
    {
        var sb = new StringBuilder(s.Length);
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (char c in s)
        {
            if (c == '\v' || c == '\f') sb.Append(Document.LineBreak);
            else if (c == '\n' || c == '\t' || (c >= ' ' && c != '\u007F' && c != (char)0xFFFC)) sb.Append(c);
        }
        return sb.ToString();
    }

    Run TypingFormat()
    {
        if (InputFormat != null) return InputFormat;
        var s = SelStart;
        var p = Body[s.Para];
        var run = HasSelection ? p.FormatOfChar(s.Off) : p.FormatAt(s.Off);
        // What is typed right after a link is not part of the link.
        if (!HasSelection && run.Props.Link != null && (s.Off >= p.Length || p.FormatOfChar(s.Off).Props.Link != run.Props.Link))
            run = run with { Props = run.Props with { Link = null, Color = null, Underline = null, Under = null } };
        return run;
    }

    // ---- editing commands ----

    public void TypeText(string raw, bool paste = false)
    {
        string text = CleanText(raw);
        if (!paste) text = text.Replace("\n", "");
        if (text.Length == 0) return;
        Pos s = SelStart, e = SelEnd;
        bool hadSel = HasSelection;
        var fmt = TypingFormat();
        if (LatexMode)
        {
            EnsureLatexStyle();
            fmt = fmt with { CharStyle = LatexStyle };
        }
        // Outside the mode, what is typed is ordinary text again, unless it is typed into the middle of LaTeX that still stands as text.
        else if (fmt.CharStyle == LatexStyle && !(s.Off < Body[s.Para].Length && Body[s.Para].FormatOfChar(s.Off).CharStyle == LatexStyle))
            fmt = fmt with { CharStyle = null };
        var r = Begin(s.Para, e.Para);
        int left = Clear(s, e);
        var end = InsertText(s, text, fmt);
        // A character the font of the text does not have gets a font that has it; what is typed next keeps the font of the text.
        int covered = 0;
        for (int k = s.Para; k <= end.Para; k++) covered += FontCatalog.Cover(Doc, Body[k]);
        bool typing = !paste && !hadSel && text.Length <= 2;
        if (typing && text[0] != ' ' && s.Off > 0 && Body[s.Para].Text[s.Off - 1] == ' ' && undo.Count > 0) undo.Peek().Typing = false;
        Commit(r, end.Para - s.Para + left, end, null, typing);
        if (covered > 0 && Body[end.Para].FormatAt(end.Off).Props.Font != fmt.Props.Font) InputFormat = fmt;
    }

    public void Enter(bool pageBreak = false)
    {
        // In the LaTeX mode, Enter makes equations of what was typed in this paragraph and leaves the caret right behind
        // them, so that the line can be written on. Only where there was nothing to make does it begin a new paragraph.
        if (LatexMode && !HasSelection && FinishLatex(Caret.Para) > 0) return;
        // Enter in an empty paragraph of a list goes one level out, and at the outer level ends the list.
        if (!HasSelection && Body[Caret.Para].List != null && Body[Caret.Para].Length == 0)
        {
            if (Body[Caret.Para].ListLevel > 0) ChangeListLevel(-1);
            else SetList(null);
            return;
        }
        Pos s = SelStart, e = SelEnd;
        var r = Begin(s.Para, e.Para);
        int left = Clear(s, e);
        var p = Body[s.Para];
        bool atEnd = s.Off == p.Length;
        var tail = p.Split(s.Off);
        var st = Doc.StyleOf(p);
        if (atEnd && st.Next != p.Style && Doc.FindPara(st.Next) != null)
        {
            tail.Style = st.Next;
            tail.Align = null;
            tail.Line = tail.IndentLeft = tail.Before = tail.After = tail.IndentRight = tail.IndentFirst = null;
            tail.List = null;
            tail.Borders = null;
            tail.ListLevel = 0;
            tail.Mark = new Run("");
        }
        tail.PageBreakBefore = pageBreak && Story == 0;
        Body.Insert(s.Para + 1, tail);
        Commit(r, 1 + left, new Pos(s.Para + 1, 0));
    }

    /// <summary>Puts an object (a picture, a footnote reference) in place of the selection. Body only.</summary>
    public bool InsertObject(Inline obj)
    {
        // A table cell takes pictures, equations and charts in its lines; a footnote takes text only.
        if (Story > 0 || Story < 0 && obj is NoteRef or Cite or Picture { Floating: true }) return false;
        Pos s = SelStart, e = SelEnd;
        var fmt = TypingFormat();
        var r = Begin(s.Para, e.Para);
        int left = Clear(s, e);
        Body[s.Para].InsertRuns(s.Off, new[] { Run.Object(obj, fmt) });
        Commit(r, left, new Pos(s.Para, s.Off + 1));
        return true;
    }

    /// <summary>Adds a footnote at the caret and moves the caret into it.</summary>
    public bool InsertFootnote()
    {
        if (Story != 0) return false;
        var note = Doc.NewNote();
        InsertObject(new NoteRef(note.Id));
        MoveTo(new Pos(0, 0), false, story: note.Id);
        return true;
    }

    /// <summary>From a footnote back to the body, behind its reference.</summary>
    public void LeaveNote()
    {
        if (Story == 0) return;
        if (Story < 0)
        {
            // Out of a table cell: to the paragraph behind the table.
            int tablePara = Layout.FindCell(-Story)?.Para ?? 0;
            MoveTo(new Pos(Math.Min(tablePara + 1, Doc.Body.Count - 1), 0), false, story: 0);
            return;
        }
        int id = Story;
        var to = new Pos(0, 0);
        for (int i = 0; i < Doc.Body.Count; i++)
            foreach (var (off, o) in Doc.Body[i].Objects)
                if (o is NoteRef nr && nr.Id == id) to = new Pos(i, off + 1);
        MoveTo(to, false, story: 0);
    }

    /// <summary>The picture, equation or chart, when the selection is exactly one of them.</summary>
    public (Pos Pos, Inline Obj)? SelectedBox()
    {
        Pos s = SelStart, e = SelEnd;
        if (Story > 0 || s.Para != e.Para || e.Off - s.Off != 1) return null;
        return Body[s.Para].ObjectAt(s.Off) is { } o && o is not NoteRef and not Cite ? (s, o) : null;
    }

    /// <summary>Replaces the selected object by a changed one (size, own line, content).</summary>
    public void ChangeObject(Func<Inline, Inline> f)
    {
        if (SelectedBox() is not { } sel) return;
        Pos caret = Caret, anchor = Anchor;
        var r = Begin(sel.Pos.Para, sel.Pos.Para);
        Body[sel.Pos.Para].Apply(sel.Pos.Off, sel.Pos.Off + 1, run => run with { Obj = f(sel.Obj) });
        Commit(r, 1, caret, anchor);
    }

    public void InsertLineBreak() => TypeText(Document.LineBreak.ToString(), true);

    void DeleteSelection()
    {
        Pos s = SelStart, e = SelEnd;
        var r = Begin(s.Para, e.Para);
        int left = DeleteRange(s, e);
        Commit(r, left, s);
    }

    public void Backspace(bool word)
    {
        if (HasSelection) { DeleteSelection(); return; }
        var c = Caret;
        var p = Body[c.Para];
        if (c.Off == 0)
        {
            if (p.Table != null) { RemoveTable(c.Para); return; }
            if (c.Para > 0 && Body[c.Para - 1].Table != null)
            {
                // Behind a table: an empty paragraph goes, otherwise the caret steps onto the table.
                if (p.Length == 0 && c.Para < Body.Count - 1)
                {
                    var rt = Begin(c.Para, c.Para);
                    Body.RemoveAt(c.Para);
                    Commit(rt, 0, new Pos(c.Para - 1, 0));
                }
                else MoveTo(new Pos(c.Para - 1, 0), false);
                return;
            }
            if (p.List != null)
            {
                SetList(null);
                return;
            }
            if (p.PageBreakBefore)
            {
                var r0 = Begin(c.Para, c.Para);
                p.PageBreakBefore = false;
                p.Touch();
                Commit(r0, 1, c);
                return;
            }
            if (c.Para == 0) return;
            var prev = Body[c.Para - 1];
            var r = Begin(c.Para - 1, c.Para);
            int at = prev.Length;
            if (at == 0) { prev.TakeFormat(p); }
            prev.Append(p);
            Body.RemoveAt(c.Para);
            Commit(r, 1, new Pos(c.Para - 1, at));
            return;
        }
        Pos from = word ? PrevWord(c) : PrevChar(c);
        if (from.Para != c.Para) from = new Pos(c.Para, 0);
        var r2 = Begin(c.Para, c.Para);
        p.Delete(from.Off, c.Off - from.Off);
        Commit(r2, 1, from);
    }

    public void Delete(bool word)
    {
        if (HasSelection) { DeleteSelection(); return; }
        var c = Caret;
        var p = Body[c.Para];
        if (c.Off == p.Length)
        {
            if (p.Table != null) { RemoveTable(c.Para); return; }
            if (c.Para < Body.Count - 1 && Body[c.Para + 1].Table != null) { MoveTo(new Pos(c.Para + 1, 0), false); return; }
            if (c.Para == Body.Count - 1) return;
            var next = Body[c.Para + 1];
            var r = Begin(c.Para, c.Para + 1);
            if (p.Length == 0) { p.TakeFormat(next); }
            p.Append(next);
            Body.RemoveAt(c.Para + 1);
            Commit(r, 1, c);
            return;
        }
        Pos to = word ? NextWord(c) : NextChar(c);
        if (to.Para != c.Para) to = new Pos(c.Para, p.Length);
        var r2 = Begin(c.Para, c.Para);
        p.Delete(c.Off, to.Off - c.Off);
        Commit(r2, 1, c);
    }

    /// <summary>The selection as plain text; also kept with its formatting for a paste inside the program.</summary>
    public string? Copy()
    {
        if (!HasSelection) return null;
        Pos s = SelStart, e = SelEnd;
        var frag = new List<Paragraph>();
        var sb = new StringBuilder();
        for (int i = s.Para; i <= e.Para; i++)
        {
            var p = Body[i];
            int a = i == s.Para ? s.Off : 0, b = i == e.Para ? e.Off : p.Length;
            frag.Add(new Paragraph { Style = p.Style, Align = p.Align, Line = p.Line, IndentLeft = p.IndentLeft, Before = p.Before, After = p.After, IndentRight = p.IndentRight, IndentFirst = p.IndentFirst, List = p.List, ListLevel = p.ListLevel, Mark = p.Mark, Runs = p.Slice(a, b) });
            if (i > s.Para) sb.Append("\r\n");
            sb.Append(p.Text, a, b - a);
            if (p.Table != null && i < e.Para)
            {
                // A table inside the selection is copied whole; as plain text its cells are set apart by tabs.
                frag[^1].Table = p.Table.Clone();
                for (int tr = 0; tr < p.Table.Rows.Count; tr++)
                {
                    if (tr > 0) sb.Append("\r\n");
                    sb.Append(string.Join("\t", p.Table.Rows[tr].Distinct().Select(id =>
                        Doc.Cells.TryGetValue(id, out var cell) ? string.Join(" ", cell.Paras.Select(cp => cp.Text)) : "")));
                }
            }
        }
        string text = sb.ToString().Replace(Document.LineBreak.ToString(), "\r\n");
        clip = (text, frag, Doc);
        return text;
    }

    public string? Cut()
    {
        string? t = Copy();
        if (t != null) DeleteSelection();
        return t;
    }

    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (clip is { } c && c.Text == text && ReferenceEquals(c.Doc, Doc))
        {
            Pos s = SelStart, e = SelEnd;
            var r = Begin(s.Para, e.Para);
            int left = Clear(s, e);
            var end = InsertFragment(s, c.Frag);
            Commit(r, end.Para - s.Para + left, end);
        }
        // LaTeX in pasted text becomes equations only while the LaTeX mode is on; otherwise it is pasted as the text it is.
        else if (LatexMode && Story == 0 && WithEquations(text) is { } made)
        {
            Pos s = SelStart, e = SelEnd;
            var r = Begin(s.Para, e.Para);
            int left = Clear(s, e);
            var end = InsertFragment(s, made);
            for (int k = s.Para; k <= end.Para; k++) FontCatalog.Cover(Doc, Body[k]);
            Commit(r, end.Para - s.Para + left, end);
        }
        else TypeText(text, true);
    }

    /// <summary>A pasted text as paragraphs like the one at the caret, with its LaTeX made into equations; null if it holds none.</summary>
    List<Paragraph>? WithEquations(string raw)
    {
        string text = CleanText(raw);
        if (text.IndexOf('\\') < 0 && text.IndexOf('$') < 0) return null;
        var fmt = TypingFormat();
        var like = Body[SelStart.Para];
        var res = new List<Paragraph>();
        int made = 0;
        Paragraph Of(string line)
        {
            var p = like.Clone();
            p.Table = null;
            p.PageBreakBefore = false;
            p.Runs = new List<Run>();
            if (line.Length > 0) p.Runs.Add(fmt with { Text = line });
            p.Touch();
            return p;
        }
        string[] lines = text.Split('\n');
        for (int k = 0; k < lines.Length; k++)
        {
            // Rows between \begin and \end on lines of their own are read as one.
            int last = Latex.LastOfGroup(lines, k);
            if (last > k)
            {
                var all = Of(string.Join(" ", lines[k..(last + 1)]));
                int n = Latex.Apply(all, true);
                if (n > 0)
                {
                    made += n;
                    res.Add(all);
                    k = last;
                    continue;
                }
            }
            var p = Of(lines[k]);
            made += Latex.Apply(p, true);
            res.Add(p);
        }
        // Where the equations stand, if that was chosen.
        if (LatexForm != 0)
            foreach (var p in res)
            {
                for (int i = 0; i < p.Runs.Count; i++)
                    if (p.Runs[i].Obj is Equation eq && eq.Block != (LatexForm == 2)) p.Runs[i] = p.Runs[i] with { Obj = eq with { Block = LatexForm == 2 } };
                p.Touch();
            }
        return made > 0 ? res : null;
    }

    /// <summary>Changes character properties of the selection, or of the next typed text.</summary>
    public void ApplyFormat(Func<Run, Run> f)
    {
        if (!HasSelection)
        {
            var p = Body[Caret.Para];
            InputFormat = f(InputFormat ?? p.FormatAt(Caret.Off)) with { Text = "" };
            if (p.Length == 0)
            {
                var keep = InputFormat;
                var r0 = Begin(Caret.Para, Caret.Para);
                p.Mark = keep;
                p.Touch();
                Commit(r0, 1, Caret);
                InputFormat = keep;
            }
            SelectionChanged?.Invoke();
            return;
        }
        Pos s = SelStart, e = SelEnd, caret = Caret, anchor = Anchor;
        var r = Begin(s.Para, e.Para);
        for (int i = s.Para; i <= e.Para; i++)
        {
            var p = Body[i];
            int a = i == s.Para ? s.Off : 0, b = i == e.Para ? e.Off : p.Length;
            p.Apply(a, b, f);
            if (i < e.Para || p.Length == 0) p.Mark = f(p.Mark) with { Text = "" };
            p.Touch();
        }
        Commit(r, e.Para - s.Para + 1, caret, anchor);
    }

    public void SetProps(Func<CharProps, CharProps> f) => ApplyFormat(r => r with { Props = f(r.Props) });
    /// <summary>Switches the look of program code (the character style Code: a font with letters of equal width) on or off.</summary>
    public void ToggleCode()
    {
        bool on = CurrentRun().CharStyle == "Code";
        if (!on && Doc.FindChar("Code") == null) Doc.CharStyles.Add(new CharStyle { Id = "Code", Name = "Code", Props = new(Font: "Consolas") });
        SetCharStyle(on ? null : "Code");
    }

    public void SetCharStyle(string? id) => ApplyFormat(r => r with { CharStyle = id });

    public void ToggleBold() { bool v = !CurrentFormat().Bold; SetProps(p => p with { Bold = v }); }
    public void ToggleItalic() { bool v = !CurrentFormat().Italic; SetProps(p => p with { Italic = v }); }
    public void ToggleUnderline() { bool v = !CurrentFormat().Underline; SetProps(p => p with { Underline = v }); }

    void ForParagraphs(Action<Paragraph> f)
    {
        Pos s = SelStart, e = SelEnd, caret = Caret, anchor = Anchor;
        var r = Begin(s.Para, e.Para);
        for (int i = s.Para; i <= e.Para; i++) { f(Body[i]); Body[i].Touch(); }
        Commit(r, e.Para - s.Para + 1, caret, anchor);
    }

    /// <summary>What marks the character style that belongs to a paragraph style: its id is that of the paragraph style with this behind it.</summary>
    public const string LinkedEnd = "Char";

    static CharProps LookOf(ParaStyle st) => new(Font: st.Char.Font, Size: st.Char.Size, Bold: st.Char.Bold, Italic: st.Char.Italic, Underline: st.Char.Underline, Color: st.Char.Color);

    /// <summary>The character style that gives words the look of a paragraph style; made if it is not there yet.</summary>
    string LinkedStyle(ParaStyle st)
    {
        string id = st.Id + LinkedEnd;
        if (Doc.FindChar(id) == null) Doc.CharStyles.Add(new CharStyle { Id = id, Name = st.Name + " (words)", Props = LookOf(st) });
        return id;
    }

    /// <summary>After undo or redo: a character style that belongs to a paragraph style is there exactly while some text has
    /// it, so that undoing leaves no style behind and redoing finds it again.</summary>
    void MendLinked()
    {
        bool Linked(string id) => id.EndsWith(LinkedEnd, StringComparison.Ordinal) && Doc.FindPara(id[..^LinkedEnd.Length]) != null;
        var used = new HashSet<string>();
        foreach (var p in Spell.Everywhere(Doc))
            foreach (var run in p.Runs)
                if (run.CharStyle is { } id && Linked(id)) used.Add(id);
        if (used.Count == 0 && !Doc.CharStyles.Any(cs => Linked(cs.Id))) return;
        Doc.CharStyles.RemoveAll(cs => Linked(cs.Id) && !used.Contains(cs.Id));
        foreach (string id in used)
            if (Doc.FindChar(id) == null) LinkedStyle(Doc.FindPara(id[..^LinkedEnd.Length])!);
    }

    /// <summary>After the paragraph styles changed: every character style that belongs to one takes its look again.</summary>
    void FollowStyles()
    {
        foreach (var cs in Doc.CharStyles)
            if (cs.Id.EndsWith(LinkedEnd, StringComparison.Ordinal) && Doc.FindPara(cs.Id[..^LinkedEnd.Length]) is { } st)
            {
                cs.Props = LookOf(st);
                cs.Name = st.Name + " (words)";
            }
    }

    /// <summary>Gives a style. Without a selection, or with one that takes in whole paragraphs or reaches over several, the
    /// paragraphs get the style. With text selected inside one paragraph, and not all of it, only the selected text takes
    /// the look of the style (its font, size, weight, slant, underline and colour), through the character style that belongs
    /// to it, and go on following the style when it is changed; the paragraph keeps its own style.</summary>
    public void SetParaStyle(string id)
    {
        if (Doc.FindPara(id) is not { } style) return;
        Pos s = SelStart, e = SelEnd;
        if (HasSelection && s.Para == e.Para && !(s.Off == 0 && e.Off >= Body[s.Para].Length))
        {
            // The words get the character style that belongs to the paragraph style, so that they follow it when it is changed.
            string words = LinkedStyle(style);
            ApplyFormat(r => r with { CharStyle = words, Props = r.Props with { Font = null, Size = null, Bold = null, Italic = null, Underline = null, Color = null } });
            return;
        }
        ForParagraphs(p => { p.Style = id; p.Align = null; });
    }

    ParaStyle[] StylesNow() => Doc.ParaStyles.Select(s => s.Clone()).ToArray();

    /// <summary>Puts back the paragraph styles kept for undo or redo.</summary>
    void PutStyles(ParaStyle[] styles)
    {
        Doc.ParaStyles.Clear();
        Doc.ParaStyles.AddRange(styles.Select(s => s.Clone()));
        FollowStyles();
        Layout.InvalidateAll();
    }

    /// <summary>The style that "update the style to match" changes: when the words at the caret carry the look of a paragraph
    /// style (see SetParaStyle), that style; otherwise the style of the paragraph. Differs: whether the text at the caret looks
    /// otherwise than the style says (font, size, weight, slant, underline, colour; for the paragraph's own style also its
    /// alignment, spacing, indents and lines).</summary>
    public (ParaStyle Style, bool Words, bool Differs) MatchTarget()
    {
        var run = CurrentRun();
        var p = Body[SelStart.Para];
        ParaStyle? linked = run.CharStyle is { } id && id.EndsWith(LinkedEnd, StringComparison.Ordinal) ? Doc.FindPara(id[..^LinkedEnd.Length]) : null;
        var style = linked ?? Doc.StyleOf(p);
        var f = CurrentFormat();
        var c = style.Char;
        bool differs = f.Font != c.Font || f.Size != c.Size || f.Bold != c.Bold || f.Italic != c.Italic || f.Underline != c.Underline || f.Color != c.Color;
        if (linked == null)
            differs |= p.Align is { } a && a != style.Align || p.Line is { } l && l != style.Line || p.Before is { } b && b != style.Before
                || p.After is { } af && af != style.After || p.IndentLeft is { } il && il != style.IndentLeft || p.IndentRight is { } ir && ir != style.IndentRight
                || p.IndentFirst is { } fi && fi != style.IndentFirst || p.Borders is { } bo && bo != style.Borders;
        return (style, linked != null, differs);
    }

    /// <summary>The run's own font, size, weight, slant, underline and colour, without those that only repeat what its styles give.</summary>
    CharProps Spare(ParaStyle st, Run run)
    {
        var pr = run.Props;
        var bare = Doc.Resolve(st, run with { Props = pr with { Font = null, Size = null, Bold = null, Italic = null, Underline = null, Color = null } });
        return pr with
        {
            Font = pr.Font == bare.Font ? null : pr.Font,
            Size = pr.Size == bare.Size ? null : pr.Size,
            Bold = pr.Bold == bare.Bold ? null : pr.Bold,
            Italic = pr.Italic == bare.Italic ? null : pr.Italic,
            Underline = pr.Underline == bare.Underline ? null : pr.Underline,
            Color = pr.Color == bare.Color ? null : pr.Color,
        };
    }

    /// <summary>Gives the style (see MatchTarget) the look of the text at the caret, or at the start of the selection: its font,
    /// size, weight, slant, underline and colour, and for the paragraph's own style also the paragraph's own alignment, spacing,
    /// indents and lines. All text of the style then takes that look. In the selected paragraphs whatever was set by hand and now
    /// only repeats the style is let go, so that it follows the style from then on. Can be undone.</summary>
    public void MatchStyle()
    {
        var (style, words, differs) = MatchTarget();
        if (!differs) return;
        var f = CurrentFormat();
        var p = Body[SelStart.Para];
        Pos s = SelStart, e = SelEnd, caret = Caret, anchor = Anchor;
        var r = Begin(s.Para, e.Para);
        r.StylesBefore = StylesNow();
        style.Char = style.Char with { Font = f.Font, Size = f.Size, Bold = f.Bold, Italic = f.Italic, Underline = f.Underline, Color = f.Color };
        if (!words)
        {
            style.Align = p.Align ?? style.Align;
            style.Line = p.Line ?? style.Line;
            style.Before = p.Before ?? style.Before;
            style.After = p.After ?? style.After;
            style.IndentLeft = p.IndentLeft ?? style.IndentLeft;
            style.IndentRight = p.IndentRight ?? style.IndentRight;
            style.IndentFirst = p.IndentFirst ?? style.IndentFirst;
            style.Borders = p.Borders ?? style.Borders;
        }
        FollowStyles();
        string linkedId = style.Id + LinkedEnd;
        for (int i = s.Para; i <= e.Para; i++)
        {
            var q = Body[i];
            var st = Doc.StyleOf(q);
            bool mine = !words && q.Style == style.Id;
            Run Let(Run run) => mine || words && run.CharStyle == linkedId ? run with { Props = Spare(st, run) } : run;
            q.Apply(0, q.Length, Let);
            q.Mark = Let(q.Mark);
            if (mine)
            {
                if (q.Align == style.Align) q.Align = null;
                if (q.Line == style.Line) q.Line = null;
                if (q.Before == style.Before) q.Before = null;
                if (q.After == style.After) q.After = null;
                if (q.IndentLeft == style.IndentLeft) q.IndentLeft = null;
                if (q.IndentRight == style.IndentRight) q.IndentRight = null;
                if (q.IndentFirst == style.IndentFirst) q.IndentFirst = null;
                if (q.Borders == style.Borders) q.Borders = null;
            }
            q.Touch();
        }
        r.StylesAfter = StylesNow();
        Layout.InvalidateAll();
        Commit(r, e.Para - s.Para + 1, caret, anchor);
    }

    public void SetAlign(Align a) => ForParagraphs(p => p.Align = a);

    public void ToggleStrike() { bool v = !CurrentFormat().Strike; SetProps(p => p with { Strike = v }); }

    /// <summary>Raised (1) or lowered (2) text; choosing the one that is already set turns it off.</summary>
    public void SetVert(int v)
    {
        int now = CurrentFormat().Vert == v ? 0 : v;
        SetProps(p => p with { Vert = now });
    }

    /// <summary>Types text raised (1) or lowered (2) at the caret; what is typed next goes on in the format from before.</summary>
    public void TypeVert(string text, int vert)
    {
        var before = TypingFormat();
        InputFormat = before with { Props = before.Props with { Vert = vert } };
        TypeText(text);
        InputFormat = before;
        SelectionChanged?.Invoke();
    }

    public void SetLine(int thousandths) => ForParagraphs(p => p.Line = thousandths);

    /// <summary>The spacing and the indents of the paragraph of the caret, as they are; with style, as its style alone says.</summary>
    public ParaLook CurrentLook(bool style = false)
    {
        var p = CurrentParagraph;
        var st = Doc.StyleOf(p);
        return style
            ? new ParaLook(st.Before, st.After, st.Line, st.IndentLeft, st.IndentFirst, st.IndentRight, false, st.Borders)
            : new ParaLook(p.Before ?? st.Before, p.After ?? st.After, p.Line ?? st.Line, p.IndentLeft ?? st.IndentLeft, p.IndentFirst ?? st.IndentFirst, p.IndentRight ?? st.IndentRight, p.Together, p.Borders ?? st.Borders);
    }

    /// <summary>Gives the selected paragraphs a spacing and indents of their own. What is the same as in a paragraph's style
    /// is not kept with the paragraph, so that it goes on following the style.</summary>
    public void SetLook(ParaLook v) => ForParagraphs(p =>
    {
        var st = Doc.StyleOf(p);
        p.Before = v.Before == st.Before ? null : Math.Max(0, v.Before);
        p.After = v.After == st.After ? null : Math.Max(0, v.After);
        p.Line = v.Line == st.Line ? null : Math.Clamp(v.Line, 100, 10000);
        p.IndentLeft = v.Left == st.IndentLeft ? null : Math.Max(p.List != null ? -LayoutEngine.ListMarkAt : 0, v.Left);
        p.IndentFirst = v.First == st.IndentFirst ? null : v.First;
        p.IndentRight = v.Right == st.IndentRight ? null : Math.Max(0, v.Right);
        p.Borders = p.Table != null || v.Borders.Checked() == st.Borders ? null : v.Borders.Checked();
        // A block is begun by one paragraph: of several selected ones, by the first. Tables and paragraphs outside the text begin none.
        p.Together = v.Together && Story == 0 && p.Table == null && ReferenceEquals(p, Body[SelStart.Para]);
    });

    public void ChangeIndent(int delta)
    {
        int max = Math.Max(0, Doc.Page.ContentWidth - 3 * U.PerInch / 2);
        ForParagraphs(p => p.IndentLeft = Math.Clamp((p.IndentLeft ?? Doc.StyleOf(p).IndentLeft) + delta, 0, max));
    }

    /// <summary>Makes the paragraphs of the selection a list of this kind, or plain paragraphs if they all are one already.</summary>
    public void ToggleList(string kind)
    {
        bool all = true;
        for (int i = SelStart.Para; i <= SelEnd.Para; i++) all &= Body[i].List == kind;
        SetList(all ? null : kind);
    }

    public void SetList(string? kind) => ForParagraphs(p =>
    {
        p.List = Story == 0 ? kind : null;
        if (p.List == null) p.ListLevel = 0;
    });

    /// <summary>True when the caret or the selection touches a paragraph of a list.</summary>
    public bool InList
    {
        get
        {
            if (Story != 0) return false;
            for (int i = SelStart.Para; i <= SelEnd.Para; i++)
                if (Body[i].List != null) return true;
            return false;
        }
    }

    /// <summary>Moves the list paragraphs of the selection one level in (+1) or out (-1).</summary>
    public void ChangeListLevel(int delta) => ForParagraphs(p =>
    {
        if (p.List != null) p.ListLevel = Math.Clamp(p.ListLevel + delta, 0, LayoutEngine.MaxListLevel);
    });

    /// <summary>Moves a floating picture: its item goes to another place in the text and it gets new offsets.</summary>
    public void MoveFloat(Pos from, Pos to, int dx, int dy)
    {
        if (Story != 0 || Body[from.Para].ObjectAt(from.Off) is not Picture pic) return;
        to = Clamp(to);
        if (Body[to.Para].Table != null) return;
        int a = Math.Min(from.Para, to.Para), b = Math.Max(from.Para, to.Para);
        var rec = Begin(a, b);
        var run = Body[from.Para].Slice(from.Off, from.Off + 1)[0];
        Body[from.Para].Delete(from.Off, 1);
        int off = to.Para == from.Para && to.Off > from.Off ? to.Off - 1 : to.Off;
        off = Math.Min(off, Body[to.Para].Length);
        Body[to.Para].InsertRuns(off, new[] { run with { Obj = pic with { Dx = dx, Dy = dy } } });
        Commit(rec, b - a + 1, new Pos(to.Para, off + 1), new Pos(to.Para, off));
    }

    /// <summary>Sets header and footer. This is a setting of the document, not a step for undo.</summary>
    // ---- sources and citations ----

    /// <summary>Takes the sources and the look of the citations; every citation and the list of references follow.</summary>
    public void SetSources(List<Source> sources, string style)
    {
        style = Citations.Known(style);
        if (style == Doc.CiteStyle && sources.SequenceEqual(Doc.Sources)) return;
        Doc.Sources = sources;
        Doc.CiteStyle = style;
        Dirty = true;
        Layout.InvalidateAll();
        Layout.Update();
        if (!UpdateRefs(false))
        {
            Changed?.Invoke();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Puts a citation of a source where the caret is. Only in the text of the document itself.</summary>
    public bool InsertCite(string source)
    {
        if (Story != 0 || !InsertObject(new Cite(source))) return false;
        UpdateRefs(false);
        return true;
    }

    /// <summary>Writes the list of references again; with create it is put in below the caret's paragraph if there is none yet.</summary>
    public bool UpdateRefs(bool create)
    {
        if (Story != 0) return false;
        var body = Doc.Body;
        var fresh = Citations.BuildRefs(Doc);
        int a = body.FindIndex(p => p.Refs);
        if (a < 0)
        {
            if (!create) return false;
            int at = SelEnd.Para;
            var r0 = Begin(at, at);
            body.InsertRange(at + 1, fresh);
            Commit(r0, 1 + fresh.Count, new Pos(at + fresh.Count, body[at + fresh.Count].Length));
            return true;
        }
        int b = a;
        while (b < body.Count && body[b].Refs) b++;
        bool same = b - a == fresh.Count;
        for (int i = 0; same && i < fresh.Count; i++) same = body[a + i].Text == fresh[i].Text && body[a + i].Style == fresh[i].Style;
        if (same) return true;
        Pos caret = Caret;
        if (caret.Para >= b) caret = new Pos(caret.Para + fresh.Count - (b - a), caret.Off);
        else if (caret.Para >= a) caret = new Pos(a, 0);
        var r = Begin(a, b - 1);
        body.RemoveRange(a, b - a);
        body.InsertRange(a, fresh);
        Commit(r, fresh.Count, caret);
        return true;
    }

    /// <summary>Writes the table of contents again from the headings; with create it is put in above the caret's paragraph
    /// if there is none yet. The list takes room itself, so the pages are looked up again until they stay; all of it is one step of undo.</summary>
    public bool UpdateToc(bool create)
    {
        if (Story != 0)
        {
            if (create) return false;
            // The caret is in a footnote or a table cell: the list is written from the text, and the caret goes back.
            int story = Story;
            Pos c = Caret, an = Anchor;
            Story = 0;
            Caret = Anchor = new Pos(0, 0);
            bool done = UpdateToc(false);
            Story = story;
            Caret = c;
            Anchor = an;
            SelectionChanged?.Invoke();
            return done;
        }
        var body = Doc.Body;
        int a = body.FindIndex(p => p.Toc);
        if (a < 0 && !create) return false;
        UndoRec? rec = null;
        bool patched = false;
        for (int round = 0; round < 4; round++)
        {
            var fresh = Contents.Build(Doc);
            if (a < 0)
            {
                a = SelStart.Para;
                rec = Begin(a, a);
                body.InsertRange(a, fresh);
                Commit(rec, fresh.Count + 1, new Pos(Caret.Para + fresh.Count, Caret.Off), new Pos(Anchor.Para + fresh.Count, Anchor.Off));
                continue;
            }
            int b = a;
            while (b < body.Count && body[b].Toc) b++;
            if (Contents.Same(body, a, b - a, fresh)) break;
            if (rec != null && b - a == fresh.Count)
            {
                // Only the pages moved, because the list itself took room.
                for (int i = 0; i < fresh.Count; i++)
                {
                    body[a + i] = fresh[i];
                    rec.New[i] = fresh[i].Clone();
                }
                Layout.Update();
                patched = true;
                continue;
            }
            Pos caret = Caret;
            if (caret.Para >= b) caret = new Pos(caret.Para + fresh.Count - (b - a), caret.Off);
            else if (caret.Para >= a) caret = new Pos(a, 0);
            rec = Begin(a, b - 1);
            body.RemoveRange(a, b - a);
            body.InsertRange(a, fresh);
            Commit(rec, fresh.Count, caret);
        }
        if (patched)
        {
            Changed?.Invoke();
            SelectionChanged?.Invoke();
        }
        return true;
    }

    // ---- links ----

    public const int LinkColour = 0x0563C1;

    /// <summary>The link at the caret, or where the selection starts: its address and its stretch in the paragraph; null if there is none.</summary>
    public (string Address, int From, int To)? LinkAround()
    {
        var p = Body[SelStart.Para];
        int at = SelStart.Off, off = 0, from = 0;
        bool sel = HasSelection;
        string? open = null;
        (string, int, int)? found = null;
        void Close(int end)
        {
            if (open != null && found == null && from <= at && (sel ? at < end : at <= end)) found = (open, from, end);
        }
        foreach (var r in p.Runs)
        {
            string? link = r.Props.Link;
            if (link != open)
            {
                Close(off);
                open = link;
                from = off;
            }
            off += r.Text.Length;
        }
        Close(off);
        return found;
    }

    /// <summary>Makes the selected text a link to an address; with null it is plain text again.
    /// Without a selection the whole link the caret is in is meant.</summary>
    public bool SetLink(string? address)
    {
        if (!HasSelection)
        {
            if (LinkAround() is not { } l) return false;
            Select(new Pos(Caret.Para, l.From), new Pos(Caret.Para, l.To));
        }
        if (address != null) SetProps(p => p with { Link = address, Color = LinkColour, Underline = true, Under = null });
        else SetProps(p => p with { Link = null, Color = null, Underline = null, Under = null });
        return true;
    }

    /// <summary>Puts in a text that is a link, where the caret is; the caret ends up behind it.</summary>
    public void InsertLink(string text, string address)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (text.Length == 0 || HasSelection) return;
        var start = Caret;
        TypeText(text, true);
        if (Caret.Para != start.Para || Caret.Off <= start.Off) return;
        var end = Caret;
        Select(start, end);
        SetLink(address);
        MoveTo(end, false);
    }

    // ---- case and underline ----

    /// <summary>Changes the case of the selected text: "lower", "upper", "toggle", "words" (each word with a capital)
    /// or "sentence" (each sentence with a capital). The formats stay as they are.</summary>
    public void ChangeCase(string mode)
    {
        if (!HasSelection) return;
        Pos s = SelStart, e = SelEnd, caret = Caret, anchor = Anchor;
        var r = Begin(s.Para, e.Para);
        for (int i = s.Para; i <= e.Para; i++)
        {
            var p = Body[i];
            int a = i == s.Para ? s.Off : 0, b = i == e.Para ? e.Off : p.Length;
            if (b <= a) continue;
            p.Rewrite(a, Recase(p.Text, a, b, mode));
            p.Touch();
        }
        Commit(r, e.Para - s.Para + 1, caret, anchor);
    }

    static string Recase(string text, int a, int b, string mode)
    {
        var c = text.Substring(a, b - a).ToCharArray();
        bool wordStart = a == 0 || !char.IsLetterOrDigit(text[a - 1]), sentenceStart = true;
        for (int i = 0; i < c.Length; i++)
        {
            char ch = c[i];
            bool letter = char.IsLetter(ch);
            if (letter)
                c[i] = mode switch
                {
                    "lower" => char.ToLowerInvariant(ch),
                    "upper" => char.ToUpperInvariant(ch),
                    "toggle" => char.IsUpper(ch) ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch),
                    "words" => wordStart ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch),
                    _ => sentenceStart ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch),
                };
            if (letter || char.IsDigit(ch)) wordStart = sentenceStart = false;
            else
            {
                if (ch != '\'' && ch != (char)0x2019) wordStart = true;
                if (ch is '.' or '!' or '?') sentenceStart = true;
            }
        }
        return new string(c);
    }

    /// <summary>Underlines with one of the kinds (single, double, thick, dotted, dashed, wave), or with null takes the underline away.</summary>
    public void SetUnder(string? kind) => SetProps(p => p with { Underline = kind != null, Under = kind });

    /// <summary>Gives the document another page: its paper, its direction and its margins. Everything is laid out again.</summary>
    public void SetPage(PageSetup page)
    {
        var d = Doc.Page;
        int before = d.ContentWidth;
        (d.Width, d.Height, d.Top, d.Bottom, d.Left, d.Right) = (page.Width, page.Height, page.Top, page.Bottom, page.Left, page.Right);
        int after = d.ContentWidth;
        // A table that filled the width of the text fills it again; one that would not fit any more is made to fit.
        if (after != before && after > 0)
            foreach (var p in Doc.Body)
            {
                if (p.Table is not { } t || t.Width <= 0 || (t.Width != before && t.Width <= after)) continue;
                var g = t.Clone();
                long total = t.Width, sum = 0, placed = 0;
                for (int c = 0; c < g.Columns; c++)
                {
                    sum += t.Widths[c];
                    long edge = U.RoundDiv(sum * after, total);
                    g.Widths[c] = (int)Math.Max(1, edge - placed);
                    placed += g.Widths[c];
                }
                p.Table = g;
                p.Touch();
            }
        LaidOutAgain();
    }

    /// <summary>Takes the styles as they were changed in the box of styles. A paragraph whose style is gone becomes one of the first style.</summary>
    public void SetStyles(List<ParaStyle> styles)
    {
        if (styles.Count == 0) return;
        var r = new UndoRec { Story = Story, CaretBefore = Caret, AnchorBefore = Anchor, StylesBefore = StylesNow() };
        Doc.ParaStyles.Clear();
        Doc.ParaStyles.AddRange(styles);
        void Mend(List<Paragraph> paras)
        {
            foreach (var p in paras)
                if (Doc.FindPara(p.Style) == null) p.Style = styles[0].Id;
        }
        Mend(Doc.Body);
        foreach (var n in Doc.Notes.Values) Mend(n.Paras);
        foreach (var c in Doc.Cells.Values) Mend(c.Paras);
        FollowStyles();
        r.StylesAfter = StylesNow();
        Layout.InvalidateAll();
        Commit(r, 0, Caret, Anchor);
    }

    /// <summary>How the text runs over the pages: whether single lines are kept from standing alone, the language whose
    /// rules divide words at the ends of lines (empty for none), and whether the room between paragraphs gives at the ends
    /// of pages (unchanged when not given). Everything is laid out again.</summary>
    public void SetTextFlow(bool keepLines, string hyphenation, bool? adapt = null)
    {
        if (!Hyphenator.Knows(hyphenation)) hyphenation = "";
        bool give = adapt ?? Doc.AdaptSpacing;
        if (keepLines == Doc.WidowControl && hyphenation == Doc.Hyphenation && give == Doc.AdaptSpacing) return;
        Doc.WidowControl = keepLines;
        Doc.AdaptSpacing = give;
        Doc.Hyphenation = hyphenation;
        // The places where words are divided are found again, or forgotten.
        foreach (var p in Doc.Body) p.Hyphens = null;
        foreach (var n in Doc.Notes.Values)
            foreach (var p in n.Paras) p.Hyphens = null;
        foreach (var c in Doc.Cells.Values)
            foreach (var p in c.Paras) p.Hyphens = null;
        LaidOutAgain();
    }

    void LaidOutAgain()
    {
        Dirty = true;
        Layout.InvalidateAll();
        Layout.Update();
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void SetBands(Band header, Band footer, bool skipFirst, Band firstHeader, Band firstFooter)
    {
        Doc.Header = header;
        Doc.Footer = footer;
        Doc.FirstHeader = skipFirst ? firstHeader : Band.Empty;
        Doc.FirstFooter = skipFirst ? firstFooter : Band.Empty;
        Doc.BandsSkipFirst = skipFirst;
        Dirty = true;
        Layout.Update();
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    // ---- tables ----

    /// <summary>The table cell the caret is in: the paragraph of its table, its row and its column.</summary>
    public (int Para, int Row, int Col)? CurrentCell => Story < 0 ? Layout.FindCell(-Story) : null;

    void IntoCell(int cellId, bool selectAll)
    {
        MoveTo(new Pos(0, 0), false, story: -cellId);
        if (selectAll && DocEnd != new Pos(0, 0)) Select(new Pos(0, 0), DocEnd);
    }

    /// <summary>Puts a table behind the paragraph of the caret and the caret into its first cell.</summary>
    public bool InsertTable(int rows, int columns)
    {
        if (Story != 0 || rows < 1 || columns < 1) return false;
        int para = SelEnd.Para;
        var grid = new TableGrid { Widths = new int[columns] };
        for (int c = 0; c < columns; c++) grid.Widths[c] = Doc.Page.ContentWidth / columns;
        for (int r = 0; r < rows; r++) grid.Rows.Add(Enumerable.Range(0, columns).Select(_ => Doc.NewCell().Id).ToArray());
        var rec = Begin(para, para);
        Body.Insert(para + 1, new Paragraph { Style = Doc.ParaStyles[0].Id, Table = grid });
        int count = 2;
        if (para + 2 == Body.Count)
        {
            // Behind a table there is always a paragraph to go on writing in.
            Body.Add(new Paragraph { Style = Doc.ParaStyles[0].Id });
            count = 3;
        }
        Commit(rec, count, new Pos(para + 1, 0));
        IntoCell(grid.Rows[0][0], false);
        return true;
    }

    /// <summary>Changes the grid of the table in paragraph para as one step for undo; then the caret goes into a cell.</summary>
    void ChangeTable(int para, Action<TableGrid> change, Func<TableGrid, int> then)
    {
        Story = 0;
        Caret = Anchor = new Pos(para, 0);
        var rec = Begin(para, para);
        var grid = Body[para].Table!.Clone();
        change(grid);
        Body[para].Table = grid;
        Body[para].Touch();
        Commit(rec, 1, new Pos(para, 0));
        IntoCell(then(grid), false);
    }

    /// <summary>A new row below the row of the caret (or above it).</summary>
    public void InsertRow(bool above = false)
    {
        if (CurrentCell is not { } at) return;
        int row = at.Row + (above ? 0 : 1), col = at.Col;
        // A cell joined across the place of the new row goes through it; everywhere else there is a new cell.
        ChangeTable(at.Para, g => g.Rows.Insert(row, Enumerable.Range(0, g.Columns).Select(c =>
            row > 0 && row < g.Rows.Count && g.Rows[row - 1][c] == g.Rows[row][c] ? g.Rows[row][c] : Doc.NewCell().Id).ToArray()), g => g.Rows[row][col]);
    }

    /// <summary>A new column to the right of the column of the caret: the two share the width the one had.</summary>
    public void InsertColumn()
    {
        if (CurrentCell is not { } at) return;
        var now = Doc.Body[at.Para].Table!;
        // Behind a joined cell the column comes behind its last column.
        int col = at.Col + now.Span(at.Row, at.Col).Cols - 1;
        if (now.Columns >= 12 || now.Widths[col] < 720000) return;
        ChangeTable(at.Para, g =>
        {
            var widths = g.Widths.ToList();
            int half = widths[col] / 2;
            widths[col] -= half;
            widths.Insert(col + 1, half);
            g.Widths = widths.ToArray();
            for (int r = 0; r < g.Rows.Count; r++)
            {
                var ids = g.Rows[r].ToList();
                ids.Insert(col + 1, col + 1 < ids.Count && ids[col] == ids[col + 1] ? ids[col] : Doc.NewCell().Id);
                g.Rows[r] = ids.ToArray();
            }
        }, g => g.Rows[at.Row][col + 1]);
    }

    public void DeleteRow()
    {
        if (CurrentCell is not { } at) return;
        if (Doc.Body[at.Para].Table!.Rows.Count == 1) { DeleteTable(); return; }
        ChangeTable(at.Para, g => g.Rows.RemoveAt(at.Row), g => g.Rows[Math.Min(at.Row, g.Rows.Count - 1)][at.Col]);
    }

    /// <summary>Takes the column of the caret away; its width goes to the column beside it.</summary>
    public void DeleteColumn()
    {
        if (CurrentCell is not { } at) return;
        if (Doc.Body[at.Para].Table!.Columns == 1) { DeleteTable(); return; }
        ChangeTable(at.Para, g =>
        {
            var widths = g.Widths.ToList();
            widths[at.Col > 0 ? at.Col - 1 : 1] += widths[at.Col];
            widths.RemoveAt(at.Col);
            g.Widths = widths.ToArray();
            for (int r = 0; r < g.Rows.Count; r++)
            {
                var ids = g.Rows[r].ToList();
                ids.RemoveAt(at.Col);
                g.Rows[r] = ids.ToArray();
            }
        }, g => g.Rows[at.Row][Math.Min(at.Col, g.Columns - 1)]);
    }

    public void DeleteTable()
    {
        if (CurrentCell is not { } at) return;
        Story = 0;
        Caret = Anchor = new Pos(at.Para, 0);
        RemoveTable(at.Para);
    }

    /// <summary>Moves the right edge of a column by delta: the column grows and the one beside it shrinks.
    /// The last column grows or shrinks alone, as far as the text area allows.</summary>
    public void MoveColumnEdge(int para, int col, int delta)
    {
        if (para < 0 || para >= Doc.Body.Count || Doc.Body[para].Table is not { } now || col < 0 || col >= now.Columns) return;
        const int least = 360000;
        int back = Story;
        Pos caret = Caret, anchor = Anchor;
        bool last = col == now.Columns - 1;
        int d = last
            ? Math.Clamp(delta, least - now.Widths[col], Math.Max(least - now.Widths[col], Doc.Page.ContentWidth - now.Width))
            : Math.Clamp(delta, least - now.Widths[col], Math.Max(least - now.Widths[col], now.Widths[col + 1] - least));
        if (d == 0) return;
        int stay = back < 0 ? -back : now.Rows[0][0];
        ChangeTable(para, g =>
        {
            g.Widths[col] += d;
            if (!last) g.Widths[col + 1] -= d;
        }, _ => stay);
        if (back < 0)
        {
            Caret = Clamp(caret);
            Anchor = Clamp(anchor);
        }
        else
        {
            Story = 0;
            Caret = Anchor = Clamp(caret);
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>To the next cell (or the one before), with what it holds selected. Behind the last cell a row is added.</summary>
    public void NextCell(int dir)
    {
        if (CurrentCell is not { } at) return;
        var grid = Doc.Body[at.Para].Table!;
        int index = at.Row * grid.Columns + at.Col, here = grid.Rows[at.Row][at.Col];
        while (true)
        {
            index += dir;
            if (index < 0) return;
            if (index >= grid.Rows.Count * grid.Columns)
            {
                IntoCell(grid.Rows[^1][0], false);
                InsertRow();
                return;
            }
            // Over the places a joined cell only reaches.
            int r = index / grid.Columns, c = index % grid.Columns;
            if (grid.Rows[r][c] == here || !grid.Starts(r, c)) continue;
            IntoCell(grid.Rows[r][c], true);
            return;
        }
    }

    // ---- joined cells, shading, lines ----

    public TableGrid? CurrentTable => CurrentCell is { } at ? Doc.Body[at.Para].Table : null;

    /// <summary>The cell of the caret: its table, where it starts, and how many columns and rows it fills.</summary>
    (TableGrid Grid, int Para, int Row, int Col, int Cols, int Rows)? CellBlock()
    {
        if (CurrentCell is not { } at) return null;
        var g = Doc.Body[at.Para].Table!;
        var (cols, rows) = g.Span(at.Row, at.Col);
        return (g, at.Para, at.Row, at.Col, cols, rows);
    }

    public bool CellJoined => CellBlock() is { } b && b.Cols * b.Rows > 1;

    /// <summary>True if the cell of the caret can be joined with the one to its right (or below it):
    /// there is one, and it is as high (or as wide), so that the two make a block.</summary>
    public bool CanJoin(bool down)
    {
        if (CellBlock() is not { } b) return false;
        var g = b.Grid;
        if (down)
        {
            int r = b.Row + b.Rows;
            return r < g.Rows.Count && g.Starts(r, b.Col) && g.Span(r, b.Col).Cols == b.Cols;
        }
        int c = b.Col + b.Cols;
        return c < g.Columns && g.Starts(b.Row, c) && g.Span(b.Row, c).Rows == b.Rows;
    }

    /// <summary>Joins the cell of the caret with the one to its right, or below it. The joined cell holds what both held.</summary>
    public void JoinCell(bool down)
    {
        if (!CanJoin(down) || CellBlock() is not { } b) return;
        int mine = b.Grid.Rows[b.Row][b.Col];
        int other = down ? b.Grid.Rows[b.Row + b.Rows][b.Col] : b.Grid.Rows[b.Row][b.Col + b.Cols];
        // The joined cell is a new one; the two stay as they were, so that undo brings them back.
        var cell = Doc.NewCell();
        cell.Paras.Clear();
        foreach (int id in new[] { mine, other })
            if (Doc.Cells.TryGetValue(id, out var from) && from.Paras.Any(p => p.Length > 0))
                foreach (var p in from.Paras)
                {
                    var copy = p.Clone();
                    copy.Touch();
                    cell.Paras.Add(copy);
                }
        if (cell.Paras.Count == 0) cell.Paras.Add(new Paragraph { Style = Doc.ParaStyles[0].Id });
        ChangeTable(b.Para, g =>
        {
            foreach (var row in g.Rows)
                for (int k = 0; k < row.Length; k++)
                    if (row[k] == mine || row[k] == other) row[k] = cell.Id;
            if (g.Shades.Remove(mine, out int shade)) g.Shades[cell.Id] = shade;
            g.Shades.Remove(other);
        }, _ => cell.Id);
    }

    /// <summary>Makes a joined cell single cells again: the first keeps what it holds, the others are empty.</summary>
    public void SplitCell()
    {
        if (CellBlock() is not { } b || b.Cols * b.Rows == 1) return;
        int mine = b.Grid.Rows[b.Row][b.Col];
        ChangeTable(b.Para, g =>
        {
            bool shaded = g.Shades.TryGetValue(mine, out int shade);
            for (int r = b.Row; r < b.Row + b.Rows; r++)
                for (int c = b.Col; c < b.Col + b.Cols; c++)
                {
                    if (r == b.Row && c == b.Col) continue;
                    g.Rows[r][c] = Doc.NewCell().Id;
                    if (shaded) g.Shades[g.Rows[r][c]] = shade;
                }
        }, _ => mine);
    }

    /// <summary>Changes the look of the table of the caret; the caret stays where it is.</summary>
    void StyleTable(int para, Action<TableGrid> change)
    {
        int back = Story;
        Pos caret = Caret, anchor = Anchor;
        ChangeTable(para, change, _ => -back);
        Story = back;
        Caret = Clamp(caret);
        Anchor = Clamp(anchor);
        SelectionChanged?.Invoke();
    }

    /// <summary>Shades the cell of the caret, its row, its column or the whole table ("cell", "row", "column", "table"); null takes the shading away.</summary>
    public void ShadeCells(string scope, int? rgb)
    {
        if (CellBlock() is not { } b) return;
        int mine = b.Grid.Rows[b.Row][b.Col];
        StyleTable(b.Para, g =>
        {
            for (int r = 0; r < g.Rows.Count; r++)
                for (int c = 0; c < g.Columns; c++)
                {
                    bool hit = scope switch
                    {
                        "table" => true,
                        "row" => r >= b.Row && r < b.Row + b.Rows,
                        "column" => c >= b.Col && c < b.Col + b.Cols,
                        _ => g.Rows[r][c] == mine,
                    };
                    if (!hit) continue;
                    if (rgb is { } v) g.Shades[g.Rows[r][c]] = v & 0xFFFFFF;
                    else g.Shades.Remove(g.Rows[r][c]);
                }
        });
    }

    /// <summary>Which lines the table of the caret has ("all", "horizontal", "outer", "none"), and how thick they are; null leaves one as it is.</summary>
    public void SetTableLines(string? kind, int? width)
    {
        if (CellBlock() is not { } b) return;
        StyleTable(b.Para, g =>
        {
            if (kind is "all" or "horizontal" or "outer" or "none") g.Lines = kind;
            if (width is { } w) g.LineWidth = Math.Clamp(w, TableGrid.LeastLine, TableGrid.MostLine);
        });
    }

    public int CurrentLine => CurrentParagraph.Line ?? Doc.StyleOf(CurrentParagraph).Line;

    // ---- copying and erasing formats ----

    /// <summary>The copied character format, and whether the next selection made with the mouse takes it.</summary>
    public Run? Brush;
    public bool BrushArmed;

    public void CopyFormat(bool arm)
    {
        Brush = CurrentRun().Format;
        BrushArmed = arm;
        SelectionChanged?.Invoke();
    }

    public void DropBrush()
    {
        BrushArmed = false;
        SelectionChanged?.Invoke();
    }

    public void PasteFormat()
    {
        BrushArmed = false;
        if (Brush is { } b) ApplyFormat(r => r with { Props = b.Props, CharStyle = b.CharStyle });
        else SelectionChanged?.Invoke();
    }

    /// <summary>With a selection: its text goes back to the look of its paragraph style.
    /// Without: the whole paragraph does, and it loses its own alignment, line spacing and indent.</summary>
    public void ClearFormat()
    {
        if (HasSelection)
        {
            ApplyFormat(r => r with { Props = default, CharStyle = null });
            return;
        }
        var caret = Caret;
        var rec = Begin(caret.Para, caret.Para);
        var p = Body[caret.Para];
        p.Apply(0, p.Length, r => r with { Props = default, CharStyle = null });
        p.Mark = new Run("");
        p.Align = null;
        p.Line = p.IndentLeft = p.Before = p.After = p.IndentRight = p.IndentFirst = null;
        p.List = null;
        p.ListLevel = 0;
        p.Touch();
        Commit(rec, 1, caret);
    }

    // ---- finding and replacing ----

    /// <summary>Selects the next place where the text stands, from the end of the selection on, starting over at the top.</summary>
    public bool Find(string what, bool matchCase)
    {
        if (what.Length == 0) return false;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var from = SelEnd;
        int n = Body.Count;
        for (int k = 0; k <= n; k++)
        {
            int para = (from.Para + k) % n;
            string t = Body[para].Text;
            int start = k == 0 ? from.Off : 0;
            int at = start <= t.Length ? t.IndexOf(what, start, cmp) : -1;
            if (at < 0) continue;
            Select(new Pos(para, at), new Pos(para, at + what.Length));
            return true;
        }
        return false;
    }

    static string OneLine(string s) => CleanText(s).Replace('\n', ' ');

    /// <summary>Replaces the selection if it is the text looked for; then goes to the next place.</summary>
    public bool Replace(string what, string with, bool matchCase)
    {
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Pos s = SelStart, e = SelEnd;
        bool hit = what.Length > 0 && s.Para == e.Para && string.Equals(Body[s.Para].Text[s.Off..e.Off], what, cmp);
        if (hit)
        {
            string text = OneLine(with);
            if (text.Length == 0) DeleteSelection();
            else TypeText(text, true);
        }
        Find(what, matchCase);
        return hit;
    }

    /// <summary>Replaces every place in the body, or in the footnote the caret is in. One step for undo.</summary>
    public int ReplaceAll(string what, string with, bool matchCase)
    {
        if (what.Length == 0) return 0;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = OneLine(with);
        int count = 0;
        var rec = Begin(0, Body.Count - 1);
        foreach (var p in Body)
        {
            string t = p.Text;
            var hits = new List<int>();
            for (int i = 0; (i = t.IndexOf(what, i, cmp)) >= 0; i += what.Length) hits.Add(i);
            for (int h = hits.Count - 1; h >= 0; h--)
            {
                var fmt = p.FormatOfChar(hits[h]);
                p.Delete(hits[h], what.Length);
                p.Insert(hits[h], text, fmt);
            }
            count += hits.Count;
        }
        if (count > 0) Commit(rec, Body.Count, Clamp(SelStart));
        return count;
    }

    /// <summary>Words of the body: stretches without spaces that hold at least one letter or digit.</summary>
    public int WordCount()
    {
        int n = 0;
        foreach (var p in Doc.Body.Concat(Doc.CellsInOrder().SelectMany(c => c.Paras)))
        {
            bool word = false;
            foreach (char c in p.Text)
            {
                if (char.IsWhiteSpace(c) || c == Document.LineBreak || c == Run.ObjectChar)
                {
                    if (word) n++;
                    word = false;
                }
                else word |= char.IsLetterOrDigit(c);
            }
            if (word) n++;
        }
        return n;
    }

    // ---- what the toolbar shows ----

    public Run CurrentRun()
    {
        if (InputFormat != null && !HasSelection) return InputFormat;
        var s = SelStart;
        var p = Body[s.Para];
        return HasSelection ? p.FormatOfChar(s.Off) : p.FormatAt(s.Off);
    }

    public CharFormat CurrentFormat() => Doc.Resolve(Doc.StyleOf(Body[SelStart.Para]), CurrentRun());
    public Paragraph CurrentParagraph => Body[SelStart.Para];
    public Align CurrentAlign => CurrentParagraph.Align ?? Doc.StyleOf(CurrentParagraph).Align;

    // ---- moving ----

    public void MoveTo(Pos p, bool extend, bool atLineEnd = false, bool keepX = false, int? story = null)
    {
        if (story is { } st && st != Story)
        {
            Story = st;
            extend = false;
        }
        Caret = Clamp(p);
        if (!extend) Anchor = Caret;
        CaretAtLineEnd = atLineEnd;
        if (!keepX) preferX = null;
        InputFormat = null;
        SelectionChanged?.Invoke();
    }

    public void Select(Pos anchor, Pos caret)
    {
        Anchor = Clamp(anchor);
        MoveTo(caret, true);
    }

    public Pos PrevChar(Pos p)
    {
        if (p.Off == 0) return p.Para == 0 ? p : new Pos(p.Para - 1, Body[p.Para - 1].Length);
        string t = Body[p.Para].Text;
        int o = p.Off - 1;
        if (o > 0 && char.IsLowSurrogate(t[o]) && char.IsHighSurrogate(t[o - 1])) o--;
        return new Pos(p.Para, o);
    }

    public Pos NextChar(Pos p)
    {
        string t = Body[p.Para].Text;
        if (p.Off >= t.Length) return p.Para == Body.Count - 1 ? p : new Pos(p.Para + 1, 0);
        int o = p.Off + 1;
        if (o < t.Length && char.IsLowSurrogate(t[o]) && char.IsHighSurrogate(t[o - 1])) o++;
        return new Pos(p.Para, o);
    }

    static int Class(char c) =>
        char.IsWhiteSpace(c) || c == Document.LineBreak ? 0 : char.IsLetterOrDigit(c) || c == '_' || c == '\'' || char.IsSurrogate(c) ? 1 : 2;

    public Pos PrevWord(Pos p)
    {
        if (p.Off == 0) return PrevChar(p);
        string t = Body[p.Para].Text;
        int o = p.Off;
        while (o > 0 && Class(t[o - 1]) == 0) o--;
        if (o > 0)
        {
            int c = Class(t[o - 1]);
            while (o > 0 && Class(t[o - 1]) == c) o--;
        }
        return new Pos(p.Para, o);
    }

    public Pos NextWord(Pos p)
    {
        string t = Body[p.Para].Text;
        if (p.Off >= t.Length) return NextChar(p);
        int o = p.Off;
        int c = Class(t[o]);
        if (c != 0) while (o < t.Length && Class(t[o]) == c) o++;
        while (o < t.Length && Class(t[o]) == 0) o++;
        return new Pos(p.Para, o);
    }

    public void MoveChar(int dir, bool extend)
    {
        if (HasSelection && !extend) { MoveTo(dir < 0 ? SelStart : SelEnd, false); return; }
        MoveTo(dir < 0 ? PrevChar(Caret) : NextChar(Caret), extend);
    }

    public void MoveWord(int dir, bool extend) => MoveTo(dir < 0 ? PrevWord(Caret) : NextWord(Caret), extend);

    public void MoveLine(int dir, bool extend)
    {
        var line = Layout.LineAt(Story, Caret, CaretAtLineEnd, out _);
        int x = preferX ?? Layout.CaretX(Story, Caret, CaretAtLineEnd);
        List<(int Para, LineBox Line)> lines;
        if (Story != 0)
        {
            lines = new List<(int, LineBox)>();
            for (int i = 0; i < Body.Count; i++)
                foreach (var l in Body[i].Lines!) lines.Add((i, l));
        }
        else lines = Layout.Pages[line.Page].Lines;
        int idx = lines.FindIndex(l => ReferenceEquals(l.Line, line)) + dir;
        if (Story == 0 && idx < 0 && line.Page > 0)
        {
            lines = Layout.Pages[line.Page - 1].Lines;
            idx = lines.Count - 1;
        }
        else if (Story == 0 && idx >= lines.Count && line.Page < Layout.Pages.Count - 1)
        {
            lines = Layout.Pages[line.Page + 1].Lines;
            idx = 0;
        }
        if (idx < 0 || idx >= lines.Count) { MoveTo(dir < 0 ? new Pos(0, 0) : DocEnd, extend); return; }
        var target = lines[idx];
        int off = Layout.OffsetAtX(Body[target.Para], target.Line, x, out bool atEnd);
        MoveTo(new Pos(target.Para, off), extend, atEnd, true);
        preferX = x;
    }

    public void MoveHome(bool extend, bool document)
    {
        if (document) { MoveTo(new Pos(0, 0), extend); return; }
        var line = Layout.LineAt(Story, Caret, CaretAtLineEnd, out _);
        MoveTo(new Pos(Caret.Para, line.Start), extend);
    }

    public void MoveEnd(bool extend, bool document)
    {
        if (document) { MoveTo(DocEnd, extend); return; }
        var p = Body[Caret.Para];
        var line = Layout.LineAt(Story, Caret, CaretAtLineEnd, out _);
        int off = line.End;
        bool soft = off < p.Length;
        if (line.Length > 0 && p.Text[off - 1] == Document.LineBreak) { off--; soft = false; }
        MoveTo(new Pos(Caret.Para, off), extend, soft);
    }

    public void SelectAll() => Select(new Pos(0, 0), DocEnd);

    public void SelectWord(Pos p)
    {
        string t = Body[p.Para].Text;
        if (t.Length == 0) { MoveTo(p, false); return; }
        int o = Math.Min(p.Off, t.Length - 1);
        int c = Class(t[o]);
        int a = o, b = o + 1;
        while (a > 0 && Class(t[a - 1]) == c) a--;
        while (b < t.Length && Class(t[b]) == c) b++;
        Select(new Pos(p.Para, a), new Pos(p.Para, b));
    }

    public void SelectParagraph(Pos p) => Select(new Pos(p.Para, 0), new Pos(p.Para, Body[p.Para].Length));
}
