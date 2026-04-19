using System.Runtime.InteropServices;
using System.ComponentModel;
using MouseTrap.Models;
using MouseTrap.Native;

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
                cn == "WindowsForms10.EDIT.app.0" ||
                cn == "Chrome_RenderWidgetHostHWND" ||
                cn == "MozillaWindowClass")
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

        // 4. Cursor shape fallback — catches CMD, PuTTY, and anything else
        //    with an I-beam that we didn't explicitly enumerate
        if (IsIBeamCursor())
            return true;

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
        // Mouse.SwitchToInputDesktop();

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
