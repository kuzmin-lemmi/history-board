using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace HistoryBoard;

public partial class App : Application
{
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HistoryBoard", "Data");
    private SingleInstance? instance;

    // The same executable is the installer and the board. Downloaded release files
    // ("HistoryBoard-Setup-win-x64.exe", also "… (1).exe" from browsers) and the local
    // "Установить доску.exe" open the installer; any other name opens the board.
    internal static bool IsInstallerName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("Установить", StringComparison.OrdinalIgnoreCase) || name.Contains("setup", StringComparison.OrdinalIgnoreCase);
    }

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
        var examplesIndex = Array.IndexOf(e.Args, "--write-examples");
        if (examplesIndex >= 0 && examplesIndex + 1 < e.Args.Length)
        {
            try { DemoLessons.WriteAll(Path.GetFullPath(e.Args[examplesIndex + 1])); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
            return;
        }
        var shotsIndex = Array.IndexOf(e.Args, "--promo-screenshots");
        if (shotsIndex >= 0 && shotsIndex + 1 < e.Args.Length)
        {
            var folder = Path.GetFullPath(e.Args[shotsIndex + 1]);
            DataDirectory = Path.Combine(folder, "data"); Directory.CreateDirectory(DataDirectory);
            var window = new MainWindow(false); MainWindow = window; window.Show();
            try { await window.WritePromoScreenshots(folder); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "failure.txt"), ex.ToString()); window.CloseWithoutPrompt(); Shutdown(1); }
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
        if (e.Args.Contains("--install") || IsInstallerName(executable))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Installer.ShowInstaller(); Shutdown(); return;
        }

        DispatcherUnhandledException += OnUnhandledUiException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { CrashLog.Write("Фоновая задача", args.Exception); args.SetObserved(); };

        var file = e.Args.FirstOrDefault(a => a.EndsWith(".hboard", StringComparison.OrdinalIgnoreCase));
        if (file != null) file = Path.GetFullPath(file);
        try { instance = SingleInstance.Acquire(DataDirectory, file); }
        catch (TimeoutException ex) { MessageBox.Show(ex.Message, "Доска истории", MessageBoxButton.OK, MessageBoxImage.Warning); Shutdown(1); return; }
        if (instance == null) { Shutdown(0); return; } // the running board opens the file
        var guard = instance;
        Exit += (_, _) => guard.Dispose();

        // The autosave is restored first; a file from the command line then opens like any other,
        // so unsaved work from the previous session is never replaced without a question.
        var main = new MainWindow(true, file);
        MainWindow = main; main.Show();
        guard.Listen(message => Dispatcher.BeginInvoke(() => main.ReceiveFromAnotherStart(message)));
        _ = main.CheckForUpdatesAsync(false);
    }

    private bool handlingError;
    private DateTime errorSeriesStart;
    private int errorsInSeries;

    // Any error that escapes a command is caught here: the lesson is written to the autosave, the
    // error goes to the log, and the board keeps working. Repeated errors close the board safely.
    private void OnUnhandledUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CrashLog.Write("Ошибка в окне доски", e.Exception);
        if (handlingError) return;
        handlingError = true;
        try
        {
            var board = MainWindow as HistoryBoard.MainWindow;
            var saved = board?.EmergencySave() == true;
            var now = DateTime.UtcNow;
            if (now - errorSeriesStart > TimeSpan.FromSeconds(30)) { errorSeriesStart = now; errorsInSeries = 0; }
            var closeBoard = ++errorsInSeries >= 3;
            var lessonText = saved ? "Урок сохранён в автосохранение и откроется при следующем запуске." : "Сохранить урок автоматически не удалось — попробуйте сохранить его через Ctrl+S.";
            var next = closeBoard ? "Ошибка повторяется, поэтому доска будет закрыта. Запустите её снова." : "Доска продолжит работу. Если что-то ведёт себя странно, сохраните урок и перезапустите программу.";
            MessageBox.Show($"Произошла непредвиденная ошибка: {e.Exception.Message}\n\n{lessonText}\n{next}\n\nПодробности записаны в журнал:\n{CrashLog.LogPath}",
                "Доска истории", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (closeBoard) { board?.CloseWithoutPrompt(); Shutdown(1); }
        }
        catch (Exception nested) { CrashLog.Write("Обработка ошибки", nested); }
        finally { handlingError = false; }
    }

    // Errors on background threads end the process; save what can be saved first.
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception error) CrashLog.Write("Ошибка в фоновом потоке", error);
        if (!e.IsTerminating) return;
        try
        {
            if (Dispatcher.CheckAccess()) (MainWindow as HistoryBoard.MainWindow)?.EmergencySave();
            else Dispatcher.Invoke(() => (MainWindow as HistoryBoard.MainWindow)?.EmergencySave(), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(5));
        }
        catch { /* The process is ending; the regular autosave remains. */ }
    }
}
