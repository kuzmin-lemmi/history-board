using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunShortcutFeatureTests(string directory, Action<bool, string> check)
    {
        var profile = ShortcutProfile.Default();
        check(ShortcutProfile.Definitions.All(d => d.Defaults.All(g => new[] { ShortcutContext.Editor, ShortcutContext.Presentation }.Where(c => (d.Context & c) != 0).All(c => profile.Resolve(g.Key, g.Modifiers, c, false) == d.Command))), "Every default shortcut resolves to its command in the applicable context");
        var ctrlK = new ShortcutGesture(Key.K, ModifierKeys.Control);
        check(profile.Assign(BoardCommand.Copy, ctrlK) == null && profile.Resolve(Key.K, ModifierKeys.Control, ShortcutContext.Editor, false) == BoardCommand.Copy && profile.Resolve(Key.C, ModifierKeys.Control, ShortcutContext.Editor, false) == null, "Rebinding replaces the old shortcut and activates the new one");
        check(profile.Assign(BoardCommand.Paste, ctrlK) != null && profile.Get(BoardCommand.Paste).Single().Key == Key.V, "Conflicting assignments are rejected without changing existing bindings");
        check(profile.Assign(BoardCommand.PresentationNext, ctrlK) == null && profile.Assign(BoardCommand.Fullscreen, ctrlK) != null, "Different modes can share shortcuts while global conflicts are detected");
        check(profile.Resolve(Key.K, ModifierKeys.Control | ModifierKeys.Shift, ShortcutContext.Editor, false) == null, "Shortcut matching requires the exact modifier set");
        check(profile.Assign(BoardCommand.Copy, new ShortcutGesture(Key.Escape)) != null && profile.Assign(BoardCommand.Copy, new ShortcutGesture(Key.Space)) != null && profile.Assign(BoardCommand.Copy, new ShortcutGesture(Key.F4, ModifierKeys.Alt)) != null, "Escape, pan-space and Windows close combinations stay reserved");
        check(profile.Assign(BoardCommand.Copy, new ShortcutGesture(Key.LeftCtrl)) != null && profile.Assign(BoardCommand.Copy, new ShortcutGesture(Key.K, ModifierKeys.Windows)) != null, "Modifier-only and Windows-key shortcuts cannot be assigned");
        profile.Assign(BoardCommand.Copy);
        check(profile.Get(BoardCommand.Copy).Count == 0, "A shortcut can be disabled independently of its command");
        var path = Path.Combine(directory, "shortcut-tests", Guid.NewGuid().ToString("N"), "shortcuts.json"); profile.Save(path);
        var loaded = ShortcutProfile.Load(path);
        check(loaded.Get(BoardCommand.Copy).Count == 0 && loaded.Get(BoardCommand.PresentationNext).Single() == ctrlK, "Custom and disabled bindings survive save and reload");
        File.WriteAllText(path, "broken json"); loaded = ShortcutProfile.Load(path);
        check(loaded.Warning != null && loaded.Get(BoardCommand.Copy).Single().Key == Key.C, "Corrupted settings fall back to defaults with a visible warning");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, ShortcutGesture[]> { ["Copy"] = [new(Key.S, ModifierKeys.Control)] }));
        check(ShortcutProfile.Load(path).Warning != null, "Conflicting on-disk settings cannot bypass validation");
        profile = ShortcutProfile.Default();
        check(profile.Resolve(Key.V, ModifierKeys.Control, ShortcutContext.Editor, true) == null && profile.Resolve(Key.T, ModifierKeys.None, ShortcutContext.Editor, true) == null && profile.Resolve(Key.S, ModifierKeys.Control, ShortcutContext.Editor, true) == BoardCommand.Save, "Text entry keeps ordinary clipboard and letter keys while allowing save");
        profile.Assign(BoardCommand.Save, new ShortcutGesture(Key.Q));
        check(profile.Resolve(Key.Q, ModifierKeys.None, ShortcutContext.Editor, true) == null, "An unmodified save-letter never interrupts typing");
        profile.Assign(BoardCommand.Save, new ShortcutGesture(Key.Q, ModifierKeys.Shift));
        check(profile.Resolve(Key.Q, ModifierKeys.Shift, ShortcutContext.Editor, true) == null && profile.Resolve(Key.Q, ModifierKeys.Shift, ShortcutContext.Editor, false) == BoardCommand.Save, "Shift-only uppercase shortcuts stay available on the board without interrupting typed capital letters");
        profile.Assign(BoardCommand.Save, new ShortcutGesture(Key.OemQuestion, ModifierKeys.Shift));
        check(profile.Resolve(Key.OemQuestion, ModifierKeys.Shift, ShortcutContext.Editor, true) == null, "Shifted punctuation remains ordinary text inside an editor");
        profile.Assign(BoardCommand.Copy); profile.Assign(BoardCommand.Save, new ShortcutGesture(Key.C, ModifierKeys.Control));
        check(profile.Resolve(Key.C, ModifierKeys.Control, ShortcutContext.Editor, true) == null, "Native text copy remains intact even if another board action uses Ctrl+C");

        var original = shortcuts;
        var originalLesson = lesson; var originalAssets = assets;
        try
        {
            shortcuts = ShortcutProfile.Default(); shortcuts.Assign(BoardCommand.PenTool, new ShortcutGesture(Key.F6)); ApplyShortcutHints();
            check(await HandleShortcutAsync(Key.F6, ModifierKeys.None) && tool == BoardTool.Pen, "The production key dispatcher executes a reassigned tool shortcut");
            SetTool(BoardTool.Select);
            check(!await HandleShortcutAsync(Key.P, ModifierKeys.None) && tool == BoardTool.Select, "A tool's old key no longer executes it after rebinding");
            check(!await HandleShortcutAsync(Key.F6, ModifierKeys.None, true) && tool == BoardTool.Select, "Reassigned tool commands do not run inside text input");
            check(toolButtons[BoardTool.Pen].ToolTip.ToString()!.Contains("F6"), "Toolbar hints show the current binding");
            lesson = new Lesson(); assets = []; Surface.SetLesson(lesson, assets);
            Surface.Add(new BoardItem { Text = "Проверка клавиш" }); Surface.Select(lesson.Items[0]);
            shortcuts.Assign(BoardCommand.Duplicate, new ShortcutGesture(Key.F7));
            await HandleShortcutAsync(Key.F7, ModifierKeys.None);
            check(lesson.Items.Count == 2, "A reassigned editing shortcut reaches the existing duplicate operation");
            shortcuts.Assign(BoardCommand.Undo, new ShortcutGesture(Key.F8)); await HandleShortcutAsync(Key.F8, ModifierKeys.None);
            check(lesson.Items.Count == 1, "A reassigned undo command reverses the complete editing operation");
            if (fullscreen) ToggleFullscreen();
            StartPresentation(); await HandleShortcutAsync(Key.F11, ModifierKeys.None); EndPresentation();
            check(!fullscreen, "Ending presentation restores a windowed editor after changing fullscreen during the show");
            ToggleFullscreen(); StartPresentation(); await HandleShortcutAsync(Key.F11, ModifierKeys.None); EndPresentation();
            check(fullscreen, "Ending presentation restores an originally fullscreen editor after changing the show window mode");
            ToggleFullscreen();
        }
        finally
        {
            shortcuts = original; lesson = originalLesson; assets = originalAssets; Surface.SetLesson(lesson, UsedAssets(lesson));
            RestoreCamera(); ApplyShortcutHints(); undo.Clear(); redo.Clear();
        }

        var settingsPath = Path.Combine(Path.GetDirectoryName(path)!, "window-settings.json");
        var current = ShortcutProfile.Default();
        var window = new SettingsWindow(current, settingsPath) { Owner = this }; window.Show(); window.UpdateLayout();
        window.BeginCapture(BoardCommand.PenTool);
        check(!window.TryCapture(new ShortcutGesture(Key.T)) && window.Draft.Get(BoardCommand.PenTool).Single().Key == Key.P, "Settings capture reports conflicts and preserves the previous assignment");
        check(window.TryCapture(new ShortcutGesture(Key.F6)) && current.Get(BoardCommand.PenTool).Single().Key == Key.P, "Settings edits remain isolated from the active profile before saving");
        window.BeginCapture(BoardCommand.Copy); window.TryCapture(new ShortcutGesture(Key.Escape));
        check(window.Draft.Get(BoardCommand.Copy).Single().Key == Key.C, "Escape cancels only the pending shortcut capture");
        check(window.SaveProfile() && ShortcutProfile.Load(settingsPath).Get(BoardCommand.PenTool).Single().Key == Key.F6, "Saving the settings window persists its edited draft");
        ((TextBox)window.FindName("SearchBox")).Text = "карандаш";
        check(((ItemsControl)window.FindName("CommandsList")).Items.Count == 1, "Settings search finds commands by Cyrillic name");
        ((TextBox)window.FindName("SearchBox")).Text = ""; window.UpdateLayout();
        CaptureSettingsWindow(window, Path.Combine(directory, "10-shortcut-settings.png"));
        window.Width = 700; window.Height = 540; window.UpdateLayout();
        CaptureSettingsWindow(window, Path.Combine(directory, "11-settings-compact.png")); window.Close();
        var cancelled = new SettingsWindow(current, settingsPath) { Owner = this }; cancelled.Show();
        cancelled.BeginCapture(BoardCommand.PenTool); cancelled.TryCapture(new ShortcutGesture(Key.F9)); cancelled.Close();
        check(cancelled.SavedProfile == null && ShortcutProfile.Load(settingsPath).Get(BoardCommand.PenTool).Single().Key == Key.F6, "Closing settings without saving leaves the stored profile unchanged");
    }

    private static void CaptureSettingsWindow(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
