using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace musicpresense
{
    internal sealed class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Action _showSettings;
        private readonly Action _exitApp;
        private readonly Action _toggleScrcpy;
        private readonly ToolStripMenuItem _scrcpyItem;

        public TrayIconManager(Action showSettings, Action toggleScrcpy, Action exitApp)
        {
            _showSettings = showSettings ?? throw new ArgumentNullException(nameof(showSettings));
            _toggleScrcpy = toggleScrcpy ?? throw new ArgumentNullException(nameof(toggleScrcpy));
            _exitApp = exitApp ?? throw new ArgumentNullException(nameof(exitApp));

            _notifyIcon = new NotifyIcon
            {
                Visible = true,
                Text = "Music Presence"
            };

            TryLoadIcon();

            var menu = new ContextMenuStrip();
            var settingsItem = new ToolStripMenuItem("Settings");
            settingsItem.Click += (s, e) => _showSettings();
            menu.Items.Add(settingsItem);

            _scrcpyItem = new ToolStripMenuItem("Start Scrcpy (No Audio)");
            _scrcpyItem.Click += (s, e) => _toggleScrcpy();
            menu.Items.Add(_scrcpyItem);

            menu.Items.Add(new ToolStripSeparator());
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => _exitApp();
            menu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (s, e) => _showSettings();
            _notifyIcon.Click += (s, e) =>
            {
                if (e is MouseEventArgs { Button: MouseButtons.Left })
                    _showSettings();
            };
        }

        private void TryLoadIcon()
        {
            try
            {
                var exe = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    var exeDir = Path.GetDirectoryName(exe);
                    var logoPath = exeDir != null ? Path.Combine(exeDir, "logo.ico") : null;
                    if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                    {
                        _notifyIcon.Icon = new Icon(logoPath);
                        return;
                    }

                    _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }

        public void SetScrcpyRunning(bool running)
        {
            _scrcpyItem.Text = running ? "Stop Scrcpy" : "Start Scrcpy (No Audio)";
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }
        }
    }
}
