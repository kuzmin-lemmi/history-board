using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HistoryBoard;

public static class Installer
{
    private static readonly string InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "HistoryBoard");
    // Retain the executable path so existing shortcuts and file associations keep working.
    private static readonly string ExePath = Path.Combine(InstallDirectory, "Историческая доска.exe");
    private static readonly string IconPath = Path.Combine(InstallDirectory, "HistoryBoard-1.1.ico");
    private static readonly string MenuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Доска истории");
    private static readonly string LegacyMenuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Историческая доска");
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HistoryBoard";

    public static void ShowInstaller() => CreateInstallerWindow().ShowDialog();

    internal static Window CreateInstallerWindow()
    {
        var window = new Window { Title = "Установка — Доска истории", Width = 550, Height = 530, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = BoardSurface.Brush("#FAFBF7") };
        var panel = new StackPanel { Margin = new Thickness(35) };
        panel.Children.Add(new TextBlock { Text = "ДОСКА ИСТОРИИ", FontSize = 11, Foreground = BoardSurface.Brush("#58725B"), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Большая история.\nНа вашем компьютере.", FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 15) });
        panel.Children.Add(new TextBlock { Text = "Надписи, карты и цветные стрелки.\nРаботает без интернета и дополнительных установок.", FontSize = 13, Foreground = BoardSurface.Brush("#7B827C"), LineHeight = 22 });
        var shortcut = new CheckBox { Content = "Создать ярлык на рабочем столе", IsChecked = true, Margin = new Thickness(0, 23, 0, 16) };
        panel.Children.Add(shortcut);
        var status = new TextBlock { Text = "Установка для текущего пользователя. Права администратора не нужны.", FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = BoardSurface.Brush("#929A8B"), Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(status);
        var button = new Button { Content = "Установить и открыть", Background = BoardSurface.Brush("#58725B"), Foreground = Brushes.White, Padding = new Thickness(20, 12, 20, 12), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) =>
        {
            try
            {
                // Empty Location is intentional here: it distinguishes our bundled release.
#pragma warning disable IL3000
                if (!string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location)) throw new InvalidOperationException("Для установки используйте автономный файл из папки «Готовое приложение». Эта сборка предназначена для разработки.");
#pragma warning restore IL3000
                button.IsEnabled = false; status.Text = "Устанавливаем…";
                Install(shortcut.IsChecked == true);
                Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true }); window.Close();
            }
            catch (Exception ex) { status.Text = ex.Message; button.IsEnabled = true; }
        };
        panel.Children.Add(button); window.Content = panel; return window;
    }

    private static void Install(bool desktopShortcut)
    {
        foreach (var process in Process.GetProcessesByName("Историческая доска"))
        {
            using (process)
            {
                try { if (string.Equals(process.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Закройте открытую доску и снова нажмите «Установить и открыть». Ваши уроки сохранятся."); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        Directory.CreateDirectory(InstallDirectory);
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), ExePath, StringComparison.OrdinalIgnoreCase))
            File.Copy(Environment.ProcessPath!, ExePath, true);
        using (var source = Application.GetResourceStream(new Uri("pack://application:,,,/HistoryBoard;component/Assets/app.ico")).Stream)
        using (var destination = File.Create(IconPath)) source.CopyTo(destination);
        Directory.CreateDirectory(MenuDirectory);
        CreateShortcut(Path.Combine(MenuDirectory, "Доска истории.lnk"), ExePath);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var oldDesktopShortcut = Path.Combine(desktop, "Историческая доска.lnk");
        var hadOldShortcut = RemoveLegacyShortcut(oldDesktopShortcut);
        if (desktopShortcut || hadOldShortcut) CreateShortcut(Path.Combine(desktop, "Доска истории.lnk"), ExePath);
        RemoveLegacyShortcut(Path.Combine(LegacyMenuDirectory, "Историческая доска.lnk"));
        if (Directory.Exists(LegacyMenuDirectory) && !Directory.EnumerateFileSystemEntries(LegacyMenuDirectory).Any()) Directory.Delete(LegacyMenuDirectory);
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("DisplayName", "Доска истории"); key.SetValue("DisplayVersion", UpdateService.DisplayVersion);
        key.SetValue("Publisher", "Доска истории"); key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("DisplayIcon", ExePath); key.SetValue("UninstallString", $"\"{ExePath}\" --uninstall");
        key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        using var fileType = Registry.CurrentUser.CreateSubKey(@"Software\Classes\HistoryBoard.Lesson\shell\open\command");
        fileType.SetValue("", $"\"{ExePath}\" \"%1\"");
        using var extension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.hboard"); extension.SetValue("", "HistoryBoard.Lesson");
    }

    private static void CreateShortcut(string path, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows не разрешает создание ярлыка.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        try { link.TargetPath = target; link.WorkingDirectory = InstallDirectory; link.IconLocation = IconPath + ",0"; link.Description = "Интерактивная доска для учителя истории"; link.Save(); }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static bool RemoveLegacyShortcut(string path)
    {
        if (!File.Exists(path)) return false;
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return false;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        bool owned;
        try { owned = string.Equals((string)link.TargetPath, ExePath, StringComparison.OrdinalIgnoreCase); }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        if (owned) File.Delete(path);
        return owned;
    }

    public static void Uninstall()
    {
        if (MessageBox.Show("Удалить приложение? Ваши файлы уроков и автосохранение останутся на компьютере.", "Доска истории", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            // Only remove this product's known files. Never recursively remove an install directory.
            foreach (var path in new[] { Path.Combine(MenuDirectory, "Доска истории.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Доска истории.lnk") })
                if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(MenuDirectory) && !Directory.EnumerateFileSystemEntries(MenuDirectory).Any()) Directory.Delete(MenuDirectory);
            Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
            using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.hboard"))
            {
                if ((string?)ext?.GetValue("") == "HistoryBoard.Lesson") Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.hboard", false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\HistoryBoard.Lesson", false);
            if (File.Exists(IconPath)) File.Delete(IconPath);
            if (string.Equals(Path.GetFullPath(Environment.ProcessPath!), ExePath, StringComparison.OrdinalIgnoreCase))
            {
                var literal = ExePath.Replace("'", "''");
                var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{literal}' -Force -ErrorAction SilentlyContinue";
                Process.Start(new ProcessStartInfo("powershell.exe") { Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось завершить удаление"); }
    }
}
