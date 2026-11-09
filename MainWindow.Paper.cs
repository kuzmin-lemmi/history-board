using System.Windows;
using System.Windows.Controls;

namespace HistoryBoard;

public partial class MainWindow
{
    private void SetPaper(string paper)
    {
        if (paper is not ("grid" or "dots" or "ruled" or "plain" or "white") || lesson.Paper == paper) return;
        CommitEditor(); FinishGesture();
        var before = lesson.Clone(); lesson.Paper = paper; Surface.InvalidateVisual(); Changed(before);
        Status.Text = "● Фон доски изменён и будет сохранён в уроке";
    }

    private void OnPaper(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        (string Id, string Name)[] papers = [("grid", "Мелкая клетка · тёплая бумага"), ("dots", "Точки · тёплая бумага"), ("ruled", "Линейка · тёплая бумага"), ("plain", "Тёплая бумага без узора"), ("white", "Белый лист")];
        foreach (var (id, name) in papers)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = lesson.Paper == id };
            item.Click += (_, _) => SetPaper(id); menu.Items.Add(item);
        }
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
}
