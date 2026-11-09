using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace HistoryBoard;

[Flags]
public enum ShortcutContext { Editor = 1, Presentation = 2, Both = 3 }
public enum BoardCommand
{
    New, Open, Library, Save, SaveAs, AddImage, Undo, Redo, Copy, Paste, Duplicate, SelectAll,
    Group, Ungroup, ToggleLock, Delete, Bold, Fit, AddFrame, Present, Fullscreen, Settings,
    SelectTool, HandTool, TextTool, ListTool, PenTool, ArrowTool, EraserTool,
    NextFrame, PreviousFrame, PresentationNext, PresentationPrevious, PresentationFirst, PresentationLast,
    ResetZoom, MarkerTool, RectangleTool, EllipseTool, CalloutTool, AddTimeline, PresentationPointer, PresentationSpotlight, PresentationCurtain
}

public readonly record struct ShortcutGesture(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public override string ToString()
    {
        var key = Key switch
        {
            Key.Return => "Enter", Key.Space => "Пробел", Key.Back => "Backspace", Key.Next => "PageDown", Key.Prior => "PageUp",
            Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemQuestion => "/", Key.OemSemicolon => ";",
            >= Key.D0 and <= Key.D9 => ((int)Key - (int)System.Windows.Input.Key.D0).ToString(),
            _ => Key.ToString()
        };
        return (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
            (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + key;
    }
}

public sealed record ShortcutDefinition(BoardCommand Command, string Name, string Category, ShortcutContext Context, bool InText, params ShortcutGesture[] Defaults);

public sealed class ShortcutProfile
{
    private readonly Dictionary<BoardCommand, ShortcutGesture[]> bindings = [];
    public string? Warning { get; private set; }
    private static ShortcutGesture Ctrl(Key key) => new(key, ModifierKeys.Control);
    private static ShortcutGesture CtrlShift(Key key) => new(key, ModifierKeys.Control | ModifierKeys.Shift);
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } = new ShortcutDefinition[]
    {
        new(BoardCommand.New, "Новый урок", "Уроки", ShortcutContext.Editor, false, Ctrl(Key.N)),
        new(BoardCommand.Open, "Открыть файл урока", "Уроки", ShortcutContext.Editor, false, Ctrl(Key.O)),
        new(BoardCommand.Library, "Библиотека уроков", "Уроки", ShortcutContext.Editor, false, CtrlShift(Key.O)),
        new(BoardCommand.Save, "Сохранить урок", "Уроки", ShortcutContext.Editor, true, Ctrl(Key.S)),
        new(BoardCommand.SaveAs, "Сохранить копию", "Уроки", ShortcutContext.Editor, true, CtrlShift(Key.S)),
        new(BoardCommand.AddImage, "Добавить изображение", "Уроки", ShortcutContext.Editor, false, Ctrl(Key.M)),
        new(BoardCommand.Undo, "Отменить", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.Z)),
        new(BoardCommand.Redo, "Повторить", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.Y)),
        new(BoardCommand.Copy, "Копировать объекты", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.C)),
        new(BoardCommand.Paste, "Вставить изображение / объекты", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.V)),
        new(BoardCommand.Duplicate, "Дублировать", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.D)),
        new(BoardCommand.SelectAll, "Выделить всё", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.A)),
        new(BoardCommand.Group, "Группировать", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.G)),
        new(BoardCommand.Ungroup, "Разгруппировать", "Редактирование", ShortcutContext.Editor, false, CtrlShift(Key.G)),
        new(BoardCommand.ToggleLock, "Закрепить / открепить", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.L)),
        new(BoardCommand.Delete, "Удалить выделение", "Редактирование", ShortcutContext.Editor, false, new ShortcutGesture(Key.Delete)),
        new(BoardCommand.Bold, "Полужирный", "Редактирование", ShortcutContext.Editor, false, Ctrl(Key.B)),
        new(BoardCommand.Fit, "Показать все объекты", "Доска и кадры", ShortcutContext.Editor, false, Ctrl(Key.D0)),
        new(BoardCommand.ResetZoom, "Обычный масштаб 100%", "Доска и кадры", ShortcutContext.Editor, false, Ctrl(Key.D1), Ctrl(Key.NumPad1)),
        new(BoardCommand.AddFrame, "Добавить кадр", "Доска и кадры", ShortcutContext.Editor, false, Ctrl(Key.Return)),
        new(BoardCommand.NextFrame, "Следующий кадр в редакторе", "Доска и кадры", ShortcutContext.Editor, false, new ShortcutGesture(Key.Next)),
        new(BoardCommand.PreviousFrame, "Предыдущий кадр в редакторе", "Доска и кадры", ShortcutContext.Editor, false, new ShortcutGesture(Key.Prior)),
        new(BoardCommand.SelectTool, "Выбор", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.V)),
        new(BoardCommand.HandTool, "Рука", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.H)),
        new(BoardCommand.TextTool, "Текст", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.T)),
        new(BoardCommand.ListTool, "Список", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.L)),
        new(BoardCommand.PenTool, "Карандаш", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.P)),
        new(BoardCommand.ArrowTool, "Стрелка", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.A)),
        new(BoardCommand.EraserTool, "Ластик", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.E)),
        new(BoardCommand.MarkerTool, "Маркер-выделитель", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.M)),
        new(BoardCommand.RectangleTool, "Прямоугольник", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.R)),
        new(BoardCommand.EllipseTool, "Овал", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.O)),
        new(BoardCommand.CalloutTool, "Выноска", "Инструменты", ShortcutContext.Editor, false, new ShortcutGesture(Key.B)),
        new(BoardCommand.AddTimeline, "Добавить ленту времени", "Уроки", ShortcutContext.Editor, false, Ctrl(Key.T)),
        new(BoardCommand.Present, "Начать / завершить показ", "Показ", ShortcutContext.Both, false, new ShortcutGesture(Key.F5)),
        new(BoardCommand.PresentationNext, "Следующий кадр в показе", "Показ", ShortcutContext.Presentation, false, new ShortcutGesture(Key.Right), new ShortcutGesture(Key.Next), new ShortcutGesture(Key.Space)),
        new(BoardCommand.PresentationPrevious, "Предыдущий кадр в показе", "Показ", ShortcutContext.Presentation, false, new ShortcutGesture(Key.Left), new ShortcutGesture(Key.Prior), new ShortcutGesture(Key.Back)),
        new(BoardCommand.PresentationFirst, "Первый кадр в показе", "Показ", ShortcutContext.Presentation, false, new ShortcutGesture(Key.Home)),
        new(BoardCommand.PresentationLast, "Последний кадр в показе", "Показ", ShortcutContext.Presentation, false, new ShortcutGesture(Key.End)),
        new(BoardCommand.PresentationPointer, "Указка", "Показ", ShortcutContext.Presentation, false, new ShortcutGesture(Key.P)),
        new(BoardCommand.PresentationSpotlight, "Прожектор", "Показ", ShortcutContext.Presentation, false, new ShortcutGesture(Key.S)),
        new(BoardCommand.PresentationCurtain, "Шторка", "Показ", ShortcutContext.Presentation, false, new ShortcutGesture(Key.B)),
        new(BoardCommand.Fullscreen, "Полный экран", "Общие", ShortcutContext.Both, true, new ShortcutGesture(Key.F11)),
        new(BoardCommand.Settings, "Настройки", "Общие", ShortcutContext.Editor, true, Ctrl(Key.OemComma))
    };

    public static ShortcutProfile Default()
    {
        var profile = new ShortcutProfile();
        foreach (var definition in Definitions) profile.bindings[definition.Command] = definition.Defaults.ToArray();
        return profile;
    }
    public ShortcutProfile Clone()
    {
        var profile = new ShortcutProfile();
        foreach (var pair in bindings) profile.bindings[pair.Key] = pair.Value.ToArray();
        return profile;
    }
    public IReadOnlyList<ShortcutGesture> Get(BoardCommand command) => bindings[command];
    public string Display(BoardCommand command) => bindings[command].Length == 0 ? "Не назначено" : string.Join(" / ", bindings[command].Select(g => g.ToString()));

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    private static string? Invalid(ShortcutDefinition definition, ShortcutGesture gesture)
    {
        if (!Enum.IsDefined(gesture.Key) || gesture.Key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed || IsModifier(gesture.Key)) return "Выберите клавишу вместе с модификаторами, а не только Ctrl, Alt или Shift.";
        if ((gesture.Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) != 0) return "Сочетания с Windows недоступны приложению.";
        if (gesture.Key is Key.Escape or Key.Tab || gesture.Key == Key.F4 && gesture.Modifiers.HasFlag(ModifierKeys.Alt) || gesture.Key == Key.Delete && gesture.Modifiers.HasFlag(ModifierKeys.Control) && gesture.Modifiers.HasFlag(ModifierKeys.Alt))
            return "Это сочетание используется для выхода, навигации или управления Windows.";
        if (gesture.Key == Key.Space && gesture.Modifiers == ModifierKeys.None && definition.Context.HasFlag(ShortcutContext.Editor)) return "Пробел используется для перемещения доски мышью.";
        return null;
    }
    public string? Assign(BoardCommand command, params ShortcutGesture[] gestures)
    {
        var definition = Definitions.Single(d => d.Command == command);
        foreach (var gesture in gestures)
        {
            if (Invalid(definition, gesture) is { } error) return error;
            var conflict = Definitions.FirstOrDefault(d => d.Command != command && (d.Context & definition.Context) != 0 && bindings[d.Command].Contains(gesture));
            if (conflict != null) return $"{gesture} уже назначено: «{conflict.Name}». Сначала измените или отключите это сочетание.";
        }
        bindings[command] = gestures.Distinct().ToArray(); return null;
    }

    public BoardCommand? Resolve(Key key, ModifierKeys modifiers, ShortcutContext context, bool textInput)
    {
        var gesture = new ShortcutGesture(key, modifiers);
        foreach (var definition in Definitions)
        {
            if ((definition.Context & context) == 0 || !bindings[definition.Command].Contains(gesture)) continue;
            if (textInput)
            {
                if (!definition.InText || (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && key is not (>= Key.F1 and <= Key.F24)) continue;
                if (modifiers.HasFlag(ModifierKeys.Control) && (modifiers.HasFlag(ModifierKeys.Alt) || key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y)) continue;
                if (key is Key.Back or Key.Delete or Key.Insert or Key.Return or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Prior or Key.Next) continue;
            }
            return definition.Command;
        }
        return null;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(bindings.ToDictionary(p => p.Key.ToString(), p => p.Value)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ShortcutProfile Load(string path)
    {
        var profile = Default();
        try
        {
            if (!File.Exists(path)) return profile;
            if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<Dictionary<string, ShortcutGesture[]>>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            foreach (var definition in Definitions)
                if (saved.TryGetValue(definition.Command.ToString(), out var gestures))
                {
                    if (gestures == null || gestures.Length > 8 || gestures.Any(g => Invalid(definition, g) != null)) throw new InvalidDataException();
                    profile.bindings[definition.Command] = gestures.Distinct().ToArray();
                }
            foreach (var definition in Definitions)
                if (profile.Assign(definition.Command, profile.bindings[definition.Command]) != null) throw new InvalidDataException();
            return profile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            profile = Default(); profile.Warning = "Настройки клавиш не прочитаны. Сейчас действуют стандартные сочетания."; return profile;
        }
    }
}
