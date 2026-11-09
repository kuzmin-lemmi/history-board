using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace HistoryBoard;

public sealed record PdfPage(byte[] Jpeg, int PixelWidth, int PixelHeight);

// A minimal PDF 1.4 writer: one page per frame, each page is a single JPEG picture.
// No external libraries are needed and the result opens in any PDF viewer or printer.
public static class PdfWriter
{
    public const double LongSide = 842; // A4 long side in points

    public static void Write(Stream output, string title, IReadOnlyList<PdfPage> pages)
    {
        if (pages.Count == 0) throw new InvalidOperationException("Нет страниц для PDF.");
        var offsets = new List<long>();
        var latin = Encoding.Latin1;
        void Text(string value) { var bytes = latin.GetBytes(value); output.Write(bytes); }
        void Begin(int number) { while (offsets.Count < number) offsets.Add(0); offsets[number - 1] = output.Position; Text($"{number} 0 obj\n"); }
        string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        Text("%PDF-1.4\n%âãÏÓ\n");
        // Objects: 1 catalog, 2 page tree, 3 info, then three objects per page: page, contents, image.
        var kids = string.Join(" ", pages.Select((_, i) => $"{4 + i * 3} 0 R"));
        Begin(1); Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2); Text($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");
        Begin(3); Text($"<< /Title {Utf16(title)} /Producer {Utf16("Доска истории " + UpdateService.DisplayVersion)} /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>\nendobj\n");
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i]; var number = 4 + i * 3;
            var landscape = page.PixelWidth >= page.PixelHeight;
            var width = landscape ? LongSide : LongSide * page.PixelWidth / page.PixelHeight;
            var height = landscape ? LongSide * page.PixelHeight / page.PixelWidth : LongSide;
            Begin(number);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(width)} {N(height)}] /Resources << /XObject << /Im{i} {number + 2} 0 R >> >> /Contents {number + 1} 0 R >>\nendobj\n");
            var content = $"q {N(width)} 0 0 {N(height)} 0 0 cm /Im{i} Do Q\n";
            Begin(number + 1); Text($"<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
            Begin(number + 2);
            Text($"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            output.Write(page.Jpeg); Text("\nendstream\nendobj\n");
        }
        var xref = output.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Text($"{offset:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    // PDF text strings outside Latin-1 are written as UTF-16BE with a byte order mark.
    private static string Utf16(string value) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
}

public partial class MainWindow
{
    private const int PdfPagePixels = 2400;

    internal void ExportFramesPdf(string path)
    {
        var pages = lesson.Frames.Select(f => { var (jpeg, w, h) = Surface.RenderJpeg(f.Bounds, PdfPagePixels); return new PdfPage(jpeg, w, h); }).ToList();
        var temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { PdfWriter.Write(file, lesson.Title, pages); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void OnExportPdf()
    {
        CommitEditor(); FinishGesture();
        if (lesson.Frames.Count == 0) { Status.Text = "Сначала добавьте кадры кнопкой «＋ Кадр»."; return; }
        var dialog = new SaveFileDialog { Title = "Сохранить кадры урока в PDF", Filter = "Документ PDF|*.pdf", FileName = SafeName(lesson.Title) + ".pdf", DefaultExt = ".pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            ExportFramesPdf(dialog.FileName);
            Status.Text = $"●  PDF сохранён: {lesson.Frames.Count} стр. — {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) { Error("Не удалось сохранить PDF", ex); }
        finally { Mouse.OverrideCursor = null; }
    }
}
