using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Raw P/Invoke bindings for scrcpy_audio.dll (the audio-only scrcpy port).
    /// Use <see cref="ScrcpyAudioPlayer"/> instead of calling these directly.
    /// </summary>
    internal static class ScrcpyAudioNative
    {
        private const string DllName = "scrcpy_audio";

        /// <summary>
        /// Directory containing scrcpy_audio.dll and its dependencies
        /// (avcodec-62, avutil-60, swresample-6, SDL3). Must be set before the
        /// first P/Invoke. AMPL sets this to AppPaths.ResourceRoot, which is
        /// %AppData%\Snail\Assets when installed and .\Assets when portable.
        /// </summary>
        public static string NativeDllDirectory { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "Assets");

        static ScrcpyAudioNative()
        {
            // The DLL and its dependencies are not on the default probing path
            NativeLibrary.SetDllImportResolver(typeof(ScrcpyAudioNative).Assembly, ResolveDll);
        }

        private static IntPtr ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != DllName)
                return IntPtr.Zero;

            // Loading by absolute path also resolves the DLL's own
            // dependencies from that directory
            string path = Path.Combine(NativeDllDirectory, "scrcpy_audio.dll");
            return NativeLibrary.Load(path);
        }

        // enum sca_event
        public const int EventConnected = 0;
        public const int EventConnectionFailed = 1;
        public const int EventStreamStarted = 2;
        public const int EventStreamStopped = 3;
        public const int EventDisconnected = 4;
        public const int EventAudioDisabled = 5;
        public const int EventError = 6;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventCallback(int eventId, IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogCallback(int level, [MarshalAs(UnmanagedType.LPUTF8Str)] string message, IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PcmCallback(IntPtr data, uint numBytes, IntPtr userdata);

        /// <summary>Mirror of struct sca_settings in scrcpy_audio.h.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Settings
        {
            public uint StructSize;

            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? Serial;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AdbPath;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? ServerPath;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioCodec;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioSource;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioEncoder;
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string? AudioCodecOptions;

            public uint AudioBitRate;
            public uint AudioBufferMs;
            public uint OutputBufferMs;
            public ushort PortFirst;
            public ushort PortLast;
            public byte AudioDup;
            public byte LogLevel;

            public EventCallback? EventCb;
            public LogCallback? LogCb;
            public PcmCallback? PcmCb;
            public IntPtr Userdata;
        }

        /// <summary>
        /// Create a Settings struct with the same defaults as the native
        /// sca_settings_init(). StructSize doubles as an ABI check: if the
        /// C# struct layout does not match the native one, sca_start
        /// returns -1 instead of reading garbage.
        /// </summary>
        public static Settings CreateDefaultSettings()
        {
            return new Settings
            {
                StructSize = (uint)Marshal.SizeOf<Settings>(),
                AudioBufferMs = 50,
                LogLevel = 2, // info
            };
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sca_start(ref Settings settings);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sca_stop();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sca_get_format(out uint sampleRate, out uint channels, out uint bitsPerSample);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sca_read(IntPtr buffer, int maxBytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sca_get_device_name();

        public static string GetDeviceName()
        {
            IntPtr ptr = sca_get_device_name();
            return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }
    }
}
