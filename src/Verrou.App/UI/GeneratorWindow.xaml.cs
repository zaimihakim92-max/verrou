using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Verrou.Core.Generation;
using Verrou.Services;

namespace Verrou.UI
{
    public partial class GeneratorWindow : Window
    {
        private readonly bool _pickMode;
        private bool _ready;
        private string[] _words;

        /// <summary>Mot de passe choisi (mode « Utiliser »).</summary>
        public string Generated { get; private set; }

        /// <param name="pickMode">true : ouvert depuis une entrée, le bouton « Utiliser » renvoie la valeur.</param>
        public GeneratorWindow(bool pickMode)
        {
            InitializeComponent();
            WindowSecurity.Protect(this);
            L.TrackText(EntropyText, WordlistText);
            _pickMode = pickMode;
            if (!pickMode)
            {
                UseButton.Content = "Copier et fermer";
                ShowInTaskbar = true;
            }
            else ShowInTaskbar = false;

            var s = App.Current.Settings;
            LengthSlider.Value = Math.Clamp(s.Generator.Length, 8, 64);
            UseUpper.IsChecked = s.Generator.Upper;
            UseLower.IsChecked = s.Generator.Lower;
            UseDigits.IsChecked = s.Generator.Digits;
            UseSymbols.IsChecked = s.Generator.Symbols;
            NoAmbiguous.IsChecked = s.Generator.ExcludeAmbiguous;
            ExtraChars.Text = s.Generator.Extra;
            ExcludeChars.Text = s.Generator.Exclude;
            WordsSlider.Value = Math.Clamp(s.Passphrase.WordCount, 3, 12);
            Separator.Text = s.Passphrase.Separator;
            Capitalize.IsChecked = s.Passphrase.Capitalize;
            AddDigit.IsChecked = s.Passphrase.AddDigit;
            ModePassphrase.IsChecked = s.GeneratorPassphraseMode;
            ModePassword.IsChecked = !s.GeneratorPassphraseMode;

            _ready = true;
            Option_Changed(null, null);
            PreviewKeyDown += (o, e) => { if (e.Key == Key.F5) { Regenerate(); e.Handled = true; } };
            Closed += (o, e) => SaveOptions();
        }

        private PasswordOptions PwOptions() => new PasswordOptions
        {
            Length = (int)LengthSlider.Value,
            Upper = UseUpper.IsChecked == true,
            Lower = UseLower.IsChecked == true,
            Digits = UseDigits.IsChecked == true,
            Symbols = UseSymbols.IsChecked == true,
            ExcludeAmbiguous = NoAmbiguous.IsChecked == true,
            Extra = ExtraChars.Text,
            Exclude = ExcludeChars.Text,
        };

        private PassphraseOptions PpOptions() => new PassphraseOptions
        {
            WordCount = (int)WordsSlider.Value,
            Separator = Separator.Text,
            Capitalize = Capitalize.IsChecked == true,
            AddDigit = AddDigit.IsChecked == true,
        };

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            bool pp = ModePassphrase.IsChecked == true;
            PasswordPanel.Visibility = pp ? Visibility.Collapsed : Visibility.Visible;
            PassphrasePanel.Visibility = pp ? Visibility.Visible : Visibility.Collapsed;
            Regenerate();
        }

        private void Regenerate_Click(object sender, RoutedEventArgs e) => Regenerate();

        private void Regenerate()
        {
            try
            {
                double bits;
                if (ModePassphrase.IsChecked == true)
                {
                    var words = _words ?? PassphraseGenerator.DefaultWordlist;
                    Output.Text = PassphraseGenerator.Generate(PpOptions(), words);
                    bits = PassphraseGenerator.EntropyBits(PpOptions(), words.Length);
                }
                else
                {
                    Output.Text = PasswordGenerator.Generate(PwOptions());
                    bits = PasswordGenerator.EntropyBits(PwOptions());
                }
                Strength.Show(bits, $"{bits:0} bits");
                EntropyText.Text = $"Tirage cryptographique · {Output.Text.Length} caractères";
            }
            catch (ArgumentException ex)
            {
                Output.Text = "";
                EntropyText.Text = ex.Message;
                Strength.Show(0, null, empty: true);
            }
        }

        private void LoadWordlist_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*", Title = "Liste de mots" };
            if (d.ShowDialog(this) != true) return;
            var words = PassphraseGenerator.ParseWordlist(File.ReadAllText(d.FileName));
            if (words.Length < 100)
            {
                Dialogs.Error(this, $"Liste trop courte ({words.Length} mots) : il en faut au moins 100, idéalement 7 776.");
                return;
            }
            _words = words;
            WordlistText.Text = $"{Path.GetFileName(d.FileName)} ({words.Length} mots)";
            Regenerate();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            ClipboardService.Copy(Output.Text, App.Current.Settings.ClipboardClearSeconds);
            EntropyText.Text = $"Copié — effacé du presse-papiers dans {App.Current.Settings.ClipboardClearSeconds} s.";
        }

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (Output.Text.Length == 0) return;
            if (_pickMode)
            {
                Generated = Output.Text;
                DialogResult = true;
            }
            else
            {
                ClipboardService.Copy(Output.Text, App.Current.Settings.ClipboardClearSeconds);
                Close();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (!_pickMode) Close();
        }

        private void SaveOptions()
        {
            var s = App.Current.Settings;
            s.Generator = PwOptions();
            s.Passphrase = PpOptions();
            s.GeneratorPassphraseMode = ModePassphrase.IsChecked == true;
            s.Save();
        }
    }
}
