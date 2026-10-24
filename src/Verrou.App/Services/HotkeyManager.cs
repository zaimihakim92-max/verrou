using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;

namespace Verrou.Services
{
    /// <summary>Raccourcis clavier globaux (RegisterHotKey) via une fenêtre message-only (repris de Cadrix).</summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        private int _nextId = 0x5A00;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("VerrouHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
            {
                handled = true;
                a();
            }
            return IntPtr.Zero;
        }

        /// <returns>true si le raccourci a pu être enregistré (false s'il est déjà pris par une autre application).</returns>
        public bool Register(string gesture, Action action)
        {
            if (!TryParse(gesture, out var mods, out var key)) return false;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            int id = _nextId++;
            if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk)) return false;
            _actions[id] = action;
            return true;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys.ToList()) Native.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        public static bool TryParse(string gesture, out uint mods, out Key key)
        {
            mods = 0; key = Key.None;
            if (string.IsNullOrWhiteSpace(gesture)) return false;
            foreach (var raw in gesture.Split('+'))
            {
                var part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                    case "shift": case "maj": mods |= Native.MOD_SHIFT; break;
                    case "alt": mods |= Native.MOD_ALT; break;
                    case "win": mods |= Native.MOD_WIN; break;
                    default:
                        if (part.Length == 1 && char.IsDigit(part[0])) part = "D" + part;
                        if (!Enum.TryParse(part, true, out key)) return false;
                        break;
                }
            }
            return key != Key.None && mods != 0;
        }

        public static string Format(ModifierKeys mods, Key key)
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            var k = key.ToString();
            if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k.Substring(1);
            parts.Add(k);
            return string.Join("+", parts);
        }

        public void Dispose()
        {
            UnregisterAll();
            _source.Dispose();
        }
    }
}
