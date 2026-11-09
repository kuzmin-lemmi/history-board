using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public sealed class LibraryLesson
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset LastOpened { get; set; }
    public int ObjectCount { get; set; }
    public int FrameCount { get; set; }
    [JsonIgnore] public bool Exists => File.Exists(Path);
    [JsonIgnore] public string Availability => Exists ? $"{ObjectCount} объектов · {FrameCount} кадров" : "Файл не найден — возможно, он перенесён";
    [JsonIgnore] public string OpenedLabel => "Открыт " + LastOpened.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    [JsonIgnore] public string FileLabel => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string PreviewPath { get; set; } = "";
    [JsonIgnore] public BitmapSource? Preview
    {
        get
        {
            try
            {
                if (!File.Exists(PreviewPath)) return null;
                using var stream = File.OpenRead(PreviewPath);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
            catch { return null; }
        }
    }
}

public sealed class LessonLibrary
{
    private readonly string directory;
    private string IndexPath => System.IO.Path.Combine(directory, "library.json");
    public string? Warning { get; private set; }
    public LessonLibrary(string directory) => this.directory = directory;

    public string ThumbnailPath(string path)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())));
        return System.IO.Path.Combine(directory, "previews", id + ".png");
    }

    public List<LibraryLesson> Load()
    {
        Warning = null;
        try
        {
            if (!File.Exists(IndexPath)) return [];
            if (new FileInfo(IndexPath).Length > 1024 * 1024) throw new InvalidDataException("Индекс библиотеки слишком большой.");
            var entries = JsonSerializer.Deserialize<List<LibraryLesson>>(File.ReadAllText(IndexPath)) ?? [];
            var valid = new List<LibraryLesson>();
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Title)) continue;
                try
                {
                    entry.Path = System.IO.Path.GetFullPath(entry.Path);
                    entry.PreviewPath = ThumbnailPath(entry.Path); valid.Add(entry);
                }
                catch (ArgumentException) { }
            }
            return valid.DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).OrderByDescending(e => e.LastOpened).Take(60).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { Warning = "Не удалось прочитать список уроков. Сами файлы уроков не затронуты."; return []; }
    }

    public static IEnumerable<LibraryLesson> Search(IEnumerable<LibraryLesson> entries, string query)
    {
        query = query.Trim();
        return entries.Where(e => e.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || e.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    public void Remember(string path, Lesson lesson)
    {
        path = System.IO.Path.GetFullPath(path);
        var entries = Load();
        entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, new LibraryLesson { Path = path, Title = lesson.Title, LastOpened = DateTimeOffset.Now, ObjectCount = lesson.Items.Count, FrameCount = lesson.Frames.Count });
        Write(entries.Take(60));
    }

    public void Forget(string path)
    {
        var entries = Load(); entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)); Write(entries);
        var thumbnail = ThumbnailPath(path); if (File.Exists(thumbnail)) File.Delete(thumbnail);
    }

    private void Write(IEnumerable<LibraryLesson> entries)
    {
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, IndexPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
