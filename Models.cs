using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public enum BoardTool { Select, Hand, Text, List, Pen, Arrow, Eraser }

public sealed class BoardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 600;
    public double Height { get; set; } = 400;
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#26352F";
    public double Size { get; set; } = 32;
    public bool Bold { get; set; }
    public bool Serif { get; set; }
    public bool Numbered { get; set; }
    public bool Locked { get; set; }
    public string? GroupId { get; set; }
    public string? AssetId { get; set; }
    public List<BoardPoint> Points { get; set; } = [];
    public BoardItem Clone() => new() { Id = Id, Kind = Kind, X = X, Y = Y, Width = Width, Height = Height, Text = Text, Color = Color, Size = Size, Bold = Bold, Serif = Serif, Numbered = Numbered, Locked = Locked, GroupId = GroupId, AssetId = AssetId, Points = Points.ToList() };
}

public record struct BoardPoint(double X, double Y) { public Point ToPoint() => new(X, Y); }

public sealed class BoardView
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Zoom { get; set; } = 1;
    public BoardView Clone() => new() { X = X, Y = Y, Zoom = Zoom };
}

public sealed class LessonFrame
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Кадр";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    [JsonIgnore] public Rect Bounds => new(X, Y, Width, Height);
    public LessonFrame Clone() => new() { Id = Id, Name = Name, X = X, Y = Y, Width = Width, Height = Height };
}

public sealed class Lesson
{
    public int Version { get; set; } = 2;
    public string Title { get; set; } = "Новый урок";
    public string Paper { get; set; } = "grid";
    public double Width { get; set; } = 1600;
    public double Height { get; set; } = 1000;
    public List<BoardItem> Items { get; set; } = [];
    public List<LessonFrame> Frames { get; set; } = [];
    public BoardView? View { get; set; } = new();
    public Lesson Clone() => new() { Version = Version, Title = Title, Paper = Paper, Width = Width, Height = Height, Items = Items.Select(i => i.Clone()).ToList(), Frames = Frames.Select(f => f.Clone()).ToList(), View = View?.Clone() };
}

public static class LessonFile
{
    public const long MaxFileSize = 150 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(string path, Lesson lesson, IReadOnlyDictionary<string, byte[]> assets)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var stream = zip.CreateEntry("lesson.json", CompressionLevel.Fastest).Open())
                    JsonSerializer.Serialize(stream, lesson, JsonOptions);
                foreach (var id in lesson.Items.Where(i => i.AssetId != null).Select(i => i.AssetId!).Distinct())
                {
                    if (!assets.TryGetValue(id, out var bytes)) throw new InvalidDataException("Не найдена карта урока.");
                    using var stream = zip.CreateEntry("assets/" + id, CompressionLevel.NoCompression).Open();
                    stream.Write(bytes);
                }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static (Lesson Lesson, Dictionary<string, byte[]> Assets) Load(string path)
    {
        if (new FileInfo(path).Length > MaxFileSize) throw new InvalidDataException("Файл слишком большой. Максимум — 150 МБ.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Sum(e => e.Length) > MaxFileSize) throw new InvalidDataException("Распакованный урок превышает 150 МБ.");
        var entry = zip.GetEntry("lesson.json") ?? throw new InvalidDataException("Это не файл урока.");
        if (entry.Length > 20 * 1024 * 1024) throw new InvalidDataException("Слишком много данных на доске.");
        Lesson lesson;
        using (var stream = entry.Open()) lesson = JsonSerializer.Deserialize<Lesson>(stream) ?? throw new InvalidDataException("Пустой файл.");
        Validate(lesson);
        if (lesson.Version == 1) lesson.View = null;
        lesson.Version = 2;
        var assets = new Dictionary<string, byte[]>();
        foreach (var id in lesson.Items.Where(i => i.AssetId != null).Select(i => i.AssetId!).Distinct())
        {
            var asset = zip.GetEntry("assets/" + id) ?? throw new InvalidDataException("В уроке отсутствует карта.");
            using var data = asset.Open();
            using var memory = new MemoryStream();
            data.CopyTo(memory);
            var bytes = memory.ToArray();
            DecodeImage(bytes);
            assets[id] = bytes;
        }
        return (lesson, assets);
    }

    public static void Validate(Lesson lesson)
    {
        if (lesson.Version is not (1 or 2)) throw new InvalidDataException("Эта версия урока не поддерживается.");
        if (lesson.Title == null || lesson.Title.Length > 1000) throw new InvalidDataException("Некорректное название урока.");
        if (lesson.Paper is not ("grid" or "dots" or "ruled" or "plain" or "white")) throw new InvalidDataException("Некорректный фон доски.");
        if (lesson.View is { } view && (!Coordinate(view.X) || !Coordinate(view.Y) || !double.IsFinite(view.Zoom) || view.Zoom < .02 || view.Zoom > 8))
            throw new InvalidDataException("Некорректный масштаб доски.");
        if (lesson.Frames == null || lesson.Frames.Count > 500) throw new InvalidDataException("Слишком много кадров.");
        var frameIds = new HashSet<string>();
        foreach (var f in lesson.Frames)
            if (f == null || string.IsNullOrEmpty(f.Id) || !frameIds.Add(f.Id) || f.Name == null || f.Name.Length > 200 || !Coordinate(f.X) || !Coordinate(f.Y) || !double.IsFinite(f.Width) || !double.IsFinite(f.Height) || f.Width < 1 || f.Height < 1 || f.Width > 1000000 || f.Height > 1000000)
                throw new InvalidDataException("Некорректный кадр презентации.");
        if (!double.IsFinite(lesson.Width) || !double.IsFinite(lesson.Height) || lesson.Width < 200 || lesson.Width > 8000 || lesson.Height < 200 || lesson.Height > 8000)
            throw new InvalidDataException("Некорректный размер доски.");
        if (lesson.Items == null || lesson.Items.Count > 20000) throw new InvalidDataException("Слишком много объектов.");
        var ids = new HashSet<string>();
        foreach (var i in lesson.Items)
        {
            if (i == null || string.IsNullOrEmpty(i.Id) || !ids.Add(i.Id) || i.Kind is not ("text" or "image" or "pen" or "arrow") || i.Text == null || i.Text.Length > 100000 || i.Points == null || i.Points.Count > 100000 ||
                !Coordinate(i.X) || !Coordinate(i.Y) || new[] { i.Width, i.Height, i.Size }.Any(n => !double.IsFinite(n) || Math.Abs(n) > 100000) || i.Size < 1 || i.Size > 300 || i.Width <= 0 || i.Height <= 0 ||
                i.Points.Any(p => !Coordinate(p.X) || !Coordinate(p.Y)) ||
                i.GroupId is { } groupId && (groupId.Length != 32 || groupId.Any(c => !Uri.IsHexDigit(c))) ||
                i.AssetId is { } assetId && (assetId.Length != 32 || assetId.Any(c => !Uri.IsHexDigit(c))))
                throw new InvalidDataException("Урок содержит некорректный объект.");
            try { ColorConverter.ConvertFromString(i.Color); } catch { throw new InvalidDataException("Некорректный цвет."); }
            if (i.Kind == "image" && i.AssetId == null) throw new InvalidDataException("Не указана карта.");
        }
    }

    private static bool Coordinate(double n) => double.IsFinite(n) && Math.Abs(n) <= 1_000_000_000;

    public static BitmapSource DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > 20000 || frame.PixelHeight > 20000 || (long)frame.PixelWidth * frame.PixelHeight > 80000000)
            throw new InvalidDataException("Карта слишком большая. Используйте изображение до 80 мегапикселей.");
        // Decode large maps to a useful working resolution instead of retaining every source pixel.
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (frame.PixelWidth > 3600) image.DecodePixelWidth = 3600;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
