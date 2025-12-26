using MouseTrap.Forms;
using MouseTrap.Models;
using MouseTrap.Service;
using System.Reflection.Metadata;
using System.Windows.Forms;
using System.Drawing;

namespace MouseTrap
{
    public class MouseTrapTrayIcon : TrayIcon
    {
        private readonly ServiceThread _service;
        private ToolStripMenuItem _teleportMenuItem;
        private WeakReference<ConfigFrom>? _configFromRef;
        private GlobalHotkeyManager? _hotkeys;
        private Form? _hiddenForm; // hidden form for hotkey handle

        public MouseTrapTrayIcon(ServiceThread service)
        {
            _service = service;

            // Tray icon settings
            Icon = App.Icon;
            Text = App.Name;

            // Settings menu
            ContextMenu.Items.Add(new ToolStripMenuItem("Settings", null, (s, e) => OpenSettings())
            {
                ToolTipText = "Open configuration screen"
            });

            // Mouse teleportation menu
            _teleportMenuItem = new ToolStripMenuItem(
                "Mouse teleportation",
                null,
                (sender, args) => ToggleTeleportation(_teleportMenuItem)
            )
            {
                Checked = true,
                CheckOnClick = true,
                ToolTipText = "Turn off mouse teleportation e.g. while gaming"
            };
            ContextMenu.Items.Add(_teleportMenuItem);

            // Exit menu
            ContextMenu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Close())
            {
                ToolTipText = "Fully exit MouseTrap process"
            });

            // Bold first menu item
            ContextMenu.Items[0].Font = WithFontStyle(ContextMenu.Items[0].Font, FontStyle.Bold);

            // Show tray icon
            Visible = true;

            // Create hidden form for hotkeys
            _hiddenForm = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                Size = new Size(0, 0),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-1000, -1000)
            };
            _hiddenForm.Load += (s, e) => _hiddenForm.Hide();
            _hiddenForm.Show();

            // Hotkey manager using hidden form handle
            _hotkeys = new GlobalHotkeyManager(_hiddenForm.Handle);
            _hotkeys.EnableRequested += () =>
            {
                _service.StartService();
                SetTeleportationMenuState(true);
            };
            _hotkeys.DisableRequested += () =>
            {
                _service.StopService();
                SetTeleportationMenuState(false);
            };
            _hotkeys.Register();

            // Show settings on first startup
            var settings = Settings.Load();
            if (!settings.Configured)
            {
                OpenSettings();
            }
        }

        private void SetTeleportationMenuState(bool enabled)
        {
            _teleportMenuItem.Checked = enabled;
        }

        private void ToggleTeleportation(ToolStripMenuItem checkBox)
        {
            if (checkBox.Checked)
            {
                _service.StartService();
            }
            else
            {
                _service.StopService();
            }
        }

        public void OpenSettings()
        {
            if (_configFromRef == null || !_configFromRef.TryGetTarget(out var configFrom) || configFrom.Disposing || configFrom.IsDisposed)
            {
                configFrom = new ConfigFrom(_service);
                configFrom.Show();
                _configFromRef = new WeakReference<ConfigFrom>(configFrom);
            }

            // Bring window to front
            configFrom.Activate();
            configFrom.TopMost = true;
            configFrom.TopMost = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (MutexRunner.IsMutexMessageOpen(ref m))
            {
                OpenSettings();
            }
            else if (MutexRunner.IsMutexMessageClose(ref m))
            {
                Close();
            }

            _hotkeys?.HandleWndProc(ref m);
            _service.WndProc(ref m);

            base.WndProc(ref m);
        }

        private static Font WithFontStyle(Font font, FontStyle style)
        {
            return new Font(font.Name, font.Size, style, font.Unit);
        }

        // Dispose hidden form when tray icon closes
        public new void Close()
        {
            base.Close();
            if (_hiddenForm != null && !_hiddenForm.IsDisposed)
            {
                _hiddenForm.Close();
                _hiddenForm.Dispose();
                _hiddenForm = null;
            }
        }
    }
}
