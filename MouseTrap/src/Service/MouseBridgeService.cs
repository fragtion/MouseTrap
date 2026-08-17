using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MouseTrap.Models;
using MouseTrap.Native;


namespace MouseTrap.Service;

public class MouseBridgeService : IService {
    private ScreenConfigCollection _screens;

    /// <summary>
    /// After a drag that crossed a bridge, if the window ended up almost entirely back on the
    /// source screen, nudge it onto the target screen once the modal move loop has finished.
    /// Set to <c>false</c> to disable the post-drop rescue completely.
    /// </summary>
    private const bool RescueStuckWindowAfterDrop = true;

    /// <summary>Rescue only kicks in when less than this fraction of the window made it across.</summary>
    private const float RescueThreshold = 0.10f;

    private const int DragBridgeCooldownMs = 250;


    // --- window drag bridging -------------------------------------------------------------

    private readonly WindowDragTracker _dragTracker = new();

    private long _movableSession;
    private bool _movableResult;

    private long _bridgedSession;
    private Rectangle _bridgedTargetBounds = Rectangle.Empty;
    private DateTime _lastDragBridge = DateTime.MinValue;


    // --- content drag suppression ---------------------------------------------------------

    private bool _wasMouseDown;
    private bool _suppressBridge;


    public MouseBridgeService()
    {
        _screens = ScreenConfigCollection.Load();
        ScreenConfigCollection.OnChanged += config => {
            _screens = config;
        };
    }

    public MouseBridgeService(ScreenConfigCollection screens)
    {
        _screens = screens;
    }


    public void OnStart()
    {
        _dragTracker.Start();
    }


    private int _errorCount = 0;

    public void Run(CancellationToken token)
    {
        try {
            Loop(token);
        }
        catch (Win32Exception) {
            if (token.IsCancellationRequested) {
                return;
            }

            _errorCount++;
            if (_errorCount < 5) {
                Run(token);
            }
            else {
                throw;
            }
        }
    }


    public void OnExit()
    {
        MouseTrapClear();
        _dragTracker.Stop();
    }


    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested) {
            // on win-logon etc..
            if (!Mouse.IsInputDesktop()) {
                MouseTrapClear();
                Thread.Sleep(1);
                continue;
            }

            var position = GetPosition();
            var drag = _dragTracker.Current;

            // A drag we bridged has just been dropped -> last chance to fix the landing.
            if (!drag.Active && drag.SessionId != 0 && drag.SessionId == _bridgedSession) {
                RescueAfterDrop(drag.Window);
                _dragTracker.ClearFinished(drag.SessionId);
                _bridgedSession = 0;
                _bridgedTargetBounds = Rectangle.Empty;
            }

            var dragging = drag.IsActive;

            // Left button held without a window move loop means the user is dragging *content*
            // (selecting text, dragging a scrollbar thumb, ...). Teleporting the cursor to another
            // screen in the middle of that is never what anybody wants.
            var isDown = IsLeftMouseDown();
            if (isDown && !_wasMouseDown) {
                _suppressBridge = ShouldSuppressBridge(position);
            }

            if (!isDown) {
                _suppressBridge = false;
            }

            _wasMouseDown = isDown;

            // a real move/size loop always wins over the heuristic above
            if (dragging) {
                _suppressBridge = false;
            }

            if (_suppressBridge) {
                Thread.Sleep(1);
                continue;
            }

            var current = _screens.FirstOrDefault(_ => _.Bounds.Contains(position));
            if (current != null && current.HasBridges) {
                MouseTrap(current);

                var direction = GetDirection(in position);

                // ==>
                var hotspace = WidenVertical(current.RightHotSpace, current.Bounds, dragging);
                if (direction.HasFlag(Direction.ToRight) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.RightBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = WidenVertical(targetScreen.LeftHotSpace, targetScreen.Bounds, dragging);
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newY = MapY(position.Y, in hotspace, in target);
                            var landing = new Point(target.X + target.Width + 1, newY);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, landing.X, landing.Y);

                            BridgeDraggedWindow(in drag, dragging, position, landing, targetScreen.Bounds);
                        }
                    }
                }

                // <==
                hotspace = WidenVertical(current.LeftHotSpace, current.Bounds, dragging);
                if (direction.HasFlag(Direction.ToLeft) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.LeftBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = WidenVertical(targetScreen.RightHotSpace, targetScreen.Bounds, dragging);
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newY = MapY(position.Y, in hotspace, in target);
                            var landing = new Point(target.X - 1, newY);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, landing.X, landing.Y);

                            BridgeDraggedWindow(in drag, dragging, position, landing, targetScreen.Bounds);
                        }
                    }
                }

                // ^
                hotspace = WidenHorizontal(current.TopHotSpace, current.Bounds, dragging);
                if (direction.HasFlag(Direction.ToTop) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.TopBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = WidenHorizontal(targetScreen.BottomHotSpace, targetScreen.Bounds, dragging);
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newX = MapX(position.X, in hotspace, in target);
                            var landing = new Point(newX, target.Y - 1);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, landing.X, landing.Y);

                            BridgeDraggedWindow(in drag, dragging, position, landing, targetScreen.Bounds);
                        }
                    }
                }

                // v
                hotspace = WidenHorizontal(current.BottomHotSpace, current.Bounds, dragging);
                if (direction.HasFlag(Direction.ToBottom) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.BottomBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = WidenHorizontal(targetScreen.TopHotSpace, targetScreen.Bounds, dragging);
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newX = MapX(position.X, in hotspace, in target);
                            var landing = new Point(newX, target.Y + target.Height + 1);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, landing.X, landing.Y);

                            BridgeDraggedWindow(in drag, dragging, position, landing, targetScreen.Bounds);
                        }
                    }
                }
            }

            Thread.Sleep(1);
        }
    }


    #region window drag bridging

    /// <summary>
    /// While a window is being dragged the whole edge acts as a bridge, not just the configured
    /// hot spot. Otherwise a window grabbed outside the configured band simply runs into the
    /// cursor clip and gets stuck on the source screen.
    /// </summary>
    private static Rectangle WidenVertical(Rectangle hotspace, Rectangle bounds, bool widen)
    {
        return !widen || hotspace == Rectangle.Empty
            ? hotspace
            : new Rectangle(hotspace.X, bounds.Y, hotspace.Width, bounds.Height);
    }

    private static Rectangle WidenHorizontal(Rectangle hotspace, Rectangle bounds, bool widen)
    {
        return !widen || hotspace == Rectangle.Empty
            ? hotspace
            : new Rectangle(bounds.X, hotspace.Y, bounds.Width, hotspace.Height);
    }


    private bool IsDraggedWindowMovable(in WindowDragTracker.DragState drag)
    {
        if (_movableSession != drag.SessionId) {
            _movableSession = drag.SessionId;
            _movableResult = WindowInterop.IsSafeToMove(drag.Window);

            if (!_movableResult) {
                Debug.WriteLine($"[MouseBridge] not moving hwnd=0x{drag.Window:X} class={WindowInterop.ClassNameOf(drag.Window)}");
            }
        }

        return _movableResult;
    }


    /// <summary>
    /// Follow the cursor with the window that Windows is currently dragging.
    ///
    /// <para>
    /// The window is the real top level HWND reported by the move/size loop, never a child
    /// control picked up with <c>WindowFromPoint</c>, and we only ever change its <i>position</i>.
    /// Size is left to Windows: a per-monitor aware app resizes itself from the rect it gets with
    /// <c>WM_DPICHANGED</c>, an unaware app is rescaled by the system. Forcing our own size on top
    /// of that leaves DWM compositing a redirection surface the app never painted into, which is
    /// what made dragged windows show up black.
    /// </para>
    /// </summary>
    private void BridgeDraggedWindow(in WindowDragTracker.DragState drag, bool dragging, Point from, Point to, Rectangle targetBounds)
    {
        if (!dragging || !drag.IsActive) {
            return;
        }

        if ((DateTime.UtcNow - _lastDragBridge).TotalMilliseconds < DragBridgeCooldownMs) {
            return;
        }

        if (!IsDraggedWindowMovable(in drag) || !WindowInterop.TryGetBounds(drag.Window, out var rect)) {
            return;
        }

        // Where inside the window did the user grab it? Kept as a fraction so the grab point stays
        // put even when the app resizes itself because the target monitor has a different scale.
        var fx = Math.Clamp((from.X - rect.X) / (float) rect.Width, 0f, 1f);
        var fy = Math.Clamp((from.Y - rect.Y) / (float) rect.Height, 0f, 1f);

        var x = to.X - (int) MathF.Round(fx * rect.Width);
        var y = to.Y - (int) MathF.Round(fy * rect.Height);

        // never let the title bar disappear above the target screen
        if (y < targetBounds.Top) {
            y = targetBounds.Top;
        }

        _lastDragBridge = DateTime.UtcNow;
        _bridgedSession = drag.SessionId;
        _bridgedTargetBounds = targetBounds;

        Debug.WriteLine($"[MouseBridge] drag bridge hwnd=0x{drag.Window:X} {rect} -> ({x},{y}) grab=({fx:F2},{fy:F2}) dpi {WindowInterop.DpiOf(drag.Window)}->{WindowInterop.DpiOfMonitorAt(to)}");

        WindowInterop.Move(drag.Window, x, y);
    }


    /// <summary>
    /// Runs once, after <c>EVENT_SYSTEM_MOVESIZEEND</c>, i.e. outside the modal move loop where
    /// <c>SetWindowPos</c> on a foreign window is actually safe. Only fires when the window is
    /// still essentially entirely on the screen it came from.
    /// </summary>
    private void RescueAfterDrop(IntPtr hWnd)
    {
        if (!RescueStuckWindowAfterDrop || _bridgedTargetBounds == Rectangle.Empty) {
            return;
        }

        if (!WindowInterop.IsSafeToMove(hWnd) || !WindowInterop.TryGetBounds(hWnd, out var rect)) {
            return;
        }

        var target = _bridgedTargetBounds;
        var overlap = Rectangle.Intersect(rect, target);
        var covered = (long) overlap.Width * overlap.Height;
        var total = (long) rect.Width * rect.Height;

        if (total <= 0 || covered > total * RescueThreshold) {
            return;
        }

        var x = Math.Clamp(rect.X, target.Left, Math.Max(target.Left, target.Right - rect.Width));
        var y = Math.Clamp(rect.Y, target.Top, Math.Max(target.Top, target.Bottom - rect.Height));

        if (x == rect.X && y == rect.Y) {
            return;
        }

        Debug.WriteLine($"[MouseBridge] rescue after drop hwnd=0x{hWnd:X} {rect} -> ({x},{y})");
        WindowInterop.Move(hWnd, x, y);
    }

    #endregion


    #region content drag suppression

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public Point ptScreenPos;
    }

    private const int VK_LBUTTON = 0x01;
    private const int IDC_IBEAM = 32513;

    /// <summary>How far from a window's right edge a drag is assumed to be a custom scroll bar.</summary>
    private const int CustomScrollbarEdgeWidth = 50;


    private static bool IsLeftMouseDown()
    {
        return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    }


    private static bool IsIBeamCursor()
    {
        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf(ci);
        if (!GetCursorInfo(ref ci)) {
            return false;
        }

        var ibeam = LoadCursor(IntPtr.Zero, (IntPtr) IDC_IBEAM);
        return ibeam != IntPtr.Zero && ci.hCursor == ibeam;
    }


    private static bool ShouldSuppressBridge(Point pos)
    {
        // text selection / caret placement
        if (IsIBeamCursor()) {
            return true;
        }

        var hwnd = WindowInterop.FromPoint(pos);
        if (hwnd == IntPtr.Zero) {
            return false;
        }

        // classic scroll bar thumb. SendMessageTimeout, never SendMessage: a hung target must not
        // be able to freeze the polling loop with the cursor clip still applied.
        var hit = WindowInterop.HitTest(hwnd, pos);
        if (hit is WindowInterop.HTVSCROLL or WindowInterop.HTHSCROLL) {
            return true;
        }

        var cn = WindowInterop.ClassNameOf(hwnd);
        if (cn == "Edit" || cn == "Scintilla" || cn.StartsWith("RichEdit", StringComparison.Ordinal) || cn.StartsWith("WindowsForms10.EDIT", StringComparison.Ordinal)) {
            return true;
        }

        // Custom scroll bars (Chromium/Electron, WPF, anything web rendered) answer neither
        // WM_NCHITTEST nor a known class name, so fall back to "the drag started near the right
        // edge of whatever window is under the cursor". Without this, dragging such a scroll bar
        // to the edge of the screen teleports the cursor and the thumb jumps.
        //
        // This is deliberately blunt, but it is no longer the liability it used to be: a genuine
        // window drag clears the suppression again as soon as the move/size loop reports itself,
        // so grabbing a title bar near the right edge of a window still bridges normally.
        if (WindowInterop.TryGetBounds(hwnd, out var bounds)) {
            var distanceFromRight = bounds.Right - pos.X;
            if (distanceFromRight > 0 && distanceFromRight < CustomScrollbarEdgeWidth) {
                return true;
            }
        }

        return false;
    }

    #endregion


    private Point GetPosition()
    {
        if (!Mouse.TryGetPosition(out var pos)) {
            return Point.Empty;
        }

        return pos;
    }


    private int _posOldx;
    private int _posOldy;

    private Direction GetDirection(in Point pos)
    {
        var ret = Direction.None;
        if (_posOldx < pos.X) {
            _posOldx = pos.X;
            ret |= Direction.ToRight;
        }

        if (_posOldx > pos.X) {
            _posOldx = pos.X;
            ret |= Direction.ToLeft;
        }

        if (_posOldy < pos.Y) {
            _posOldy = pos.Y;
            ret |= Direction.ToBottom;
        }

        if (_posOldy > pos.Y) {
            _posOldy = pos.Y;
            ret |= Direction.ToTop;
        }

        return ret;
    }

    private static int MapY(int y, in Rectangle src, in Rectangle dst)
    {
        var percent = (y - src.Y) / (float) src.Height;
        var newY = (int) (dst.Height * percent) + dst.Y;
        return newY;
    }

    private static int MapX(int x, in Rectangle src, in Rectangle dst)
    {
        var percent = (x - src.X) / (float) src.Width;
        var newX = (int) (dst.Width * percent) + dst.X;
        return newX;
    }

    private int _activeTrap = -1;

    private void MouseTrap(ScreenConfig config)
    {
        if (_activeTrap != config.ScreenId) {
            Mouse.SetClip(in config.Bounds);
            _activeTrap = config.ScreenId;
        }
        else {
            var clip = Mouse.GetClip();
            if (clip != config.Bounds) {
                Mouse.SetClip(in config.Bounds);
            }
        }
    }

    private void MouseTrapClear()
    {
        if (_activeTrap != -1) {
            Mouse.ClearClip();
            _activeTrap = -1;
        }
    }

    private void MouseMove(in Rectangle srcBounds, in Rectangle targetBounds, int x, int y)
    {
        Mouse.MoveCursor(x, y);

        var pos = GetPosition();
        if (pos.X != x || pos.Y != y) {
            for (var i = 0; i < 3; i++) {
                Mouse.MoveCursor(x, y);

                pos = GetPosition();
                if (pos.X == x && pos.Y == y) {
                    return;
                }
            }
        }
    }
}

[Flags]
internal enum Direction : byte {
    None = 0x00,
    ToLeft = 0x01,
    ToRight = 0x02,
    ToTop = 0x04,
    ToBottom = 0x08,
}
