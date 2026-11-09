using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunEditingFeatureTests(string directory, Action<bool, string> check)
    {
        var originalLesson = lesson.Clone(); var originalAssets = assets;
        var previousClipboard = Clipboard.GetDataObject();
        try
        {
            lesson = new Lesson(); assets = []; Surface.SetLesson(lesson, assets); SetCamera(0, 0, 1);
            var caption = new BoardItem { Text = "Походы и открытия", X = 70, Y = 60 };
            var arrow = new BoardItem { Kind = "arrow", Points = [new(100, 200), new(300, 250)] };
            Surface.Add(caption); Surface.Add(arrow); Surface.SelectMany([caption, arrow]);
            SelectArea(new Rect(60, 50, 40, 40));
            check(Surface.SelectedItems.Count == 1 && Surface.Selected == caption, "Marquee selection finds intersecting objects in board coordinates");
            Surface.ShowMarquee(Rect.Empty); Surface.SelectMany([caption, arrow]);
            GroupSelection(); var group = caption.GroupId;
            check(group != null && arrow.GroupId == group, "Grouping assigns a shared persistent identity");
            Surface.Select(null); SelectArea(new Rect(60, 50, 40, 40));
            check(Surface.SelectedItems.Count == 2, "A selection rectangle includes all members of an intersecting group");
            Surface.ShowMarquee(Rect.Empty);
            Surface.Select(caption);
            check(Surface.SelectedItems.Count == 2, "Clicking a grouped member selects the complete group");
            moveOriginals = Surface.SelectedItems.ToDictionary(i => i.Id, i => i.Clone());
            MoveSelection(new Vector(40, 25));
            check(caption.X == 110 && arrow.X == 40 && arrow.Y == 25, "Selection movement translates text and stroke coordinates together");
            ToggleLock(); var count = lesson.Items.Count; DeleteSelected(); MoveSelection(new Vector(90, 90));
            check(lesson.Items.Count == count && caption.X == 110 && Surface.SelectionLocked, "Locked groups resist movement and deletion");
            ToggleLock(); GroupSelection(true);
            check(caption.GroupId == null && arrow.GroupId == null, "Ungrouping preserves objects and clears membership");
            Surface.Select(caption); Surface.ToggleSelection(arrow);
            check(Surface.SelectedItems.Count == 2, "Additive selection includes a second independent object");
            Surface.ToggleSelection(arrow);
            check(Surface.SelectedItems.Count == 1, "Toggling selection removes a selected object");
            Surface.SelectMany([caption, arrow]); GroupSelection();
            CopySelection();
            check(Clipboard.ContainsData(BoardClipboardFormat), "Copy publishes the board format to the real Windows clipboard");
            await PasteClipboardAsync();
            check(lesson.Items.Count == 4 && Surface.SelectedItems.Count == 2 && Surface.Selected!.GroupId != caption.GroupId, "Paste creates independent object and group identities");
            Undo(); check(lesson.Items.Count == 2, "A multi-object paste is undone in one step");
            Redo(); check(lesson.Items.Count == 4, "Redo restores an entire pasted group");
            Surface.Select(lesson.Items[0]); DuplicateSelection();
            check(lesson.Items.Count == 6 && Surface.SelectedItems.Count == 2, "Duplicate copies a selected group without changing the clipboard");

            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "test-map.png"));
            using (var png = new MemoryStream(bytes))
            {
                var data = new DataObject(); data.SetData("PNG", png); Clipboard.SetDataObject(data, true);
                await PasteClipboardAsync();
            }
            check(lesson.Items.Count == 7 && Surface.Selected?.Kind == "image", "PNG clipboard images insert through the production paste path");
            Clipboard.SetImage(LessonFile.DecodeImage(bytes)); await PasteClipboardAsync();
            check(lesson.Items.Count == 8 && assets.Count == 2, "Bitmap clipboard images are encoded and retained as lesson assets");
            CopySelection(); await PasteClipboardAsync();
            check(lesson.Items.Count == 9 && assets.Count == 3, "Copied board images retain embedded pixels when pasted");
            var path = Path.Combine(directory, "clipboard-and-groups.hboard");
            LessonFile.Save(path, lesson, UsedAssets(lesson)); var loaded = LessonFile.Load(path);
            check(loaded.Assets.Count == 3 && loaded.Lesson.Items.Count(i => i.GroupId != null) == 6, "Clipboard assets and groups survive a complete lesson file round-trip");
            var beforeCount = lesson.Items.Count;
            try { PasteObjects(new BoardClipboard { Lesson = new Lesson { Items = [new BoardItem { Kind = "image", AssetId = new string('a', 32) }] } }); }
            catch (InvalidDataException) { }
            check(lesson.Items.Count == beforeCount, "Invalid clipboard assets leave the current lesson unchanged");
            CaptureWindow(Path.Combine(directory, "06-groups-and-clipboard.png"));
            var shared = Guid.NewGuid().ToString("N");
            lesson = new Lesson { Items = Enumerable.Range(0, 1000).Select(n => new BoardItem { Kind = "pen", GroupId = shared, Points = [new(n, n), new(n + 5, n + 5)] }).ToList() };
            assets = []; Surface.SetLesson(lesson, assets);
            var timer = System.Diagnostics.Stopwatch.StartNew(); Surface.SelectMany(lesson.Items);
            moveOriginals = lesson.Items.ToDictionary(i => i.Id, i => i.Clone()); MoveSelection(new Vector(12, 18));
            check(Surface.SelectedItems.Count == 1000 && lesson.Items.All(i => i.X == 12 && i.Y == 18), "A thousand-member group is selected and moved as a complete set");
            check(timer.Elapsed < TimeSpan.FromSeconds(5), "Large-group selection and movement complete without a multi-second UI stall");
            DeleteSelected(); check(lesson.Items.Count == 0, "Deleting a large group removes all selected members together");
        }
        finally
        {
            if (previousClipboard != null) { try { Clipboard.SetDataObject(previousClipboard, true); } catch { } }
            else Clipboard.Clear();
            lesson = originalLesson; assets = originalAssets; Surface.SetLesson(lesson, UsedAssets(lesson)); RestoreCamera();
            undo.Clear(); redo.Clear(); moveOriginals.Clear();
        }
    }
}
