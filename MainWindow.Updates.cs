using System.Diagnostics;
using System.Windows;

namespace HistoryBoard;

public partial class MainWindow
{
    private UpdateWindow? updateWindow;
    private bool checkingUpdates;

    internal async Task CheckForUpdatesAsync(bool interactive)
    {
        if (interactive)
        {
            CommitEditor(); FinishGesture();
            if (updateWindow != null) { updateWindow.Activate(); return; }
            updateWindow = new UpdateWindow(this, InstallUpdateAsync, ShowUpdateBadge);
            updateWindow.Closed += (_, _) => updateWindow = null;
            updateWindow.Show(); return;
        }
        if (checkingUpdates) return;
        checkingUpdates = true;
        try
        {
            using var service = new UpdateService();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var release = await service.CheckLatestAsync(timeout.Token);
            if (!closing) ShowUpdateBadge(release);
        }
        catch { /* Offline lessons remain fully usable. Manual checks explain connection errors. */ }
        finally { checkingUpdates = false; }
    }

    private void ShowUpdateBadge(AppRelease? release)
    {
        var newer = release != null && release.Version > UpdateService.CurrentVersion;
        UpdateButton.Content = newer ? "↻ Новая" : "↻";
        UpdateButton.Background = newer ? BoardSurface.Brush("#EDF2E7") : System.Windows.Media.Brushes.Transparent;
        UpdateButton.ToolTip = newer ? $"Доступна версия {release!.Version.ToString(3)}. Скачать обновление." : "Обновления программы";
        if (newer) Status.Text = $"● Доступна версия {release!.Version.ToString(3)} — кнопка «↻ Новая» наверху";
    }

    private async void OnUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);

    private async Task InstallUpdateAsync(string installerPath)
    {
        CommitEditor(); FinishGesture(); EndPan(); CommitTitle(); StopCameraAnimation(); StoreCamera();
        IsEnabled = false;
        try
        {
            var snapshot = lesson.Clone(); var data = UsedAssets(snapshot);
            await saveGate.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    LessonFile.Save(AutosavePath, snapshot, data, true);
                    if (currentPath != null) LessonFile.Save(currentPath, snapshot, data, true);
                });
                WriteSessionInfo(currentPath, currentPath == null && HasUnsavedChanges);
            }
            finally { saveGate.Release(); }
            var start = new ProcessStartInfo(installerPath) { UseShellExecute = true };
            start.ArgumentList.Add("--install"); start.ArgumentList.Add("--wait-for-process"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            if (Process.Start(start) == null) throw new InvalidOperationException("Windows не смог запустить установщик.");
            closing = true; autosaveTimer.Stop(); Application.Current.Shutdown();
        }
        finally { if (!closing) IsEnabled = true; }
    }
}
