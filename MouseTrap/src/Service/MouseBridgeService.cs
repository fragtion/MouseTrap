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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int WM_NCHITTEST = 0x84;
    private const int HTVSCROLL = 7;
    private const int HTCLIENT = 1;

    private bool IsLeftMouseDown()
    {
        return (GetAsyncKeyState(0x01) & 0x8000) != 0;
    }

    private bool ShouldSuppressBridgeOnDrag(Point pos)
    {
        var hwnd = WindowFromPoint(pos);
        if (hwnd == IntPtr.Zero)
            return false;

        int lParam = (pos.Y << 16) | (pos.X & 0xFFFF);
        var result = (int)SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, (IntPtr)lParam);

        // 1. Classic native vertical scrollbar
        if (result == HTVSCROLL)
            return true;

        // 2. Text editing controls
        if (result == HTCLIENT && IsLeftMouseDown())
        {
            var className = new System.Text.StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) > 0)
            {
                string classNameStr = className.ToString();
                
                if (classNameStr == "Edit" || 
                    classNameStr.StartsWith("RichEdit") ||
                    classNameStr == "WindowsForms10.EDIT.app.0" ||
                    classNameStr == "TextBox" ||
                    classNameStr == "Scintilla")
                {
                    return true;
                }
            }
        }

        // 3. Simple right-edge detection for modern scrollbars
        if (IsLeftMouseDown() && GetWindowRect(hwnd, out RECT windowRect))
        {
            int distanceFromWindowRight = windowRect.Right - pos.X;
            
            // Within 50 pixels of the window's right edge
            if (distanceFromWindowRight < 50 && distanceFromWindowRight > 0)
            {
                return true;
            }
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
            
            var direction = GetDirection(in position);

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