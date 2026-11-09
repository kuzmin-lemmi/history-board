using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HistoryBoard;

public sealed class UpdateWindow : Window
{
    private readonly UpdateService service = new();
    private readonly Func<string, Task> install;
    private readonly Action<AppRelease?> releaseFound;
    private readonly TextBlock headline = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Text = "Проверяем обновления…", Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock summary = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = BoardSurface.Brush("#7B827C"), LineHeight = 20 };
    private readonly TextBlock notes = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = BoardSurface.Brush("#596951"), LineHeight = 20 };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 6, Margin = new Thickness(0, 18, 0, 10), Visibility = Visibility.Collapsed };
    private readonly TextBlock status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = BoardSurface.Brush("#7B827C"), Margin = new Thickness(0, 0, 0, 14) };
    private readonly Button primary = new() { Content = "Проверить ещё раз", Background = BoardSurface.Brush("#58725B"), Foreground = Brushes.White, Padding = new Thickness(17, 10, 17, 10), IsEnabled = false };
    private readonly Button cancel = new() { Content = "Закрыть", Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(8, 0, 0, 0) };
    private CancellationTokenSource? operation;
    private AppRelease? release;
    private string? downloaded;
    private bool busy, closed;

    public UpdateWindow(Window owner, Func<string, Task> install, Action<AppRelease?> releaseFound)
    {
        Owner = owner; this.install = install; this.releaseFound = releaseFound;
        Title = "Обновления — Доска истории"; Width = 560; Height = 510; MinWidth = 460; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = BoardSurface.Brush("#FAFBF7");
        var panel = new DockPanel { Margin = new Thickness(27) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = $"ДОСКА ИСТОРИИ  ·  ВЕРСИЯ {UpdateService.DisplayVersion}", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = BoardSurface.Brush("#58725B"), Margin = new Thickness(0, 0, 0, 18) });
        top.Children.Add(headline); top.Children.Add(summary); panel.Children.Add(top);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(progress); bottom.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(primary); buttons.Children.Add(cancel); bottom.Children.Add(buttons);
        var link = new Button { Content = "Открыть релизы на GitHub ↗", FontSize = 11, Foreground = BoardSurface.Brush("#7B827C"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 12, 0, 0) };
        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo((release?.Page ?? UpdateService.ReleasesPage).AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { status.Text = ex.Message; } };
        bottom.Children.Add(link); panel.Children.Add(bottom);
        panel.Children.Add(new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 20, 0, 12) });
        Content = panel;
        primary.Click += async (_, _) => await OnPrimaryAsync();
        cancel.Click += (_, _) => { if (busy) operation?.Cancel(); else Close(); };
        Loaded += async (_, _) => await CheckAsync();
        Closed += (_, _) => { closed = true; operation?.Cancel(); service.Dispose(); };
    }

    private void BeginOperation()
    {
        operation?.Dispose(); operation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        busy = true; primary.IsEnabled = false; cancel.Content = "Отменить";
    }
    private void EndOperation()
    {
        busy = false; if (closed) return;
        primary.IsEnabled = true; cancel.Content = "Закрыть";
    }

    private async Task CheckAsync()
    {
        if (busy || closed) return;
        BeginOperation(); downloaded = null; progress.Visibility = Visibility.Collapsed;
        headline.Text = "Проверяем обновления…"; summary.Text = "Ищем последний стабильный релиз на GitHub."; notes.Text = ""; status.Text = "";
        try
        {
            release = await service.CheckLatestAsync(operation!.Token);
            if (closed) return;
            releaseFound(release); ShowRelease();
        }
        catch (OperationCanceledException) { if (!closed) { headline.Text = "Проверка отменена"; summary.Text = "Можно повторить в любое время."; primary.Content = "Проверить ещё раз"; } }
        catch (Exception ex) { if (!closed) { release = null; headline.Text = "Не удалось проверить обновления"; summary.Text = "Проверьте подключение к интернету и попробуйте ещё раз."; status.Text = ex.Message; primary.Content = "Повторить проверку"; } }
        finally { EndOperation(); }
    }

    private void ShowRelease()
    {
        if (release == null) { headline.Text = "Релизов пока нет"; summary.Text = "Когда в репозитории появится новый релиз, его можно будет скачать здесь."; primary.Content = "Проверить ещё раз"; return; }
        if (release.Version <= UpdateService.CurrentVersion) { headline.Text = "Установлена актуальная версия"; summary.Text = $"Ваша версия: {UpdateService.DisplayVersion}. Последний релиз: {release.Version.ToString(3)}."; primary.Content = "Проверить ещё раз"; }
        else
        {
            headline.Text = $"Доступна версия {release.Version.ToString(3)}";
            summary.Text = release.Installer == null ? "Релиз опубликован. Установщик ещё собирается — повторите проверку чуть позже." : $"Сейчас установлена {UpdateService.DisplayVersion}. Размер обновления: {release.Installer.Size / (1024.0 * 1024):0.#} МБ.";
            primary.Content = release.Installer == null ? "Проверить ещё раз" : "Скачать обновление";
        }
        notes.Text = string.IsNullOrWhiteSpace(release.Notes) ? "Автор не добавил описание релиза." : release.Notes;
    }

    private async Task OnPrimaryAsync()
    {
        if (busy || closed) return;
        if (downloaded != null)
        {
            primary.IsEnabled = cancel.IsEnabled = false;
            status.Text = "Сохраняем текущий урок и запускаем установщик…";
            try { await install(downloaded); }
            catch (Exception ex) { if (!closed) { status.Text = ex.Message; primary.IsEnabled = cancel.IsEnabled = true; } }
            return;
        }
        if (release?.Installer == null || release.Version <= UpdateService.CurrentVersion) { await CheckAsync(); return; }
        BeginOperation(); progress.Visibility = Visibility.Visible; progress.Value = 0;
        status.Text = "Скачиваем установщик. Можно отменить загрузку.";
        try
        {
            var report = new Progress<DownloadProgress>(value => { if (!closed) { progress.Value = value.Percent; status.Text = $"Скачано {value.Received / (1024.0 * 1024):0.#} из {value.Total / (1024.0 * 1024):0.#} МБ"; } });
            downloaded = await service.DownloadAsync(release, System.IO.Path.Combine(App.DataDirectory, "Updates", release.Version.ToString(3)), report, operation!.Token);
            if (closed) return;
            primary.Content = "Сохранить урок и установить";
            status.Text = "Обновление скачано, контрольная сумма проверена. При установке доска закроется; текущий урок будет сохранён.";
        }
        catch (OperationCanceledException) { if (!closed) { status.Text = "Загрузка отменена. Неполный файл удалён."; primary.Content = "Скачать обновление"; } }
        catch (Exception ex) { if (!closed) { status.Text = ex.Message; primary.Content = "Повторить загрузку"; } }
        finally { EndOperation(); }
    }
}
