using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace musicpresense
{
    public class AppConfig
    {
        public double WindowWidth { get; set; } = 900;
        public double WindowHeight { get; set; } = 600;
        public double WindowTop { get; set; } = 100;
        public double WindowLeft { get; set; } = 100;
        public WindowState WindowState { get; set; } = WindowState.Normal;

        public double MediaPlayerWindowWidth { get; set; } = 1080;
        public double MediaPlayerWindowHeight { get; set; } = 760;
        public double MediaPlayerWindowTop { get; set; } = 100;
        public double MediaPlayerWindowLeft { get; set; } = 100;
        public WindowState MediaPlayerWindowState { get; set; } = WindowState.Normal;
    }

    public static class Config
    {
        private static readonly string FolderPath = Path.GetDirectoryName(MusicConfigManager.ConfigPath) ?? AppPaths.GetDataPath();

        private static readonly string FilePath =
            Path.Combine(FolderPath, "config.json");

        public static AppConfig Current { get; private set; } = new AppConfig();

        public static void Load()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                    Directory.CreateDirectory(FolderPath);

                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    Current = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch
            {
                Current = new AppConfig(); // fallback if corrupted
            }
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                    Directory.CreateDirectory(FolderPath);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(Current, options);
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                
            }
        }
    }
}
