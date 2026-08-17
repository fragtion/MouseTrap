using System.Diagnostics;
using System.Runtime.InteropServices;
using MouseTrap.Native;


namespace MouseTrap.Service;

/// <summary>
/// Authoritative "is the user dragging a window right now, and which one" signal.
///
/// <para>
/// Windows runs a modal move/size loop inside <c>DefWindowProc</c> for every kind of window drag:
/// title bar, Alt+Space -> Move, snap-drag out of a maximized state, touch, and borderless apps
/// that fake a title bar by returning <c>HTCAPTION</c> from <c>WM_NCHITTEST</c> (Chrome, Electron,
/// most modern custom-chrome apps). That loop raises the accessibility events
/// <c>EVENT_SYSTEM_MOVESIZESTART</c> / <c>EVENT_SYSTEM_MOVESIZEEND</c>, so hooking those tells us
/// exactly when a drag starts and ends and gives us the real top level HWND being dragged.
/// </para>
///
/// <para>
/// This replaces the previous approach of polling <c>GetAsyncKeyState</c> and guessing the window
/// with <c>WindowFromPoint</c>. <c>WindowFromPoint</c> returns the deepest <i>child</i> under the
/// cursor, and calling <c>SetWindowPos</c> on a child control with screen coordinates moves it out
/// of its parent's client area - which is why dragged windows ended up rendering black.
/// </para>
///
/// <para>
/// Out-of-context WinEvent hooks are delivered to the thread that installed them, and only while
/// that thread pumps messages, so the hook lives on its own dedicated pumped thread. It is
/// deliberately not installed on the <see cref="MouseBridgeService"/> polling thread: that thread
/// may call <c>SetThreadDesktop</c>, which fails once a hook is registered on it.
/// </para>
/// </summary>
public sealed class WindowDragTracker : IDisposable {
    /// <summary>Immutable snapshot of the current/last drag session.</summary>
    public readonly record struct DragState(IntPtr Window, long SessionId, bool Active) {
        public bool IsActive => Active && Window != IntPtr.Zero;
    }


    private readonly object _sync = new();
    private DragState _state;
    private long _sessionCounter;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    // The delegate must be kept alive for the lifetime of the hook, otherwise the GC
    // collects it and the callback becomes a wild jump.
    private WinEventDelegate? _callback;


    public DragState Current
    {
        get {
            lock (_sync) {
                return _state;
            }
        }
    }


    /// <summary>Acknowledge a finished session so it is only post-processed once.</summary>
    public void ClearFinished(long sessionId)
    {
        lock (_sync) {
            if (!_state.Active && _state.SessionId == sessionId) {
                _state = default;
            }
        }
    }


    public void Start()
    {
        if (_thread != null) {
            return;
        }

        var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() => {
            _threadId = GetCurrentThreadId();
            _callback = OnWinEvent;

            _hook = SetWinEventHook(
                EVENT_SYSTEM_MOVESIZESTART,
                EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero,
                _callback,
                idProcess: 0,
                idThread: 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
            );

            if (_hook == IntPtr.Zero) {
                Logger.Error($"SetWinEventHook failed: {Marshal.GetLastWin32Error()}", null);
            }

            ready.Set();

            // Out-of-context hook callbacks are delivered through this thread's message queue.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hook != IntPtr.Zero) {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }) {
            Name = nameof(WindowDragTracker),
            IsBackground = true,
        };

        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }


    public void Stop()
    {
        var thread = _thread;
        _thread = null;

        if (thread == null) {
            return;
        }

        if (_threadId != 0) {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (!thread.Join(TimeSpan.FromSeconds(2))) {
            Logger.Error($"{nameof(WindowDragTracker)} thread did not exit in time.", null);
        }

        _threadId = 0;

        lock (_sync) {
            _state = default;
        }
    }


    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // only whole windows, never child objects such as a caret or a menu item
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero) {
            return;
        }

        switch (eventType) {
            case EVENT_SYSTEM_MOVESIZESTART: {
                // The hook already gives us the top level window, but normalise anyway.
                var root = WindowInterop.RootOf(hwnd);

                lock (_sync) {
                    _state = new DragState(root, ++_sessionCounter, Active: true);
                }

                Debug.WriteLine($"[DragTracker] move/size start hwnd=0x{root:X} class={WindowInterop.ClassNameOf(root)}");
                break;
            }

            case EVENT_SYSTEM_MOVESIZEEND: {
                lock (_sync) {
                    if (_state.Active) {
                        _state = _state with { Active = false };
                    }
                }

                Debug.WriteLine("[DragTracker] move/size end");
                break;
            }
        }
    }


    public void Dispose()
    {
        Stop();
    }


    #region Win32

    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private const uint WM_QUIT = 0x0012;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    #endregion
}
