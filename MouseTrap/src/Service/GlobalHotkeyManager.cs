using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseTrap.Service;

public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const int HOTKEY_ENABLE  = 1;
    private const int HOTKEY_DISABLE = 2;

    private const uint MOD_WIN = 0x0008;

    private readonly IntPtr _windowHandle;
    private bool _registered;

    public event Action? EnableRequested;
    public event Action? DisableRequested;

    public GlobalHotkeyManager(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    public void Register()
    {
        if (_registered)
            return;

        // Win + PageUp
        RegisterHotKey(_windowHandle, HOTKEY_ENABLE, MOD_WIN, (uint)Keys.PageUp);

        // Win + PageDown
        RegisterHotKey(_windowHandle, HOTKEY_DISABLE, MOD_WIN, (uint)Keys.PageDown);

        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered)
            return;

        UnregisterHotKey(_windowHandle, HOTKEY_ENABLE);
        UnregisterHotKey(_windowHandle, HOTKEY_DISABLE);

        _registered = false;
    }

    public void HandleWndProc(ref Message m)
    {
        if (m.Msg != WM_HOTKEY)
            return;

        switch ((int)m.WParam)
        {
            case HOTKEY_ENABLE:
                EnableRequested?.Invoke();
                break;

            case HOTKEY_DISABLE:
                DisableRequested?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        Unregister();
    }

    #region Win32

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk
    );

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id
    );

    #endregion
}
