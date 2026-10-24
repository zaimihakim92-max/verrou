using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Verrou.Core.AutoType;
using Verrou.Core.Model;
using Verrou.Services;
using Verrou.UI;
using WinForms = System.Windows.Forms;

namespace Verrou
{
    public partial class App : Application
    {
        private Mutex _mutex;
        private Mutex _runningMutex; // nom fixe : permet à l'installeur de savoir que Verrou est ouvert
        private EventWaitHandle _showEvent;
        private WinForms.NotifyIcon _tray;
        private HotkeyManager _hotkeys;
        private DispatcherTimer _lockTimer;
        private DateTime _lastActivity = DateTime.Now;
        private (IntPtr Hwnd, string Title)? _pendingAutoType;
        private bool _autoTypeRunning;

        public static new App Current => (App)Application.Current;
        public AppSettings Settings { get; private set; }
        public VaultSession Session { get; private set; }
        public TwoFactorStore TwoFactor { get; private set; }
        public ForegroundTracker Foreground { get; private set; }
        public MainWindow Shell { get; private set; }
        public System.Drawing.Icon TrayIcon { get; private set; }

        /// <summary>Message d'état affiché dans la barre du bas de la fenêtre principale.</summary>
        public event Action<string> StatusMessage;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args.ToList();
            int di = args.FindIndex(a => a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
            if (di >= 0 && di + 1 < args.Count) { AppSettings.UseDataDir(args[di + 1]); args.RemoveRange(di, 2); }
            bool minimized = args.Remove("--minimized");
            string fileArg = args.FirstOrDefault(a => a.EndsWith(".verrou", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

            // Une seule instance par dossier de données : une deuxième ouverture ramène la fenêtre existante.
            string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppSettings.DataDir.ToLowerInvariant()))).Substring(0, 12);
            _mutex = new Mutex(true, "Verrou.SingleInstance." + suffix, out bool first);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Verrou.Show." + suffix);
            if (!first)
            {
                // Double-clic sur un .verrou alors que Verrou est déjà ouvert : on transmet le chemin.
                if (fileArg != null)
                    try { Directory.CreateDirectory(AppSettings.DataDir); File.WriteAllText(OpenRequestFile, Path.GetFullPath(fileArg)); } catch (IOException) { }
                _showEvent.Set();
                Shutdown();
                return;
            }
            _runningMutex = new Mutex(false, "Verrou.Running");
            new Thread(() =>
            {
                while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(new Action(OnShowRequested));
            }) { IsBackground = true }.Start();

            DispatcherUnhandledException += (s, ex) =>
            {
                Verrou.UI.Dialogs.Error(null, "Erreur inattendue :\n\n" + ex.Exception.Message);
                ex.Handled = true;
            };

            Settings = AppSettings.Load();
            WindowSecurity.ExcludeFromCapture = Settings.ProtectFromScreenCapture;
            L.Init(Settings.Language);
            ThemeManager.Apply(Settings.Theme);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            ThemeManager.Changed += UpdateTray;
            Session = new VaultSession(Settings);
            TwoFactor = new TwoFactorStore();
            if (fileArg != null) Session.SetPath(Path.GetFullPath(fileArg));
            else if (File.Exists(Settings.LastVaultPath)) Session.SetPath(Settings.LastVaultPath);
            Session.Unlocked += OnUnlocked;
            Session.Locked += () => { _pendingAutoType = null; UpdateTray(); };

            Foreground = new ForegroundTracker();
            TrayIcon = LoadIcon();
            SetupTray();
            RegisterHotkeys();

            // Verrouillage : inactivité dans Verrou, verrouillage de session Windows, mise en veille.
            InputManager.Current.PreProcessInput += (s, ev) =>
            {
                if (ev.StagingItem.Input is KeyboardEventArgs || ev.StagingItem.Input is MouseButtonEventArgs || ev.StagingItem.Input is MouseWheelEventArgs)
                    _lastActivity = DateTime.Now;
            };
            _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _lockTimer.Tick += (s, ev) =>
            {
                if (Session.IsUnlocked && Settings.AutoLockMinutes > 0 && !_autoTypeRunning &&
                    DateTime.Now - _lastActivity > TimeSpan.FromMinutes(Settings.AutoLockMinutes))
                    LockVault("Verrouillé automatiquement après inactivité.");
            };
            _lockTimer.Start();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Shell = new MainWindow();
            if (!minimized) Shell.Show();
        }

        public void TouchActivity() => _lastActivity = DateTime.Now;

        private static string OpenRequestFile => Path.Combine(AppSettings.DataDir, "open-request.txt");

        private void OnShowRequested()
        {
            try
            {
                if (File.Exists(OpenRequestFile))
                {
                    string path = File.ReadAllText(OpenRequestFile).Trim();
                    File.Delete(OpenRequestFile);
                    if (File.Exists(path) && !string.Equals(path, Session.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        LockVault("Coffre précédent verrouillé.");
                        Session.SetPath(path);
                        Shell?.RefreshView();
                    }
                }
            }
            catch (IOException) { }
            ShowMainWindow();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (!Settings.LockOnSessionLock) return;
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect ||
                e.Reason == SessionSwitchReason.RemoteDisconnect || e.Reason == SessionSwitchReason.SessionLogoff)
                Dispatcher.BeginInvoke(new Action(() => LockVault("Verrouillé : session Windows verrouillée.")));
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
                Dispatcher.BeginInvoke(new Action(ThemeManager.OnSystemThemeChanged));
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
                Dispatcher.Invoke(() => LockVault("Verrouillé : mise en veille."));
        }

        public void LockVault(string reason = "Coffre verrouillé.")
        {
            if (!Session.IsUnlocked) return;
            Session.Lock();
            SetStatus(reason);
        }

        public void SetStatus(string message) => StatusMessage?.Invoke(L.T(message));

        // ---------------- Raccourcis globaux ----------------

        public List<string> RegisterHotkeys()
        {
            _hotkeys?.Dispose();
            _hotkeys = new HotkeyManager();
            var failed = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.AutoTypeHotkey) && !_hotkeys.Register(Settings.AutoTypeHotkey, GlobalAutoType))
                failed.Add(Settings.AutoTypeHotkey);
            if (!string.IsNullOrWhiteSpace(Settings.ShowWindowHotkey) && !_hotkeys.Register(Settings.ShowWindowHotkey, ShowMainWindow))
                failed.Add(Settings.ShowWindowHotkey);
            return failed;
        }

        // ---------------- Auto-type ----------------

        /// <summary>Raccourci global : remplit la fenêtre active avec l'entrée qui lui correspond.</summary>
        public async void GlobalAutoType()
        {
            if (_autoTypeRunning) return;
            IntPtr target = Native.GetForegroundWindow();
            if (target == IntPtr.Zero || Native.BelongsToThisProcess(target)) return;
            string title = Native.GetWindowTitle(target);

            if (!Session.IsUnlocked)
            {
                if (Session.Path == null) { ShowMainWindow(); return; }
                _pendingAutoType = (target, title);
                ShowMainWindow();
                SetStatus($"Déverrouillez le coffre pour remplir « {title} ».");
                return;
            }
            await AutoTypeForWindowAsync(target, title);
        }

        private async void OnUnlocked()
        {
            UpdateTray();
            if (_pendingAutoType is (IntPtr hwnd, string title) && Native.IsWindow(hwnd))
            {
                _pendingAutoType = null;
                Shell.WindowState = WindowState.Minimized;
                await AutoTypeForWindowAsync(hwnd, title);
            }
            _pendingAutoType = null;
        }

        private async Task AutoTypeForWindowAsync(IntPtr target, string title)
        {
            var entries = Session.Vault.Data.Entries;
            var matches = WindowMatcher.FindMatches(entries, title);
            Entry chosen;
            if (matches.Count == 1) chosen = matches[0];
            else
            {
                chosen = AutoTypePickerWindow.Pick(matches.Count > 0 ? matches : entries.Where(x => x.AutoType.Enabled).ToList(),
                    Session.Vault.Data, title, matches.Count == 0);
                if (chosen == null) return;
            }
            await TypeEntryAsync(chosen, target);
        }

        /// <summary>Auto-type depuis la fenêtre de Verrou : vers la dernière fenêtre utilisée avant Verrou.</summary>
        public async Task AutoTypeToPreviousWindowAsync(Entry entry)
        {
            IntPtr target = Foreground.LastExternalWindow;
            if (target == IntPtr.Zero || !Native.IsWindow(target))
            {
                SetStatus("Aucune fenêtre cible : cliquez d'abord dans le formulaire à remplir.");
                return;
            }
            Shell.WindowState = WindowState.Minimized;
            await TypeEntryAsync(entry, target);
        }

        public async Task TypeEntryAsync(Entry entry, IntPtr target)
        {
            if (!Session.IsUnlocked) return;
            string seq = string.IsNullOrWhiteSpace(entry.AutoType.Sequence) ? Session.Vault.Data.Options.DefaultAutoTypeSequence : entry.AutoType.Sequence;
            List<AutoTypeAction> actions;
            try { actions = AutoTypeSequence.Parse(seq, entry); }
            catch (AutoTypeParseException ex)
            {
                Verrou.UI.Dialogs.Warning(null, $"Séquence d'auto-type invalide pour « {entry.Title} » :\n{ex.Message}");
                return;
            }

            _autoTypeRunning = true;
            try
            {
                await AutoTyper.RunAsync(actions, target, Settings.AutoTypeKeyDelayMs, entry.AutoType.Obfuscate);
                SetStatus($"Auto-type : « {entry.Title} » saisi.");
            }
            catch (AutoTyper.AutoTypeAbortedException ex)
            {
                SetStatus(ex.Message);
                _tray?.ShowBalloonTip(4000, "Verrou", L.T(ex.Message), WinForms.ToolTipIcon.Warning);
            }
            finally
            {
                _autoTypeRunning = false;
                _lastActivity = DateTime.Now;
            }
        }

        // ---------------- Fenêtre et zone de notification ----------------

        public void ShowMainWindow()
        {
            if (Shell == null) return;
            Shell.Show();
            if (Shell.WindowState == WindowState.Minimized) Shell.WindowState = WindowState.Normal;
            Shell.Activate();
            Shell.Topmost = true;
            Shell.Topmost = false;
            Shell.Focus();
        }

        private static System.Drawing.Icon LoadIcon()
        {
            var sri = GetResourceStream(new Uri("pack://application:,,,/Assets/Verrou.ico"));
            using var s = sri.Stream;
            return new System.Drawing.Icon(s);
        }

        private void SetupTray()
        {
            _tray = new WinForms.NotifyIcon { Icon = TrayIcon, Text = "Verrou", Visible = true };
            _tray.DoubleClick += (s, e) => ShowMainWindow();
            UpdateTray();
        }

        private void UpdateTray()
        {
            if (_tray == null) return;
            bool light = ThemeManager.IsLight;
            var menu = new WinForms.ContextMenuStrip
            {
                Renderer = light ? new WinForms.ToolStripProfessionalRenderer() : new WinForms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
                BackColor = light ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(0x1E, 0x25, 0x2F),
                ForeColor = light ? System.Drawing.Color.FromArgb(0x0B, 0x0F, 0x14) : System.Drawing.Color.FromArgb(0xE8, 0xED, 0xF2),
                ShowImageMargin = false,
                Font = new System.Drawing.Font("Segoe UI", 9.5f),
            };
            menu.Items.Add(L.T("Afficher Verrou"), null, (s, e) => ShowMainWindow());
            if (Session.IsUnlocked) menu.Items.Add(L.T("Verrouiller"), null, (s, e) => LockVault());
            menu.Items.Add(L.T("Générateur de mots de passe"), null, (s, e) => new GeneratorWindow(false).Show());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(L.T("Quitter"), null, (s, e) => Quit());
            _tray.ContextMenuStrip?.Dispose();
            _tray.ContextMenuStrip = menu;
            _tray.Text = L.T(Session.IsUnlocked ? "Verrou — déverrouillé" : "Verrou — verrouillé");
        }

        /// <summary>Couleurs du menu de la zone de notification (charte sombre).</summary>
        private sealed class DarkMenuColors : WinForms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x1E, 0x25, 0x2F);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x26, 0x2E, 0x39);
            private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x3A, 0x44, 0x52);
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
            public override System.Drawing.Color MenuBorder => Border;
            public override System.Drawing.Color MenuItemBorder => Hover;
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(0x2A, 0x32, 0x3D);
            public override System.Drawing.Color SeparatorLight => Bg;
        }

        public bool IsQuitting { get; private set; }

        public void Quit()
        {
            if (IsQuitting) return;
            IsQuitting = true;
            Session.Lock();
            ClipboardService.ClearIfOurs();
            Settings.Save();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Session?.Lock();
            _hotkeys?.Dispose();
            Foreground?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnExit(e);
        }
    }
}
