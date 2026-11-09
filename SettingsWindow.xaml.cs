using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HistoryBoard;

public partial class SettingsWindow : Window
{
    private ShortcutProfile draft;
    private readonly string settingsPath;
    private BoardCommand? capturing;
    public ShortcutProfile? SavedProfile { get; private set; }
    internal ShortcutProfile Draft => draft;

    public SettingsWindow(ShortcutProfile current, string settingsPath)
    {
        draft = current.Clone(); this.settingsPath = settingsPath;
        InitializeComponent(); RefreshRows();
        if (current.Warning != null) Status.Text = current.Warning;
    }
    private void RefreshRows()
    {
        if (CommandsList == null) return;
        var query = SearchBox.Text.Trim();
        var rows = ShortcutProfile.Definitions.Where(d => d.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || d.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(d => new { d.Command, d.Name, d.Category, Keys = capturing == d.Command ? "Нажмите сочетание…" : draft.Display(d.Command), Background = capturing == d.Command ? "#E8EDDF" : "#F5F5EC" }).ToArray();
        CommandsList.ItemsSource = rows; EmptyMessage.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnSearch(object sender, TextChangedEventArgs e) { capturing = null; RefreshRows(); }
    internal void BeginCapture(BoardCommand command)
    {
        capturing = command; RefreshRows();
        Status.Text = "Нажмите новое сочетание. Esc — отменить назначение.";
        Focus(); Keyboard.Focus(this);
    }
    internal bool TryCapture(ShortcutGesture gesture)
    {
        if (capturing is not { } command) return false;
        if (gesture.Key == Key.Escape) { capturing = null; RefreshRows(); Status.Text = "Назначение отменено."; return false; }
        if (ShortcutProfile.IsModifier(gesture.Key)) return false;
        if (draft.Assign(command, gesture) is { } error) { Status.Text = error; return false; }
        capturing = null; RefreshRows(); Status.Text = "Сочетание изменено. Нажмите «Сохранить», чтобы применить."; return true;
    }
    private void OnCapture(object sender, RoutedEventArgs e) => BeginCapture((BoardCommand)((Button)sender).Tag);
    private void OnClear(object sender, RoutedEventArgs e)
    {
        capturing = null; draft.Assign((BoardCommand)((Button)sender).Tag); RefreshRows();
        Status.Text = "Сочетание отключено. Сама команда остаётся доступной через интерфейс.";
    }
    private void OnReset(object sender, RoutedEventArgs e)
    {
        capturing = null; draft = ShortcutProfile.Default(); RefreshRows(); Status.Text = "Восстановлены стандартные сочетания. Нажмите «Сохранить», чтобы применить.";
    }
    internal bool SaveProfile()
    {
        try { draft.Save(settingsPath); SavedProfile = draft.Clone(); return true; }
        catch (Exception ex) { Status.Text = "Настройки не сохранены: " + ex.Message; return false; }
    }
    private void OnSave(object sender, RoutedEventArgs e) { if (SaveProfile()) DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (capturing != null) { e.Handled = true; TryCapture(new ShortcutGesture(key, Keyboard.Modifiers)); }
        else if (key == Key.Escape) { e.Handled = true; Close(); }
    }
}
