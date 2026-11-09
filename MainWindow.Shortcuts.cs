using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HistoryBoard;

public partial class MainWindow
{
    private ShortcutProfile shortcuts = ShortcutProfile.Default();
    private string SettingsPath => Path.Combine(App.DataDirectory, "shortcuts.json");

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        CommitEditor(); FinishGesture(); EndPan();
        var window = new SettingsWindow(shortcuts, SettingsPath) { Owner = this };
        if (window.ShowDialog() == true && window.SavedProfile is { } profile)
        {
            shortcuts = profile; ApplyShortcutHints(); Status.Text = "● Горячие клавиши сохранены";
        }
    }

    private void ApplyShortcutHints()
    {
        foreach (var definition in ShortcutProfile.Definitions)
        {
            var button = definition.Command switch
            {
                BoardCommand.New => NewLessonButton, BoardCommand.Open => null, BoardCommand.Library => LibraryButton,
                BoardCommand.Save => SaveButton, BoardCommand.AddImage => AddImageButton,
                BoardCommand.Undo => UndoButton, BoardCommand.Redo => RedoButton, BoardCommand.Bold => BoldButton,
                BoardCommand.ToggleLock => LockButton, BoardCommand.Delete => DeleteButton,
                BoardCommand.Present => PresentButton, BoardCommand.Fullscreen => FullscreenButton,
                BoardCommand.Fit => FitButton, BoardCommand.AddFrame => AddFrameButton, BoardCommand.ResetZoom => ZoomResetButton,
                BoardCommand.NextFrame => NextFrameButton, BoardCommand.PreviousFrame => PreviousFrameButton,
                BoardCommand.Settings => SettingsButton, BoardCommand.AddTimeline => TimelineButton,
                BoardCommand.PresentationPointer => PointerButton, BoardCommand.PresentationSpotlight => SpotlightButton, BoardCommand.PresentationCurtain => CurtainButton, _ => null
            };
            if (button != null) button.ToolTip = definition.Name + " · " + shortcuts.Display(definition.Command);
        }
        foreach (var (tool, command) in ToolCommands)
            toolButtons[tool].ToolTip = ShortcutProfile.Definitions.Single(d => d.Command == command).Name + " · " + shortcuts.Display(command);
    }

    private static readonly (BoardTool Tool, BoardCommand Command)[] ToolCommands =
    [ (BoardTool.Select, BoardCommand.SelectTool), (BoardTool.Hand, BoardCommand.HandTool), (BoardTool.Text, BoardCommand.TextTool),
      (BoardTool.List, BoardCommand.ListTool), (BoardTool.Pen, BoardCommand.PenTool), (BoardTool.Arrow, BoardCommand.ArrowTool), (BoardTool.Eraser, BoardCommand.EraserTool),
      (BoardTool.Marker, BoardCommand.MarkerTool), (BoardTool.Rectangle, BoardCommand.RectangleTool), (BoardTool.Ellipse, BoardCommand.EllipseTool), (BoardTool.Callout, BoardCommand.CalloutTool) ];

    private void ShowShortcutHelp()
    {
        BoardCommand[] commands = [BoardCommand.Save, BoardCommand.Open, BoardCommand.Undo, BoardCommand.Redo, BoardCommand.Copy, BoardCommand.Paste, BoardCommand.Duplicate, BoardCommand.Group, BoardCommand.Ungroup, BoardCommand.Present, BoardCommand.Fullscreen];
        var keys = string.Join("\n", commands.Select(c => ShortcutProfile.Definitions.Single(d => d.Command == c).Name + " — " + shortcuts.Display(c)));
        MessageBox.Show(this, "ВАШИ ГОРЯЧИЕ КЛАВИШИ\n" + keys +
            "\n\nКнопка ⚙ открывает настройки всех сочетаний. В текстовых полях работают обычные команды ввода.\n\nДОСКА\nПробел + мышь, средняя кнопка или «Рука» перемещают поле. Ctrl + колёсико меняет масштаб. Рамка инструментом «Выбор» и Shift+клик выделяют несколько объектов. Двойной клик редактирует текст; Enter завершает надпись, Shift+Enter — новая строка. Выделение тянется за любое место рамки. Ctrl+1 — обычный масштаб 100%.\n\nКАДРЫ\nКадр — слайд презентации: рамка на доске показывает его границы. «＋ Кадр» сохраняет текущий вид, «↻ Обновить кадр» — новый вид для кадра, F5 — показ.\n\nИСТОРИЯ НА ДОСКЕ\n«＋ Лента» — лента времени из строк «дата — событие». Прямоугольник, овал и «Заливка» выделяют районы карты, маркер подчёркивает, выноска подписывает место. У выделенной карты — «Кадрировать» и «Повернуть».\n\nУРОКИ И ПОКАЗ\n«Уроки» открывает библиотеку с поиском. «Фон» меняет бумагу. Кнопка «＋ Кадр» запоминает текущий вид. В показе: указка (P), прожектор (S), шторка (B); «Действия → Появление в показе» задаёт порядок появления объектов. Esc завершает ввод или показ; «Экспорт» сохраняет PNG или все кадры в PDF.",
            "Как пользоваться доской", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task<bool> HandleShortcutAsync(Key key, ModifierKeys modifiers, bool textInput = false)
    {
        var command = shortcuts.Resolve(key, modifiers, presenting ? ShortcutContext.Presentation : ShortcutContext.Editor, textInput);
        if (command == null) return false;
        switch (command)
        {
            case BoardCommand.New: OnNew(this, new RoutedEventArgs()); break;
            case BoardCommand.Open: OnOpen(this, new RoutedEventArgs()); break;
            case BoardCommand.Library: OnLibrary(this, new RoutedEventArgs()); break;
            case BoardCommand.Save: await SaveLessonAsync(); break;
            case BoardCommand.SaveAs: await SaveLessonAsync(true); break;
            case BoardCommand.AddImage: OnAddMap(this, new RoutedEventArgs()); break;
            case BoardCommand.Undo: Undo(); break;
            case BoardCommand.Redo: Redo(); break;
            case BoardCommand.Copy: CopySelection(); break;
            case BoardCommand.Paste: await PasteClipboardAsync(); break;
            case BoardCommand.Duplicate: DuplicateSelection(); break;
            case BoardCommand.SelectAll: SetTool(BoardTool.Select); Surface.SelectMany(lesson.Items); UpdateUi(); break;
            case BoardCommand.Group: GroupSelection(); break;
            case BoardCommand.Ungroup: GroupSelection(true); break;
            case BoardCommand.ToggleLock: ToggleLock(); break;
            case BoardCommand.Delete: DeleteSelected(); break;
            case BoardCommand.Bold: OnBold(this, new RoutedEventArgs()); break;
            case BoardCommand.Fit: Fit(); break;
            case BoardCommand.ResetZoom: ResetZoom(); break;
            case BoardCommand.AddFrame: AddFrame(); break;
            case BoardCommand.Present: if (presenting) EndPresentation(); else StartPresentation(); break;
            case BoardCommand.Fullscreen: ToggleFullscreen(); break;
            case BoardCommand.Settings: OnSettings(this, new RoutedEventArgs()); break;
            case BoardCommand.NextFrame: NextFrame(1); break;
            case BoardCommand.PreviousFrame: NextFrame(-1); break;
            case BoardCommand.PresentationNext: PresentStep(1); break;
            case BoardCommand.PresentationPrevious: PresentStep(-1); break;
            case BoardCommand.PresentationFirst: revealStep = 0; GoToFrame(0); break;
            case BoardCommand.PresentationLast: revealStep = 0; GoToFrame(lesson.Frames.Count - 1); break;
            case BoardCommand.AddTimeline: OnAddTimeline(this, new RoutedEventArgs()); break;
            case BoardCommand.PresentationPointer: TogglePresentationTool(PresentationTool.Pointer); break;
            case BoardCommand.PresentationSpotlight: TogglePresentationTool(PresentationTool.Spotlight); break;
            case BoardCommand.PresentationCurtain: TogglePresentationTool(PresentationTool.Curtain); break;
            default: SetTool(ToolCommands.Single(p => p.Command == command).Tool); break;
        }
        return true;
    }
}
