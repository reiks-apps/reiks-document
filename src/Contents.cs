using System.Collections.Generic;
using System.Globalization;

namespace ReiksDocument;

/// <summary>The table of contents: a heading, one paragraph for each heading of the document with the page it is on, and an empty line.
/// They are ordinary paragraphs marked as belonging to it, so that they can be written again.</summary>
static class Contents
{
    public const string Title = "Contents";
    /// <summary>How far each level below the first stands in from the one above.</summary>
    public const int Step = 228600;

    /// <summary>1 to 3 for a paragraph in one of the heading styles, else 0.</summary>
    public static int Level(Paragraph p) => p.Table != null || p.Toc ? 0 : p.Style switch { "Heading1" => 1, "Heading2" => 2, "Heading3" => 3, _ => 0 };

    public static string TextOf(Paragraph p) =>
        p.Text.Replace(Run.ObjectChar.ToString(), "").Replace(Document.LineBreak, ' ').Replace('\t', ' ').Trim();

    /// <summary>True if the document has a heading to list.</summary>
    public static bool Any(Document doc) => doc.Body.Exists(p => Level(p) > 0 && TextOf(p).Length > 0);

    /// <summary>The paragraphs of the table of contents, with the pages of the layout as it is now.</summary>
    public static List<Paragraph> Build(Document doc)
    {
        string plain = (doc.FindPara("NoSpacing") ?? doc.ParaStyles[0]).Id;
        var list = new List<Paragraph>
        {
            new() { Style = doc.FindPara("Heading1") != null ? "Heading1" : doc.ParaStyles[0].Id, Toc = true, Runs = { new Run(Title) } },
        };
        foreach (var p in doc.Body)
        {
            int level = Level(p);
            if (level == 0) continue;
            string text = TextOf(p);
            if (text.Length == 0) continue;
            int page = p.Lines is { Count: > 0 } lines ? lines[0].Page + 1 : 1;
            var entry = new Paragraph { Style = plain, Toc = true, Align = Align.Left, IndentLeft = (level - 1) * Step };
            entry.Runs.Add(new Run(text + "\t" + page.ToString(CultureInfo.InvariantCulture)));
            list.Add(entry);
        }
        // An empty line keeps the list apart from what follows.
        list.Add(new Paragraph { Style = plain, Toc = true });
        return list;
    }

    /// <summary>The heading each entry of the list stands for: the entries and the headings in their order.
    /// Empty when the list no longer fits the headings.</summary>
    public static Dictionary<Paragraph, Paragraph> Targets(Document doc)
    {
        var entries = doc.Body.FindAll(p => p.Toc && p.Table == null && p.Text.Contains('\t'));
        var heads = doc.Body.FindAll(p => Level(p) > 0 && TextOf(p).Length > 0);
        var map = new Dictionary<Paragraph, Paragraph>();
        if (entries.Count == heads.Count)
            for (int i = 0; i < heads.Count; i++) map[entries[i]] = heads[i];
        return map;
    }

    /// <summary>True if the paragraphs from a on are this list already.</summary>
    public static bool Same(List<Paragraph> body, int a, int count, List<Paragraph> fresh)
    {
        if (count != fresh.Count) return false;
        for (int i = 0; i < count; i++)
            if (body[a + i].Text != fresh[i].Text || body[a + i].Style != fresh[i].Style || (i > 0 && body[a + i].IndentLeft != fresh[i].IndentLeft)) return false;
        return true;
    }

    /// <summary>Puts a table of contents into a document that has no window, before the paragraph at.
    /// The list takes room itself, so the pages are looked up again until they stay.</summary>
    public static void Put(Document doc, int at)
    {
        var eng = new LayoutEngine(doc);
        eng.Update();
        var now = Build(doc);
        doc.Body.InsertRange(at, now);
        for (int round = 0; round < 4; round++)
        {
            eng.InvalidateAll();
            eng.Update();
            var fresh = Build(doc);
            if (Same(doc.Body, at, now.Count, fresh)) break;
            doc.Body.RemoveRange(at, now.Count);
            doc.Body.InsertRange(at, fresh);
            now = fresh;
        }
        eng.InvalidateAll();
    }
}
