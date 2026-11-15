using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Xps.Packaging;

namespace ReiksDocument;

/// <summary>Hands the laid-out pages to Windows, one drawing per page, exactly as they are drawn on screen.</summary>
sealed class PagePaginator : DocumentPaginator
{
    readonly LayoutEngine eng;
    readonly IReadOnlyList<int> pages;
    readonly Size page;
    Size size;

    public PagePaginator(LayoutEngine engine, int firstPage, int pageCount) : this(engine, Enumerable.Range(firstPage, pageCount).ToList(), null) { }

    /// <summary>The given pages, in that order, on paper of the given size (in 1/96 inch); without a size, on paper as large as the page.</summary>
    public PagePaginator(LayoutEngine engine, IReadOnlyList<int> which, Size? paper)
    {
        eng = engine;
        pages = which;
        page = new Size(U.Dip(engine.Doc.Page.Width), U.Dip(engine.Doc.Page.Height));
        size = paper ?? page;
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        var visual = new DrawingVisual();
        PageRenderer.ForPrint = true;
        try
        {
            using var dc = visual.RenderOpen();
            // On other paper the page stands in the middle, and is made smaller if it does not fit.
            double scale = Math.Min(1, Math.Min(size.Width / page.Width, size.Height / page.Height));
            bool moved = scale < 1 || Math.Abs(size.Width - page.Width) > 0.5 || Math.Abs(size.Height - page.Height) > 0.5;
            if (moved)
            {
                dc.PushTransform(new TranslateTransform((size.Width - page.Width * scale) / 2, (size.Height - page.Height * scale) / 2));
                dc.PushTransform(new ScaleTransform(scale, scale));
            }
            PageRenderer.DrawPage(dc, eng, pages[pageNumber], 0, null, null, 1f);
            if (moved) { dc.Pop(); dc.Pop(); }
        }
        finally { PageRenderer.ForPrint = false; }
        var box = new Rect(size);
        return new DocumentPage(visual, size, box, box);
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => pages.Count;
    public override Size PageSize { get => size; set { } }
    public override IDocumentPaginatorSource Source => null!;
}

static class Printing
{
    /// <summary>Writes chosen pages for paper of a given size to an XPS file through the same path as printing, without any printer.</summary>
    public static void WriteXps(LayoutEngine eng, string path, IReadOnlyList<int> pages, Size paper)
    {
        if (File.Exists(path)) File.Delete(path);
        using var doc = new XpsDocument(path, FileAccess.Write);
        XpsDocument.CreateXpsDocumentWriter(doc).Write(new PagePaginator(eng, pages, paper));
    }

    /// <summary>Writes the pages to an XPS file through the same path as printing, without any printer.</summary>
    public static void WriteXps(LayoutEngine eng, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var doc = new XpsDocument(path, FileAccess.Write);
        XpsDocument.CreateXpsDocumentWriter(doc).Write(new PagePaginator(eng, 0, eng.Pages.Count));
    }

    /// <summary>A page of an XPS file as a picture, to look at what was sent to the printing path.</summary>
    public static System.Windows.Media.Imaging.BitmapSource XpsPage(string path, int page)
    {
        using var doc = new XpsDocument(path, FileAccess.Read);
        var visual = doc.GetFixedDocumentSequence().DocumentPaginator.GetPage(page).Visual;
        var size = doc.GetFixedDocumentSequence().DocumentPaginator.GetPage(page).Size;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        var back = new DrawingVisual();
        using (var dc = back.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(size));
        bmp.Render(back);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public static int XpsPages(string path)
    {
        using var doc = new XpsDocument(path, FileAccess.Read);
        var pages = doc.GetFixedDocumentSequence().DocumentPaginator;
        pages.ComputePageCount();
        return pages.PageCount;
    }
}
