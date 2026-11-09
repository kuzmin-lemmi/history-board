using System.IO;
using System.Windows;

namespace HistoryBoard;

public partial class App : Application
{
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HistoryBoard", "Data");

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length) DataDirectory = Path.GetFullPath(e.Args[dataIndex + 1]);
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            DataDirectory = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(DataDirectory);
            File.Delete(Path.Combine(DataDirectory, "failure.txt"));
            var window = new MainWindow(false); MainWindow = window; window.Show();
            try { await window.RunSelfTests(DataDirectory); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(DataDirectory, "failure.txt"), ex.ToString()); window.CloseWithoutPrompt(); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--uninstall")) { Installer.Uninstall(); Shutdown(); return; }
        var waitIndex = Array.IndexOf(e.Args, "--wait-for-process");
        if (waitIndex >= 0 && waitIndex + 1 < e.Args.Length && int.TryParse(e.Args[waitIndex + 1], out var processId) && processId > 0 && processId != Environment.ProcessId)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            catch (Exception) { MessageBox.Show("Предыдущая версия ещё работает. Закройте доску и запустите скачанный установщик снова.", "Доска истории"); Shutdown(1); return; }
        }
        var executable = Environment.ProcessPath ?? "";
        if (e.Args.Contains("--install") || Path.GetFileNameWithoutExtension(executable).StartsWith("Установить", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Installer.ShowInstaller(); Shutdown(); return;
        }
        var main = new MainWindow(!e.Args.Any(a => a.EndsWith(".hboard", StringComparison.OrdinalIgnoreCase)));
        MainWindow = main; main.Show();
        var file = e.Args.FirstOrDefault(a => a.EndsWith(".hboard", StringComparison.OrdinalIgnoreCase));
        if (file != null) await main.OpenLessonAsync(file, false);
        _ = main.CheckForUpdatesAsync(false);
    }
}
