using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Verrou.Core.AutoType;
using Verrou.Core.Crypto;
using Verrou.Core.Generation;
using Verrou.Core.Model;
using Verrou.Services;

namespace Verrou.UI
{
    public partial class EntryWindow : Window
    {
        private sealed class FieldRow
        {
            public string Name { get; set; } = "";
            public string Value { get; set; } = "";
            public bool Protected { get; set; }
            public string Display => (Name.Length > 0 ? Name : Services.L.T("(sans nom)")) + (Protected ? "  🔒" : "");
        }

        private sealed class HistoryRow
        {
            public EntrySnapshot Snapshot { get; set; }
            public string Date => Snapshot.Modified.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        }

        private readonly Entry _entry;      // null = nouvelle entrée
        private readonly VaultData _data;
        private readonly List<FieldRow> _fields = new List<FieldRow>();
        private readonly List<EntrySnapshot> _history;
        private bool _loadingField;

        /// <summary>L'entrée créée ou modifiée.</summary>
        public Entry Result { get; private set; }

        public EntryWindow(Entry entry, Guid groupId)
        {
            InitializeComponent();
            WindowSecurity.Protect(this);
            L.TrackText(ErrorText, SeqError);
            L.Track(HistoryTab, HeaderedContentControl.HeaderProperty);
            _entry = entry;
            _data = App.Current.Session.Vault.Data;
            _history = entry?.History.ToList() ?? new List<EntrySnapshot>();
            Title = entry == null ? "Nouvelle entrée" : $"Modifier « {entry.Title} »";

            GroupCombo.ItemsSource = _data.Groups
                .Select(g => new KeyValuePair<Guid, string>(g.Id, _data.PathOf(g.Id)))
                .OrderBy(k => k.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
            GroupCombo.SelectedValue = groupId;
            DefaultSeqText.Text = "Séquence par défaut du coffre : " + _data.Options.DefaultAutoTypeSequence;
            PwdBox.TextChanged += (s, e) => UpdateStrength();
            FieldValue.TextChanged += (s, e) => FieldEdit_Changed(s, null);

            if (entry != null) LoadFields(entry);
            else
            {
                PwdBox.Text = PasswordGenerator.Generate(App.Current.Settings.Generator);
                HistoryTab.Visibility = Visibility.Collapsed;
            }
            RefreshHistory();
            UpdateStrength();
            Loaded += (s, e) => { TitleBox.Focus(); TitleBox.SelectAll(); };
        }

        private void LoadFields(EntryFields f)
        {
            TitleBox.Text = f.Title;
            UserBox.Text = f.UserName;
            PwdBox.Text = f.Password.Reveal();
            UrlBox.Text = f.Url;
            NotesBox.Text = f.Notes;
            TagsBox.Text = string.Join(", ", f.Tags);
            ExpiresCheck.IsChecked = f.Expires.HasValue;
            ExpiresDate.SelectedDate = f.Expires?.ToLocalTime().Date ?? DateTime.Today.AddYears(1);
            _fields.Clear();
            _fields.AddRange(f.Fields.Select(x => new FieldRow { Name = x.Name, Value = x.Value.Reveal(), Protected = x.Protected }));
            FieldList.ItemsSource = null;
            FieldList.ItemsSource = _fields;
            AutoTypeEnabled.IsChecked = f.AutoType.Enabled;
            ObfuscateCheck.IsChecked = f.AutoType.Obfuscate;
            bool custom = !string.IsNullOrWhiteSpace(f.AutoType.Sequence);
            UseCustomSeq.IsChecked = custom;
            UseDefaultSeq.IsChecked = !custom;
            SeqBox.Text = custom ? f.AutoType.Sequence : _data.Options.DefaultAutoTypeSequence;
            WindowsBox.Text = string.Join(Environment.NewLine, f.AutoType.WindowPatterns);
        }

        private EntrySnapshot BuildSnapshot()
        {
            var s = new EntrySnapshot
            {
                Title = TitleBox.Text.Trim(),
                UserName = UserBox.Text.Trim(),
                Password = new SecretString(PwdBox.Text),
                Url = UrlBox.Text.Trim(),
                Notes = NotesBox.Text,
                Tags = TagsBox.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList(),
                Fields = _fields.Where(f => f.Name.Trim().Length > 0)
                                .Select(f => new CustomField { Name = f.Name.Trim(), Value = new SecretString(f.Value), Protected = f.Protected }).ToList(),
                Expires = ExpiresCheck.IsChecked == true && ExpiresDate.SelectedDate is DateTime d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Local).ToUniversalTime() : (DateTime?)null,
                Modified = _entry?.Modified ?? DateTime.UtcNow,
            };
            s.AutoType.Enabled = AutoTypeEnabled.IsChecked == true;
            s.AutoType.Obfuscate = ObfuscateCheck.IsChecked == true;
            s.AutoType.Sequence = UseCustomSeq.IsChecked == true ? SeqBox.Text.Trim() : "";
            s.AutoType.WindowPatterns = WindowsBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return s;
        }

        private void UpdateStrength()
        {
            double bits = PasswordStrength.EstimateBits(PwdBox.Text);
            if (PwdBox.Text.Length == 0) Strength.Show(0, "Aucun mot de passe", empty: true);
            else Strength.Show(bits, $"{PwdBox.Text.Length} caractères · ≈ {bits:0} bits");
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            var g = new GeneratorWindow(true) { Owner = this };
            if (g.ShowDialog() == true && g.Generated != null)
            {
                PwdBox.Text = g.Generated;
                UpdateStrength();
            }
        }

        // ---- Champs personnalisés ----

        private FieldRow CurrentField => FieldList.SelectedItem as FieldRow;

        private void AddField_Click(object sender, RoutedEventArgs e)
        {
            var row = new FieldRow { Name = "Nouveau champ" };
            _fields.Add(row);
            FieldList.Items.Refresh();
            FieldList.SelectedItem = row;
            FieldName.Focus();
            FieldName.SelectAll();
        }

        private void RemoveField_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentField == null) return;
            _fields.Remove(CurrentField);
            FieldList.Items.Refresh();
        }

        private void FieldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var f = CurrentField;
            _loadingField = true;
            FieldEditor.IsEnabled = f != null;
            FieldName.Text = f?.Name ?? "";
            FieldValue.Revealed = false;
            FieldValue.Text = f?.Value ?? "";
            FieldProtected.IsChecked = f?.Protected ?? false;
            FieldValue.Revealed = f != null && !f.Protected;
            _loadingField = false;
        }

        private void FieldEdit_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingField || CurrentField == null) return;
            var f = CurrentField;
            bool protChanged = f.Protected != (FieldProtected.IsChecked == true);
            f.Name = FieldName.Text;
            f.Value = FieldValue.Text;
            f.Protected = FieldProtected.IsChecked == true;
            if (protChanged) FieldValue.Revealed = !f.Protected;
            int caret = FieldName.CaretIndex;
            FieldList.Items.Refresh();
            FieldList.SelectedItem = f;
            if (sender == FieldName) FieldName.CaretIndex = caret;
        }

        // ---- Auto-type ----

        private void Seq_Changed(object sender, RoutedEventArgs e)
        {
            if (SeqBox == null) return;
            bool custom = UseCustomSeq.IsChecked == true;
            SeqBox.IsEnabled = SeqButtons.IsEnabled = custom;
            ValidateSequence();
        }

        private void SeqBox_TextChanged(object sender, TextChangedEventArgs e) => ValidateSequence();

        private bool ValidateSequence()
        {
            if (SeqError == null) return true;
            SeqError.Text = "";
            if (UseCustomSeq.IsChecked != true) return true;
            try
            {
                // Valide avec les champs personnalisés actuels pour que {S:…} soit reconnu.
                var probe = new Entry();
                probe.Fields = _fields.Where(f => f.Name.Trim().Length > 0).Select(f => new CustomField { Name = f.Name.Trim() }).ToList();
                AutoTypeSequence.Parse(SeqBox.Text, probe);
                return true;
            }
            catch (AutoTypeParseException ex)
            {
                SeqError.Text = ex.Message;
                return false;
            }
        }

        private void InsertToken_Click(object sender, RoutedEventArgs e)
        {
            string token = (string)((Button)sender).Tag;
            int i = SeqBox.CaretIndex;
            SeqBox.Text = SeqBox.Text.Insert(i, token);
            SeqBox.CaretIndex = token == "{S:}" ? i + 3 : i + token.Length;
            SeqBox.Focus();
        }

        // ---- Historique ----

        private void RefreshHistory()
        {
            HistoryList.ItemsSource = _history.AsEnumerable().Reverse().Select(h => new HistoryRow { Snapshot = h }).ToList();
            HistoryTab.Header = $"Historique ({_history.Count})";
            HistoryPwd.Text = "";
        }

        private EntrySnapshot SelectedVersion => (HistoryList.SelectedItem as HistoryRow)?.Snapshot;

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RestoreButton.IsEnabled = DeleteVersionButton.IsEnabled = SelectedVersion != null;
            HistoryPwd.Text = SelectedVersion == null ? "" : "••••••••••";
            HistoryReveal.Content = "";
        }

        private void HistoryReveal_Click(object sender, RoutedEventArgs e)
        {
            var v = SelectedVersion;
            if (v == null) return;
            bool show = HistoryPwd.Text.StartsWith("••");
            HistoryPwd.Text = show ? v.Password.Reveal() : "••••••••••";
            HistoryReveal.Content = show ? "" : "";
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            var v = SelectedVersion;
            if (v == null) return;
            LoadFields(v);
            Tabs.SelectedIndex = 0;
            ErrorText.Text = "Ancienne version chargée : cliquez sur OK pour la rétablir (la version actuelle sera gardée dans l'historique).";
        }

        private void DeleteVersion_Click(object sender, RoutedEventArgs e)
        {
            var v = SelectedVersion;
            if (v == null || !Dialogs.Confirm(this, "Supprimer définitivement cette version de l'historique ?")) return;
            _history.Remove(v);
            RefreshHistory();
        }

        // ---- Validation ----

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateSequence())
            {
                Tabs.SelectedIndex = 2;
                ErrorText.Text = "La séquence d'auto-type contient une erreur.";
                return;
            }
            var snap = BuildSnapshot();
            if (snap.Title.Length == 0 && snap.UserName.Length == 0 && snap.Url.Length == 0)
            {
                Tabs.SelectedIndex = 0;
                ErrorText.Text = "Donnez au moins un titre à l'entrée.";
                TitleBox.Focus();
                return;
            }
            Guid group = GroupCombo.SelectedValue is Guid g ? g : _data.RootGroupId;

            if (_entry == null)
            {
                var entry = new Entry { GroupId = group };
                entry.ApplyFields(snap);
                entry.Modified = entry.Created = DateTime.UtcNow;
                _data.Entries.Add(entry);
                Result = entry;
            }
            else
            {
                _entry.History = _history;
                _entry.UpdateWithHistory(snap, _data.Options.HistoryMaxItems);
                _entry.GroupId = group;
                Result = _entry;
            }
            DialogResult = true;
        }
    }
}
