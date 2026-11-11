using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace HistoryBoard;

// Renders PDF pages with PDFium (Apache-2.0, bundled as pdfium.dll). PDFium is not thread-safe,
// so every call goes through one lock.
public sealed class PdfDocument : IDisposable
{
    private static readonly object Gate = new();
    private static bool initialized;
    private IntPtr document, data;
    public int PageCount { get; }
    public const int MaxPages = 40;

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_InitLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr buffer, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern uint FPDF_GetLastError();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    public PdfDocument(byte[] bytes)
    {
        lock (Gate)
        {
            if (!initialized) { FPDF_InitLibrary(); initialized = true; }
            // PDFium reads from this memory for as long as the document is open.
            data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            document = FPDF_LoadMemDocument(data, bytes.Length, null);
            if (document == IntPtr.Zero)
            {
                var error = FPDF_GetLastError();
                Marshal.FreeHGlobal(data); data = IntPtr.Zero;
                throw new InvalidDataException(error switch
                {
                    4 => "Документ защищён паролем. Снимите защиту и добавьте его снова.",
                    3 or 2 => "Файл повреждён или это не PDF.",
                    6 => "Этот PDF защищён от копирования.",
                    _ => "Не удалось открыть PDF."
                });
            }
            PageCount = FPDF_GetPageCount(document);
        }
    }

    // Renders a page so that its longer side has the given number of pixels, on white paper.
    public BitmapSource RenderPage(int index, int longSide)
    {
        lock (Gate)
        {
            var page = FPDF_LoadPage(document, index);
            if (page == IntPtr.Zero) throw new InvalidDataException($"Не удалось прочитать страницу {index + 1}.");
            try
            {
                double w = FPDF_GetPageWidthF(page), h = FPDF_GetPageHeightF(page);
                if (!(w > 0 && h > 0)) throw new InvalidDataException($"Страница {index + 1} пустая.");
                var scale = longSide / Math.Max(w, h);
                var width = Math.Max(1, (int)Math.Round(w * scale)); var height = Math.Max(1, (int)Math.Round(h * scale));
                var bitmap = FPDFBitmap_Create(width, height, 0);
                if (bitmap == IntPtr.Zero) throw new InvalidDataException("Недостаточно памяти для страницы PDF.");
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0x01 | 0x10); // annotations, for printing quality
                    var stride = FPDFBitmap_GetStride(bitmap);
                    var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, FPDFBitmap_GetBuffer(bitmap), stride * height, stride);
                    source.Freeze(); return source;
                }
                finally { FPDFBitmap_Destroy(bitmap); }
            }
            finally { FPDF_ClosePage(page); }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (document != IntPtr.Zero) { FPDF_CloseDocument(document); document = IntPtr.Zero; }
            if (data != IntPtr.Zero) { Marshal.FreeHGlobal(data); data = IntPtr.Zero; }
        }
    }

    // "1-3, 5" → zero-based page indexes; null when the text is not a valid page list.
    public static List<int>? ParsePages(string text, int count)
    {
        var pages = new List<int>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split(['-', '–', '—']);
            if (range.Length > 2 || !int.TryParse(range[0], out var from) || !int.TryParse(range[^1], out var to)) return null;
            if (from < 1 || to < from || to > count) return null;
            for (var p = from; p <= to; p++) if (!pages.Contains(p - 1)) pages.Add(p - 1);
        }
        return pages.Count == 0 ? null : pages;
    }
}

public partial class MainWindow
{
    private const int PdfImportPixels = 2000;

    private void OnAddMenu(object sender, RoutedEventArgs e)
    {
        CommitEditor(); FinishGesture();
        var menu = new ContextMenu();
        void Add(string title, BoardCommand command, Action action)
        {
            var entry = new MenuItem { Header = title, InputGestureText = shortcuts.Display(command) == "Не назначено" ? "" : shortcuts.Display(command) };
            entry.Click += (_, _) => action(); menu.Items.Add(entry);
        }
        Add("Карта или изображение…", BoardCommand.AddImage, () => OnAddMap(this, new RoutedEventArgs()));
        Add("Страницы PDF…", BoardCommand.AddImage, () => OnImportPdf(this, new RoutedEventArgs()));
        ((MenuItem)menu.Items[^1]).InputGestureText = "";
        Add("Снимок области экрана", BoardCommand.ScreenCapture, () => _ = CaptureScreenAsync());
        Add("Лента времени…", BoardCommand.AddTimeline, () => OnAddTimeline(this, new RoutedEventArgs()));
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }

    // ---- Screen capture: the Windows snipping overlay puts the picture on the clipboard ----
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private bool capturing;

    private async void OnCaptureScreen(object sender, RoutedEventArgs e) => await CaptureScreenAsync();

    internal async Task CaptureScreenAsync()
    {
        if (capturing || presenting) return;
        CommitEditor(); FinishGesture();
        capturing = true;
        try
        {
            var start = GetClipboardSequenceNumber();
            try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
            catch { Status.Text = "Нажмите Win+Shift+S, выделите область экрана — снимок появится на доске."; }
            Status.Text = "Выделите область экрана — снимок сразу появится на доске. Esc отменяет.";
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(250);
                if (GetClipboardSequenceNumber() == start) continue;
                await Task.Delay(200); // let the snipping tool finish writing all formats
                if (Clipboard.ContainsImage() || Clipboard.GetDataObject()?.GetDataPresent("PNG") == true)
                {
                    Activate(); await PasteClipboardAsync(); return;
                }
                start = GetClipboardSequenceNumber();
            }
            Status.Text = "Снимок экрана не получен. Можно повторить или нажать Win+Shift+S и затем Ctrl+V.";
        }
        catch (Exception ex) { Status.Text = "Не удалось сделать снимок: " + ex.Message; }
        finally { capturing = false; }
    }

    // ---- PDF import ----
    private async void OnImportPdf(object sender, RoutedEventArgs e)
    {
        if (presenting) return;
        CommitEditor(); FinishGesture();
        var dialog = new OpenFileDialog { Title = "Добавить страницы PDF", Filter = "Документ PDF|*.pdf" };
        if (dialog.ShowDialog(this) == true) await ImportPdfAsync(dialog.FileName);
    }

    internal async Task<int> ImportPdfAsync(string path, string? pages = null)
    {
        try
        {
            if (new FileInfo(path).Length > 200 * 1024 * 1024) throw new InvalidDataException("Используйте PDF размером до 200 МБ.");
            var activeLesson = lesson;
            Status.Text = "Открываем PDF…";
            var bytes = await File.ReadAllBytesAsync(path);
            using var pdf = await Task.Run(() => new PdfDocument(bytes));
            if (pdf.PageCount == 0) throw new InvalidDataException("В документе нет страниц.");
            var chosen = pages != null ? PdfDocument.ParsePages(pages, pdf.PageCount) : pdf.PageCount == 1 ? [0] : AskPdfPages(Path.GetFileName(path), pdf.PageCount);
            if (chosen == null) { if (pages != null) throw new InvalidDataException("Неверный список страниц."); Status.Text = "Добавление PDF отменено."; return 0; }
            if (chosen.Count > PdfDocument.MaxPages) throw new InvalidDataException($"За один раз можно добавить до {PdfDocument.MaxPages} страниц.");
            var rendered = new List<(byte[] Bytes, BitmapSource Image)>();
            foreach (var index in chosen)
            {
                Status.Text = $"Готовим страницу {index + 1} ({rendered.Count + 1} из {chosen.Count})…";
                var page = await Task.Run(() =>
                {
                    var bitmap = pdf.RenderPage(index, PdfImportPixels);
                    var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var memory = new MemoryStream(); encoder.Save(memory);
                    var jpeg = memory.ToArray(); return (jpeg, LessonFile.DecodeImage(jpeg));
                });
                rendered.Add(page);
            }
            if (activeLesson != lesson) { Status.Text = "Урок изменился. Добавьте PDF ещё раз."; return 0; }
            CommitEditor(); FinishGesture();
            if (lesson.Items.Count + rendered.Count > 20000) throw new InvalidDataException("В уроке может быть до 20 000 объектов.");
            EnsureAssetSpace(rendered.Sum(p => (long)p.Bytes.Length));
            // Pages are laid out left to right at the height of the current view, behind annotations.
            var before = lesson.Clone(); var visible = VisibleRegion;
            var height = visible.Height * .9; var gap = height * .04; var x = visible.X + visible.Width * .05;
            var items = new List<BoardItem>();
            foreach (var (pageBytes, image) in rendered)
            {
                var id = Guid.NewGuid().ToString("N");
                var item = new BoardItem { Kind = "image", AssetId = id, Height = height, Width = height * image.PixelWidth / image.PixelHeight, X = x, Y = visible.Y + visible.Height * .05 };
                x += item.Width + gap;
                assets[id] = pageBytes; Surface.AddImage(id, image); items.Add(item);
            }
            lesson.Items.InsertRange(0, items); Surface.Rebuild();
            SetTool(BoardTool.Select); Surface.SelectMany(items); Changed(before);
            if (items.Count > 1) { var bounds = Surface.SelectedBounds; bounds.Inflate(gap, gap); FocusRegion(bounds, true); }
            Status.Text = $"● Добавлено страниц PDF: {items.Count}. Пишите и рисуйте поверх, страницы можно двигать и закреплять.";
            return items.Count;
        }
        catch (DllNotFoundException) { Error("Не удалось открыть PDF", new InvalidOperationException("Не найден модуль чтения PDF. Переустановите программу.")); return 0; }
        catch (Exception ex) { Error("Не удалось открыть PDF", ex); return 0; }
    }

    private List<int>? AskPdfPages(string file, int count)
    {
        var window = new Window { Owner = this, Title = "Страницы PDF", Width = 420, Height = 230, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BoardSurface.Brush("#FAF7EF") };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = $"«{file}»: {count} стр. Какие страницы добавить на доску?", TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = $"Например: 1-3, 5. За один раз — до {PdfDocument.MaxPages} страниц.", FontSize = 11, Foreground = BoardSurface.Brush("#7B827C"), Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = count <= 5 ? $"1-{count}" : "1", Padding = new Thickness(6), FontSize = 13 };
        var error = new TextBlock { Foreground = BoardSurface.Brush("#C94F49"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Добавить", IsDefault = true, Padding = new Thickness(12, 6, 12, 6), Background = BoardSurface.Brush("#58725B"), Foreground = Brushes.White };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        panel.Children.Add(input); panel.Children.Add(error); panel.Children.Add(buttons); window.Content = panel;
        List<int>? result = null;
        ok.Click += (_, _) =>
        {
            result = PdfDocument.ParsePages(input.Text, count);
            if (result == null) error.Text = $"Укажите страницы от 1 до {count}, например 1-3, 5.";
            else if (result.Count > PdfDocument.MaxPages) { error.Text = $"Не больше {PdfDocument.MaxPages} страниц за раз."; result = null; }
            else window.DialogResult = true;
        };
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? result : null;
    }
}
