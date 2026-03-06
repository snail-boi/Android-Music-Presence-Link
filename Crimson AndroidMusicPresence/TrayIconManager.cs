using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace musicpresense
{
    internal enum TrayIconState
    {
        ActiveUsb,
        InactiveUsb,
        ActiveWifi,
        InactiveWifi,
        ActiveUsbScrcpy,
        InactiveUsbScrcpy,
        ActiveWifiScrcpy,
        InactiveWifiScrcpy,
        NeedsUsbReconnect,
        NoDevice
    }

    internal sealed class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Action _showSettings;
        private readonly Action _exitApp;
        private readonly Action _toggleScrcpy;
        private readonly ToolStripMenuItem _scrcpyItem;
        private readonly ToolStripMenuItem _settingsItem;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _connectionItem;
        private readonly ToolStripMenuItem _audioLinkItem;
        private readonly ToolStripMenuItem _audioSettingsItem;
        private readonly ToolStripMenuItem _nowPlayingItem;
        private readonly Dictionary<TrayIconState, Icon> _stateIcons = new();
        private Bitmap? _connectionDotBitmap;
        private Bitmap? _audioLinkDotBitmap;
        private Bitmap? _settingsBitmap;
        private TrayIconState _lastState = TrayIconState.NoDevice;
        private bool _scrcpyRunning;

        public TrayIconManager(Action showSettings, Action toggleScrcpy, Action exitApp, bool useDarkMode)
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

            _menu = new ContextMenuStrip();

            _connectionItem = CreateInfoItem("Connection: No device connected");
            _audioLinkItem = CreateInfoItem("Audio Link: Inactive");
            _audioSettingsItem = CreateInfoItem("Settings: Encoder raw | Bitrate auto | Buffer 50ms");
            _nowPlayingItem = CreateInfoItem("Now Playing: -");
            _nowPlayingItem.Visible = false;

            _menu.Items.Add(_connectionItem);
            _menu.Items.Add(_audioLinkItem);
            _menu.Items.Add(_audioSettingsItem);
            _menu.Items.Add(_nowPlayingItem);
            _menu.Items.Add(new ToolStripSeparator());

            _settingsItem = new ToolStripMenuItem("Settings");
            _settingsItem.Click += (s, e) => _showSettings();
            _menu.Items.Add(_settingsItem);

            _scrcpyItem = new ToolStripMenuItem("Start Scrcpy (No Audio)");
            _scrcpyItem.Click += (s, e) => _toggleScrcpy();
            _menu.Items.Add(_scrcpyItem);

            _menu.Items.Add(new ToolStripSeparator());
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => _exitApp();
            _menu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.DoubleClick += (s, e) => _showSettings();
            _notifyIcon.Click += (s, e) =>
            {
                if (e is MouseEventArgs { Button: MouseButtons.Left })
                    _showSettings();
            };

            SetDarkMode(useDarkMode);
            SetState(TrayIconState.NoDevice);
        }

        private static ToolStripMenuItem CreateInfoItem(string text)
        {
            return new ToolStripMenuItem(text)
            {
                Enabled = true
            };
        }

        private static Bitmap CreateDotBitmap(Color color)
        {
            var bmp = new Bitmap(14, 14);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 10, 10);
            return bmp;
        }

        private static Bitmap CreateCogBitmap(Color color)
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.7f);
            using var brush = new SolidBrush(color);

            g.DrawEllipse(pen, 4, 4, 8, 8);
            g.FillEllipse(brush, 6, 6, 4, 4);

            g.DrawLine(pen, 8, 1, 8, 3);
            g.DrawLine(pen, 8, 13, 8, 15);
            g.DrawLine(pen, 1, 8, 3, 8);
            g.DrawLine(pen, 13, 8, 15, 8);
            g.DrawLine(pen, 3, 3, 4.5f, 4.5f);
            g.DrawLine(pen, 12, 12, 13.5f, 13.5f);
            g.DrawLine(pen, 12, 4, 13.5f, 2.5f);
            g.DrawLine(pen, 3, 13, 4.5f, 11.5f);

            return bmp;
        }

        private void TryLoadIcon()
        {
            try
            {
                var exe = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(exe))
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                    return;
                }

                var exeDir = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(exeDir))
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                    return;
                }

                var fallbackIcon = Path.Combine(exeDir, "logo.ico");
                if (File.Exists(fallbackIcon))
                {
                    _notifyIcon.Icon = new Icon(fallbackIcon);
                }
                else
                {
                    _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
                }

                var trayIconsDir = Path.Combine(exeDir, "Tray_Icons");
                TryAddStateIcon(TrayIconState.ActiveUsb, trayIconsDir, "Tray_Active_USB.ico");
                TryAddStateIcon(TrayIconState.InactiveUsb, trayIconsDir, "Tray_Active_USB.ico");
                TryAddStateIcon(TrayIconState.ActiveWifi, trayIconsDir, "Tray_Active_WiFi.ico");
                TryAddStateIcon(TrayIconState.InactiveWifi, trayIconsDir, "Tray_Active_WiFi.ico");
                TryAddStateIcon(TrayIconState.ActiveUsbScrcpy, trayIconsDir, "Tray_Scrcpy_USB.ico");
                TryAddStateIcon(TrayIconState.InactiveUsbScrcpy, trayIconsDir, "Tray_Scrcpy_USB.ico");
                TryAddStateIcon(TrayIconState.ActiveWifiScrcpy, trayIconsDir, "Tray_Scrcpy_WiFi.ico");
                TryAddStateIcon(TrayIconState.InactiveWifiScrcpy, trayIconsDir, "Tray_Scrcpy_WiFi.ico");
                TryAddStateIcon(TrayIconState.NeedsUsbReconnect, trayIconsDir, "Tray_WiFi_Noport.ico");
                TryAddStateIcon(TrayIconState.NoDevice, trayIconsDir, "Tray_NoDevice.ico");
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }

        private void TryAddStateIcon(TrayIconState state, string trayIconsDir, string fileName)
        {
            try
            {
                var path = Path.Combine(trayIconsDir, fileName);
                if (!File.Exists(path))
                    return;

                _stateIcons[state] = new Icon(path);
            }
            catch
            {
            }
        }

        public void SetState(TrayIconState state)
        {
            if (_stateIcons.TryGetValue(state, out var icon))
            {
                _notifyIcon.Icon = icon;
            }

            _lastState = state;

            var (text, color) = state switch
            {
                TrayIconState.ActiveUsb => ("Connection: USB connected", Color.FromArgb(52, 201, 84)),
                TrayIconState.InactiveUsb => ("Connection: USB connected", Color.FromArgb(52, 201, 84)),
                TrayIconState.ActiveUsbScrcpy => ("Connection: USB connected", Color.FromArgb(52, 201, 84)),
                TrayIconState.InactiveUsbScrcpy => ("Connection: USB connected", Color.FromArgb(52, 201, 84)),
                TrayIconState.ActiveWifi => ("Connection: Wi-Fi connected", Color.FromArgb(0, 122, 255)),
                TrayIconState.InactiveWifi => ("Connection: Wi-Fi connected", Color.FromArgb(0, 122, 255)),
                TrayIconState.ActiveWifiScrcpy => ("Connection: Wi-Fi connected", Color.FromArgb(0, 122, 255)),
                TrayIconState.InactiveWifiScrcpy => ("Connection: Wi-Fi connected", Color.FromArgb(0, 122, 255)),
                TrayIconState.NeedsUsbReconnect => ("Connection: Wi-Fi port lost", Color.FromArgb(175, 82, 222)),
                _ => ("Connection: No device connected", Color.FromArgb(255, 59, 48))
            };

            _connectionItem.Text = text;
            _connectionDotBitmap?.Dispose();
            _connectionDotBitmap = CreateDotBitmap(color);
            _connectionItem.Image = _connectionDotBitmap;

            UpdateAudioLinkIndicator();
        }

        public void SetScrcpyRunning(bool running)
        {
            _scrcpyRunning = running;
            _scrcpyItem.Text = running ? "Stop Scrcpy" : "Start Scrcpy (No Audio)";
            _audioLinkItem.Text = running ? "Audio Link: Active" : "Audio Link: Inactive";
            UpdateAudioLinkIndicator();
        }

        private void UpdateAudioLinkIndicator()
        {
            _audioLinkDotBitmap?.Dispose();
            _audioLinkDotBitmap = null;
            _audioLinkItem.Image = null;

            if (!_scrcpyRunning)
                return;

            var audioColor = _lastState switch
            {
                TrayIconState.ActiveUsb or TrayIconState.InactiveUsb or TrayIconState.ActiveUsbScrcpy or TrayIconState.InactiveUsbScrcpy
                    => Color.FromArgb(255, 204, 0),
                TrayIconState.ActiveWifi or TrayIconState.InactiveWifi or TrayIconState.ActiveWifiScrcpy or TrayIconState.InactiveWifiScrcpy
                    => Color.FromArgb(255, 149, 0),
                _ => Color.Empty
            };

            if (audioColor == Color.Empty)
                return;

            _audioLinkDotBitmap = CreateDotBitmap(audioColor);
            _audioLinkItem.Image = _audioLinkDotBitmap;
        }

        public void SetAudioSettings(string codec, string bitrate, int bufferMs)
        {
            var resolvedCodec = string.IsNullOrWhiteSpace(codec) ? "raw" : codec.Trim().ToLowerInvariant();
            var resolvedBitrate = string.IsNullOrWhiteSpace(bitrate)
                ? "auto"
                : (bitrate.Trim().EndsWith("K", StringComparison.OrdinalIgnoreCase) ? bitrate.Trim() : $"{bitrate.Trim()}K");

            _audioSettingsItem.Text = $"Settings: Encoder {resolvedCodec} | Bitrate {resolvedBitrate} | Buffer {Math.Max(1, bufferMs)}ms";
        }

        public void SetNowPlaying(string? artist, string? title, string? album)
        {
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                _nowPlayingItem.Visible = false;
                _nowPlayingItem.Text = "Now Playing: -";
                return;
            }

            var displayTitle = title.Trim();
            if (displayTitle.Length > 50)
            {
                displayTitle = displayTitle[..50] + "...";
            }

            _nowPlayingItem.Text = $"Now Playing: {artist} - {displayTitle}";
            _nowPlayingItem.Visible = true;
        }

        public void SetDarkMode(bool useDarkMode)
        {
            var back = useDarkMode ? Color.FromArgb(32, 32, 32) : Color.White;
            var fore = useDarkMode ? Color.FromArgb(235, 235, 235) : Color.FromArgb(24, 24, 24);

            _menu.BackColor = back;
            _menu.ForeColor = fore;
            _menu.RenderMode = ToolStripRenderMode.System;

            foreach (ToolStripItem item in _menu.Items)
            {
                item.BackColor = back;
                item.ForeColor = fore;
            }

            _settingsBitmap?.Dispose();
            _settingsBitmap = CreateCogBitmap(fore);
            _settingsItem.Image = _settingsBitmap;
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                foreach (var icon in _stateIcons.Values)
                {
                    icon.Dispose();
                }

                _connectionDotBitmap?.Dispose();
                _audioLinkDotBitmap?.Dispose();
                _settingsBitmap?.Dispose();
            }
            catch { }
        }
    }
}
