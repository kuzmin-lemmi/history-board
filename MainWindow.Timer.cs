using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HistoryBoard;

// A countdown timer and stopwatch that floats above the board, in the editor and during a show.
// It is a classroom tool, not part of the lesson, so it is never saved into the file.
public partial class MainWindow
{
    private Border? timerPanel;
    private TextBlock? timerText;
    private Button? timerToggle;
    private readonly DispatcherTimer timerClock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch timerWatch = new();
    private TimeSpan timerDuration = TimeSpan.FromMinutes(5), timerElapsedBefore;
    private bool timerCountsUp, timerFinished, timerDragging;
    private Point timerGrip;
    private int timerBlink;
    private readonly TranslateTransform timerOffset = new();

    internal bool TimerVisible => timerPanel?.Visibility == Visibility.Visible;
    internal bool TimerRunning => timerWatch.IsRunning;
    internal bool TimerFinished => timerFinished;
    internal string TimerText => timerText?.Text ?? "";

    private TimeSpan TimerElapsed => timerElapsedBefore + timerWatch.Elapsed;

    private void EnsureTimerPanel()
    {
        if (timerPanel != null) return;
        timerText = new TextBlock { FontSize = 56, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), Foreground = BoardSurface.Brush("#26352F"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        Button Small(string text, string tip, Action action)
        {
            var button = new Button { Content = text, ToolTip = tip, FontSize = 16, MinWidth = 38, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(2, 0, 2, 0), Focusable = false };
            button.Click += (_, _) => action(); return button;
        }
        var presets = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var minutes in new[] { 1, 3, 5, 10 }) presets.Children.Add(Small($"{minutes}", $"Обратный отсчёт: {minutes} мин", () => StartTimer(TimeSpan.FromMinutes(minutes))));
        presets.Children.Add(Small("+1", "Добавить минуту", () => AddTimerMinute()));
        presets.Children.Add(Small("⏱", "Секундомер: считать время вверх", StartStopwatch));
        timerToggle = Small("▶", "Пуск / пауза", ToggleTimer);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        controls.Children.Add(timerToggle);
        controls.Children.Add(Small("↺", "Сбросить", ResetTimer));
        controls.Children.Add(Small("×", "Скрыть таймер", () => ShowTimer(false)));
        var body = new StackPanel { Margin = new Thickness(14, 8, 14, 10) };
        body.Children.Add(new TextBlock { Text = "ТАЙМЕР · потяните, чтобы переместить", FontSize = 11, Foreground = BoardSurface.Brush("#8A9581"), HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(timerText); body.Children.Add(presets); body.Children.Add(controls);
        timerPanel = new Border
        {
            Child = body, Background = BoardSurface.Brush("#F7FFFFFF"), BorderBrush = BoardSurface.Brush("#C9D3C0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 14, 14, 0), RenderTransform = timerOffset, Visibility = Visibility.Collapsed, Cursor = Cursors.SizeAll
        };
        // Clicks on the timer never reach the board underneath.
        timerPanel.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) { timerDragging = true; timerGrip = e.GetPosition(Viewport) - new Vector(timerOffset.X, timerOffset.Y); timerPanel.CaptureMouse(); } e.Handled = true; };
        timerPanel.MouseMove += (_, e) => { if (!timerDragging) return; var p = e.GetPosition(Viewport); timerOffset.X = p.X - timerGrip.X; timerOffset.Y = p.Y - timerGrip.Y; e.Handled = true; };
        timerPanel.MouseLeftButtonUp += (_, e) => { timerDragging = false; timerPanel.ReleaseMouseCapture(); e.Handled = true; };
        timerPanel.PreviewMouseWheel += (_, e) => e.Handled = true;
        timerPanel.TouchDown += (_, e) => e.Handled = false;
        Panel.SetZIndex(timerPanel, 50);
        Viewport.Children.Add(timerPanel);
        timerClock.Tick += (_, _) => UpdateTimer();
        UpdateTimer();
    }

    internal void ShowTimer(bool show = true)
    {
        EnsureTimerPanel();
        timerPanel!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TimerButton.Background = show ? BoardSurface.Brush("#E6EDDF") : Brushes.Transparent;
        TimerPresentationButton.Background = TimerButton.Background;
        if (!show) { timerWatch.Stop(); timerClock.Stop(); UpdateTimer(); }
        Surface.Focus();
    }
    private void OnTimer(object sender, RoutedEventArgs e) => ShowTimer(!TimerVisible);

    internal void StartTimer(TimeSpan duration)
    {
        EnsureTimerPanel(); if (!TimerVisible) ShowTimer();
        timerCountsUp = false; timerDuration = duration; timerElapsedBefore = TimeSpan.Zero; timerFinished = false;
        timerWatch.Restart(); timerClock.Start(); UpdateTimer();
    }
    private void StartStopwatch()
    {
        timerCountsUp = true; timerElapsedBefore = TimeSpan.Zero; timerFinished = false;
        timerWatch.Restart(); timerClock.Start(); UpdateTimer();
    }
    private void AddTimerMinute()
    {
        if (timerCountsUp) return;
        timerDuration += TimeSpan.FromMinutes(1); timerFinished = false;
        if (!timerWatch.IsRunning && TimerElapsed > TimeSpan.Zero) { timerWatch.Start(); timerClock.Start(); }
        UpdateTimer();
    }
    private void ToggleTimer()
    {
        if (timerWatch.IsRunning) { timerElapsedBefore += timerWatch.Elapsed; timerWatch.Reset(); timerClock.Stop(); }
        else
        {
            if (timerFinished) { timerElapsedBefore = TimeSpan.Zero; timerFinished = false; }
            timerWatch.Start(); timerClock.Start();
        }
        UpdateTimer();
    }
    private void ResetTimer()
    {
        timerWatch.Reset(); timerClock.Stop(); timerElapsedBefore = TimeSpan.Zero; timerFinished = false; UpdateTimer();
    }

    // Shifts the timer's start into the past; used by the self-tests instead of waiting.
    internal void AdvanceTimer(TimeSpan amount) { timerElapsedBefore += amount; UpdateTimer(); }

    internal void UpdateTimer()
    {
        if (timerText == null || timerToggle == null) return;
        var shown = timerCountsUp ? TimerElapsed : timerDuration - TimerElapsed;
        if (!timerCountsUp && shown <= TimeSpan.Zero && (timerWatch.IsRunning || timerElapsedBefore > TimeSpan.Zero) && !timerFinished)
        {
            timerFinished = true; timerElapsedBefore = timerDuration; timerWatch.Reset(); timerBlink = 0;
            try { SystemSounds.Exclamation.Play(); } catch { /* Sound is optional. */ }
        }
        if (timerFinished) shown = TimeSpan.Zero;
        var seconds = (int)Math.Ceiling(Math.Max(0, shown.TotalSeconds) - (timerCountsUp ? .999 : 0));
        timerText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
        // The last ten seconds turn red; a finished timer blinks for a few seconds.
        var warning = !timerCountsUp && (timerFinished || shown.TotalSeconds <= 10 && TimerElapsed > TimeSpan.Zero);
        timerText.Foreground = BoardSurface.Brush(warning ? "#C94F49" : "#26352F");
        if (timerFinished && timerBlink < 12) { timerBlink++; timerText.Opacity = timerBlink % 2 == 0 ? 1 : .35; if (!timerClock.IsEnabled) timerClock.Start(); }
        else { timerText.Opacity = 1; if (timerFinished) timerClock.Stop(); }
        timerToggle.Content = timerWatch.IsRunning ? "⏸" : "▶";
    }
}
