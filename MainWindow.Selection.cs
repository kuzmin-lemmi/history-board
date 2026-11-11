using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HistoryBoard;

public partial class MainWindow
{
    private bool selectingArea;
    private BoardItem[] marqueeStartSelection = [];
    private Dictionary<string, BoardItem> moveOriginals = [];

    private void SelectArea(Rect area)
    {
        Surface.SelectMany(marqueeStartSelection.Concat(lesson.Items.Where(i => area.IntersectsWith(Surface.Bounds(i)))));
        Surface.ShowMarquee(area); UpdateSelectionUi();
    }

    private void MoveSelection(Vector delta)
    {
        if (Surface.SelectionLocked) return;
        foreach (var item in Surface.SelectedItems)
        {
            if (!moveOriginals.TryGetValue(item.Id, out var original)) continue;
            item.X = original.X + delta.X; item.Y = original.Y + delta.Y;
        }
        Surface.RefreshMany(Surface.SelectedItems);
    }

    private void GroupSelection(bool ungroup = false)
    {
        CommitEditor(); FinishGesture();
        var items = Surface.SelectedItems.ToArray();
        if (Surface.SelectionLocked || items.Length < (ungroup ? 1 : 2)) return;
        if (ungroup && items.All(i => i.GroupId == null)) return;
        var before = lesson.Clone(); var id = ungroup ? null : Guid.NewGuid().ToString("N");
        foreach (var item in items) item.GroupId = id;
        Surface.SelectMany(items); Changed(before);
        Status.Text = ungroup ? "● Группа разобрана" : "● Объекты объединены в группу";
    }

    private void OnSelectionActions(object sender, RoutedEventArgs e)
    {
        CommitEditor(); FinishGesture();
        var menu = new ContextMenu();
        void Add(string title, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = title, IsEnabled = enabled };
            entry.Click += (_, _) => action(); menu.Items.Add(entry);
        }
        var count = Surface.SelectedItems.Count;
        Add("Копировать    " + shortcuts.Display(BoardCommand.Copy), CopySelection, count > 0);
        Add("Вставить    " + shortcuts.Display(BoardCommand.Paste), () => _ = PasteClipboardAsync());
        Add("Дублировать    " + shortcuts.Display(BoardCommand.Duplicate), DuplicateSelection, count > 0);
        menu.Items.Add(new Separator());
        Add("Группировать    " + shortcuts.Display(BoardCommand.Group), () => GroupSelection(), count > 1 && !Surface.SelectionLocked);
        Add("Разгруппировать    " + shortcuts.Display(BoardCommand.Ungroup), () => GroupSelection(true), Surface.SelectedItems.Any(i => i.GroupId != null) && !Surface.SelectionLocked);
        Add("Выделить всё    " + shortcuts.Display(BoardCommand.SelectAll), () => { SetTool(BoardTool.Select); Surface.SelectMany(lesson.Items); UpdateUi(); });
        menu.Items.Add(new Separator());
        Add("Закрыть плашкой    " + shortcuts.Display(BoardCommand.CoverSelection), () => CoverSelection(), count > 0);
        menu.Items.Add(StepsMenu());
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
}
