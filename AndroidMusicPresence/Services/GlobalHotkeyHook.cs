using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Global multi-key hotkey detection via a low-level keyboard hook (WH_KEYBOARD_LL).
    /// RegisterHotKey only supports modifier+key pairs, but our hotkeys are arbitrary sets
    /// of up to five keys (e.g. CTRL+A+C). The hook tracks which keys are currently held
    /// and fires when the held set exactly matches a registered combo. The completing key
    /// press (and its auto-repeats) is swallowed, mirroring RegisterHotKey behavior.
    /// The combo callback is dispatched asynchronously so the hook callback itself never
    /// blocks the system input queue.
    /// </summary>
    internal sealed class GlobalHotkeyHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;

        private readonly Dispatcher _dispatcher;
        private readonly Action<int> _onCombo;
        // The native hook keeps calling this delegate for as long as it is installed, so it
        // is pinned with a GCHandle until Dispose. A field alone is not enough: if this
        // instance ever leaks without Dispose, the collected delegate would FailFast the
        // whole process on the next key press.
        private readonly LowLevelKeyboardProc _proc;
        private GCHandle _procHandle;
        private IntPtr _hookHandle;

        private readonly HashSet<int> _down = new HashSet<int>();
        private (int Id, HashSet<int> Keys)[] _combos = Array.Empty<(int, HashSet<int>)>();

        // While recording a new hotkey the hook must not match or swallow anything,
        // otherwise combos that are already registered can never be re-recorded.
        public bool Suspended { get; set; }

        public GlobalHotkeyHook(Dispatcher dispatcher, Action<int> onCombo)
        {
            _dispatcher = dispatcher;
            _onCombo = onCombo;
            _proc = HookCallback;
            _procHandle = GCHandle.Alloc(_proc);
            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        }

        public void SetCombos(IEnumerable<(int Id, int[] Keys)> combos)
        {
            var list = new List<(int, HashSet<int>)>();
            foreach (var (id, keys) in combos)
            {
                if (keys == null || keys.Length == 0)
                    continue;

                var set = new HashSet<int>();
                foreach (var key in keys)
                    set.Add(HotkeyHelper.NormalizeKey(key));
                list.Add((id, set));
            }

            _combos = list.ToArray();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                // vkCode is the first DWORD of KBDLLHOOKSTRUCT.
                int vk = HotkeyHelper.NormalizeKey(Marshal.ReadInt32(lParam));

                if (msg == WmKeyDown || msg == WmSysKeyDown)
                {
                    bool isRepeat = _down.Contains(vk);
                    if (!isRepeat)
                    {
                        PruneReleasedKeys();
                        _down.Add(vk);
                    }

                    if (!Suspended)
                    {
                        int matchedId = FindMatch();
                        if (matchedId != 0)
                        {
                            if (!isRepeat)
                                _dispatcher.BeginInvoke(new Action(() => _onCombo(matchedId)));
                            return (IntPtr)1;
                        }
                    }
                }
                else if (msg == WmKeyUp || msg == WmSysKeyUp)
                {
                    _down.Remove(vk);
                }
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        // Keys whose release we never saw (eaten by a secure desktop, UAC prompt, etc.)
        // would wedge the held-set forever; drop anything the OS says is no longer down.
        private void PruneReleasedKeys()
        {
            _down.RemoveWhere(k => (GetAsyncKeyState(k) & 0x8000) == 0);
        }

        private int FindMatch()
        {
            var combos = _combos;
            for (int i = 0; i < combos.Length; i++)
            {
                if (combos[i].Keys.SetEquals(_down))
                    return combos[i].Id;
            }
            return 0;
        }

        public void Dispose()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            if (_procHandle.IsAllocated)
                _procHandle.Free();
            _down.Clear();
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
