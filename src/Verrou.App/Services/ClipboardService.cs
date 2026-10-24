using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace Verrou.Services
{
    /// <summary>
    /// Copie dans le presse-papiers avec effacement automatique, et exclusion de l'historique du presse-papiers
    /// Windows (Win+V) et de la synchronisation cloud.
    /// Utilise directement l'API Win32 (OpenClipboard/SetClipboardData) avec plusieurs tentatives : plus fiable que
    /// le presse-papiers WPF quand un autre programme surveille le presse-papiers et l'ouvre à chaque changement.
    /// </summary>
    public static class ClipboardService
    {
        private static readonly DispatcherTimer Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private static byte[] _copiedHash;
        private static DateTime _clearAt;

        /// <summary>Secondes restantes avant effacement (0 = rien en attente). Mis à jour chaque seconde.</summary>
        public static event Action<int> CountdownChanged;

        static ClipboardService()
        {
            Timer.Tick += (s, e) =>
            {
                int left = (int)Math.Ceiling((_clearAt - DateTime.Now).TotalSeconds);
                if (left <= 0) ClearIfOurs();
                else CountdownChanged?.Invoke(left);
            };
        }

        public static bool Copy(string text, int clearSeconds, bool sensitive = true)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool ok = SetText(text, sensitive);
            // Même si Windows signale une erreur, la valeur a pu être copiée : on programme l'effacement dans tous les cas.
            if (sensitive && clearSeconds > 0)
            {
                _copiedHash = Hash(text);
                _clearAt = DateTime.Now.AddSeconds(clearSeconds);
                Timer.Start();
                CountdownChanged?.Invoke(clearSeconds);
            }
            return ok || string.Equals(GetText(), text, StringComparison.Ordinal);
        }

        private static byte[] _temporaryHash;

        /// <summary>Met un fragment dans le presse-papiers le temps d'un collage (brouillage de l'auto-type).</summary>
        public static bool SetTemporary(string text)
        {
            _temporaryHash = Hash(text);
            return SetText(text, sensitive: true);
        }

        /// <summary>Efface le fragment temporaire s'il est encore dans le presse-papiers.</summary>
        public static void ClearTemporary()
        {
            if (_temporaryHash == null) return;
            string current = GetText();
            if (current != null && CryptographicOperations.FixedTimeEquals(Hash(current), _temporaryHash))
                WithClipboard(() => { EmptyClipboard(); return true; });
            _temporaryHash = null;
        }

        /// <summary>Efface le presse-papiers s'il contient encore ce que Verrou y a mis.</summary>
        public static void ClearIfOurs()
        {
            Timer.Stop();
            if (_copiedHash != null)
            {
                string current = GetText();
                if (current != null && CryptographicOperations.FixedTimeEquals(Hash(current), _copiedHash))
                    WithClipboard(() => { EmptyClipboard(); return true; });
            }
            _copiedHash = null;
            CountdownChanged?.Invoke(0);
        }

        private static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

        // ---------------- Win32 ----------------

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        private static bool SetText(string text, bool sensitive) => WithClipboard(() =>
        {
            if (!EmptyClipboard()) return false;
            if (!SetData(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"))) return false;
            if (sensitive)
            {
                // Formats reconnus par Windows 10/11 et les gestionnaires de presse-papiers respectueux.
                SetData(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), new byte[] { 0 });
                SetData(RegisterClipboardFormat("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0));
                SetData(RegisterClipboardFormat("CanUploadToCloudClipboard"), BitConverter.GetBytes(0));
            }
            return true;
        });

        private static string GetText()
        {
            string result = null;
            WithClipboard(() =>
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return true;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return true;
                try { result = Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
                return true;
            });
            return result;
        }

        private static bool SetData(uint format, byte[] data)
        {
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return false; }
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(h);
            if (SetClipboardData(format, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true; // la mémoire appartient désormais au système
        }

        private static bool WithClipboard(Func<bool> action)
        {
            for (int i = 0; i < 40; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try { return action(); }
                    finally { CloseClipboard(); }
                }
                Thread.Sleep(25);
            }
            return false;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
