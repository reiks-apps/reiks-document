using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReiksDocument;

/// <summary>Writes the laid-out pages as a PDF. Every piece stands where the layout put it. Text is real text
/// (it can be selected and searched) in fonts with TrueType outlines whose licence allows embedding; of those fonts only
/// the glyphs used are put into the file. Text in any other font is written as outlines.</summary>
static class PdfExport
{
    sealed class FontUse
    {
        public int Index, ObjId;
        public ResolvedFont Font = null!;
        public bool Embedded;
        /// <summary>Glyph number to the character it stands for.</summary>
        public SortedDictionary<int, int> Used = new();
    }

    static readonly Encoding Latin = Encoding.Latin1;

    static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    static string N4(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
    static string Colour(int rgb) => N(((rgb >> 16) & 255) / 255.0) + " " + N(((rgb >> 8) & 255) / 255.0) + " " + N((rgb & 255) / 255.0);

    static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>The numbered objects of the file.</summary>
    sealed class Objects
    {
        readonly List<byte[]?> bodies = new() { null };

        public int Reserve()
        {
            bodies.Add(null);
            return bodies.Count - 1;
        }

        public void Set(int id, string body) => bodies[id] = Latin.GetBytes(body);

        public int Add(string body)
        {
            int id = Reserve();
            Set(id, body);
            return id;
        }

        public int AddStream(string dict, byte[] data, bool deflate)
        {
            if (deflate) data = Deflate(data);
            using var ms = new MemoryStream();
            ms.Write(Latin.GetBytes("<< " + dict + (deflate ? " /Filter /FlateDecode" : "") + " /Length " + data.Length + " >>\nstream\n"));
            ms.Write(data);
            ms.Write(Latin.GetBytes("\nendstream"));
            int id = Reserve();
            bodies[id] = ms.ToArray();
            return id;
        }

        public void Save(Stream s, int root, int info)
        {
            var offsets = new long[bodies.Count];
            s.Write(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });
            for (int id = 1; id < bodies.Count; id++)
            {
                offsets[id] = s.Position;
                s.Write(Latin.GetBytes(id + " 0 obj\n"));
                s.Write(bodies[id] ?? Latin.GetBytes("null"));
                s.Write(Latin.GetBytes("\nendobj\n"));
            }
            long xref = s.Position;
            var sb = new StringBuilder("xref\n0 " + bodies.Count + "\n0000000000 65535 f \n");
            for (int id = 1; id < bodies.Count; id++) sb.Append(offsets[id].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size " + bodies.Count + " /Root " + root + " 0 R /Info " + info + " 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            s.Write(Latin.GetBytes(sb.ToString()));
        }
    }

    sealed class Job
    {
        public readonly Objects Objs = new();
        public readonly Dictionary<ResolvedFont, FontUse> Fonts = new();
        public readonly Dictionary<string, int> Images = new(StringComparer.Ordinal);

        public FontUse FontOf(ResolvedFont font)
        {
            if (Fonts.TryGetValue(font, out var f)) return f;
            f = new FontUse { Font = font, Index = Fonts.Count + 1, Embedded = font.Face.HasOutlines && font.Face.MayEmbed };
            if (f.Embedded) f.ObjId = Objs.Reserve();
            return Fonts[font] = f;
        }

        /// <summary>The picture as an object of the file; 0 if it is missing or cannot be read.</summary>
        public int ImageOf(Document doc, string media)
        {
            if (Images.TryGetValue(media, out int id)) return id;
            id = 0;
            if (doc.Media.TryGetValue(media, out var data) && PageRenderer.Decode(data, out bool jpeg) is { } bmp)
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                string head = "/Type /XObject /Subtype /Image /Width " + w + " /Height " + h + " /BitsPerComponent 8";
                bool grey = bmp.Format == PixelFormats.Gray8;
                if (jpeg && (grey || bmp.Format == PixelFormats.Bgr24 || bmp.Format == PixelFormats.Bgr32 || bmp.Format == PixelFormats.Rgb24))
                    id = Objs.AddStream(head + " /ColorSpace " + (grey ? "/DeviceGray" : "/DeviceRGB") + " /Filter /DCTDecode", data, false);
                else
                {
                    // Any other picture: its pixels, and its transparency as a second picture.
                    var px = new byte[w * h * 4];
                    new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);
                    var rgb = new byte[w * h * 3];
                    var alpha = new byte[w * h];
                    bool clear = false;
                    for (int i = 0; i < w * h; i++)
                    {
                        rgb[3 * i] = px[4 * i + 2];
                        rgb[3 * i + 1] = px[4 * i + 1];
                        rgb[3 * i + 2] = px[4 * i];
                        alpha[i] = px[4 * i + 3];
                        clear |= alpha[i] != 255;
                    }
                    string mask = clear ? " /SMask " + Objs.AddStream(head + " /ColorSpace /DeviceGray", alpha, true) + " 0 R" : "";
                    id = Objs.AddStream(head + " /ColorSpace /DeviceRGB" + mask, rgb, true);
                }
            }
            return Images[media] = id;
        }
    }

    sealed class Sink : PageSink
    {
        public readonly StringBuilder S = new();
        readonly Job job;
        readonly long height;

        public Sink(Job j, long pageHeight)
        {
            job = j;
            height = pageHeight;
        }

        /// <summary>The places on the page that lead somewhere: to an address, or to a heading.</summary>
        public readonly List<(long X, long Y, long W, long H, string? Address, Paragraph? To)> Marks = new();
        public Dictionary<Paragraph, Paragraph>? Targets;

        public override void Link(long x, long y, long w, long h, string address) => Marks.Add((x, y, w, h, address, null));

        public override void Entry(Paragraph p, long x, long y, long w, long h)
        {
            if (Targets != null && Targets.TryGetValue(p, out var to)) Marks.Add((x, y, w, h, null, to));
        }

        // A PDF page is measured in points from its bottom left corner.
        static double Px(double emu) => emu / 12700.0;
        double Py(double emu) => (height - emu) / 12700.0;

        public override void Text(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb) => TextAt(font, size, x, baseline, chars, advances, rgb, false);

        public override void TextUp(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb) => TextAt(font, size, x, baseline, chars, advances, rgb, true);

        /// <summary>Text along the baseline, or (up) turned a quarter turn so that it runs upward from x, baseline.</summary>
        void TextAt(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb, bool up)
        {
            if (chars.Count == 0 || size <= 0) return;
            var use = job.FontOf(font);
            if (!use.Embedded)
            {
                Outline(font, size, x, baseline, chars, advances, rgb, up);
                return;
            }
            var face = font.Face;
            double pt = size / 12700.0;
            S.Append("BT ").Append(Colour(rgb)).Append(" rg /F").Append(use.Index).Append(' ').Append(N4(pt)).Append(" Tf ");
            // A bold or slanted style the font family does not have is imitated, as on screen.
            if (font.SimBold) S.Append("2 Tr ").Append(N(pt / 30)).Append(" w ").Append(Colour(rgb)).Append(" RG ");
            else S.Append("0 Tr ");
            S.Append(up ? "0 1 -1 " : "1 0 ").Append(font.SimItalic ? "0.2" : "0").Append(up ? " " : " 1 ").Append(N(Px(x))).Append(' ').Append(N(Py(baseline))).Append(" Tm [");
            double carry = 0;
            for (int i = 0; i < chars.Count; i++)
            {
                var (glyph, adv) = face.Lookup(chars[i]);
                if (glyph != 0) use.Used.TryAdd(glyph, chars[i]);
                else use.Used.TryAdd(0, 0);
                S.Append('<').Append(((int)glyph).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
                // A reader moves on by the width it has for the glyph, a whole number of thousandths of the size.
                // The difference to the advance of the layout is given after the glyph, so nothing drifts.
                double move = Width(face, adv) - advances[i] * 1000.0 / size + carry;
                double given = i + 1 < chars.Count && Math.Abs(move) > 0.02 ? Math.Round(move, 3) : 0;
                if (given != 0) S.Append(N(given));
                carry = move - given;
            }
            S.Append("] TJ ET\n");
        }

        void Outline(ResolvedFont font, int size, int x, int baseline, List<int> chars, List<int> advances, int rgb, bool up = false)
        {
            if (PageRenderer.Run(font, size, 0, 0, chars, advances, 1f) is not { } run) return;
            var path = PathGeometry.CreateFromGeometry(run.BuildGeometry());
            // WPF measures in pixels of 1/96 inch, downward; 0.75 point each.
            string P(Point p) => up
                ? N(Px(x) + p.Y * 0.75) + " " + N(Py(baseline) + p.X * 0.75) + " "
                : N(Px(x) + p.X * 0.75) + " " + N(Py(baseline) - p.Y * 0.75) + " ";
            S.Append(Colour(rgb)).Append(" rg ");
            foreach (var figure in path.Figures)
            {
                var at = figure.StartPoint;
                S.Append(P(at)).Append("m ");
                foreach (var seg in figure.Segments)
                {
                    switch (seg)
                    {
                        case LineSegment l:
                            S.Append(P(at = l.Point)).Append("l ");
                            break;
                        case PolyLineSegment pl:
                            foreach (var q in pl.Points) S.Append(P(at = q)).Append("l ");
                            break;
                        case BezierSegment b:
                            S.Append(P(b.Point1)).Append(P(b.Point2)).Append(P(at = b.Point3)).Append("c ");
                            break;
                        case PolyBezierSegment pb:
                            for (int i = 0; i + 2 < pb.Points.Count; i += 3)
                                S.Append(P(pb.Points[i])).Append(P(pb.Points[i + 1])).Append(P(at = pb.Points[i + 2])).Append("c ");
                            break;
                        case QuadraticBezierSegment qb:
                            at = Quad(at, qb.Point1, qb.Point2, P);
                            break;
                        case PolyQuadraticBezierSegment pq:
                            for (int i = 0; i + 1 < pq.Points.Count; i += 2) at = Quad(at, pq.Points[i], pq.Points[i + 1], P);
                            break;
                    }
                }
                S.Append("h ");
            }
            if (path.Figures.Count > 0) S.Append("f\n");
        }

        /// <summary>A curve with one control point, written as the same curve with two.</summary>
        Point Quad(Point from, Point c, Point to, Func<Point, string> p)
        {
            var c1 = new Point(from.X + 2 * (c.X - from.X) / 3, from.Y + 2 * (c.Y - from.Y) / 3);
            var c2 = new Point(to.X + 2 * (c.X - to.X) / 3, to.Y + 2 * (c.Y - to.Y) / 3);
            S.Append(p(c1)).Append(p(c2)).Append(p(to)).Append("c ");
            return to;
        }

        public override void Fill(long x, long y, long w, long h, int rgb, byte alpha = 255)
        {
            if (alpha != 255 || w <= 0 || h <= 0) return;
            S.Append(Colour(rgb)).Append(" rg ").Append(N(Px(x))).Append(' ').Append(N(Py(y + h))).Append(' ')
                .Append(N(Px(w))).Append(' ').Append(N(Px(h))).Append(" re f\n");
        }

        public override void Line(int[] points, int thickness, int rgb)
        {
            if (points.Length < 4) return;
            S.Append(Colour(rgb)).Append(" RG ").Append(N(Px(thickness))).Append(" w 1 j 0 J ");
            for (int i = 0; i < points.Length; i += 2)
                S.Append(N(Px(points[i]))).Append(' ').Append(N(Py(points[i + 1]))).Append(i == 0 ? " m " : " l ");
            S.Append("S\n");
        }

        /// <summary>An arc of a circle, clockwise as seen on the page, as curves of at most a quarter turn each.</summary>
        void Arc(double cx, double cy, double r, double a0, double a1)
        {
            int parts = Math.Max(1, (int)Math.Ceiling((a1 - a0) / (Math.PI / 2) - 1e-9));
            double step = (a1 - a0) / parts, k = 4.0 / 3 * Math.Tan(step / 4);
            for (int i = 0; i < parts; i++)
            {
                double t0 = a0 + i * step, t1 = t0 + step;
                // On the page (y downward) the point at angle t is (cx + r sin t, cy − r cos t); its direction of travel (cos t, sin t).
                double x0 = cx + r * Math.Sin(t0), y0 = cy - r * Math.Cos(t0), x1 = cx + r * Math.Sin(t1), y1 = cy - r * Math.Cos(t1);
                S.Append(N(Px(x0 + k * r * Math.Cos(t0)))).Append(' ').Append(N(Py(y0 + k * r * Math.Sin(t0)))).Append(' ')
                    .Append(N(Px(x1 - k * r * Math.Cos(t1)))).Append(' ').Append(N(Py(y1 - k * r * Math.Sin(t1)))).Append(' ')
                    .Append(N(Px(x1))).Append(' ').Append(N(Py(y1))).Append(" c ");
            }
        }

        public override void Disc(int cx, int cy, int r, int rgb)
        {
            S.Append(Colour(rgb)).Append(" rg ").Append(N(Px(cx))).Append(' ').Append(N(Py(cy - r))).Append(" m ");
            Arc(cx, cy, r, 0, 2 * Math.PI);
            S.Append("h f\n");
        }

        public override void Slice(int cx, int cy, int r, long from, long to, long total, int rgb)
        {
            if (to - from >= total) { Disc(cx, cy, r, rgb); return; }
            double a0 = 2 * Math.PI * from / total, a1 = 2 * Math.PI * to / total;
            // Filled and outlined thinly in its own colour, so that no seam shows between neighbours.
            S.Append("q ").Append(Colour(rgb)).Append(" rg ").Append(Colour(rgb)).Append(" RG 0.4 w 1 j ").Append(N(Px(cx))).Append(' ').Append(N(Py(cy))).Append(" m ")
                .Append(N(Px(cx + r * Math.Sin(a0)))).Append(' ').Append(N(Py(cy - r * Math.Cos(a0)))).Append(" l ");
            Arc(cx, cy, r, a0, a1);
            S.Append("h B Q\n");
        }

        public override void Image(Document doc, Picture pic, int x, int y)
        {
            int id = job.ImageOf(doc, pic.Media);
            if (id == 0)
            {
                Fill(x, y, pic.Width, pic.Height, 0xE0E0E0);
                return;
            }
            S.Append("q ").Append(N(Px(pic.Width))).Append(" 0 0 ").Append(N(Px(pic.Height))).Append(' ')
                .Append(N(Px(x))).Append(' ').Append(N(Py((long)y + pic.Height))).Append(" cm /Im").Append(id).Append(" Do Q\n");
        }
    }

    /// <summary>The width of a glyph as the PDF states it: whole thousandths of the font size (readers keep whole numbers).</summary>
    static int Width(FontFace face, int advance) => (int)Math.Round(advance * 1000.0 / face.UnitsPerEm, MidpointRounding.AwayFromZero);

    static string Hex(int value, int digits) => value.ToString("X" + digits, CultureInfo.InvariantCulture);

    /// <summary>Tells a reader which characters the glyphs stand for, so that text can be selected, copied and searched.</summary>
    static byte[] UnicodeMap(FontUse f)
    {
        var sb = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var lines = new List<string>();
        foreach (var (glyph, cp) in f.Used)
        {
            if (glyph == 0 || cp == 0) continue;
            string s = char.ConvertFromUtf32(cp), hex = "";
            foreach (char c in s) hex += Hex(c, 4);
            lines.Add("<" + Hex(glyph, 4) + "> <" + hex + ">\n");
        }
        for (int i = 0; i < lines.Count; i += 100)
        {
            int n = Math.Min(100, lines.Count - i);
            sb.Append(n).Append(" beginbfchar\n");
            for (int k = 0; k < n; k++) sb.Append(lines[i + k]);
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Latin.GetBytes(sb.ToString());
    }

    static void FinishFont(Objects objs, FontUse f)
    {
        var face = f.Font.Face;
        double k = 1000.0 / face.UnitsPerEm;
        byte[] program = face.Subset(f.Used.Keys);
        int file = objs.AddStream("/Length1 " + program.Length, program, true);
        // Six letters before the name mark a font of which only a part is in the file.
        string tag = "";
        for (int i = 0, v = f.Index * 7919; i < 6; i++, v /= 26) tag += (char)('A' + v % 26);
        string name = tag + "+" + face.PostScriptName;
        int desc = objs.Add("<< /Type /FontDescriptor /FontName /" + name + " /Flags 4 /FontBBox [" + N(face.XMin * k) + " " + N(face.YMin * k) + " "
            + N(face.XMax * k) + " " + N(face.YMax * k) + "] /ItalicAngle " + face.ItalicAngle + " /Ascent " + N(face.Ascender * k) + " /Descent " + N(-face.Descender * k)
            + " /CapHeight " + N(face.CapHeight * k) + " /StemV 80 /FontFile2 " + file + " 0 R >>");
        var widths = new StringBuilder("/W [");
        foreach (int glyph in f.Used.Keys) widths.Append(glyph).Append(" [").Append(Width(face, face.AdvanceOfGlyph(glyph))).Append("] ");
        widths.Append(']');
        int cid = objs.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /" + name + " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"
            + " /FontDescriptor " + desc + " 0 R /DW 1000 " + widths + " /CIDToGIDMap /Identity >>");
        int map = objs.AddStream("", UnicodeMap(f), true);
        objs.Set(f.ObjId, "<< /Type /Font /Subtype /Type0 /BaseFont /" + name + " /Encoding /Identity-H /DescendantFonts [" + cid + " 0 R] /ToUnicode " + map + " 0 R >>");
    }

    /// <summary>An address as the text of a PDF string: plain characters only, the others written as %XX.</summary>
    static string Address(string address)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(address))
            if (b > 32 && b < 127 && b != '(' && b != ')' && b != '\\' && b != '<' && b != '>' && b != '"') sb.Append((char)b);
            else sb.Append('%').Append(Hex(b, 2));
        return sb.ToString();
    }

    /// <summary>The headings as bookmarks, the lower levels inside the higher ones. Gives the number of their object, 0 if there are none.</summary>
    static int Bookmarks(Objects objs, Document doc, Func<Paragraph, string?> dest)
    {
        var heads = doc.Body.FindAll(p => Contents.Level(p) > 0 && Contents.TextOf(p).Length > 0 && dest(p) != null);
        if (heads.Count == 0) return 0;
        int n = heads.Count, root = objs.Reserve();
        var ids = new int[n];
        var parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = objs.Reserve();
            parent[i] = -1;
            for (int j = i - 1; j >= 0; j--)
                if (Contents.Level(heads[j]) < Contents.Level(heads[i])) { parent[i] = j; break; }
        }
        // First and last child and the neighbours of each, by parent; -1 stands for the top.
        int First(int of) { for (int i = 0; i < n; i++) if (parent[i] == of) return i; return -1; }
        int Last(int of) { for (int i = n - 1; i >= 0; i--) if (parent[i] == of) return i; return -1; }
        int Next(int i) { for (int j = i + 1; j < n; j++) if (parent[j] == parent[i]) return j; return -1; }
        int Prev(int i) { for (int j = i - 1; j >= 0; j--) if (parent[j] == parent[i]) return j; return -1; }
        int top = 0;
        for (int i = 0; i < n; i++)
        {
            if (parent[i] < 0) top++;
            var title = new StringBuilder("<FEFF");
            foreach (char c in Contents.TextOf(heads[i])) title.Append(Hex(c, 4));
            var s = new StringBuilder("<< /Title " + title + "> /Parent " + (parent[i] < 0 ? root : ids[parent[i]]) + " 0 R /Dest " + dest(heads[i]));
            if (Prev(i) >= 0) s.Append(" /Prev " + ids[Prev(i)] + " 0 R");
            if (Next(i) >= 0) s.Append(" /Next " + ids[Next(i)] + " 0 R");
            if (First(i) >= 0) s.Append(" /First " + ids[First(i)] + " 0 R /Last " + ids[Last(i)] + " 0 R");
            objs.Set(ids[i], s.Append(" >>").ToString());
        }
        objs.Set(root, "<< /Type /Outlines /First " + ids[First(-1)] + " 0 R /Last " + ids[Last(-1)] + " 0 R /Count " + top + " >>");
        return root;
    }

    public static void Write(LayoutEngine eng, string path)
    {
        var job = new Job();
        var objs = job.Objs;
        var page = eng.Doc.Page;
        int pagesId = objs.Reserve(), resources = objs.Reserve();
        var kids = new StringBuilder();
        var targets = Contents.Targets(eng.Doc);
        var pageIds = new List<int>();
        var annotIds = new List<int>();
        var marks = new List<List<(long X, long Y, long W, long H, string? Address, Paragraph? To)>>();
        for (int k = 0; k < eng.Pages.Count; k++)
        {
            var sink = new Sink(job, page.Height) { Targets = targets };
            PageDrawer.Page(sink, eng, k, 0, null, null);
            int content = objs.AddStream("", Latin.GetBytes(sink.S.ToString()), true);
            int annots = objs.Reserve();
            int id = objs.Add("<< /Type /Page /Parent " + pagesId + " 0 R /MediaBox [0 0 " + N(page.Width / 12700.0) + " " + N(page.Height / 12700.0)
                + "] /Resources " + resources + " 0 R /Contents " + content + " 0 R /Annots " + annots + " 0 R >>");
            kids.Append(id).Append(" 0 R ");
            pageIds.Add(id);
            annotIds.Add(annots);
            marks.Add(sink.Marks);
        }
        // Where a paragraph starts, as a place a reader can jump to.
        string? Dest(Paragraph p) => p.Lines is { Count: > 0 } ls && ls[0].Page >= 0 && ls[0].Page < pageIds.Count
            ? "[" + pageIds[ls[0].Page] + " 0 R /XYZ null " + N((page.Height - ls[0].Top) / 12700.0) + " null]" : null;
        for (int k = 0; k < marks.Count; k++)
        {
            var list = new StringBuilder("[");
            foreach (var m in marks[k])
            {
                string? act = m.Address != null ? "/A << /S /URI /URI (" + Address(m.Address) + ") >>" : m.To != null && Dest(m.To) is { } d ? "/Dest " + d : null;
                if (act == null) continue;
                list.Append(objs.Add("<< /Type /Annot /Subtype /Link /Rect [" + N(m.X / 12700.0) + " " + N((page.Height - m.Y - m.H) / 12700.0) + " "
                    + N((m.X + m.W) / 12700.0) + " " + N((page.Height - m.Y) / 12700.0) + "] /Border [0 0 0] " + act + " >>")).Append(" 0 R ");
            }
            objs.Set(annotIds[k], list.Append(']').ToString());
        }
        int outlines = Bookmarks(objs, eng.Doc, Dest);
        var fonts = new StringBuilder();
        foreach (var f in job.Fonts.Values)
        {
            if (!f.Embedded) continue;
            FinishFont(objs, f);
            fonts.Append("/F").Append(f.Index).Append(' ').Append(f.ObjId).Append(" 0 R ");
        }
        var images = new StringBuilder();
        foreach (int id in job.Images.Values)
            if (id != 0) images.Append("/Im").Append(id).Append(' ').Append(id).Append(" 0 R ");
        objs.Set(resources, "<< /Font << " + fonts + ">> /XObject << " + images + ">> >>");
        objs.Set(pagesId, "<< /Type /Pages /Count " + eng.Pages.Count + " /Kids [" + kids + "] >>");
        int root = objs.Add("<< /Type /Catalog /Pages " + pagesId + " 0 R" + (outlines != 0 ? " /Outlines " + outlines + " 0 R" : "") + " >>");
        int info = objs.Add("<< /Producer (Reiks Document) >>");

        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) objs.Save(fs, root, info);
        File.Move(tmp, path, true);
    }

    /// <summary>A plain check of the file's own bookkeeping: every object stands where the table at the end says.
    /// Returns the number of pages, or -1 if something is wrong.</summary>
    public static int Verify(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        string s = Latin.GetString(b);
        if (!s.StartsWith("%PDF-", StringComparison.Ordinal)) return -1;
        int sx = s.LastIndexOf("startxref", StringComparison.Ordinal);
        if (sx < 0 || !int.TryParse(s[(sx + 9)..].Trim().Split('\n')[0], out int xref) || string.CompareOrdinal(s, xref, "xref\n0 ", 0, 7) != 0) return -1;
        var lines = s[xref..].Split('\n');
        if (!int.TryParse(lines[1][2..], out int count)) return -1;
        for (int id = 1; id < count; id++)
        {
            if (!int.TryParse(lines[2 + id][..10], out int at) || string.CompareOrdinal(s, at, id + " 0 obj\n", 0, (id + " 0 obj\n").Length) != 0) return -1;
        }
        int pages = 0;
        for (int i = s.IndexOf("/Type /Page /Parent", StringComparison.Ordinal); i >= 0; i = s.IndexOf("/Type /Page /Parent", i + 1, StringComparison.Ordinal)) pages++;
        return pages;
    }
}
