using System.IO;
using System.Text.Json;

namespace HistoryBoard;

// Small personal settings, stored next to the autosave and separately from lessons and shortcuts.
public sealed class Preferences
{
    public bool ShowLibraryOnStart { get; set; } = true;
    // The only network request made without the user asking. It can be turned off in the update window.
    public bool CheckUpdatesOnStart { get; set; } = true;

    public static string FilePath => Path.Combine(App.DataDirectory, "preferences.json");

    public static Preferences Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 64 * 1024) return new();
            return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { return new(); } // a damaged file only resets these conveniences
    }

    public void Save()
    {
        Directory.CreateDirectory(App.DataDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }
}
