using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Verrou.Core.Crypto;
using Verrou.Core.Import;
using Verrou.Core.Model;
using Verrou.Core.Storage;
using Verrou.Services;

namespace Verrou.UI
{
    public partial class MainWindow : Window
    {
        private const string DragFormat = "VerrouEntryIds";

        public sealed class EntryRow
        {
            public Entry Entry { get; set; }
            public string Title => Entry.Title;
            public string UserName => Entry.UserName;
            public string Url => Entry.Url;
            public string GroupPath { get; set; }
            public string ModifiedText => Entry.Modified.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            public bool IsExpired => Entry.Expires is DateTime d && d < DateTime.UtcNow;
        }

        private static App A => App.Current;
        private static VaultSession S => A.Session;
        private static VaultData D => S.Vault?.Data;

        private Guid? _selectedGroup;
        private Point _dragStart;
        private bool _unlocking;

        public MainWindow()
        {
            InitializeComponent();
            WindowSecurity.Protect(this);
            Icon = Imaging.CreateBitmapSourceFromHIcon(A.TrayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            WelcomeLogo.Source = Icon;

            S.Unlocked += OnUnlocked;
            S.Locked += OnLocked;
            A.StatusMessage += msg => StatusText.Text = msg;
            ClipboardService.CountdownChanged += n => ClipText.Text = n > 0 ? $"⎘ Presse-papiers effacé dans {n} s" : "";

            L.TrackText(StatusText, EmptyListText, ClipText, InfoText, UnlockError, RecoveryHelp);
            L.TrackContent(UnlockButton, RecoveryLink);
            PreviewKeyDown += OnPreviewKeyDown;
            ThemeManager.Changed += () =>
            {
                if (!S.IsUnlocked) return;
                BuildTree();
                ShowDetails(Current);
            };
            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Minimized && A.Settings.LockOnMinimize) A.LockVault("Verrouillé à la réduction de la fenêtre.");
            };
            Closing += (s, e) =>
            {
                if (A.IsQuitting) return;
                if (A.Settings.CloseToTray && !_quitting)
                {
                    e.Cancel = true;
                    Hide();
                    A.SetStatus("Verrou continue dans la zone de notification (raccourci d'auto-type actif).");
                }
                else Dispatcher.BeginInvoke(new Action(A.Quit));
            };
            Activated += (s, e) => UpdateCapsLock();
            MasterPwd.KeyUp += (s, e) => UpdateCapsLock();
            SearchBox.GotKeyboardFocus += (s, e) => SearchHint.Visibility = Visibility.Collapsed;
            SearchBox.LostKeyboardFocus += (s, e) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateView();
            StatusText.Text = $"Auto-type : {A.Settings.AutoTypeHotkey} dans n'importe quelle fenêtre de connexion.";
        }

        private bool _quitting;
        public void QuitFromWindow() { _quitting = true; Close(); }

        // ================= États : accueil / verrouillé / ouvert =================

        public void RefreshView() => UpdateView();

        private void UpdateView()
        {
            bool unlocked = S.IsUnlocked;
            VaultView.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
            LockView.Visibility = !unlocked && S.Path != null ? Visibility.Visible : Visibility.Collapsed;
            WelcomeView.Visibility = !unlocked && S.Path == null ? Visibility.Visible : Visibility.Collapsed;
            foreach (var b in new[] { BtnLock, BtnAddEntry, BtnAddGroup, BtnImport, BtnExport }) b.IsEnabled = unlocked;
            SearchBox.IsEnabled = unlocked;
            Title = unlocked ? $"{D.Name} — Verrou" : "Verrou";
            InfoText.Text = unlocked
                ? (A.Settings.AutoLockMinutes > 0 ? $"Verrouillage auto : {A.Settings.AutoLockMinutes} min" : "Verrouillage auto désactivé")
                : "";

            if (!unlocked && S.Path != null)
            {
                LockTitle.Text = Path.GetFileNameWithoutExtension(S.Path);
                LockPath.Text = S.Path;
                LockPath.ToolTip = S.Path;
                UnlockErrorBox.Visibility = Visibility.Collapsed;
                bool needsKf = false;
                _header = null;
                try
                {
                    if (File.Exists(S.Path))
                    {
                        _header = ReadHeader(S.Path);
                        needsKf = _header.RequiresKeyFile;
                    }
                }
                catch (Exception) { }
                SetupTwoFactorFields(recovery: false);
                A.Settings.KeyFilePaths.TryGetValue(S.Path, out var kf);
                LockUseKeyFile.IsChecked = needsKf || !string.IsNullOrEmpty(kf);
                LockKeyFile.Text = kf ?? "";
                Dispatcher.BeginInvoke(new Action(() => { if (A.Settings.UseSecureDesktop) UnlockButton.Focus(); else MasterPwd.Focus(); }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void OnUnlocked()
        {
            _selectedGroup = D.RootGroupId;
            SearchBox.Text = "";
            BuildTree();
            RefreshList();
            UpdateView();
            StatusText.Text = $"Coffre ouvert : {D.Entries.Count} entrée(s). Auto-type : {A.Settings.AutoTypeHotkey}.";
        }

        private void OnLocked()
        {
            foreach (Window w in OwnedWindows.Cast<Window>().ToList()) w.Close();
            foreach (Window w in Application.Current.Windows.OfType<EntryWindow>().ToList()) w.Close();
            GroupTree.Items.Clear();
            EntryList.ItemsSource = null;
            DetailsPanel.Children.Clear();
            SearchBox.Text = "";
            MasterPwd.Clear();
            UpdateView();
        }

        private VaultHeaderInfo _header;
        private bool _recoveryMode;

        private static VaultHeaderInfo ReadHeader(string path)
        {
            using var fs = File.OpenRead(path);
            byte[] buf = new byte[VaultFile.HeaderLength + VaultFile.TagLength];
            int n = 0;
            while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r == 0) break; n += r; }
            return VaultFile.ReadHeader(buf.AsSpan(0, n));
        }

        /// <summary>Champs de double authentification de l'écran verrouillé (code, ou clé de secours).</summary>
        private void SetupTwoFactorFields(bool recovery)
        {
            bool secure = A.Settings.UseSecureDesktop;
            PasswordRow.Visibility = secure ? Visibility.Collapsed : Visibility.Visible;
            SecureHint.Visibility = secure ? Visibility.Visible : Visibility.Collapsed;
            UnlockButton.Content = secure ? "Déverrouiller (saisie sécurisée)" : "Déverrouiller";
            bool required = _header?.RequiresTwoFactor == true;
            TwoFactorPanel.Visibility = required ? Visibility.Visible : Visibility.Collapsed;
            CodeBox.Clear();
            RecoveryBox.Clear();
            if (!required) { _recoveryMode = false; return; }
            bool known = A.TwoFactor.Has(_header.TwoFactorId);
            _recoveryMode = recovery || !known;
            CodeRow.Visibility = _recoveryMode || secure ? Visibility.Collapsed : Visibility.Visible;
            RecoveryRow.Visibility = _recoveryMode && !secure ? Visibility.Visible : Visibility.Collapsed;
            RecoveryLink.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
            RecoveryLink.Content = _recoveryMode ? "J'ai mon téléphone : utiliser le code" : "Téléphone perdu ? Utiliser la clé de secours";
            RecoveryHelp.Text = known
                ? "La clé de secours (32 caractères notés à l'activation) remplace le code du téléphone."
                : "Ce PC ne connaît pas encore la double authentification de ce coffre (autre PC, nouvelle session ou réinstallation de Windows). Entrez une fois la clé de secours notée à l'activation : elle sera ensuite mémorisée, protégée par Windows.";
        }

        private void RecoveryLink_Click(object sender, RoutedEventArgs e)
        {
            SetupTwoFactorFields(!_recoveryMode);
            if (_recoveryMode) RecoveryBox.Focus(); else CodeBox.Focus();
        }

        private void UpdateCapsLock() =>
            CapsLockText.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

        // ================= Déverrouillage =================

        private void MasterPwd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Unlock_Click(sender, e); e.Handled = true; }
        }

        private void LockUseKeyFile_Changed(object sender, RoutedEventArgs e) => LockKeyFileRow.IsEnabled = LockUseKeyFile.IsChecked == true;

        private void LockBrowseKeyFile_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Tous les fichiers (*.*)|*.*", Title = "Choisir le fichier-clé" };
            if (d.ShowDialog(this) == true) LockKeyFile.Text = d.FileName;
        }

        private async void Unlock_Click(object sender, RoutedEventArgs e)
        {
            if (_unlocking || S.Path == null) return;
            UnlockErrorBox.Visibility = Visibility.Collapsed;
            string path = S.Path;
            byte[] keyFile = null, secret = null;
            long codeStep = -1;
            bool usedRecovery = false;
            CompositeKey ck;
            string securePwd = null, codeText = CodeBox.Text, recoveryText = RecoveryBox.Text;
            if (A.Settings.UseSecureDesktop)
            {
                VaultHeaderInfo h0;
                try { h0 = ReadHeader(path); }
                catch (Exception ex) when (ex is IOException || ex is VaultException || ex is UnauthorizedAccessException) { ShowUnlockError(ex.Message); return; }
                var factor = !h0.RequiresTwoFactor ? SecureDesktop.SecondFactor.None
                    : (_recoveryMode || !A.TwoFactor.Has(h0.TwoFactorId)) ? SecureDesktop.SecondFactor.Recovery : SecureDesktop.SecondFactor.Code;
                SecureDesktop.Result sr;
                _unlocking = true;
                try { sr = await SecureDesktop.PromptAsync(Path.GetFileNameWithoutExtension(path), factor); }
                catch (Exception ex)
                {
                    ShowUnlockError("Bureau sécurisé indisponible : " + ex.Message + " Désactivez l'option dans les Paramètres pour saisir ici.");
                    return;
                }
                finally { _unlocking = false; Activate(); }
                if (sr == null) return;
                securePwd = sr.Password;
                codeText = sr.Code;
                recoveryText = sr.Recovery;
            }
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("Fichier du coffre introuvable :\n" + path);
                var header = ReadHeader(path);
                _header = header;
                if (LockUseKeyFile.IsChecked == true) keyFile = CredentialsWindow.ReadKeyFile(LockKeyFile.Text.Trim());
                if ((securePwd != null ? securePwd.Length : MasterPwd.SecurePassword.Length) == 0 && keyFile == null)
                {
                    if (securePwd == null) MasterPwd.Focus(); else ShowUnlockError("Mot de passe maître vide.");
                    return;
                }

                if (header.RequiresTwoFactor)
                {
                    if (_recoveryMode || !A.TwoFactor.Has(header.TwoFactorId))
                    {
                        if (string.IsNullOrWhiteSpace(recoveryText)) { ShowUnlockError("Entrez la clé de secours de double authentification."); RecoveryBox.Focus(); return; }
                        try { secret = Base32.Decode(recoveryText); }
                        catch (FormatException ex) { ShowUnlockError("Clé de secours invalide : " + ex.Message); return; }
                        if (secret.Length != Totp.SecretLength) { ShowUnlockError("Clé de secours invalide : elle doit compter 32 caractères (A à Z, 2 à 7)."); return; }
                        usedRecovery = true;
                    }
                    else
                    {
                        int locked = A.TwoFactor.LockedSeconds(header.TwoFactorId);
                        if (locked > 0)
                        {
                            ShowUnlockError($"Trop de codes incorrects. Réessayez dans {locked} s (ou utilisez la clé de secours).");
                            return;
                        }
                        secret = await A.TwoFactor.GetAsync(header.TwoFactorId);
                        if (secret == null)
                        {
                            ShowUnlockError("Le secret 2FA mémorisé sur ce PC est illisible (autre compte Windows ?). Utilisez la clé de secours.");
                            SetupTwoFactorFields(recovery: true);
                            return;
                        }
                        if (!Totp.Verify(secret, codeText, DateTimeOffset.UtcNow, A.TwoFactor.LastStep(header.TwoFactorId), out codeStep))
                        {
                            CryptographicOperations.ZeroMemory(secret);
                            int wait = A.TwoFactor.RegisterFailure(header.TwoFactorId);
                            if (wait > 0)
                                ShowUnlockError($"Code incorrect. Saisie bloquée {wait} s après {A.TwoFactor.FailedAttempts(header.TwoFactorId)} essais (le blocage survit au redémarrage de Verrou).");
                            else ShowUnlockError(string.IsNullOrWhiteSpace(codeText)
                                ? "Entrez le code à 6 chiffres affiché par votre application d'authentification."
                                : "Code de double authentification incorrect ou déjà utilisé. Attendez le code suivant et vérifiez l'heure du téléphone.");
                            CodeBox.SelectAll();
                            CodeBox.Focus();
                            return;
                        }
                    }
                }
                ck = securePwd != null ? CompositeKey.Create(securePwd, keyFile, secret) : Dialogs.KeyFrom(MasterPwd, keyFile, secret);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is VaultException || ex is WindowsHello.HelloException)
            {
                if (secret != null) CryptographicOperations.ZeroMemory(secret);
                ShowUnlockError(ex.Message);
                return;
            }
            finally
            {
                if (keyFile != null) CryptographicOperations.ZeroMemory(keyFile);
            }

            _unlocking = true;
            UnlockButton.IsEnabled = false;
            UnlockButton.Content = "Déchiffrement…";
            try
            {
                var vault = await Task.Run(() => VaultStore.Open(path, ck));
                if (LockUseKeyFile.IsChecked == true && A.Settings.RememberKeyFilePath) A.Settings.KeyFilePaths[path] = LockKeyFile.Text.Trim();
                else A.Settings.KeyFilePaths.Remove(path);
                if (vault.Key.RequiresTwoFactor)
                {
                    string id = vault.Key.TwoFactorId;
                    if (usedRecovery) await A.TwoFactor.SetAsync(id, secret); // mémorisée pour les prochaines fois (DPAPI)
                    else A.TwoFactor.SetLastStep(id, codeStep);               // ce code ne pourra pas resservir
                    A.TwoFactor.ResetFailures(id);
                }
                MasterPwd.Clear();
                CodeBox.Clear();
                RecoveryBox.Clear();
                S.Unlock(vault);
                if (usedRecovery) StatusText.Text = "Clé de secours acceptée : la double authentification est de nouveau active sur ce PC.";
            }
            catch (WrongCredentialsException ex)
            {
                ShowUnlockError(usedRecovery ? "Mot de passe maître, fichier-clé ou clé de secours incorrect." : ex.Message);
                MasterPwd.SelectAll();
                MasterPwd.Focus();
            }
            catch (Exception ex) when (ex is VaultException || ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowUnlockError(ex.Message);
            }
            finally
            {
                ck.Dispose();
                if (secret != null) CryptographicOperations.ZeroMemory(secret);
                _unlocking = false;
                UnlockButton.IsEnabled = true;
                UnlockButton.Content = A.Settings.UseSecureDesktop ? "Déverrouiller (saisie sécurisée)" : "Déverrouiller";
            }
        }

        private void ShowUnlockError(string msg)
        {
            UnlockError.Text = msg;
            UnlockErrorBox.Visibility = Visibility.Visible;
        }

        // ================= Arbre des groupes =================

        private void BuildTree()
        {
            GroupTree.Items.Clear();
            if (D == null) return;
            GroupTree.Items.Add(MakeGroupItem(D.Root));
        }

        private TreeViewItem MakeGroupItem(Group g)
        {
            int count = D.Entries.Count(e => e.GroupId == g.Id);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var icon = Dialogs.Icon(g.Id == D.RootGroupId ? "" : "", 14);
            icon.Foreground = (Brush)FindResource(g.Id == D.RootGroupId ? "AccentDarkBrush" : "MutedTextBrush");
            icon.Margin = new Thickness(0, 0, 7, 0);
            header.Children.Add(icon);
            header.Children.Add(new TextBlock { Text = g.Name, Tag = L.DataTag, FontWeight = g.Id == D.RootGroupId ? FontWeights.SemiBold : FontWeights.Normal });
            if (count > 0) header.Children.Add(new TextBlock { Text = "  " + count, Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

            var item = new TreeViewItem { Header = header, Tag = g.Id, IsExpanded = g.IsExpanded, AllowDrop = true };
            item.Expanded += (s, e) => { if (e.OriginalSource == item) g.IsExpanded = true; };
            item.Collapsed += (s, e) => { if (e.OriginalSource == item) g.IsExpanded = false; };
            item.Drop += (s, e) => { e.Handled = true; DropOnGroup(g.Id, e); };
            item.DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };

            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Nouvelle entrée ici", "", () => AddEntry(g.Id)));
            menu.Items.Add(MenuItem("Nouveau sous-groupe…", "", () => AddGroup(g.Id)));
            menu.Items.Add(MenuItem("Renommer…", "", () => RenameGroup(g)));
            if (g.Id != D.RootGroupId) menu.Items.Add(MenuItem("Supprimer le groupe…", "", () => DeleteGroup(g)));
            item.ContextMenu = menu;
            item.PreviewMouseRightButtonDown += (s, e) => { if (s == item) { item.IsSelected = true; } };

            foreach (var c in D.ChildrenOf(g.Id)) item.Items.Add(MakeGroupItem(c));
            if (_selectedGroup == g.Id) item.IsSelected = true;
            return item;
        }

        private static MenuItem MenuItem(string text, string glyph, Action action, string gesture = null)
        {
            var mi = new MenuItem { Header = text, InputGestureText = gesture ?? "", Icon = Dialogs.Icon(glyph, 13) };
            mi.Click += (s, e) => action();
            return mi;
        }

        private void GroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (GroupTree.SelectedItem is TreeViewItem tvi && tvi.Tag is Guid id && id != _selectedGroup)
            {
                _selectedGroup = id;
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else RefreshList();
            }
        }

        private void AddGroup_Click(object sender, RoutedEventArgs e) => AddGroup(_selectedGroup ?? D.RootGroupId);

        private void AddGroup(Guid parent)
        {
            string name = Dialogs.Prompt(this, "Nouveau groupe", "Nom du groupe :");
            if (string.IsNullOrWhiteSpace(name)) return;
            var g = new Group { Name = name, ParentId = parent };
            D.Groups.Add(g);
            var p = D.FindGroup(parent);
            if (p != null) p.IsExpanded = true;
            _selectedGroup = g.Id;
            S.MarkChanged();
            BuildTree();
            RefreshList();
        }

        private void RenameGroup(Group g)
        {
            string name = Dialogs.Prompt(this, "Renommer le groupe", "Nouveau nom :", g.Name);
            if (string.IsNullOrWhiteSpace(name) || name == g.Name) return;
            g.Name = name;
            g.Modified = DateTime.UtcNow;
            if (g.Id == D.RootGroupId) D.Name = name;
            S.MarkChanged();
            BuildTree();
            UpdateView();
        }

        private void DeleteGroup(Group g)
        {
            var ids = D.Subtree(g.Id);
            int entries = D.Entries.Count(e => ids.Contains(e.GroupId));
            string msg = entries == 0 && ids.Count == 1
                ? $"Supprimer le groupe « {g.Name} » ?"
                : $"Supprimer le groupe « {g.Name} », ses {ids.Count - 1} sous-groupe(s) et ses {entries} entrée(s) ?\n\nUne sauvegarde du coffre est faite avant la première modification de chaque session.";
            if (!Dialogs.Confirm(this, msg)) return;
            D.Entries.RemoveAll(e => ids.Contains(e.GroupId));
            D.Groups.RemoveAll(x => ids.Contains(x.Id));
            _selectedGroup = g.ParentId ?? D.RootGroupId;
            S.MarkChanged();
            BuildTree();
            RefreshList();
        }

        private void DropOnGroup(Guid groupId, DragEventArgs e)
        {
            if (!(e.Data.GetData(DragFormat) is string raw)) return;
            var ids = raw.Split(';').Select(Guid.Parse).ToHashSet();
            int moved = 0;
            foreach (var entry in D.Entries.Where(x => ids.Contains(x.Id) && x.GroupId != groupId)) { entry.GroupId = groupId; moved++; }
            if (moved == 0) return;
            S.MarkChanged();
            BuildTree();
            RefreshList();
            StatusText.Text = $"{moved} entrée(s) déplacée(s) vers « {D.PathOf(groupId)} ».";
        }

        // ================= Liste des entrées =================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 && !SearchBox.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
            RefreshList();
        }

        private void RefreshList()
        {
            if (D == null) return;
            var keep = SelectedEntries().Select(x => x.Id).ToHashSet();
            string q = SearchBox.Text.Trim();
            IEnumerable<Entry> items;
            if (q.Length > 0)
                items = D.Entries.Where(x => EntrySearch.Matches(x, q, D.PathOf(x.GroupId)));
            else
            {
                var ids = D.Subtree(_selectedGroup ?? D.RootGroupId);
                items = D.Entries.Where(x => ids.Contains(x.GroupId));
            }
            var rows = items.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                            .Select(x => new EntryRow { Entry = x, GroupPath = GroupLabel(x.GroupId) }).ToList();
            EntryList.ItemsSource = rows;
            foreach (var r in rows.Where(r => keep.Contains(r.Entry.Id))) EntryList.SelectedItems.Add(r);

            EmptyListText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyListText.Text = q.Length > 0 ? $"Aucune entrée ne correspond à « {q} »." : "Aucune entrée dans ce groupe.\nCtrl+N pour en ajouter une.";
            if (EntryList.SelectedItems.Count == 0) ShowDetails(null);
        }

        private string GroupLabel(Guid id) => id == D.RootGroupId ? D.Root.Name : D.FindGroup(id)?.Name ?? "";

        private List<Entry> SelectedEntries() => EntryList.SelectedItems.OfType<EntryRow>().Select(r => r.Entry).ToList();
        private Entry Current => (EntryList.SelectedItem as EntryRow)?.Entry;

        private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(Current);

        private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<ListViewItem>(d) != null && Current != null) EditEntry(Current);
        }

        private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

        private void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || EntryList.SelectedItems.Count == 0) return;
            var diff = _dragStart - e.GetPosition(null);
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!(e.OriginalSource is DependencyObject d) || FindAncestor<ListViewItem>(d) == null) return;
            string ids = string.Join(";", SelectedEntries().Select(x => x.Id));
            DragDrop.DoDragDrop(EntryList, new DataObject(DragFormat, ids), DragDropEffects.Move);
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private ContextMenu BuildEntryMenu()
        {
            var m = new ContextMenu();
            m.Items.Add(MenuItem("Copier le nom d'utilisateur", "", () => CopyUser(Current), "Ctrl+B"));
            m.Items.Add(MenuItem("Copier le mot de passe", "", () => CopyPassword(Current), "Ctrl+C"));
            m.Items.Add(MenuItem("Ouvrir l'URL", "", () => OpenUrl(Current), "Ctrl+U"));
            m.Items.Add(MenuItem("Auto-type dans la fenêtre précédente", "", () => AutoType(Current), "Ctrl+V"));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem("Modifier…", "", () => EditEntry(Current), "Entrée"));
            m.Items.Add(MenuItem("Dupliquer", "", () => DuplicateEntry(Current), "Ctrl+K"));
            var move = new MenuItem { Header = "Déplacer vers", Icon = Dialogs.Icon("", 13) };
            foreach (var g in D.Groups.OrderBy(x => D.PathOf(x.Id), StringComparer.CurrentCultureIgnoreCase))
            {
                var gid = g.Id;
                var mi = new MenuItem { Header = D.PathOf(gid), Tag = L.DataTag };
                mi.Click += (s, e) => MoveSelected(gid);
                move.Items.Add(mi);
            }
            m.Items.Add(move);
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem("Supprimer", "", DeleteSelected, "Suppr"));
            return m;
        }

        protected override void OnContextMenuOpening(ContextMenuEventArgs e)
        {
            base.OnContextMenuOpening(e);
            if (e.OriginalSource is DependencyObject d && FindAncestor<ListViewItem>(d) != null && Current != null)
                EntryList.ContextMenu = BuildEntryMenu();
            else if (FindAncestor<ListView>(e.OriginalSource as DependencyObject) == EntryList)
                EntryList.ContextMenu = null;
        }

        // ================= Détails de l'entrée =================

        private void ShowDetails(Entry e)
        {
            DetailsPanel.Children.Clear();
            if (e == null || D == null)
            {
                if (D != null)
                    DetailsPanel.Children.Add(new TextBlock
                    {
                        Text = "Sélectionnez une entrée pour voir ses détails.\n\nRaccourcis :\nCtrl+B  copier l'identifiant\nCtrl+C  copier le mot de passe\nCtrl+V  auto-type dans la fenêtre précédente\nCtrl+U  ouvrir l'URL\n" +
                               $"{A.Settings.AutoTypeHotkey}  auto-type global (depuis la page de connexion)",
                        Foreground = (Brush)FindResource("MutedTextBrush"), TextWrapping = TextWrapping.Wrap, LineHeight = 20,
                    });
                L.Translate(DetailsPanel);
                return;
            }

            DetailsPanel.Children.Add(new TextBlock { Text = e.Title.Length > 0 ? e.Title : L.T("(sans titre)"), Tag = L.DataTag, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            DetailsPanel.Children.Add(new TextBlock { Text = D.PathOf(e.GroupId), Tag = L.DataTag, Foreground = (Brush)FindResource("MutedTextBrush"), Margin = new Thickness(0, 2, 0, 12), TextWrapping = TextWrapping.Wrap });

            if (e.Expires is DateTime exp)
                AddDetail("Expiration", exp.ToLocalTime().ToString("dd/MM/yyyy"), danger: exp < DateTime.UtcNow);
            AddDetail("Nom d'utilisateur", e.UserName, copy: () => CopyUser(e));
            AddSecretDetail("Mot de passe", e.Password, () => CopyPassword(e));
            AddDetail("URL", e.Url, copy: () => CopyText(e.Url, "URL", sensitive: false), open: e.Url.Length > 0 ? () => OpenUrl(e) : (Action)null);
            if (e.Tags.Count > 0) AddDetail("Étiquettes", string.Join(", ", e.Tags));
            foreach (var f in e.Fields)
            {
                var field = f;
                if (f.Protected) AddSecretDetail(f.Name, f.Value, () => CopyText(field.Value.Reveal(), field.Name), isData: true);
                else AddDetail(f.Name, f.Value.Reveal(), copy: () => CopyText(field.Value.Reveal(), field.Name), isData: true);
            }
            if (e.Notes.Length > 0) AddDetail("Notes", e.Notes, multiline: true);

            var buttons = new WrapPanel { Margin = new Thickness(-3, 12, 0, 0) };
            var edit = new Button { Content = "Modifier", Style = (Style)FindResource("PrimaryButton") };
            edit.Click += (s, a) => EditEntry(e);
            var at = new Button { Content = "Auto-type", ToolTip = "Remplit la dernière fenêtre utilisée avant Verrou (Ctrl+V)" };
            at.Click += (s, a) => AutoType(e);
            buttons.Children.Add(edit);
            buttons.Children.Add(at);
            DetailsPanel.Children.Add(buttons);

            string seq = string.IsNullOrWhiteSpace(e.AutoType.Sequence) ? "séquence par défaut" : e.AutoType.Sequence;
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = $"Auto-type : {(e.AutoType.Enabled ? seq : "désactivé")}\n" +
                       $"Créée le {e.Created.ToLocalTime():dd/MM/yyyy HH:mm} · modifiée le {e.Modified.ToLocalTime():dd/MM/yyyy HH:mm}\n" +
                       $"{e.History.Count} version(s) précédente(s) dans l'historique",
                Style = (Style)FindResource("HelpText"), Margin = new Thickness(0, 14, 0, 0), LineHeight = 18,
            });
            L.Translate(DetailsPanel);
        }

        private void AddDetail(string label, string value, Action copy = null, Action open = null, bool multiline = false, bool danger = false, bool isData = false)
        {
            if (string.IsNullOrEmpty(value)) return;
            DetailsPanel.Children.Add(new TextBlock { Text = label, Tag = isData ? L.DataTag : null, Style = (Style)FindResource("HelpText"), Margin = new Thickness(0, 8, 0, 2) });
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tb = new TextBox
            {
                Text = value, Style = (Style)FindResource("ReadOnlyText"),
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, MaxHeight = multiline ? 220 : double.PositiveInfinity,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                Foreground = danger ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("InkBrush"),
            };
            g.Children.Add(tb);
            var bp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            if (open != null) bp.Children.Add(IconBtn("", "Ouvrir — " + label, open));
            if (copy != null) bp.Children.Add(IconBtn("", "Copier — " + label, copy));
            Grid.SetColumn(bp, 1);
            g.Children.Add(bp);
            DetailsPanel.Children.Add(g);
        }

        private void AddSecretDetail(string label, SecretString secret, Action copy, bool isData = false)
        {
            if (secret.IsEmpty) return;
            DetailsPanel.Children.Add(new TextBlock { Text = label, Tag = isData ? L.DataTag : null, Style = (Style)FindResource("HelpText"), Margin = new Thickness(0, 8, 0, 2) });
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tb = new TextBox
            {
                Text = "••••••••••", Style = (Style)FindResource("ReadOnlyText"),
                FontFamily = (FontFamily)FindResource("MonoFont"), TextWrapping = TextWrapping.Wrap,
            };
            g.Children.Add(tb);
            var bp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            Button eye = null;
            eye = IconBtn("", "Afficher / masquer — " + label, () =>
            {
                bool show = tb.Text.StartsWith("••");
                tb.Text = show ? secret.Reveal() : "••••••••••";
                eye.Content = show ? "" : "";
            });
            bp.Children.Add(eye);
            bp.Children.Add(IconBtn("", "Copier — " + label, copy));
            Grid.SetColumn(bp, 1);
            g.Children.Add(bp);
            DetailsPanel.Children.Add(g);
        }

        private Button IconBtn(string glyph, string tip, Action a)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton") };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (s, e) => a();
            return b;
        }

        // ================= Actions sur les entrées =================

        private void CopyText(string value, string what, bool sensitive = true)
        {
            if (string.IsNullOrEmpty(value)) { StatusText.Text = $"{what} : vide."; return; }
            int secs = A.Settings.ClipboardClearSeconds;
            if (ClipboardService.Copy(value, secs, sensitive))
                StatusText.Text = sensitive && secs > 0 ? $"{what} copié — effacé du presse-papiers dans {secs} s." : $"{what} copié.";
            else StatusText.Text = "Le presse-papiers est occupé par une autre application, réessayez.";
        }

        private void CopyUser(Entry e) { if (e != null) CopyText(e.UserName, "Nom d'utilisateur"); }
        private void CopyPassword(Entry e) { if (e != null) CopyText(e.Password.Reveal(), "Mot de passe"); }

        private void OpenUrl(Entry e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Url)) return;
            string url = e.Url.Trim();
            if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !new[] { "http", "https", "ftp", "mailto" }.Contains(uri.Scheme))
            {
                Dialogs.Error(this, "Seules les adresses http, https, ftp et mailto peuvent être ouvertes.");
                return;
            }
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Dialogs.Error(this, ex.Message); }
        }

        private async void AutoType(Entry e)
        {
            if (e != null) await A.AutoTypeToPreviousWindowAsync(e);
        }

        private void AddEntry_Click(object sender, RoutedEventArgs e) => AddEntry(_selectedGroup ?? D.RootGroupId);

        private void AddEntry(Guid groupId)
        {
            var w = new EntryWindow(null, groupId) { Owner = this };
            if (w.ShowDialog() == true) AfterEntryChange(w.Result);
        }

        private void EditEntry(Entry e)
        {
            if (e == null) return;
            var w = new EntryWindow(e, e.GroupId) { Owner = this };
            if (w.ShowDialog() == true) AfterEntryChange(e);
        }

        private void AfterEntryChange(Entry e)
        {
            S.MarkChanged();
            BuildTree();
            RefreshList();
            var row = EntryList.Items.OfType<EntryRow>().FirstOrDefault(r => r.Entry == e);
            if (row != null) { EntryList.SelectedItem = row; EntryList.ScrollIntoView(row); }
            ShowDetails(e);
        }

        private void DuplicateEntry(Entry e)
        {
            if (e == null) return;
            var copy = e.Duplicate();
            D.Entries.Add(copy);
            AfterEntryChange(copy);
        }

        private void MoveSelected(Guid groupId)
        {
            foreach (var e in SelectedEntries()) e.GroupId = groupId;
            S.MarkChanged();
            BuildTree();
            RefreshList();
        }

        private void DeleteSelected()
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            string msg = sel.Count == 1 ? $"Supprimer l'entrée « {sel[0].Title} » ?" : $"Supprimer ces {sel.Count} entrées ?";
            if (!Dialogs.Confirm(this, msg)) return;
            foreach (var e in sel) D.Entries.Remove(e);
            S.MarkChanged();
            BuildTree();
            RefreshList();
        }

        // ================= Raccourcis clavier =================

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            bool ctrl = mods == ModifierKeys.Control;
            if (ctrl && e.Key == Key.O) { OpenVault_Click(null, null); e.Handled = true; return; }
            if (!S.IsUnlocked) return;

            bool inText = Keyboard.FocusedElement is TextBox && Keyboard.FocusedElement != SearchBox
                          || (Keyboard.FocusedElement == SearchBox && SearchBox.SelectionLength > 0);
            switch (e.Key)
            {
                case Key.F when ctrl: SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return;
                case Key.L when ctrl: A.LockVault(); e.Handled = true; return;
                case Key.N when ctrl: AddEntry_Click(null, null); e.Handled = true; return;
                case Key.G when ctrl: Generator_Click(null, null); e.Handled = true; return;
                case Key.Escape when Keyboard.FocusedElement == SearchBox: SearchBox.Text = ""; EntryList.Focus(); e.Handled = true; return;
                case Key.Down when Keyboard.FocusedElement == SearchBox:
                    if (EntryList.Items.Count > 0)
                    {
                        EntryList.SelectedIndex = Math.Max(0, EntryList.SelectedIndex);
                        (EntryList.ItemContainerGenerator.ContainerFromIndex(EntryList.SelectedIndex) as ListViewItem)?.Focus();
                    }
                    e.Handled = true; return;
            }
            if (inText || Current == null) return;
            switch (e.Key)
            {
                case Key.B when ctrl: CopyUser(Current); e.Handled = true; break;
                case Key.C when ctrl: CopyPassword(Current); e.Handled = true; break;
                case Key.U when ctrl: OpenUrl(Current); e.Handled = true; break;
                case Key.V when ctrl: AutoType(Current); e.Handled = true; break;
                case Key.K when ctrl: DuplicateEntry(Current); e.Handled = true; break;
                case Key.E when ctrl: EditEntry(Current); e.Handled = true; break;
                case Key.Enter when mods == ModifierKeys.None && Keyboard.FocusedElement != SearchBox: EditEntry(Current); e.Handled = true; break;
                case Key.Enter when mods == ModifierKeys.None && Keyboard.FocusedElement == SearchBox && EntryList.Items.Count == 1:
                    EntryList.SelectedIndex = 0; e.Handled = true; break;
                case Key.Delete when mods == ModifierKeys.None && Keyboard.FocusedElement != SearchBox: DeleteSelected(); e.Handled = true; break;
            }
        }

        // ================= Barre d'outils =================

        private void NewVault_Click(object sender, RoutedEventArgs e)
        {
            var w = new CredentialsWindow(CredentialsWindow.Mode.Create) { Owner = this };
            if (w.ShowDialog() == true && w.Created != null)
            {
                S.Unlock(w.Created);
                StatusText.Text = $"Coffre créé : {w.Created.Path}";
            }
        }

        private void OpenVault_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Coffre Verrou (*.verrou)|*.verrou|Tous les fichiers (*.*)|*.*", Title = "Ouvrir un coffre" };
            if (S.Path != null) { try { d.InitialDirectory = Path.GetDirectoryName(S.Path); } catch (Exception) { } }
            if (d.ShowDialog(this) != true) return;
            try { VaultFile.ReadHeader(File.ReadAllBytes(d.FileName)); }
            catch (Exception ex) when (ex is VaultException || ex is IOException)
            {
                Dialogs.Error(this, ex.Message);
                return;
            }
            if (S.IsUnlocked) A.LockVault("Coffre précédent verrouillé.");
            S.SetPath(d.FileName);
            UpdateView();
        }

        private void Lock_Click(object sender, RoutedEventArgs e) => A.LockVault();

        private void Generator_Click(object sender, RoutedEventArgs e) => new GeneratorWindow(false) { Owner = this }.Show();

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = BtnImport, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            m.Items.Add(MenuItem("Fichier CSV (KeePass, KeePassXC, Bitwarden, Chrome, Firefox…)", "", () => ImportFile(csv: true)));
            m.Items.Add(MenuItem("KeePass XML (2.x)", "", () => ImportFile(csv: false)));
            m.IsOpen = true;
        }

        private void ImportFile(bool csv)
        {
            var d = new OpenFileDialog
            {
                Filter = csv ? "CSV (*.csv)|*.csv|Tous les fichiers (*.*)|*.*" : "KeePass XML (*.xml)|*.xml|Tous les fichiers (*.*)|*.*",
                Title = csv ? "Importer un fichier CSV" : "Importer un export KeePass XML",
            };
            if (d.ShowDialog(this) != true) return;
            string groupName = L.T($"Importé le {DateTime.Now:dd-MM-yyyy HH.mm}");
            try
            {
                ImportResult r;
                if (csv)
                {
                    byte[] bytes = File.ReadAllBytes(d.FileName);
                    string text;
                    try { text = new UTF8Encoding(false, true).GetString(bytes); }
                    catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); } // export Excel en ANSI
                    r = CsvImporter.Import(D, text, groupName);
                }
                else
                {
                    using var fs = File.OpenRead(d.FileName);
                    r = KeePassXmlImporter.Import(D, fs, groupName);
                }
                _selectedGroup = r.Target.Id;
                S.MarkChanged();
                BuildTree();
                RefreshList();
                Dialogs.Info(this, $"{r.Entries} entrée(s) importée(s) dans le groupe « {r.Target.Name} ».\n\n" +
                                   "Pensez à supprimer le fichier d'export : il contient vos mots de passe en clair.");
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is System.Xml.XmlException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, "Import impossible :\n" + ex.Message);
            }
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            byte[] current = await ConfirmIdentityAsync(S.Vault, "exporter une copie du coffre");
            if (current == null) return;
            if (current.Length > 0) CryptographicOperations.ZeroMemory(current);
            var w = new CredentialsWindow(CredentialsWindow.Mode.Export, S.Vault) { Owner = this };
            if (w.ShowDialog() == true) StatusText.Text = "Copie chiffrée exportée : " + w.ResultPath;
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = BtnMore, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            m.Items.Add(MenuItem("Paramètres…", "", OpenSettings));
            if (S.IsUnlocked)
            {
                m.Items.Add(MenuItem("Mot de passe maître et double authentification…", "\uE8D7", async () =>
                {
                    byte[] current = await ConfirmIdentityAsync(S.Vault, "modifier la sécurité du coffre");
                    if (current == null) return;
                    var w = new CredentialsWindow(CredentialsWindow.Mode.ChangeKey, S.Vault, current.Length > 0 ? current : null) { Owner = this };
                    if (w.ShowDialog() == true) Dialogs.Info(this, "Sécurité du coffre mise à jour : le coffre a été réenregistré avec la nouvelle clé.\n\nDouble authentification : " +
                        (S.Vault.Key.RequiresTwoFactor ? "activée." : "désactivée."));
                }));
                if (S.Vault.Key.RequiresTwoFactor)
                {
                    m.Items.Add(MenuItem("Configurer un nouveau téléphone (QR code)…", "\uE8EA", ReconfigurePhone));
                    bool hello = A.TwoFactor.IsHelloProtected(S.Vault.Key.TwoFactorId);
                    m.Items.Add(MenuItem(hello ? "Retirer la protection Windows Hello…" : "Protéger la 2FA avec Windows Hello…", "\uE928", ToggleHello));
                }
                m.Items.Add(MenuItem("Ouvrir le dossier des sauvegardes", "", () =>
                {
                    string dir = A.Settings.BackupDirFor(S.Path);
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                }));
            }
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem("À propos de Verrou", "", () => Dialogs.Info(this,
                "Verrou 1.4 — gestionnaire de mots de passe local, avec double authentification, bureau sécurisé et brouillage de l'auto-type.\n\n" +
                "Chiffrement : AES-256-GCM · Dérivation de clé : Argon2id · Intégrité vérifiée à chaque ouverture.\n" +
                "Liste de mots des phrases de passe : EFF (CC BY 3.0).\n\n© 2026 Abdel Hakim ZAIMI")));
            m.Items.Add(MenuItem("Quitter", "", QuitFromWindow));
            m.IsOpen = true;
        }

        /// <summary>Réaffiche le QR code (nouveau téléphone), après confirmation de l'identité.</summary>
        private async void ReconfigurePhone()
        {
            var vault = S.Vault;
            byte[] secret = await ConfirmIdentityAsync(vault, "configurer un nouveau téléphone");
            if (secret == null || secret.Length == 0) return;
            try
            {
                var w = new TwoFactorSetupWindow((byte[])secret.Clone(), vault.Data.Name, reconfigure: true) { Owner = this };
                if (w.ShowDialog() == true)
                {
                    A.TwoFactor.SetLastStep(vault.Key.TwoFactorId, w.ConfirmedStep);
                    Dialogs.Info(this, "Nouveau téléphone configuré.");
                }
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }

        /// <summary>Active ou retire la protection du secret 2FA par Windows Hello (PIN / empreinte / visage).</summary>
        private async void ToggleHello()
        {
            var vault = S.Vault;
            string id = vault.Key.TwoFactorId;
            bool enable = !A.TwoFactor.IsHelloProtected(id);
            if (enable && !await WindowsHello.IsAvailableAsync())
            {
                Dialogs.Warning(this, "Windows Hello n'est pas configuré sur ce PC. Activez un PIN, l'empreinte ou la reconnaissance faciale dans Paramètres Windows › Comptes › Options de connexion.");
                return;
            }
            byte[] secret = await ConfirmIdentityAsync(vault, enable ? "activer Windows Hello" : "retirer Windows Hello");
            if (secret == null || secret.Length == 0) return;
            try
            {
                if (enable)
                    Dialogs.Info(this, "Windows va vous demander votre PIN (ou empreinte / visage) pour créer la clé de Verrou dans la puce de sécurité du PC.");
                await A.TwoFactor.SetAsync(id, secret, helloProtect: enable);
                if (enable)
                {
                    // Vérification immédiate : on relit le secret avec Windows Hello avant de s'y fier.
                    byte[] check = await A.TwoFactor.GetAsync(id);
                    bool same = check != null && CryptographicOperations.FixedTimeEquals(check, secret);
                    if (check != null) CryptographicOperations.ZeroMemory(check);
                    if (!same)
                    {
                        await A.TwoFactor.SetAsync(id, secret);
                        Dialogs.Error(this, "La vérification Windows Hello a échoué : protection non activée.");
                        return;
                    }
                }
                Dialogs.Info(this, enable
                    ? "Protection Windows Hello activée. À chaque ouverture, Windows demandera votre PIN (ou empreinte / visage) en plus du mot de passe et du code du téléphone. Le secret 2FA ne peut plus être lu par un programme de votre session sans cette confirmation."
                    : "Protection Windows Hello retirée (le secret reste protégé par votre compte Windows).");
            }
            catch (WindowsHello.HelloException ex) { Dialogs.Error(this, ex.Message); }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }

        /// <summary>
        /// Re-confirmation avant une action sensible : mot de passe maître et, si le coffre a la 2FA, code du
        /// téléphone. Renvoie le secret 2FA (tableau vide sans 2FA), ou null si annulé.
        /// </summary>
        private async System.Threading.Tasks.Task<byte[]> ConfirmIdentityAsync(OpenedVault vault, string action)
        {
            string id = vault.Key.TwoFactorId;
            byte[] secret = Array.Empty<byte>();
            if (vault.Key.RequiresTwoFactor)
            {
                try { secret = await A.TwoFactor.GetAsync(id); }
                catch (WindowsHello.HelloException ex) { Dialogs.Error(this, ex.Message); return null; }
                if (secret == null) { Dialogs.Error(this, "Secret de double authentification introuvable sur ce PC : verrouillez puis rouvrez le coffre avec la clé de secours."); return null; }
            }

            var w = new Window
            {
                Title = "Confirmer votre identité", Owner = this, Width = 440, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Style = (Style)FindResource("BaseWindow"), Background = (Brush)FindResource("SurfaceBrush"),
            };
            WindowSecurity.Protect(w);
            var pb = new PasswordBox { Margin = new Thickness(0, 6, 0, 12) };
            var code = new TextBox { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 16, MaxLength = 7, Margin = new Thickness(0, 6, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Center };
            var err = new TextBlock { Foreground = (Brush)FindResource("DangerBrush"), TextWrapping = TextWrapping.Wrap };
            var ok = new Button { Content = "Confirmer", IsDefault = true, MinWidth = 110, Style = (Style)FindResource("PrimaryButton") };
            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
            root.Children.Add(new TextBlock { Text = $"Par sécurité, confirmez votre identité pour {action}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            root.Children.Add(new TextBlock { Text = "Mot de passe maître", Style = (Style)FindResource("FieldLabel") });
            root.Children.Add(pb);
            if (vault.Key.RequiresTwoFactor)
            {
                root.Children.Add(new TextBlock { Text = "Code de double authentification", Style = (Style)FindResource("FieldLabel") });
                root.Children.Add(code);
            }
            root.Children.Add(err);
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 0) };
            bp.Children.Add(ok);
            bp.Children.Add(new Button { Content = "Annuler", IsCancel = true, MinWidth = 96 });
            root.Children.Add(bp);
            w.Content = root;
            w.Loaded += (s, e) => pb.Focus();
            ok.Click += async (s, e) =>
            {
                err.Text = "";
                if (vault.Key.RequiresTwoFactor)
                {
                    int locked = A.TwoFactor.LockedSeconds(id);
                    if (locked > 0) { err.Text = $"Trop de codes incorrects. Réessayez dans {locked} s."; return; }
                    if (!Totp.Verify(secret, code.Text, DateTimeOffset.UtcNow, A.TwoFactor.LastStep(id), out long step))
                    {
                        int wait = A.TwoFactor.RegisterFailure(id);
                        err.Text = wait > 0 ? $"Code incorrect. Saisie bloquée {wait} s." : "Code incorrect ou déjà utilisé.";
                        code.SelectAll(); code.Focus();
                        return;
                    }
                    A.TwoFactor.SetLastStep(id, step);
                }
                ok.IsEnabled = false;
                err.Text = "Vérification…";
                try
                {
                    byte[] kf = null;
                    if (vault.Key.RequiresKeyFile && A.Settings.KeyFilePaths.TryGetValue(vault.Path, out var kfPath)) kf = CredentialsWindow.ReadKeyFile(kfPath);
                    using var ck = Dialogs.KeyFrom(pb, kf, vault.Key.RequiresTwoFactor ? secret : null);
                    byte[] file = File.ReadAllBytes(vault.Path);
                    bool good = await System.Threading.Tasks.Task.Run(() =>
                    {
                        using var k = MasterKey.Derive(ck, vault.Key.Kdf, vault.Key.Salt);
                        try { CryptographicOperations.ZeroMemory(VaultFile.DecryptWithKey(file, k)); return true; }
                        catch (VaultException) { return false; }
                    });
                    if (good) { A.TwoFactor.ResetFailures(id); w.DialogResult = true; }
                    else { err.Text = "Mot de passe incorrect."; pb.SelectAll(); pb.Focus(); }
                }
                catch (Exception ex) when (ex is IOException || ex is VaultException || ex is ArgumentException) { err.Text = ex.Message; }
                finally { ok.IsEnabled = true; }
            };
            if (w.ShowDialog() == true) return secret;
            if (secret.Length > 0) CryptographicOperations.ZeroMemory(secret);
            return null;
        }

        private void OpenSettings()
        {
            var w = new SettingsWindow { Owner = this };
            if (w.ShowDialog() == true)
            {
                UpdateView();
                if (S.IsUnlocked) ShowDetails(Current);
            }
        }
    }
}
