using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public sealed class BoardClipboard
{
    public Lesson Lesson { get; set; } = new();
    public Dictionary<string, byte[]> Assets { get; set; } = [];
}

public partial class MainWindow
{
    private const string BoardClipboardFormat = "HistoryBoard.Objects.v1";

    private BoardClipboard SelectedClipboard() => new()
    {
        Lesson = new Lesson { Items = Surface.SelectedItems.Select(i => i.Clone()).ToList() },
        Assets = Surface.SelectedItems.Where(i => i.AssetId != null).Select(i => i.AssetId!).Distinct().ToDictionary(id => id, id => assets[id])
    };

    private void CopySelection()
    {
        CommitEditor(); FinishGesture();
        if (Surface.SelectedItems.Count == 0) return;
        try
        {
            var data = new DataObject();
            data.SetData(BoardClipboardFormat, JsonSerializer.Serialize(SelectedClipboard()));
            var text = string.Join(Environment.NewLine, Surface.SelectedItems.Where(i => i.Kind == "text").Select(i => i.Text));
            if (text.Length > 0) data.SetText(text);
            Clipboard.SetDataObject(data, true);
            Status.Text = $"● Скопировано объектов: {Surface.SelectedItems.Count}";
        }
        catch (Exception ex) { Status.Text = "Не удалось скопировать: " + ex.Message; }
    }

    private void DuplicateSelection()
    {
        CommitEditor(); FinishGesture();
        if (Surface.SelectedItems.Count == 0) return;
        try { PasteObjects(SelectedClipboard(), new Vector(24 / zoom, 24 / zoom)); }
        catch (Exception ex) { Status.Text = "Не удалось дублировать: " + ex.Message; }
    }

    private void PasteObjects(BoardClipboard package, Vector? offset = null)
    {
        LessonFile.Validate(package.Lesson);
        if (package.Lesson.Items.Count == 0) return;
        if (lesson.Items.Count + package.Lesson.Items.Count > 20000) throw new InvalidDataException("В уроке может быть до 20 000 объектов.");
        if (package.Assets == null || package.Assets.Values.Any(b => b == null) || package.Assets.Values.Sum(b => (long)b.Length) > LessonFile.MaxFileSize)
            throw new InvalidDataException("Слишком много данных изображений.");
        var groupIds = new Dictionary<string, string>(); var assetIds = new Dictionary<string, string>();
        var newAssets = new Dictionary<string, byte[]>(); var decoded = new Dictionary<string, BitmapSource>();
        var copies = package.Lesson.Items.Select(i => i.Clone()).ToArray();
        var bounds = Rect.Empty; foreach (var item in copies) bounds.Union(Surface.Bounds(item));
        var visible = VisibleRegion;
        var delta = offset ?? new Vector(visible.X + visible.Width / 2 - (bounds.X + bounds.Width / 2), visible.Y + visible.Height / 2 - (bounds.Y + bounds.Height / 2));
        foreach (var item in copies)
        {
            item.Id = Guid.NewGuid().ToString("N"); item.X += delta.X; item.Y += delta.Y;
            if (item.GroupId is { } group)
            {
                if (!groupIds.ContainsKey(group)) groupIds[group] = Guid.NewGuid().ToString("N");
                item.GroupId = groupIds[group];
            }
            if (item.AssetId is { } source)
            {
                if (!assetIds.TryGetValue(source, out var target))
                {
                    if (!package.Assets.TryGetValue(source, out var bytes)) throw new InvalidDataException("В буфере отсутствует изображение.");
                    target = Guid.NewGuid().ToString("N"); assetIds[source] = target;
                    decoded[target] = LessonFile.DecodeImage(bytes); newAssets[target] = bytes;
                }
                item.AssetId = target;
            }
        }
        LessonFile.Validate(new Lesson { Items = copies.ToList() });
        var before = lesson.Clone();
        foreach (var pair in newAssets) { assets[pair.Key] = pair.Value; Surface.AddImage(pair.Key, decoded[pair.Key]); }
        foreach (var item in copies) Surface.Add(item);
        SetTool(BoardTool.Select); Surface.SelectMany(copies); Changed(before);
        Status.Text = $"● Вставлено объектов: {copies.Length}";
    }

    private async Task PasteClipboardAsync()
    {
        if (presenting) return;
        CommitEditor(); FinishGesture();
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            if (data.GetDataPresent(BoardClipboardFormat, false) && data.GetData(BoardClipboardFormat, false) is string json)
            {
                if (json.Length > 210 * 1024 * 1024) throw new InvalidDataException("Буфер содержит слишком много данных.");
                var package = JsonSerializer.Deserialize<BoardClipboard>(json) ?? throw new InvalidDataException("Пустой буфер.");
                PasteObjects(package); return;
            }
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                if (png.Length > 40 * 1024 * 1024) throw new InvalidDataException("Используйте изображение до 40 МБ.");
                await AddImageBytesAsync(png.ToArray()); return;
            }
            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap == null) return;
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 80_000_000) throw new InvalidDataException("Используйте изображение до 80 мегапикселей.");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var memory = new MemoryStream(); encoder.Save(memory);
                await AddImageBytesAsync(memory.ToArray()); return;
            }
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            { await AddMapAsync(files[0]); return; }
            Status.Text = "Скопируйте изображение или объекты доски, затем нажмите Ctrl+V.";
        }
        catch (Exception ex) { Status.Text = "Не удалось вставить: " + ex.Message; }
    }

    private async Task AddImageBytesAsync(byte[] bytes)
    {
        if (bytes.Length > 40 * 1024 * 1024) throw new InvalidDataException("Используйте изображение до 40 МБ.");
        var activeLesson = lesson;
        var image = await Task.Run(() => LessonFile.DecodeImage(bytes));
        if (activeLesson != lesson) { Status.Text = "Урок изменился. Вставьте изображение ещё раз."; return; }
        CommitEditor(); FinishGesture();
        if (lesson.Items.Count >= 20000) throw new InvalidDataException("В уроке может быть до 20 000 объектов.");
        var before = lesson.Clone(); var id = Guid.NewGuid().ToString("N");
        var visible = VisibleRegion;
        var factor = Math.Min(visible.Width * .82 / image.PixelWidth, visible.Height * .82 / image.PixelHeight);
        var item = new BoardItem { Kind = "image", AssetId = id, Width = image.PixelWidth * factor, Height = image.PixelHeight * factor };
        item.X = visible.X + (visible.Width - item.Width) / 2; item.Y = visible.Y + (visible.Height - item.Height) / 2;
        assets[id] = bytes; Surface.AddImage(id, image); lesson.Items.Insert(0, item); Surface.Rebuild();
        SetTool(BoardTool.Select); Surface.Select(item); Changed(before);
        Status.Text = "● Изображение добавлено. Можно рисовать и размещать подписи поверх.";
    }
}
