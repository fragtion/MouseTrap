using System.Runtime.InteropServices;
using System.ComponentModel;
using MouseTrap.Models;
using MouseTrap.Native;
using System.Diagnostics;

namespace MouseTrap.Service;

public class MouseBridgeService : IService {
    private ScreenConfigCollection _screens;

    private bool _wasMouseDown = false;
    private bool _suppressBridge = false;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point pt);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public Point ptScreenPos;
    }

    private const int WM_NCHITTEST = 0x84;
    private const int HTVSCROLL = 7;
    private const int IDC_IBEAM = 32513;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const int BRIDGE_COOLDOWN_MS = 200;

    // Drag tracking variables
    private IntPtr _draggedWindow = IntPtr.Zero;
    private bool _isDragging = false;
    private DateTime _lastBridgeTime = DateTime.MinValue;
    private Point _windowPosAtEdge;
    private Point _cursorPosAtEdge;
    private bool _isAtEdge = false;
    private int _accumulatedDeltaX = 0;
    private int _accumulatedDeltaY = 0;
    private string _edgeDirection = "";

    private bool IsLeftMouseDown()
    {
        return (GetAsyncKeyState(0x01) & 0x8000) != 0;
    }

    private bool IsIBeamCursor()
    {
        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf(ci);
        if (!GetCursorInfo(ref ci))
            return false;

        IntPtr ibeam = LoadCursor(IntPtr.Zero, IDC_IBEAM);
        return ibeam != IntPtr.Zero && ci.hCursor == ibeam;
    }

    private bool ShouldSuppressBridgeOnDrag(Point pos)
    {
        // If we have an I-beam cursor, suppress bridging (text selection/cursor placement)
        if (IsIBeamCursor())
            return true;

        var hwnd = WindowFromPoint(pos);
        if (hwnd == IntPtr.Zero)
            return false;

        // 1. Check for native/classic scrollbar via hit test
        int lParam = (pos.Y << 16) | (pos.X & 0xFFFF);
        var hitTest = (int)SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, (IntPtr)lParam);
        if (hitTest == HTVSCROLL)
            return true;

        // 2. Check window class - if it's a text control, suppress on any drag
        var className = new System.Text.StringBuilder(256);
        if (GetClassName(hwnd, className, className.Capacity) > 0)
        {
            string cn = className.ToString();
            if (cn == "Edit" ||
                cn.StartsWith("RichEdit") ||
                cn == "Scintilla" ||
                cn == "WindowsForms10.EDIT.app.0")
            {
                return true;
            }
        }

        // 3. Right-edge heuristic for custom scrollbars
        if (GetWindowRect(hwnd, out RECT windowRect))
        {
            int distanceFromRight = windowRect.Right - pos.X;
            if (distanceFromRight < 50 && distanceFromRight > 0)
                return true;
        }

        return false;
    }

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
    }

    private void UpdateDragState()
    {
        bool leftButtonDown = IsLeftMouseDown();
        
        if (leftButtonDown && !_isDragging)
        {
            GetCursorPos(out Point cursorPos);
            IntPtr hWnd = WindowFromPoint(cursorPos);
            if (hWnd != IntPtr.Zero)
            {
                _draggedWindow = hWnd;
                _isDragging = true;
                _isAtEdge = false;
                _accumulatedDeltaX = 0;
                _accumulatedDeltaY = 0;
                Debug.WriteLine($"[MouseBridge] Drag started on window handle {_draggedWindow}");
            }
        }
        else if (!leftButtonDown && _isDragging)
        {
            _isDragging = false;
            _draggedWindow = IntPtr.Zero;
            _isAtEdge = false;
            _accumulatedDeltaX = 0;
            _accumulatedDeltaY = 0;
            Debug.WriteLine($"[MouseBridge] Drag ended");
        }
    }

    private void CheckAndAccumulateEdgeDelta(Point currentCursor, Rectangle screenBounds)
    {
        if (!_isDragging || _draggedWindow == IntPtr.Zero) return;
        
        GetWindowRect(_draggedWindow, out RECT windowRect);
        int windowLeft = windowRect.Left;
        int windowRight = windowRect.Right;
        int windowTop = windowRect.Top;
        int windowBottom = windowRect.Bottom;
        
        bool wasAtEdge = _isAtEdge;
        
        // Check if window is at screen edge
        if (!_isAtEdge)
        {
            // Right edge
            if (windowRight >= screenBounds.Right - 5 && windowRight <= screenBounds.Right + 5)
            {
                _isAtEdge = true;
                _edgeDirection = "right";
                _windowPosAtEdge = new Point(windowLeft, windowTop);
                _cursorPosAtEdge = currentCursor;
                _accumulatedDeltaX = 0;
                _accumulatedDeltaY = 0;
                Debug.WriteLine($"[MouseBridge] Window hit right edge - starting accumulation");
            }
            // Left edge
            else if (windowLeft <= screenBounds.Left + 5 && windowLeft >= screenBounds.Left - 5)
            {
                _isAtEdge = true;
                _edgeDirection = "left";
                _windowPosAtEdge = new Point(windowLeft, windowTop);
                _cursorPosAtEdge = currentCursor;
                _accumulatedDeltaX = 0;
                _accumulatedDeltaY = 0;
                Debug.WriteLine($"[MouseBridge] Window hit left edge - starting accumulation");
            }
            // Top edge
            else if (windowTop <= screenBounds.Top + 5 && windowTop >= screenBounds.Top - 5)
            {
                _isAtEdge = true;
                _edgeDirection = "top";
                _windowPosAtEdge = new Point(windowLeft, windowTop);
                _cursorPosAtEdge = currentCursor;
                _accumulatedDeltaX = 0;
                _accumulatedDeltaY = 0;
                Debug.WriteLine($"[MouseBridge] Window hit top edge - starting accumulation");
            }
            // Bottom edge
            else if (windowBottom >= screenBounds.Bottom - 5 && windowBottom <= screenBounds.Bottom + 5)
            {
                _isAtEdge = true;
                _edgeDirection = "bottom";
                _windowPosAtEdge = new Point(windowLeft, windowTop);
                _cursorPosAtEdge = currentCursor;
                _accumulatedDeltaX = 0;
                _accumulatedDeltaY = 0;
                Debug.WriteLine($"[MouseBridge] Window hit bottom edge - starting accumulation");
            }
        }
        else
        {
            // Accumulate mouse movement while at edge
            int deltaX = currentCursor.X - _cursorPosAtEdge.X;
            int deltaY = currentCursor.Y - _cursorPosAtEdge.Y;
            
            // Only accumulate in the direction of the edge
            switch (_edgeDirection)
            {
                case "right":
                    if (deltaX > 0) _accumulatedDeltaX = deltaX;
                    break;
                case "left":
                    if (deltaX < 0) _accumulatedDeltaX = deltaX;
                    break;
                case "top":
                    if (deltaY < 0) _accumulatedDeltaY = deltaY;
                    break;
                case "bottom":
                    if (deltaY > 0) _accumulatedDeltaY = deltaY;
                    break;
            }
            
            if (wasAtEdge && _isAtEdge && (_accumulatedDeltaX != 0 || _accumulatedDeltaY != 0))
            {
                Debug.WriteLine($"[MouseBridge] Accumulated delta: X={_accumulatedDeltaX}, Y={_accumulatedDeltaY}");
            }
        }
    }

    private void TeleportWindowWithOffset(IntPtr hWnd, Rectangle targetScreenBounds, string direction, int accumulatedDeltaX, int accumulatedDeltaY)
    {
        if (hWnd == IntPtr.Zero) return;
        
        // Get current window size
        GetWindowRect(hWnd, out RECT windowRect);
        int windowWidth = windowRect.Right - windowRect.Left;
        int windowHeight = windowRect.Bottom - windowRect.Top;
        
        int newX = 0, newY = 0;
        
        // Calculate new position based on edge direction and accumulated delta
        switch (direction)
        {
            case "right":
                // Coming from right edge, entering left edge of target screen
                newX = targetScreenBounds.Left + accumulatedDeltaX;
                newY = _windowPosAtEdge.Y;
                break;
            case "left":
                // Coming from left edge, entering right edge of target screen
                newX = targetScreenBounds.Right - windowWidth + accumulatedDeltaX;
                newY = _windowPosAtEdge.Y;
                break;
            case "top":
                // Coming from top edge, entering bottom edge of target screen
                newX = _windowPosAtEdge.X;
                newY = targetScreenBounds.Bottom - windowHeight + accumulatedDeltaY;
                break;
            case "bottom":
                // Coming from bottom edge, entering top edge of target screen
                newX = _windowPosAtEdge.X;
                newY = targetScreenBounds.Top + accumulatedDeltaY;
                break;
        }
        
        // Clamp to screen bounds
        newX = Math.Max(targetScreenBounds.Left, Math.Min(targetScreenBounds.Right - windowWidth, newX));
        newY = Math.Max(targetScreenBounds.Top, Math.Min(targetScreenBounds.Bottom - windowHeight, newY));
        
        Debug.WriteLine($"[MouseBridge] Teleporting window with offset: new pos ({newX}, {newY}), accumulated delta: X={accumulatedDeltaX}, Y={accumulatedDeltaY}");
        
        // Move the window
        SetWindowPos(hWnd, IntPtr.Zero, newX, newY, 0, 0, 
                     SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    private void HandleBridgeDuringDrag(Rectangle targetScreenBounds, string direction)
    {
        if (!_isDragging || _draggedWindow == IntPtr.Zero)
            return;
        
        // Prevent rapid successive bridges
        if ((DateTime.Now - _lastBridgeTime).TotalMilliseconds < BRIDGE_COOLDOWN_MS)
            return;
        
        _lastBridgeTime = DateTime.Now;
        
        Debug.WriteLine($"[MouseBridge] Bridge detected during drag! Direction: {direction}, Accumulated: X={_accumulatedDeltaX}, Y={_accumulatedDeltaY}");
        
        // Teleport the window using the accumulated offset
        TeleportWindowWithOffset(_draggedWindow, targetScreenBounds, direction, _accumulatedDeltaX, _accumulatedDeltaY);
        
        // Reset edge tracking
        _isAtEdge = false;
        _accumulatedDeltaX = 0;
        _accumulatedDeltaY = 0;
    }

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested) {
            if (!Mouse.IsInputDesktop()) {
                MouseTrapClear();
                Thread.Sleep(1);
                continue;
            }

            var position = GetPosition();
            var isDown = IsLeftMouseDown();

            // Update drag state tracking
            UpdateDragState();

            if (isDown && !_wasMouseDown)
            {
                _suppressBridge = ShouldSuppressBridgeOnDrag(position);
            }

            if (!isDown)
            {
                _suppressBridge = false;
            }

            _wasMouseDown = isDown;

            if (_suppressBridge)
            {
                Thread.Sleep(1);
                continue;
            }

            var current = _screens.FirstOrDefault(_ => _.Bounds.Contains(position));
            if (current != null && current.HasBridges) {
                MouseTrap(current);

                var direction = GetDirection(in position);
                
                // Check if window is at screen edge and accumulate movement
                CheckAndAccumulateEdgeDelta(position, current.Bounds);

                // ==>
                var hotspace = current.RightHotSpace;
                if (direction.HasFlag(Direction.ToRight) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.RightBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = targetScreen.LeftHotSpace;
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newY = MapY(position.Y, in hotspace, in target);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, (target.X + target.Width + 1), newY);
                            
                            // Handle window drag during bridge
                            HandleBridgeDuringDrag(targetScreen.Bounds, "right");
                        }
                    }
                }

                // <==
                hotspace = current.LeftHotSpace;
                if (direction.HasFlag(Direction.ToLeft) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.LeftBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = targetScreen.RightHotSpace;
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newY = MapY(position.Y, in hotspace, in target);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, (target.X - 1), newY);
                            
                            // Handle window drag during bridge
                            HandleBridgeDuringDrag(targetScreen.Bounds, "left");
                        }
                    }
                }

                // ^
                hotspace = current.TopHotSpace;
                if (direction.HasFlag(Direction.ToTop) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.TopBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = targetScreen.BottomHotSpace;
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newX = MapX(position.X, in hotspace, in target);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, newX, (target.Y - 1));
                            
                            // Handle window drag during bridge
                            HandleBridgeDuringDrag(targetScreen.Bounds, "top");
                        }
                    }
                }

                // v
                hotspace = current.BottomHotSpace;
                if (direction.HasFlag(Direction.ToBottom) && hotspace.Contains(position)) {
                    var targetScreen = _screens.FirstOrDefault(_ => _.ScreenId == current.BottomBridge!.TargetScreenId);
                    if (targetScreen != null) {
                        var target = targetScreen.TopHotSpace;
                        if (target != Rectangle.Empty) {
                            MouseTrapClear();

                            var newX = MapX(position.X, in hotspace, in target);
                            MouseMove(in current.Bounds, in targetScreen.Bounds, newX, (target.Y + target.Height + 1));
                            
                            // Handle window drag during bridge
                            HandleBridgeDuringDrag(targetScreen.Bounds, "bottom");
                        }
                    }
                }
            }

            Thread.Sleep(1);
        }
    }

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