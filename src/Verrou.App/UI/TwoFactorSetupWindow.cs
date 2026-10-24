using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;
using Verrou.Core.Crypto;
using Verrou.Services;

namespace Verrou.UI
{
    /// <summary>
    /// Configuration de la double authentification : QR code à scanner, clé de secours à noter, puis saisie d'un
    /// premier code pour prouver que le téléphone est bien configuré.
    /// </summary>
    public sealed class TwoFactorSetupWindow : Window
    {
        private readonly byte[] _secret;
        private readonly TextBox _code = new TextBox { FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 20, MaxLength = 7, Width = 160, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly CheckBox _saved = new CheckBox();
        private readonly TextBlock _error = new TextBlock { Foreground = Theme.Brush("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Button _ok;

        /// <summary>Secret confirmé (null si annulé).</summary>
        public byte[] Secret { get; private set; }
        /// <summary>Pas de temps du code de confirmation (marqué comme utilisé).</summary>
        public long ConfirmedStep { get; private set; } = -1;

        public TwoFactorSetupWindow(byte[] secret, string vaultName, bool reconfigure = false)
        {
            _secret = secret;
            Title = reconfigure ? "Reconfigurer la double authentification" : "Activer la double authentification";
            Style = Theme.Style("BaseWindow");
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            WindowSecurity.Protect(this);

            var root = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var shield = Dialogs.Icon("", 24);
            shield.Foreground = Theme.Brush("AccentBrush");
            shield.Margin = new Thickness(0, 0, 12, 0);
            head.Children.Add(shield);
            head.Children.Add(new TextBlock { Text = "Double authentification (2FA)", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(head);
            root.Children.Add(new TextBlock
            {
                Style = Theme.Style("HelpText"), FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 8, 0, 20),
                Text = reconfigure
                    ? "Scannez à nouveau ce QR code avec votre nouveau téléphone. La clé reste la même : l'ancien téléphone, s'il fonctionne encore, continuera aussi à générer des codes valides."
                    : $"À chaque ouverture de « {vaultName} », Verrou demandera, en plus du mot de passe maître, le code à 6 chiffres affiché par l'application d'authentification de votre téléphone.",
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            // QR code : modules sombres sur fond blanc (lisible par tous les scanners)
            var qr = new Image { Width = 196, Height = 196, Source = BuildQr(Totp.BuildUri(secret, vaultName)) };
            RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
            grid.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(10), VerticalAlignment = VerticalAlignment.Top, Child = qr });

            var steps = new StackPanel();
            Grid.SetColumn(steps, 2);
            steps.Children.Add(Step("1", "Scannez le QR code", "avec Google Authenticator, Microsoft Authenticator, Aegis, 2FAS, Bitwarden Authenticator…"));
            steps.Children.Add(Step("2", "Gardez la clé de secours", "Elle remplace le téléphone s'il est perdu, et elle est indispensable pour ouvrir ce coffre sur un autre PC. Notez-la sur papier."));

            string key = Base32.Group(Base32.Encode(secret));
            var keyBox = new TextBox
            {
                Text = key, IsReadOnly = true, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("CyanBrush"), Margin = new Thickness(36, 8, 0, 0),
            };
            steps.Children.Add(keyBox);
            var keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(32, 4, 0, 0) };
            var copy = new Button { Content = "Copier" };
            copy.Click += (s, e) => { ClipboardService.Copy(Base32.Encode(_secret), App.Current.Settings.ClipboardClearSeconds); copy.Content = L.T("Copié ✓"); };
            var save = new Button { Content = "Enregistrer…" };
            save.Click += (s, e) => SaveRecovery(vaultName);
            keyButtons.Children.Add(copy);
            keyButtons.Children.Add(save);
            steps.Children.Add(keyButtons);
            _saved.Content = new TextBlock { Text = "J'ai mis la clé de secours en lieu sûr", TextWrapping = TextWrapping.Wrap };
            _saved.Margin = new Thickness(36, 8, 0, 0);
            if (!reconfigure) steps.Children.Add(_saved);
            else _saved.IsChecked = true;
            grid.Children.Add(steps);
            root.Children.Add(grid);

            root.Children.Add(new Border { Height = 1, Background = Theme.Brush("BorderBrush"), Margin = new Thickness(0, 20, 0, 16) });
            root.Children.Add(Step("3", "Entrez le code affiché par l'application", null));
            _code.Margin = new Thickness(36, 8, 0, 0);
            root.Children.Add(_code);
            _error.Margin = new Thickness(36, 8, 0, 0);
            root.Children.Add(_error);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, -4, 0) };
            _ok = new Button { Content = reconfigure ? "Valider" : "Activer la 2FA", IsDefault = true, MinWidth = 140, Style = Theme.Style("PrimaryButton") };
            _ok.Click += (s, e) => Confirm();
            buttons.Children.Add(_ok);
            buttons.Children.Add(new Button { Content = "Annuler", IsCancel = true, MinWidth = 96 });
            root.Children.Add(buttons);

            Content = new Border { Background = Theme.Brush("SurfaceBrush"), Child = root };
            Loaded += (s, e) => _code.Focus();
        }

        private static UIElement Step(string number, string title, string text)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Theme.Brush("AccentSoftBrush"), VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = number, Foreground = Theme.Brush("AccentBrush"), FontWeight = FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            g.Children.Add(badge);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, Style = Theme.Style("HelpText") });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            return g;
        }

        private static BitmapImage BuildQr(string uri)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
            byte[] png = new PngByteQRCode(data).GetGraphic(8);
            var img = new BitmapImage();
            using (var ms = new MemoryStream(png))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
            }
            img.Freeze();
            CryptographicOperations.ZeroMemory(png);
            return img;
        }

        private void SaveRecovery(string vaultName)
        {
            var d = new SaveFileDialog { Filter = L.T("Texte (*.txt)|*.txt"), FileName = L.T($"Clé de secours 2FA - {vaultName}.txt"), Title = L.T("Enregistrer la clé de secours") };
            if (d.ShowDialog(this) != true) return;
            File.WriteAllText(d.FileName,
                L.T("Verrou — clé de secours de double authentification") + "\r\n" +
                L.T($"Coffre : {vaultName}") + "\r\n" +
                L.T($"Créée le : {DateTime.Now:dd/MM/yyyy HH:mm}") + "\r\n\r\n" +
                $"    {Base32.Group(Base32.Encode(_secret))}\r\n\r\n" +
                L.T("À utiliser si le téléphone est perdu, ou pour ouvrir le coffre sur un autre PC (lien « Utiliser la clé de secours » sur l'écran de déverrouillage).") + "\r\n" +
                L.T("Imprimez-la puis supprimez ce fichier : avec le mot de passe maître, elle suffit à ouvrir le coffre.") + "\r\n");
            Dialogs.Info(this, "Clé enregistrée. Imprimez-la ou recopiez-la, puis supprimez le fichier de cet ordinateur.");
        }

        private void Confirm()
        {
            _error.Visibility = Visibility.Collapsed;
            if (_saved.IsChecked != true)
            {
                ShowError("Confirmez d'abord que la clé de secours est en lieu sûr (étape 2).");
                return;
            }
            if (!Totp.Verify(_secret, _code.Text, DateTimeOffset.UtcNow, -1, out long step))
            {
                ShowError("Code incorrect. Vérifiez que l'heure du téléphone et du PC est à l'heure, puis tapez le code actuellement affiché.");
                _code.SelectAll();
                _code.Focus();
                return;
            }
            Secret = _secret;
            ConfirmedStep = step;
            DialogResult = true;
        }

        private void ShowError(string msg)
        {
            _error.Text = L.T(msg);
            _error.Visibility = Visibility.Visible;
        }
    }
}
