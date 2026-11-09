using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public enum BoardTool { Select, Hand, Text, List, Pen, Arrow, Eraser, Marker, Rectangle, Ellipse, Callout }

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
    /// <summary>Semi-transparent fill of rectangles and ellipses, or null for an outline.</summary>
    public string? Fill { get; set; }
    /// <summary>Image rotation in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }
    /// <summary>Visible part of an image, as fractions of the source picture.</summary>
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; } = 1;
    public double CropHeight { get; set; } = 1;
    /// <summary>Presentation reveal step: 0 — always visible, 1..99 — appears on that click within a frame.</summary>
    public int Step { get; set; }
    public BoardItem Clone() => new() { Id = Id, Kind = Kind, X = X, Y = Y, Width = Width, Height = Height, Text = Text, Color = Color, Size = Size, Bold = Bold, Serif = Serif, Numbered = Numbered, Locked = Locked, GroupId = GroupId, AssetId = AssetId, Points = Points.ToList(),
        Fill = Fill, Rotation = Rotation, CropX = CropX, CropY = CropY, CropWidth = CropWidth, CropHeight = CropHeight, Step = Step };
    public bool IsCropped => CropX != 0 || CropY != 0 || CropWidth != 1 || CropHeight != 1;
    public static bool IsStroke(string kind) => kind is "pen" or "arrow" or "marker" or "rect" or "ellipse";
    public static bool IsTextual(string kind) => kind is "text" or "callout" or "timeline";
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
    public const int MaxJsonSize = 20 * 1024 * 1024;
    // Images are stored uncompressed; together with the lesson data they must stay below MaxFileSize,
    // otherwise Save would produce a file that Load rejects.
    public const long MaxAssetBytes = MaxFileSize - MaxJsonSize;
    public const int WorkingImageWidth = 3600;
    public const string BackupExtension = ".bak";
    public const int CurrentVersion = 3;
    public static readonly string[] Kinds = ["text", "image", "pen", "arrow", "marker", "rect", "ellipse", "callout", "timeline"];

    // Version 3 adds shapes, markers, callouts, timelines, image crop/rotation and reveal steps.
    // Lessons that use none of them are still written as version 2 so that 1.2 can open them.
    public static int RequiredVersion(Lesson lesson) =>
        lesson.Items.Any(i => i.Kind is "marker" or "rect" or "ellipse" or "callout" or "timeline" || i.Fill != null || i.Rotation != 0 || i.IsCropped || i.Step != 0) ? 3 : 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BackupPath(string path) => path + BackupExtension;

    public static long AssetBytes(Lesson lesson, IReadOnlyDictionary<string, byte[]> assets) =>
        lesson.Items.Where(i => i.AssetId != null).Select(i => i.AssetId!).Distinct().Sum(id => assets.TryGetValue(id, out var bytes) ? (long)bytes.Length : 0);

    public static string Megabytes(long bytes) => $"{Math.Ceiling(bytes / (1024.0 * 1024.0)):0} МБ";

    public static void Save(string path, Lesson lesson, IReadOnlyDictionary<string, byte[]> assets, bool keepBackup = false)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        lesson.Version = RequiredVersion(lesson);
        var json = JsonSerializer.SerializeToUtf8Bytes(lesson, JsonOptions);
        if (json.Length > MaxJsonSize) throw new InvalidDataException("Слишком много данных на доске. Удалите часть пометок или разделите урок на несколько файлов.");
        var ids = lesson.Items.Where(i => i.AssetId != null).Select(i => i.AssetId!).Distinct().ToArray();
        foreach (var id in ids) if (!assets.ContainsKey(id)) throw new InvalidDataException("Не найдена карта урока.");
        var assetBytes = AssetBytes(lesson, assets);
        if (assetBytes > MaxAssetBytes)
            throw new InvalidDataException($"Изображения урока занимают {Megabytes(assetBytes)}, а в одном файле помещается до {Megabytes(MaxAssetBytes)}. Удалите или уменьшите часть карт.");
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    using (var stream = zip.CreateEntry("lesson.json", CompressionLevel.Fastest).Open())
                        stream.Write(json);
                    foreach (var id in ids)
                    {
                        using var stream = zip.CreateEntry("assets/" + id, CompressionLevel.NoCompression).Open();
                        stream.Write(assets[id]);
                    }
                }
                // Make sure the data reaches the disk before it replaces the previous file:
                // a power cut in the classroom must leave either the old or the new lesson intact.
                file.Flush(true);
            }
            Replace(temporary, path, keepBackup ? BackupPath(path) : null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Replace(string temporary, string path, string? backup)
    {
        if (backup == null || !File.Exists(path)) { File.Move(temporary, path, true); return; }
        try { File.Replace(temporary, path, backup, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some removable and network drives do not support ReplaceFile.
            File.Copy(path, backup, true);
            File.Move(temporary, path, true);
        }
    }

    public static (Lesson Lesson, Dictionary<string, byte[]> Assets) Load(string path)
    {
        if (new FileInfo(path).Length > MaxFileSize) throw new InvalidDataException("Файл слишком большой. Максимум — 150 МБ.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Sum(e => e.Length) > MaxFileSize) throw new InvalidDataException("Распакованный урок превышает 150 МБ.");
        var entry = zip.GetEntry("lesson.json") ?? throw new InvalidDataException("Это не файл урока.");
        if (entry.Length > MaxJsonSize) throw new InvalidDataException("Слишком много данных на доске.");
        Lesson lesson;
        using (var stream = entry.Open()) lesson = JsonSerializer.Deserialize<Lesson>(stream) ?? throw new InvalidDataException("Пустой файл.");
        Validate(lesson);
        if (lesson.Version == 1) lesson.View = null;
        lesson.Version = Math.Max(2, lesson.Version);
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
        if (lesson.Version is < 1 or > CurrentVersion) throw new InvalidDataException("Эта версия урока не поддерживается.");
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
            if (i == null || string.IsNullOrEmpty(i.Id) || !ids.Add(i.Id) || !Kinds.Contains(i.Kind) || i.Text == null || i.Text.Length > 100000 || i.Points == null || i.Points.Count > 100000 ||
                !Coordinate(i.X) || !Coordinate(i.Y) || new[] { i.Width, i.Height, i.Size }.Any(n => !double.IsFinite(n) || Math.Abs(n) > 100000) || i.Size < 1 || i.Size > 300 || i.Width <= 0 || i.Height <= 0 ||
                i.Points.Any(p => !Coordinate(p.X) || !Coordinate(p.Y)) ||
                i.GroupId is { } groupId && (groupId.Length != 32 || groupId.Any(c => !Uri.IsHexDigit(c))) ||
                i.AssetId is { } assetId && (assetId.Length != 32 || assetId.Any(c => !Uri.IsHexDigit(c))))
                throw new InvalidDataException("Урок содержит некорректный объект.");
            try { ColorConverter.ConvertFromString(i.Color); } catch { throw new InvalidDataException("Некорректный цвет."); }
            if (i.Kind == "image" && i.AssetId == null) throw new InvalidDataException("Не указана карта.");
            if (i.Fill != null) { if (i.Fill.Length > 9) throw new InvalidDataException("Некорректная заливка."); try { ColorConverter.ConvertFromString(i.Fill); } catch { throw new InvalidDataException("Некорректная заливка."); } }
            if (i.Rotation is not (0 or 90 or 180 or 270) || i.Step is < 0 or > 99 ||
                new[] { i.CropX, i.CropY, i.CropWidth, i.CropHeight }.Any(n => !double.IsFinite(n) || n < 0 || n > 1) || i.CropWidth < .01 || i.CropHeight < .01 || i.CropX + i.CropWidth > 1.000001 || i.CropY + i.CropHeight > 1.000001)
                throw new InvalidDataException("Урок содержит некорректный объект.");
            if (i.Kind == "callout" && i.Points.Count != 1) throw new InvalidDataException("У выноски нет указателя.");
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
        if (frame.PixelWidth > WorkingImageWidth) image.DecodePixelWidth = WorkingImageWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // The board never shows more than WorkingImageWidth pixels of a map, so storing the full
    // original only wastes space in the lesson file. Re-encode large images at working size and
    // keep whichever version is smaller.
    public static byte[] CompactImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= WorkingImageWidth && bytes.Length <= 12 * 1024 * 1024) return bytes;
        var image = DecodeImage(bytes);
        BitmapEncoder encoder = decoder is JpegBitmapDecoder ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.Length < bytes.Length ? output.ToArray() : bytes;
    }
}
