using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HistoryBoard;

public partial class LibraryWindow : Window
{
    private readonly LessonLibrary library;
    private List<LibraryLesson> entries = [];
    public string? SelectedPath { get; private set; }
    public bool NewLessonRequested { get; private set; }

    public LibraryWindow(LessonLibrary library)
    {
        this.library = library; InitializeComponent(); Reload();
        foreach (var demo in DemoLessons.All)
        {
            var button = new Button { Content = demo.Title, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 5, 10, 5), Background = System.Windows.Media.Brushes.White, ToolTip = "Готовый урок с лентой времени, кадрами, плашками и шагами появления" };
            button.Click += (_, _) => OpenExample(demo);
            Examples.Children.Add(button);
        }
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void Reload() { entries = library.Load(); ApplySearch(); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (LessonsList != null) ApplySearch(); }
    internal void ApplySearch(string? query = null)
    {
        var text = query ?? SearchBox.Text;
        var filtered = LessonLibrary.Search(entries, text).ToList();
        LessonsList.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = entries.Count == 0 ? "Здесь появятся ваши уроки" : "Ничего не найдено";
        EmptyDescription.Text = entries.Count == 0 ? "Откройте .hboard или сохраните новый урок. Он появится в библиотеке вместе с миниатюрой. Чтобы посмотреть, как выглядит готовый урок, откройте пример справа вверху." : "Попробуйте другое название или часть имени файла.";
        Summary.Text = library.Warning ?? $"Недавние уроки: {filtered.Count} · Файлы хранятся на своих местах";
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenButton == null) return;
        OpenButton.IsEnabled = LessonsList.SelectedItem is LibraryLesson { Exists: true };
        ForgetButton.IsEnabled = LessonsList.SelectedItem is LibraryLesson;
    }
    private void OnOpenSelected(object sender, RoutedEventArgs e)
    {
        if (LessonsList.SelectedItem is not LibraryLesson entry) return;
        if (!entry.Exists) { Summary.Text = "Файл не найден. Нажмите «Открыть файл…», чтобы выбрать его новое расположение."; return; }
        SelectedPath = entry.Path; DialogResult = true;
    }
    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Открыть урок", Filter = "Урок на доске (*.hboard)|*.hboard" };
        if (dialog.ShowDialog(this) == true) { SelectedPath = dialog.FileName; DialogResult = true; }
    }
    internal void OpenExample(DemoLessons.Demo demo)
    {
        try { SelectedPath = DemoLessons.EnsureFile(demo); DialogResult = true; }
        catch (Exception ex) { Summary.Text = "Не удалось подготовить пример: " + ex.Message; }
    }
    private void OnNewLesson(object sender, RoutedEventArgs e) { NewLessonRequested = true; DialogResult = true; }
    private void OnReturn(object sender, RoutedEventArgs e) => Close();
    private void OnForget(object sender, RoutedEventArgs e)
    {
        if (LessonsList.SelectedItem is not LibraryLesson entry) return;
        try { library.Forget(entry.Path); Reload(); }
        catch (Exception ex) { Summary.Text = "Не удалось обновить список: " + ex.Message; }
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { OnOpenSelected(sender, e); e.Handled = true; }
    }
}
