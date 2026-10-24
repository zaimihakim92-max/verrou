using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Verrou.Core.Crypto;
using Verrou.Core.Generation;
using Verrou.Core.Model;
using Verrou.Core.Storage;
using Verrou.Services;

namespace Verrou.UI
{
    public partial class CredentialsWindow : Window
    {
        public enum Mode { Create, ChangeKey, Export }

        private readonly Mode _mode;
        private readonly OpenedVault _vault;

        /// <summary>Coffre créé (mode Create).</summary>
        public OpenedVault Created { get; private set; }
        public string ResultPath { get; private set; }

        private readonly byte[] _currentSecret;

        /// <param name="currentSecret">Secret 2FA actuel du coffre (mode ChangeKey), obtenu après re-confirmation.</param>
        public CredentialsWindow(Mode mode, OpenedVault vault = null, byte[] currentSecret = null)
        {
            _currentSecret = currentSecret;
            InitializeComponent();
            WindowSecurity.Protect(this);
            L.TrackText(KdfInfo, ErrorText);
            _mode = mode;
            _vault = vault;
            Pwd1.TextChanged += (s, e) => UpdateStrength();
            KdfPreset.SelectedIndex = 0;
            UpdateStrength();

            switch (mode)
            {
                case Mode.Create:
                    VaultName.Text = L.T("Mon coffre");
                    PathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), L.T("Mon coffre") + ".verrou");
                    // Obligatoire pour tout nouveau coffre : badge à la place de l'interrupteur (désactivable ensuite dans les Paramètres).
                    UseTwoFactor.Visibility = Visibility.Collapsed;
                    TwoFactorBadgeRow.Visibility = Visibility.Visible;
                    TwoFactorHelp.Text += " Vous pourrez la désactiver plus tard pour ce coffre (Paramètres › Mot de passe maître et double authentification).";
                    break;
                case Mode.ChangeKey:
                    Title = Heading.Text = "Sécurité du coffre";
                    Intro.Text = "Le coffre sera réenregistré avec une nouvelle clé (nouveau sel). L'ancien mot de passe ne fonctionnera plus ; les sauvegardes déjà faites gardent l'ancien.";
                    NameRow.Visibility = PathRow.Visibility = Visibility.Collapsed;
                    OkButton.Content = "Enregistrer";
                    SelectKdf(vault.Key.Kdf);
                    UseKeyFile.IsChecked = vault.Key.RequiresKeyFile;
                    UseTwoFactor.IsChecked = vault.Key.RequiresTwoFactor;
                    TwoFactorTitle.Text = "Code à 6 chiffres du téléphone";
                    TwoFactorHelp.Text = vault.Key.RequiresTwoFactor
                        ? "Activée : le même téléphone et la même clé de secours restent valables. Désactiver la 2FA affaiblit nettement la protection du coffre."
                        : "Désactivée. Activez-la pour exiger, en plus du mot de passe, le code de votre téléphone : vous scannerez un QR code à l'étape suivante.";
                    break;
                case Mode.Export:
                    Title = Heading.Text = "Exporter une copie chiffrée";
                    Intro.Text = "Crée un fichier .verrou indépendant, protégé par le mot de passe de votre choix (par exemple pour le transmettre ou l'archiver). Le coffre actuel n'est pas modifié. La copie n'a pas de double authentification : choisissez un mot de passe fort.";
                    NameRow.Visibility = Visibility.Collapsed;
                    TwoFactorSection.Visibility = Visibility.Collapsed;
                    UseTwoFactor.IsChecked = false;
                    PathBox.Text = Path.Combine(Path.GetDirectoryName(vault.Path) ?? "", Path.GetFileNameWithoutExtension(vault.Path) + "-export.verrou");
                    OkButton.Content = "Exporter";
                    break;
            }
            Loaded += (s, e) => { if (mode == Mode.Create) VaultName.Focus(); else Pwd1.FocusInput(); };
        }

        private void SelectKdf(KdfParameters k)
        {
            if (k == KdfParameters.Standard) KdfPreset.SelectedIndex = 0;
            else if (k == KdfParameters.Strong) KdfPreset.SelectedIndex = 1;
            else
            {
                KdfPreset.SelectedIndex = 2;
                KdfMem.Text = (k.MemoryKiB / 1024).ToString();
                KdfIt.Text = k.Iterations.ToString();
                KdfPar.Text = k.Parallelism.ToString();
            }
        }

        private void UpdateStrength()
        {
            double bits = PasswordStrength.EstimateBits(Pwd1.Text);
            if (Pwd1.Text.Length == 0) Strength.Show(0, "Une longue phrase de passe, facile à retenir pour vous seul.", empty: true);
            else Strength.Show(bits, $"≈ {bits:0} bits");
        }

        private void UseKeyFile_Changed(object sender, RoutedEventArgs e) => KeyFileRow.IsEnabled = UseKeyFile.IsChecked == true;

        private void KdfPreset_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CustomKdf == null) return;
            CustomKdf.Visibility = KdfPreset.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            KdfInfo.Text = "";
        }

        private KdfParameters ReadKdf()
        {
            switch (KdfPreset.SelectedIndex)
            {
                case 0: return KdfParameters.Standard;
                case 1: return KdfParameters.Strong;
            }
            if (!int.TryParse(KdfMem.Text, out int mem) || !int.TryParse(KdfIt.Text, out int it) || !int.TryParse(KdfPar.Text, out int par))
                throw new FormatException("Paramètres de dérivation invalides.");
            var k = new KdfParameters(mem * 1024, it, par);
            try { k.Validate(); }
            catch (VaultException) { throw new FormatException("Paramètres hors limites (mémoire 8 à 4096 Mio, 1 à 100 passes, 1 à 64 fils)."); }
            return k;
        }

        private async void TestKdf_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var kdf = ReadKdf();
                KdfInfo.Text = "Mesure en cours…";
                IsEnabled = false;
                var sw = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    using var ck = CompositeKey.Create("test-de-duree");
                    using var k = MasterKey.Derive(ck, kdf);
                });
                KdfInfo.Text = $"Ouverture du coffre : environ {sw.Elapsed.TotalSeconds:0.0} s sur ce PC.";
            }
            catch (FormatException ex) { KdfInfo.Text = ex.Message; }
            finally { IsEnabled = true; }
        }

        private void BrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var d = new SaveFileDialog { Filter = "Coffre Verrou (*.verrou)|*.verrou", FileName = Path.GetFileName(PathBox.Text), DefaultExt = ".verrou" };
            try { d.InitialDirectory = Path.GetDirectoryName(PathBox.Text); } catch (Exception) { }
            if (d.ShowDialog(this) == true) PathBox.Text = d.FileName;
        }

        private void BrowseKeyFile_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Tous les fichiers (*.*)|*.*", Title = "Choisir le fichier-clé" };
            if (d.ShowDialog(this) == true) KeyFilePath.Text = d.FileName;
        }

        private void CreateKeyFile_Click(object sender, RoutedEventArgs e)
        {
            var d = new SaveFileDialog { Filter = "Fichier-clé (*.key)|*.key", FileName = "Verrou.key", DefaultExt = ".key", Title = "Créer un fichier-clé" };
            if (d.ShowDialog(this) != true) return;
            if (File.Exists(d.FileName))
            {
                Dialogs.Error(this, "Ce fichier existe déjà : choisissez un autre nom (un fichier-clé existant ne doit jamais être écrasé).");
                return;
            }
            File.WriteAllText(d.FileName, L.T("Fichier-clé Verrou — ne pas modifier, garder une copie de secours.\r\n") +
                                          Convert.ToHexString(RandomNumberGenerator.GetBytes(64)) + "\r\n");
            KeyFilePath.Text = d.FileName;
        }

        internal static byte[] ReadKeyFile(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException("Fichier-clé introuvable : " + path);
            if (fi.Length == 0) throw new IOException("Le fichier-clé est vide.");
            if (fi.Length > 64L * 1024 * 1024) throw new IOException("Le fichier-clé est trop gros (64 Mio maximum).");
            return File.ReadAllBytes(path);
        }

        private void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorText.Visibility = Visibility.Visible;
        }

        private async void Ok_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            string pw1 = Pwd1.Text, pw2 = Pwd2.Text;
            if (pw1 != pw2) { ShowError("Les deux mots de passe ne sont pas identiques."); return; }
            bool useKf = UseKeyFile.IsChecked == true;
            if (pw1.Length == 0 && !useKf) { ShowError("Il faut un mot de passe maître (ou au moins un fichier-clé)."); return; }

            KdfParameters kdf;
            byte[] keyFile = null;
            string path = PathBox.Text.Trim();
            try
            {
                kdf = ReadKdf();
                if (useKf) keyFile = ReadKeyFile(KeyFilePath.Text.Trim());
                if (_mode != Mode.ChangeKey)
                {
                    if (path.Length == 0) throw new IOException("Choisissez l'emplacement du fichier.");
                    if (!path.EndsWith(".verrou", StringComparison.OrdinalIgnoreCase)) path += ".verrou";
                    path = Path.GetFullPath(path);
                    if (File.Exists(path) && !Dialogs.Confirm(this, $"Le fichier existe déjà :\n{path}\n\nL'écraser ?")) return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                ShowError(ex.Message);
                return;
            }

            double bits = PasswordStrength.EstimateBits(pw1);
            if (pw1.Length > 0 && bits < 50 && !Dialogs.Confirm(this, $"Ce mot de passe maître est {PasswordStrength.Label(bits).ToLowerInvariant()} (≈ {bits:0} bits). Le garder quand même ?"))
                return;

            // Double authentification : nouveau secret (configuration du téléphone) ou secret existant de ce coffre.
            var store = App.Current.TwoFactor;
            bool want2fa = UseTwoFactor.IsChecked == true;
            byte[] secret = null;
            long confirmedStep = -1;
            if (want2fa)
            {
                if (_mode == Mode.ChangeKey && _vault.Key.RequiresTwoFactor)
                {
                    secret = _currentSecret == null ? null : (byte[])_currentSecret.Clone();
                    if (secret == null) { ShowError("Le secret de double authentification de ce coffre est introuvable sur ce PC. Verrouillez puis rouvrez le coffre avec la clé de secours."); return; }
                    confirmedStep = store.LastStep(_vault.Key.TwoFactorId);
                }
                else
                {
                    string name = _mode == Mode.Create ? (string.IsNullOrWhiteSpace(VaultName.Text) ? "Mon coffre" : VaultName.Text.Trim()) : _vault.Data.Name;
                    var setup = new TwoFactorSetupWindow(Totp.GenerateSecret(), name) { Owner = this };
                    if (setup.ShowDialog() != true) { if (keyFile != null) CryptographicOperations.ZeroMemory(keyFile); return; }
                    secret = setup.Secret;
                    confirmedStep = setup.ConfirmedStep;
                }
            }
            else if (_mode == Mode.ChangeKey && _vault.Key.RequiresTwoFactor &&
                     !Dialogs.Confirm(this, "Désactiver la double authentification ? Le mot de passe maître (et le fichier-clé) suffiront alors à ouvrir le coffre.", yes: "Désactiver", no: "Garder la 2FA"))
                return;

            CompositeKey ck = Pwd1.Revealed ? CompositeKey.Create(pw1, keyFile, secret) : Dialogs.KeyFrom(Pwd1.PasswordBox, keyFile, secret);
            if (keyFile != null) CryptographicOperations.ZeroMemory(keyFile);
            string oldId = _mode == Mode.ChangeKey ? _vault.Key.TwoFactorId : null;

            IsEnabled = false;
            BusyText.Visibility = Visibility.Visible;
            try
            {
                string name = string.IsNullOrWhiteSpace(VaultName.Text) ? L.T("Mon coffre") : VaultName.Text.Trim();
                switch (_mode)
                {
                    case Mode.Create:
                        Created = await Task.Run(() => VaultStore.Create(path, VaultData.CreateNew(name, new[] { "Internet", "E-mail", "Banque", "Réseau", "Divers" }.Select(L.T).ToList()), ck, kdf));
                        if (secret != null) { await store.SetAsync(Created.Key.TwoFactorId, secret); store.SetLastStep(Created.Key.TwoFactorId, confirmedStep); }
                        break;
                    case Mode.ChangeKey:
                        BackupManager.CreateBackup(_vault.Path, App.Current.Settings.BackupDirFor(_vault.Path), App.Current.Settings.BackupCount);
                        await Task.Run(() => VaultStore.ChangeKey(_vault, ck, kdf));
                        if (secret != null)
                        {
                            // La protection Windows Hello éventuelle est conservée (Windows redemande alors le PIN).
                            bool hello = oldId != null && store.IsHelloProtected(oldId);
                            await store.SetAsync(_vault.Key.TwoFactorId, secret, hello);
                            store.SetLastStep(_vault.Key.TwoFactorId, confirmedStep);
                        }
                        if (oldId != _vault.Key.TwoFactorId) store.Remove(oldId);
                        break;
                    case Mode.Export:
                        await Task.Run(() => VaultStore.ExportEncrypted(path, _vault.Data, ck, kdf));
                        break;
                }
                ResultPath = path;
                if (useKf && App.Current.Settings.RememberKeyFilePath)
                    App.Current.Settings.KeyFilePaths[_mode == Mode.ChangeKey ? _vault.Path : path] = KeyFilePath.Text.Trim();
                else if (_mode == Mode.ChangeKey)
                    App.Current.Settings.KeyFilePaths.Remove(_vault.Path);
                App.Current.Settings.Save();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ShowError("Échec : " + ex.Message);
            }
            finally
            {
                ck.Dispose();
                if (secret != null) CryptographicOperations.ZeroMemory(secret);
                IsEnabled = true;
                BusyText.Visibility = Visibility.Collapsed;
            }
        }
    }
}
