using System.Windows;
using System.Windows.Input;

namespace HistoryBoard;

// Touch screens and interactive whiteboards. A single finger or a pen is promoted to mouse input
// by WPF, so drawing, moving and typing work as with a mouse. Two fingers move and zoom the board;
// in presentation mode a horizontal swipe switches frames.
public partial class MainWindow
{
    private const double SwipeDistance = 80;
    private readonly Dictionary<int, Point> touches = [];
    private bool touchNavigating;
    private double pinchStartDistance, pinchStartZoom;
    private Point pinchStartWorld, swipeStart;

    internal readonly record struct PinchCamera(double X, double Y, double Zoom);

    // The world point that was under the fingers' centre stays under their current centre,
    // while the zoom follows the change in distance between the fingers.
    internal static PinchCamera Pinch(Point startWorld, double startZoom, double startDistance, Point center, double distance)
    {
        var scale = Math.Clamp(startZoom * (startDistance > 1 && distance > 1 ? distance / startDistance : 1), .02, 8);
        return new PinchCamera(startWorld.X - center.X / scale, startWorld.Y - center.Y / scale, scale);
    }

    private (Point Center, double Distance) TouchPair()
    {
        var pair = touches.Values.Take(2).ToArray();
        return (new Point((pair[0].X + pair[1].X) / 2, (pair[0].Y + pair[1].Y) / 2), (pair[0] - pair[1]).Length);
    }

    private void OnViewportTouchDown(object sender, TouchEventArgs e)
    {
        var point = e.GetTouchPoint(Viewport).Position;
        touches[e.TouchDevice.Id] = point;
        // Presentation buttons rely on touch promotion, so presentation touches are only observed.
        if (presenting) { if (touches.Count == 1) swipeStart = point; return; }
        if (touches.Count == 2) BeginTouchNavigation();
        // The first finger is promoted to mouse input; further fingers only steer the camera.
        if (touches.Count >= 2) { Viewport.CaptureTouch(e.TouchDevice); e.Handled = true; }
    }

    internal void BeginTouchNavigation()
    {
        CancelGesture(); EndPan(); StopCameraAnimation();
        touchNavigating = true;
        var (center, distance) = TouchPair();
        pinchStartDistance = distance; pinchStartZoom = zoom; pinchStartWorld = Surface.ScreenToWorld(center);
    }

    private void OnViewportTouchMove(object sender, TouchEventArgs e)
    {
        if (!touches.ContainsKey(e.TouchDevice.Id)) return;
        touches[e.TouchDevice.Id] = e.GetTouchPoint(Viewport).Position;
        if (presenting) return;
        if (!touchNavigating || touches.Count < 2) return;
        var (center, distance) = TouchPair();
        var camera = Pinch(pinchStartWorld, pinchStartZoom, pinchStartDistance, center, distance);
        SetCamera(camera.X, camera.Y, camera.Zoom, false);
        activeFrame = -1; UpdateFrameSelection();
        e.Handled = true;
    }

    private void OnViewportTouchUp(object sender, TouchEventArgs e)
    {
        var point = e.GetTouchPoint(Viewport).Position;
        var wasPrimary = touches.Count == 1;
        touches.Remove(e.TouchDevice.Id);
        Viewport.ReleaseTouchCapture(e.TouchDevice);
        if (presenting)
        {
            var delta = point - swipeStart;
            if (wasPrimary && Math.Abs(delta.X) > SwipeDistance && Math.Abs(delta.X) > Math.Abs(delta.Y) * 1.5) NextFrame(delta.X < 0 ? 1 : -1);
            return;
        }
        if (!touchNavigating) return;
        if (touches.Count >= 2) { BeginTouchNavigation(); e.Handled = true; return; }
        // Remaining fingers do nothing until all are lifted, so a pinch never ends with a stray stroke.
        if (touches.Count == 0) { touchNavigating = false; ScheduleAutosave(); }
        e.Handled = true;
    }

    private void OnViewportLostTouchCapture(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Captured == null && !e.TouchDevice.IsActive) touches.Remove(e.TouchDevice.Id);
        if (touches.Count == 0) touchNavigating = false;
    }

    // Abandons the current mouse gesture without recording it, used when a second finger turns
    // a stroke or a move into board navigation.
    internal void CancelGesture()
    {
        if (!gesture) return;
        if (tool == BoardTool.Eraser || selectingArea) { FinishGesture(); return; }
        gesture = false;
        if (draft != null) { draft = null; Surface.Preview(null); }
        if (moving != null)
        {
            foreach (var item in Surface.SelectedItems)
                if (moveOriginals.TryGetValue(item.Id, out var original)) { item.X = original.X; item.Y = original.Y; item.Width = original.Width; item.Height = original.Height; }
            Surface.RefreshMany(Surface.SelectedItems);
        }
        moving = null; moveOriginal = null; moveOriginals.Clear(); resizing = false; gestureBefore = null;
        Surface.ReleaseMouseCapture(); UpdateUi();
    }
}
