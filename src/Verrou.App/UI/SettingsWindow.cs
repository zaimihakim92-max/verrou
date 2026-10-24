using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Verrou.Core.AutoType;
using Verrou.Services;

namespace Verrou.UI
{
    public sealed class SettingsWindow : Window
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private readonly AppSettings _s = App.Current.Settings;
        private readonly StackPanel _root = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };

        private readonly TextBox _autoLock = Num(), _clipSecs = Num(), _keyDelay = Num(), _backupCount = Num(), _historyMax = Num();
        private readonly CheckBox _lockMin = new CheckBox { Content = "Verrouiller quand la fenêtre est réduite" };
        private readonly CheckBox _lockSession = new CheckBox { Content = "Verrouiller quand la session Windows est verrouillée (Win+L) — toujours fait à la mise en veille" };
        private readonly CheckBox _noCapture = new CheckBox { Content = "Masquer les fenêtres de Verrou aux captures d'écran et au partage d'écran" };
        private readonly CheckBox _secureDesktop = new CheckBox { Content = "Saisir le mot de passe maître et le code 2FA sur un bureau sécurisé (protège des enregistreurs de frappe)" };
        private readonly CheckBox _rememberKf = new CheckBox { Content = "Mémoriser l'emplacement du fichier-clé (jamais son contenu)" };
        private readonly CheckBox _tray = new CheckBox { Content = "Fermer la fenêtre garde Verrou dans la zone de notification (l'auto-type reste disponible)" };
        private readonly CheckBox _startup = new CheckBox { Content = "Lancer Verrou au démarrage de Windows (réduit dans la zone de notification)" };
        private readonly TextBox _hotkey = HotkeyBox(), _showHotkey = HotkeyBox();
        private readonly TextBox _defaultSeq = new TextBox { FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") };
        private readonly TextBox _backupDir = new TextBox();
        private readonly ComboBox _theme = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly ComboBox _language = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _error = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DangerBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

        public SettingsWindow()
        {
            Title = "Paramètres de Verrou";
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Style = (Style)Application.Current.Resources["BaseWindow"];
            SetResourceReference(BackgroundProperty, "SurfaceBrush");
            WindowSecurity.Protect(this);
            foreach (var cb in new[] { _lockMin, _lockSession, _noCapture, _secureDesktop, _rememberKf, _tray, _startup }) cb.Style = Theme.Style("Switch");

            var session = App.Current.Session;

            Section("Apparence");
            foreach (var mode in new[] { ThemeManager.Auto, ThemeManager.Light, ThemeManager.Dark })
                _theme.Items.Add(new ComboBoxItem { Content = ThemeManager.Label(mode), Tag = mode });
            Row("Thème", _theme, "");
            _language.Items.Add(new ComboBoxItem { Content = "Automatique (langue de Windows)", Tag = L.Auto });
            _language.Items.Add(new ComboBoxItem { Content = "Français", Tag = L.French });
            _language.Items.Add(new ComboBoxItem { Content = "English", Tag = L.English });
            Row("Langue", _language, "");
            _theme.SelectionChanged += (s, e) =>
            {
                // Aperçu immédiat ; « Annuler » revient au thème précédent.
                if (_theme.SelectedItem is ComboBoxItem it) ThemeManager.Apply((string)it.Tag);
            };

            Section("Sécurité");
            Row("Verrouiller après", _autoLock, "minutes d'inactivité dans Verrou (0 = jamais)");
            _root.Children.Add(Indent(_lockMin));
            _root.Children.Add(Indent(_lockSession));
            Row("Presse-papiers", _clipSecs, "secondes avant effacement automatique (0 = jamais)");
            _root.Children.Add(Indent(_secureDesktop));
            _root.Children.Add(Indent(_noCapture));
            _root.Children.Add(Indent(_rememberKf));

            Section("Auto-type");
            Row("Raccourci global", _hotkey, "dans la page de connexion : remplit avec l'entrée correspondante");
            Row("Afficher Verrou", _showHotkey, "raccourci global pour ramener la fenêtre");
            Row("Délai entre touches", _keyDelay, "millisecondes (augmentez si des caractères se perdent)");
            if (session.IsUnlocked)
            {
                var seqRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                seqRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                seqRow.ColumnDefinitions.Add(new ColumnDefinition());
                seqRow.Children.Add(Label("Séquence par défaut"));
                Grid.SetColumn(_defaultSeq, 1);
                seqRow.Children.Add(_defaultSeq);
                _root.Children.Add(seqRow);
            }

            Section("Coffre et sauvegardes");
            if (session.IsUnlocked) Row("Historique", _historyMax, "versions gardées par entrée (dans ce coffre)");
            Row("Sauvegardes", _backupCount, "copies chiffrées gardées (une par session de modification, 0 = aucune)");
            var dirRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            dirRow.ColumnDefinitions.Add(new ColumnDefinition());
            dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dirRow.Children.Add(Label("Dossier"));
            Grid.SetColumn(_backupDir, 1);
            dirRow.Children.Add(_backupDir);
            var browse = new Button { Content = "Parcourir…" };
            browse.Click += (s, e) =>
            {
                var d = new OpenFolderDialog { Title = "Dossier des sauvegardes" };
                if (d.ShowDialog(this) == true) _backupDir.Text = d.FolderName;
            };
            Grid.SetColumn(browse, 2);
            dirRow.Children.Add(browse);
            _root.Children.Add(dirRow);
            _root.Children.Add(new TextBlock { Text = "Vide = dossier « Verrou-sauvegardes » à côté du coffre. Mettez-le de préférence sur un autre disque ou une clé USB.", Style = (Style)Application.Current.Resources["HelpText"], Margin = new Thickness(150, 2, 0, 0) });

            Section("Général");
            _root.Children.Add(Indent(_tray));
            _root.Children.Add(Indent(_startup));

            _root.Children.Add(_error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var ok = new Button { Content = "Enregistrer", IsDefault = true, MinWidth = 110, Style = (Style)Application.Current.Resources["PrimaryButton"] };
            ok.Click += (s, e) => Save();
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Annuler", IsCancel = true, MinWidth = 90 });
            _root.Children.Add(buttons);
            Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };

            // Valeurs actuelles
            foreach (ComboBoxItem it in _theme.Items) if ((string)it.Tag == (_s.Theme ?? ThemeManager.Auto)) _theme.SelectedItem = it;
            if (_theme.SelectedItem == null) _theme.SelectedIndex = 0;
            foreach (ComboBoxItem it in _language.Items) if ((string)it.Tag == (_s.Language ?? L.Auto)) _language.SelectedItem = it;
            if (_language.SelectedItem == null) _language.SelectedIndex = 0;
            L.TrackText(_error);
            Closed += (s, e) => { if (DialogResult != true) ThemeManager.Apply(_s.Theme); };
            _autoLock.Text = _s.AutoLockMinutes.ToString();
            _lockMin.IsChecked = _s.LockOnMinimize;
            _lockSession.IsChecked = _s.LockOnSessionLock;
            _clipSecs.Text = _s.ClipboardClearSeconds.ToString();
            _noCapture.IsChecked = _s.ProtectFromScreenCapture;
            _secureDesktop.IsChecked = _s.UseSecureDesktop;
            _rememberKf.IsChecked = _s.RememberKeyFilePath;
            _hotkey.Text = _s.AutoTypeHotkey;
            _showHotkey.Text = _s.ShowWindowHotkey;
            _keyDelay.Text = _s.AutoTypeKeyDelayMs.ToString();
            _backupCount.Text = _s.BackupCount.ToString();
            _backupDir.Text = _s.BackupFolder;
            _tray.IsChecked = _s.CloseToTray;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) _startup.IsChecked = k?.GetValue("Verrou") != null;
            if (session.IsUnlocked)
            {
                _defaultSeq.Text = session.Vault.Data.Options.DefaultAutoTypeSequence;
                _historyMax.Text = session.Vault.Data.Options.HistoryMaxItems.ToString();
            }
        }

        private static TextBox Num() => new TextBox { Width = 70, HorizontalAlignment = HorizontalAlignment.Left };

        private static TextBox HotkeyBox()
        {
            var tb = new TextBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Left, IsReadOnly = true, IsReadOnlyCaretVisible = false, ToolTip = "Cliquez puis appuyez sur la combinaison (Retour arrière = aucun)" };
            tb.PreviewKeyDown += (s, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Back || key == Key.Delete) { tb.Text = ""; return; }
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin || key == Key.Tab) return;
                if (Keyboard.Modifiers == ModifierKeys.None) return;
                tb.Text = HotkeyManager.Format(Keyboard.Modifiers, key);
            };
            return tb;
        }

        private void Section(string title) =>
            _root.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitle"], Margin = new Thickness(0, 14, 0, 4) });

        private static TextBlock Label(string text) => new TextBlock { Text = text, Style = (Style)Application.Current.Resources["FieldLabel"] };

        private static FrameworkElement Indent(FrameworkElement e)
        {
            e.Margin = new Thickness(150, 3, 0, 3);
            if (e is CheckBox cb) cb.Content = new TextBlock { Text = (string)cb.Content, TextWrapping = TextWrapping.Wrap };
            return e;
        }

        private void Row(string label, Control input, string help)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Label(label));
            Grid.SetColumn(input, 1);
            g.Children.Add(input);
            var h = new TextBlock { Text = help, Style = (Style)Application.Current.Resources["HelpText"], Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(h, 2);
            g.Children.Add(h);
            _root.Children.Add(g);
        }

        private static bool TryInt(TextBox tb, int min, int max, out int v) => int.TryParse(tb.Text.Trim(), out v) && v >= min && v <= max;

        private void Save()
        {
            var session = App.Current.Session;
            if (!TryInt(_autoLock, 0, 1440, out int autoLock)) { _error.Text = "Verrouillage : nombre de minutes entre 0 et 1440."; return; }
            if (!TryInt(_clipSecs, 0, 3600, out int clip)) { _error.Text = "Presse-papiers : nombre de secondes entre 0 et 3600."; return; }
            if (!TryInt(_keyDelay, 0, 500, out int delay)) { _error.Text = "Délai entre touches : entre 0 et 500 ms."; return; }
            if (!TryInt(_backupCount, 0, 1000, out int backups)) { _error.Text = "Sauvegardes : entre 0 et 1000."; return; }
            int history = 0;
            if (session.IsUnlocked)
            {
                if (!TryInt(_historyMax, 0, 500, out history)) { _error.Text = "Historique : entre 0 et 500 versions."; return; }
                try { AutoTypeSequence.Validate(_defaultSeq.Text); }
                catch (AutoTypeParseException ex) { _error.Text = "Séquence par défaut : " + ex.Message; return; }
            }

            _s.AutoLockMinutes = autoLock;
            _s.LockOnMinimize = _lockMin.IsChecked == true;
            _s.LockOnSessionLock = _lockSession.IsChecked == true;
            _s.ClipboardClearSeconds = clip;
            _s.ProtectFromScreenCapture = _noCapture.IsChecked == true;
            _s.UseSecureDesktop = _secureDesktop.IsChecked == true;
            _s.RememberKeyFilePath = _rememberKf.IsChecked == true;
            if (!_s.RememberKeyFilePath) _s.KeyFilePaths.Clear();
            _s.AutoTypeHotkey = _hotkey.Text;
            _s.ShowWindowHotkey = _showHotkey.Text;
            _s.AutoTypeKeyDelayMs = delay;
            _s.BackupCount = backups;
            _s.BackupFolder = _backupDir.Text.Trim();
            _s.CloseToTray = _tray.IsChecked == true;
            _s.Theme = (_theme.SelectedItem as ComboBoxItem)?.Tag as string ?? ThemeManager.Auto;
            string newLang = (_language.SelectedItem as ComboBoxItem)?.Tag as string ?? L.Auto;
            bool langChanged = L.Resolve(newLang) != L.Resolve(_s.Language ?? L.Auto);
            _s.Language = newLang;
            _s.Save();

            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (_startup.IsChecked == true) k.SetValue("Verrou", $"\"{Environment.ProcessPath}\" --minimized");
                else if (k.GetValue("Verrou") != null) k.DeleteValue("Verrou");
            }

            WindowSecurity.ExcludeFromCapture = _s.ProtectFromScreenCapture;
            WindowSecurity.ApplyToAll();

            if (session.IsUnlocked)
            {
                var o = session.Vault.Data.Options;
                if (o.HistoryMaxItems != history || o.DefaultAutoTypeSequence != _defaultSeq.Text.Trim())
                {
                    o.HistoryMaxItems = history;
                    o.DefaultAutoTypeSequence = _defaultSeq.Text.Trim();
                    session.MarkChanged();
                }
            }

            var failed = App.Current.RegisterHotkeys();
            if (failed.Count > 0)
                Dialogs.Error(this, "Ce raccourci est déjà utilisé par une autre application : " + string.Join(", ", failed) + "\nChoisissez-en un autre.");
            if (langChanged)
                Dialogs.Info(this, L.Resolve(newLang) == L.English
                    ? "The language will be applied the next time Verrou starts."
                    : "La langue sera appliquée au prochain démarrage de Verrou.");
            DialogResult = true;
        }
    }
}
