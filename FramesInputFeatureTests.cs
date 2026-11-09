using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunFramesInputFeatureTests(string directory, Action<bool, string> check)
    {
        lesson = new Lesson(); assets = []; currentPath = null; undo.Clear(); redo.Clear(); activeFrame = lastFrame = -1;
        Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title; UpdateUi(); SetCamera(0, 0, 1);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        check(App.IsInstallerName(@"C:\Users\u\Downloads\HistoryBoard-Setup-win-x64.exe") && App.IsInstallerName(@"C:\Users\u\Downloads\HistoryBoard-Setup-win-x64 (1).exe") && App.IsInstallerName("Установить доску.exe"),
            "The downloaded release file opens the installer, also when the browser adds a number to its name");
        check(!App.IsInstallerName(@"C:\Users\u\AppData\Local\Programs\HistoryBoard\Историческая доска.exe") && !App.IsInstallerName("Доска истории.exe"), "The installed and portable copies open the board");

        // ---- Text input: Enter finishes, Shift+Enter breaks the line ----
        SetTool(BoardTool.Text); BeginEditor(null, new Point(100, 80));
        check(Status.Text.Contains("Enter — готово") && Status.Text.Contains("Shift+Enter"), "Starting a caption shows how to finish it and how to break a line");
        Editor.Text = "Первая строка"; Editor.CaretIndex = Editor.Text.Length;
        check(HandleEditorKey(Key.Enter, ModifierKeys.Shift) && editing != null && Editor.Text == "Первая строка" + Environment.NewLine, "Shift+Enter starts a new line without finishing the caption");
        Editor.SelectedText = "вторая"; Editor.CaretIndex = Editor.Text.Length;
        check(HandleEditorKey(Key.Enter, ModifierKeys.None) && editing == null && Editor.Visibility != Visibility.Visible && lesson.Items.Single().Text == "Первая строка\nвторая", "Enter finishes the caption");
        check(ShortcutProfile.Default().Resolve(Key.Enter, ModifierKeys.None, ShortcutContext.Editor, true) == null, "No board shortcut takes Enter away from the text box");

        SetTool(BoardTool.List); BeginEditor(null, new Point(100, 300));
        Editor.Text = "Причины"; Editor.CaretIndex = Editor.Text.Length;
        check(!HandleEditorKey(Key.Enter, ModifierKeys.None) && editing != null, "In a list Enter after an item continues with the next item");
        Editor.Text = "Причины" + Environment.NewLine + "Этапы" + Environment.NewLine; Editor.CaretIndex = Editor.Text.Length;
        var list = editing!;
        check(HandleEditorKey(Key.Enter, ModifierKeys.None) && editing == null && list.Text == "Причины\nЭтапы" && list.Numbered && lesson.Items.Contains(list), "Enter on an empty list item finishes the list without an empty point");
        SetTool(BoardTool.Text); BeginEditor(null, new Point(700, 80)); Editor.Text = "Через Esc"; var escaped = editing!;
        check(HandleEditorKey(Key.Escape, ModifierKeys.None) && editing == null && lesson.Items.Contains(escaped), "Esc still finishes a caption");
        BeginEditor(null, new Point(700, 200)); Editor.Text = "Текст\n\n"; var trimmed = editing!; CommitEditor();
        check(trimmed.Text == "Текст", "Trailing empty lines are not kept in a finished caption");

        // ---- Dragging a selection by any point of its frame ----
        SetTool(BoardTool.Select);
        var curl = new BoardItem { Kind = "pen", Size = 4, Color = "#D69A38", Points = [new(600, 100), new(540, 140), new(540, 260), new(600, 300)] };
        Surface.Add(curl); Surface.Select(curl);
        var box = Surface.SelectedBounds; var inside = new Point(box.Right - 6, box.Y + box.Height / 2);
        check(Surface.Hit(inside) == null, "The test point lies in the empty part of the drawing");
        var depth = undo.Count;
        check(!PressSelect(inside, Surface.Hit(inside), 1, false) && moving != null && !selectingArea, "Pressing inside the selection frame grabs the selection instead of starting a new frame");
        gestureBefore = lesson.Clone(); gestureStart = inside; gesture = true;
        MoveSelection(new Vector(50, 30)); FinishGesture();
        check(curl.X == 50 && curl.Y == 30 && undo.Count == depth + 1 && Surface.Selected == curl, "The selection moves when dragged from an empty spot inside its frame");

        box = Surface.SelectedBounds;
        var label = new BoardItem { Kind = "text", Text = "А", X = box.Right - 40, Y = box.Y + box.Height / 2 - 12, Width = 30, Size = 18 };
        Surface.Add(label); Surface.Select(curl);
        var onLabel = new Point(label.X + 5, label.Y + 8);
        check(Surface.Hit(onLabel) == label && Surface.SelectedBounds.Contains(onLabel), "Another object lies inside the selection frame");
        depth = undo.Count;
        check(!PressSelect(onLabel, Surface.Hit(onLabel), 1, false) && moving == curl, "A press on another object inside the frame still grabs the selection for dragging");
        gestureBefore = lesson.Clone(); gestureStart = onLabel; gesture = true; FinishGesture();
        check(Surface.Selected == label && Surface.SelectedItems.Count == 1 && undo.Count == depth, "A click without dragging selects the object under the pointer");
        Surface.Select(curl);
        PressSelect(onLabel, Surface.Hit(onLabel), 1, true);
        check(Surface.SelectedItems.Count == 2, "Shift+click inside the frame adds the object to the selection");
        Surface.Select(curl);
        var outside = new Point(box.Right + 200, box.Bottom + 200);
        check(PressSelect(outside, null, 1, false) && selectingArea, "Pressing outside the frame starts area selection as before");
        FinishGesture();

        // ---- Back to the normal scale ----
        SetCamera(cameraX, cameraY, 3.5);
        var view = VisibleRegion; var center = new Point(view.X + view.Width / 2, view.Y + view.Height / 2);
        check(ZoomResetButton.Background != Brushes.Transparent, "Far from 100% the zoom percentage is highlighted as a reset button");
        ResetZoom(false);
        view = VisibleRegion;
        check(Math.Abs(zoom - 1) < 1e-9 && (new Point(view.X + view.Width / 2, view.Y + view.Height / 2) - center).Length < 1e-6 && ZoomResetButton.Background == Brushes.Transparent, "Ctrl+1 or a click on the percentage returns to 100% around the same centre");
        SetCamera(cameraX, cameraY, .2); ResetZoom(false);
        check(Math.Abs(zoom - 1) < 1e-9, "The normal scale is restored after zooming far out as well");
        check(ShortcutProfile.Default().Resolve(Key.D1, ModifierKeys.Control, ShortcutContext.Editor, false) == BoardCommand.ResetZoom, "Ctrl+1 is the default shortcut for the normal scale");

        // ---- Frames: outlines on the board, thumbnails, update button ----
        SetCamera(0, 0, 1);
        check(FrameHint.Visibility == Visibility.Visible && FrameHint.Text.Contains("слайд"), "Without frames the tray explains what a frame is");
        AddFrame();
        var first = lesson.Frames[0];
        check(activeFrame == 0 && Surface.ActiveFrame == 0 && FrameHint.Visibility == Visibility.Collapsed && Status.Text.Contains("рамка"), "Adding a frame marks it as current and explains the outline");
        RefreshFrameThumbnails();
        check(frameImages.TryGetValue(first.Id, out var thumb) && thumb.Source != null, "Each frame in the tray shows a thumbnail of its view");
        SetCamera(cameraX + 600, cameraY, zoom);
        check(activeFrame < 0 && UpdateFrameButton.Visibility == Visibility.Visible && UpdateFrameButton.Content?.ToString() == "↻ Обновить кадр 1", "After moving away from a frame the tray offers to update it");
        var moved = VisibleRegion;
        OnUpdateFrame(this, new RoutedEventArgs());
        check(Math.Abs(first.X - moved.X) < 1e-9 && activeFrame == 0 && UpdateFrameButton.Visibility == Visibility.Collapsed, "«Обновить кадр» stores the current view in the frame");
        Undo(); check(Math.Abs(lesson.Frames[0].X - 0) < 1e-9, "Updating a frame can be undone");

        lesson.Frames.Add(new LessonFrame { Name = "Малый", X = 100, Y = 100, Width = 300, Height = 200 });
        SetCamera(0, 0, 1); RebuildFrames(); activeFrame = 1; UpdateFrameSelection(); Surface.UpdateLayout();
        var surface = RenderElement(Surface);
        var edge = Enumerable.Range(97, 7).Min(x => Pixels(surface, x, 200).R);
        check(Surface.ActiveFrame == 1 && edge < 150, "The current frame is outlined on the board");
        StartPresentation();
        check(!Surface.ShowFrames, "Frame outlines are hidden during the show");
        EndPresentation();
        check(Surface.ShowFrames && TrayPresentButton.IsEnabled, "Frame outlines return in the editor, and the tray has its own «Показ» button");
        Fit(); RefreshFrameThumbnails(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CaptureWindow(Path.Combine(directory, "14-frames-on-board.png"));
    }
}
