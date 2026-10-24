using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Verrou.Core.AutoType;

namespace Verrou.Services
{
    /// <summary>
    /// Exécute une séquence d'auto-type avec SendInput.
    /// Le texte est envoyé en Unicode (KEYEVENTF_UNICODE) : indépendant de la disposition du clavier (AZERTY,
    /// touches mortes ^ ¨…). Par sécurité, la frappe s'arrête si la fenêtre active change en cours de route :
    /// un mot de passe n'est jamais tapé dans une autre fenêtre que celle visée.
    /// </summary>
    public static class AutoTyper
    {
        private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                             VK_RETURN = 0x0D, VK_TAB = 0x09;

        private static readonly HashSet<ushort> ExtendedKeys = new HashSet<ushort>
        {
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D,
        };

        public sealed class AutoTypeAbortedException : Exception
        {
            public AutoTypeAbortedException(string m) : base(m) { }
        }

        /// <param name="obfuscate">Brouillage à deux canaux des valeurs secrètes (clavier + presse-papiers, dans le désordre).</param>
        public static async Task RunAsync(IReadOnlyList<AutoTypeAction> actions, IntPtr target, int keyDelayMs, bool obfuscate = false)
        {
            try { await RunCoreAsync(actions, target, keyDelayMs, obfuscate); }
            finally { ClipboardService.ClearTemporary(); }
        }

        private static async Task TypeObfuscatedAsync(string secret, IntPtr target, int delay)
        {
            foreach (var step in AutoTypeObfuscator.Plan(secret))
            {
                EnsureTarget(target);
                switch (step)
                {
                    case CaretHome: PressKey(0x24, KeyModifiers.None); break;
                    case CaretEnd: PressKey(0x23, KeyModifiers.None); break;
                    case CaretRight r: for (int i = 0; i < r.Count; i++) { PressKey(0x27, KeyModifiers.None); await Task.Delay(Math.Max(delay, 4)); } break;
                    case CaretLeft l: for (int i = 0; i < l.Count; i++) { PressKey(0x25, KeyModifiers.None); await Task.Delay(Math.Max(delay, 4)); } break;
                    case TypeChunk t:
                        foreach (char c in t.Text) { SendUnicode(c); if (delay > 0) await Task.Delay(delay); }
                        break;
                    case PasteChunk p:
                        if (!ClipboardService.SetTemporary(p.Text))
                            throw new AutoTypeAbortedException("Presse-papiers indisponible : brouillage interrompu.");
                        await Task.Delay(40);
                        EnsureTarget(target);
                        PressKey(0x56, KeyModifiers.Control); // Ctrl+V
                        await Task.Delay(140);                 // laisse l'application lire le presse-papiers
                        break;
                }
                await Task.Delay(Math.Max(delay, 10));
            }
            ClipboardService.ClearTemporary();
        }

        private static async Task RunCoreAsync(IReadOnlyList<AutoTypeAction> actions, IntPtr target, int keyDelayMs, bool obfuscate)
        {
            await WaitForModifiersReleasedAsync();

            if (Native.GetForegroundWindow() != target)
            {
                if (Native.IsIconic(target)) Native.ShowWindow(target, Native.SW_RESTORE);
                Native.SetForegroundWindow(target);
                for (int i = 0; i < 20 && Native.GetForegroundWindow() != target; i++) await Task.Delay(25);
            }
            await Task.Delay(60);
            EnsureTarget(target);

            int delay = Math.Max(0, keyDelayMs);
            foreach (var action in actions)
            {
                switch (action)
                {
                    case TypeTextAction t when obfuscate && t.Sensitive && AutoTypeObfuscator.CanObfuscate(t.Text):
                        await TypeObfuscatedAsync(t.Text, target, delay);
                        break;

                    case TypeTextAction t:
                        for (int i = 0; i < t.Text.Length; i++)
                        {
                            EnsureTarget(target);
                            char c = t.Text[i];
                            if (c == '\n') PressKey(VK_RETURN, KeyModifiers.None);
                            else if (c == '\t') PressKey(VK_TAB, KeyModifiers.None);
                            else if (c == '\r') continue;
                            else if (char.IsHighSurrogate(c) && i + 1 < t.Text.Length)
                            {
                                SendUnicode(c, t.Text[i + 1]);
                                i++;
                            }
                            else SendUnicode(c);
                            if (delay > 0) await Task.Delay(delay);
                        }
                        break;

                    case KeyAction k:
                        for (int r = 0; r < k.Repeat; r++)
                        {
                            EnsureTarget(target);
                            PressKey(k.VirtualKey, k.Modifiers);
                            await Task.Delay(Math.Max(delay, 15));
                        }
                        break;

                    case ShortcutCharAction s:
                        EnsureTarget(target);
                        short scan = Native.VkKeyScan(s.Char);
                        if (scan == -1) throw new AutoTypeAbortedException($"Le caractère « {s.Char} » n'existe pas sur ce clavier.");
                        var mods = s.Modifiers;
                        if ((scan & 0x100) != 0) mods |= KeyModifiers.Shift;
                        PressKey((ushort)(scan & 0xFF), mods);
                        await Task.Delay(Math.Max(delay, 15));
                        break;

                    case DelayAction d:
                        await Task.Delay(d.Milliseconds);
                        break;

                    case SetKeyDelayAction sd:
                        delay = sd.Milliseconds;
                        break;
                }
            }
        }

        private static void EnsureTarget(IntPtr target)
        {
            if (Native.GetForegroundWindow() != target)
                throw new AutoTypeAbortedException("La fenêtre active a changé : auto-type interrompu par sécurité.");
        }

        /// <summary>Attend que l'utilisateur relâche Ctrl/Alt/Maj/Win (sinon « Ctrl+Alt+A » se mélangerait à la frappe).</summary>
        private static async Task WaitForModifiersReleasedAsync()
        {
            ushort[] mods = { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN };
            for (int i = 0; i < 100; i++)
            {
                bool any = false;
                foreach (var m in mods) if ((Native.GetAsyncKeyState(m) & 0x8000) != 0) any = true;
                if (!any) return;
                await Task.Delay(20);
            }
            // Toujours enfoncées après 2 s : on les relâche logiciellement.
            var inputs = new List<Native.INPUT>();
            foreach (var m in mods) inputs.Add(Key(m, true));
            Send(inputs.ToArray());
        }

        private static void PressKey(ushort vk, KeyModifiers mods)
        {
            var list = new List<Native.INPUT>();
            if (mods.HasFlag(KeyModifiers.Control)) list.Add(Key(VK_CONTROL, false));
            if (mods.HasFlag(KeyModifiers.Alt)) list.Add(Key(VK_MENU, false));
            if (mods.HasFlag(KeyModifiers.Shift)) list.Add(Key(VK_SHIFT, false));
            if (mods.HasFlag(KeyModifiers.Win)) list.Add(Key(VK_LWIN, false));
            list.Add(Key(vk, false));
            list.Add(Key(vk, true));
            if (mods.HasFlag(KeyModifiers.Win)) list.Add(Key(VK_LWIN, true));
            if (mods.HasFlag(KeyModifiers.Shift)) list.Add(Key(VK_SHIFT, true));
            if (mods.HasFlag(KeyModifiers.Alt)) list.Add(Key(VK_MENU, true));
            if (mods.HasFlag(KeyModifiers.Control)) list.Add(Key(VK_CONTROL, true));
            Send(list.ToArray());
        }

        private static void SendUnicode(params char[] units)
        {
            var list = new List<Native.INPUT>();
            foreach (var u in units) list.Add(Unicode(u, false));
            foreach (var u in units) list.Add(Unicode(u, true));
            Send(list.ToArray());
        }

        private static Native.INPUT Key(ushort vk, bool up)
        {
            uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
            if (ExtendedKeys.Contains(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            return new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = flags } },
            };
        }

        private static Native.INPUT Unicode(char c, bool up) => new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = 0, wScan = c, dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0) } },
        };

        private static void Send(Native.INPUT[] inputs)
        {
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
            if (sent != inputs.Length)
                throw new AutoTypeAbortedException("Windows a refusé la frappe simulée (fenêtre d'une application lancée en administrateur ?).");
        }
    }
}
