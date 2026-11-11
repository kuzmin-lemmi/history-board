using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HistoryBoard;

public enum PresentationTool { None, Pointer, Spotlight, Curtain }

// Drawn above the board during a presentation: a laser pointer, a spotlight that dims everything
// except a circle around the pointer, or a curtain that hides the lower part of the screen.
public sealed class PresentationOverlay : FrameworkElement
{
    public PresentationTool Mode { get; set; }
    public Point Pointer { get; set; }
    public bool HasPointer { get; set; }
    public double SpotlightRadius { get; set; } = 170;
    public double CurtainTop { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        switch (Mode)
        {
            case PresentationTool.Pointer when HasPointer:
                dc.DrawEllipse(BoardSurface.Translucent("#E0362F", 0x40), null, Pointer, 22, 22);
                dc.DrawEllipse(BoardSurface.Translucent("#E0362F", 0x70), null, Pointer, 13, 13);
                dc.DrawEllipse(BoardSurface.Brush("#E0362F"), new Pen(Brushes.White, 2), Pointer, 7, 7);
                break;
            case PresentationTool.Spotlight:
                var center = HasPointer ? Pointer : new Point(area.Width / 2, area.Height / 2);
                var light = new EllipseGeometry(center, SpotlightRadius, SpotlightRadius);
                dc.DrawGeometry(BoardSurface.Brush("#C8121815"), null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(area), light));
                dc.DrawGeometry(null, new Pen(BoardSurface.Translucent("#FFFFFF", 0x55), 2), light);
                break;
            case PresentationTool.Curtain:
                var top = Math.Clamp(CurtainTop, 0, area.Height);
                dc.DrawRectangle(BoardSurface.Brush("#26352F"), null, new Rect(0, top, area.Width, area.Height - top));
                var handle = new Rect(area.Width / 2 - 46, top + 10, 92, 8);
                dc.DrawRoundedRectangle(BoardSurface.Translucent("#FFFFFF", 0x88), null, handle, 4, 4);
                break;
        }
    }
}

public partial class MainWindow
{
    private int revealStep;
    private readonly HashSet<string> revealedCovers = [];

    // In the show a click on a cover opens it; a click on the same place closes it again.
    internal bool ToggleCoverAt(Point world)
    {
        for (var n = lesson.Items.Count - 1; n >= 0; n--)
        {
            var item = lesson.Items[n];
            if (item.Kind != "cover" || !Surface.Bounds(item).Contains(world)) continue;
            var revealed = revealedCovers.Contains(item.Id);
            if (!revealed && Surface.IsHidden(item)) continue; // not yet shown by its reveal step
            if (revealed) revealedCovers.Remove(item.Id); else revealedCovers.Add(item.Id);
            Surface.HideItem(item, !revealed);
            return true;
        }
        return false;
    }
    private bool curtainDragging;
    private double curtainGrip;

    private void SetPresentationTool(PresentationTool mode)
    {
        Overlay.Mode = mode;
        if (mode == PresentationTool.Curtain && (Overlay.CurtainTop <= 0 || Overlay.CurtainTop >= Viewport.ActualHeight)) Overlay.CurtainTop = Viewport.ActualHeight * .35;
        curtainDragging = false;
        Surface.Cursor = mode switch { PresentationTool.Pointer or PresentationTool.Spotlight => Cursors.None, PresentationTool.Curtain => Cursors.SizeNS, _ => Cursors.Arrow };
        foreach (var (button, tool) in new[] { (PointerButton, PresentationTool.Pointer), (SpotlightButton, PresentationTool.Spotlight), (CurtainButton, PresentationTool.Curtain) })
            button.Background = mode == tool ? BoardSurface.Brush("#DDE8D3") : Brushes.Transparent;
        Overlay.InvalidateVisual();
    }
    internal void TogglePresentationTool(PresentationTool mode) { if (presenting) SetPresentationTool(Overlay.Mode == mode ? PresentationTool.None : mode); }
    private void OnPointerTool(object sender, RoutedEventArgs e) { TogglePresentationTool(PresentationTool.Pointer); Surface.Focus(); }
    private void OnSpotlightTool(object sender, RoutedEventArgs e) { TogglePresentationTool(PresentationTool.Spotlight); Surface.Focus(); }
    private void OnCurtainTool(object sender, RoutedEventArgs e) { TogglePresentationTool(PresentationTool.Curtain); Surface.Focus(); }

    private void OnSurfacePreviewMove(object sender, MouseEventArgs e)
    {
        if (!presenting || Overlay.Mode == PresentationTool.None) return;
        var point = e.GetPosition(Overlay);
        Overlay.Pointer = point; Overlay.HasPointer = true;
        if (curtainDragging) Overlay.CurtainTop = Math.Clamp(point.Y - curtainGrip, 0, Viewport.ActualHeight);
        Overlay.InvalidateVisual();
    }

    // Returns true when the press was used by a presentation tool.
    private bool BeginCurtainDrag(MouseButtonEventArgs e)
    {
        if (Overlay.Mode != PresentationTool.Curtain || e.ChangedButton != MouseButton.Left) return false;
        var y = e.GetPosition(Overlay).Y;
        curtainGrip = Math.Abs(y - Overlay.CurtainTop) < 60 ? y - Overlay.CurtainTop : 0;
        if (curtainGrip == 0) Overlay.CurtainTop = y;
        curtainDragging = true; Surface.CaptureMouse(); Overlay.InvalidateVisual();
        return true;
    }
    private void EndCurtainDrag() { if (!curtainDragging) return; curtainDragging = false; Surface.ReleaseMouseCapture(); }

    private void AdjustPresentationTool(int delta)
    {
        if (Overlay.Mode == PresentationTool.Spotlight) Overlay.SpotlightRadius = Math.Clamp(Overlay.SpotlightRadius + Math.Sign(delta) * 25, 60, 700);
        else if (Overlay.Mode == PresentationTool.Curtain) Overlay.CurtainTop = Math.Clamp(Overlay.CurtainTop - delta / 2.0, 0, Viewport.ActualHeight);
        Overlay.InvalidateVisual();
    }

    internal IReadOnlyList<int> FrameSteps(int index)
    {
        if (index < 0 || index >= lesson.Frames.Count) return [];
        var frame = lesson.Frames[index].Bounds;
        return lesson.Items.Where(i => i.Step > 0 && frame.IntersectsWith(Surface.Bounds(i))).Select(i => i.Step).Distinct().Order().ToList();
    }

    // Hides stepped objects of the current frame that have not been revealed yet.
    internal void ApplyReveal()
    {
        var frame = presenting && activeFrame >= 0 && activeFrame < lesson.Frames.Count ? lesson.Frames[activeFrame].Bounds : Rect.Empty;
        foreach (var item in lesson.Items.Where(i => i.Step > 0))
            Surface.HideItem(item, !frame.IsEmpty && item.Step > revealStep && frame.IntersectsWith(Surface.Bounds(item)));
        foreach (var item in lesson.Items.Where(i => revealedCovers.Contains(i.Id))) Surface.HideItem(item, true);
        if (!frame.IsEmpty)
        {
            var steps = FrameSteps(activeFrame);
            if (steps.Count > 0) PresentationLabel.Text += $"   · {steps.Count(s => s <= revealStep)} из {steps.Count}";
        }
    }

    // In a presentation "next" first reveals the next step of the current frame, then moves on.
    internal void PresentStep(int direction)
    {
        if (!presenting) { NextFrame(direction); return; }
        if (lesson.Frames.Count == 0) return;
        if (activeFrame < 0) { revealStep = 0; GoToFrame(0); return; }
        var steps = FrameSteps(activeFrame);
        if (direction > 0)
        {
            var next = steps.FirstOrDefault(s => s > revealStep);
            if (next > 0) { revealStep = next; UpdateFrameSelection(); ApplyReveal(); return; }
            if (activeFrame < lesson.Frames.Count - 1) { revealStep = 0; GoToFrame(activeFrame + 1); }
        }
        else
        {
            if (revealStep > 0 && steps.Count > 0) { revealStep = steps.LastOrDefault(s => s < revealStep); UpdateFrameSelection(); ApplyReveal(); return; }
            // Going back shows the previous frame as it was left: fully revealed.
            if (activeFrame > 0) { revealStep = FrameSteps(activeFrame - 1).DefaultIfEmpty(0).Max(); GoToFrame(activeFrame - 1); }
        }
    }

    private void ShowAllSteps()
    {
        foreach (var item in lesson.Items.Where(i => i.Step > 0 || revealedCovers.Contains(i.Id))) Surface.HideItem(item, false);
        revealedCovers.Clear();
    }
}
