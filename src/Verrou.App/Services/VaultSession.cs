using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Verrou.Core.Storage;

namespace Verrou.Services
{
    /// <summary>
    /// Coffre actuellement ouvert. Enregistre automatiquement après chaque modification (avec une sauvegarde
    /// horodatée avant la première écriture de chaque session) et efface la clé de la mémoire au verrouillage.
    /// </summary>
    public sealed class VaultSession
    {
        private readonly AppSettings _settings;
        private bool _backupDone;

        public VaultSession(AppSettings settings) => _settings = settings;

        public OpenedVault Vault { get; private set; }
        public bool IsUnlocked => Vault != null;
        /// <summary>Chemin du coffre (gardé après verrouillage pour le déverrouiller).</summary>
        public string Path { get; private set; }

        public event Action Unlocked;
        public event Action Locked;
        public event Action Changed;

        public void SetPath(string path) => Path = path;

        public void Unlock(OpenedVault vault)
        {
            Lock(raise: false);
            Vault = vault;
            Path = vault.Path;
            _backupDone = false;
            _settings.AddRecent(vault.Path);
            _settings.Save();
            Unlocked?.Invoke();
        }

        public void Lock() => Lock(raise: true);

        private void Lock(bool raise)
        {
            if (Vault == null) return;
            Vault.Dispose();      // efface la clé maîtresse
            Vault = null;
            ClipboardService.ClearIfOurs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (raise) Locked?.Invoke();
        }

        /// <summary>Enregistre le coffre. Renvoie false (et affiche l'erreur) en cas d'échec.</summary>
        public bool Save()
        {
            if (Vault == null) return false;
            try
            {
                if (!_backupDone)
                {
                    BackupManager.CreateBackup(Vault.Path, _settings.BackupDirFor(Vault.Path), _settings.BackupCount);
                    _backupDone = true;
                }
                VaultStore.Save(Vault);
                return true;
            }
            catch (Exception ex)
            {
                Verrou.UI.Dialogs.Error(null, "L'enregistrement du coffre a échoué :\n\n" + ex.Message);
                return false;
            }
        }

        /// <summary>À appeler après chaque modification du contenu.</summary>
        public void MarkChanged()
        {
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Options de sécurité appliquées à chaque fenêtre de Verrou.</summary>
    public static class WindowSecurity
    {
        public static bool ExcludeFromCapture { get; set; } = true;
        private static readonly List<WeakReference<Window>> Windows = new List<WeakReference<Window>>();

        public static void Protect(Window w)
        {
            w.SourceInitialized += (s, e) => { Apply(w); Verrou.UI.Theme.ApplyTitleBar(w); };
            Windows.Add(new WeakReference<Window>(w));
        }

        public static void ApplyToAll()
        {
            foreach (var wr in Windows)
                if (wr.TryGetTarget(out var w)) { Apply(w); Verrou.UI.Theme.ApplyTitleBar(w); }
        }

        private static void Apply(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero)
                Native.SetWindowDisplayAffinity(h, ExcludeFromCapture ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
        }
    }

    /// <summary>Retient la dernière fenêtre active qui n'appartient pas à Verrou (cible de l'auto-type lancé depuis Verrou).</summary>
    public sealed class ForegroundTracker : IDisposable
    {
        private readonly Native.WinEventDelegate _callback; // gardé en champ pour ne pas être collecté
        private readonly IntPtr _hook;

        public IntPtr LastExternalWindow { get; private set; }

        public ForegroundTracker()
        {
            _callback = OnEvent;
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            var fg = Native.GetForegroundWindow();
            if (!Native.BelongsToThisProcess(fg)) LastExternalWindow = fg;
        }

        private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd != IntPtr.Zero && !Native.BelongsToThisProcess(hwnd) && Native.GetWindowTitle(hwnd).Length > 0)
                LastExternalWindow = hwnd;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        }
    }
}
