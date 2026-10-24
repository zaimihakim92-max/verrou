using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Verrou.UI
{
    /// <summary>Champ de mot de passe masqué, avec bouton « œil » pour l'afficher.</summary>
    public sealed class SecretBox : UserControl
    {
        private readonly PasswordBox _pwd = new PasswordBox();
        private readonly TextBox _txt = new TextBox { Visibility = Visibility.Collapsed };
        private readonly Button _eye;
        private bool _syncing;

        public event EventHandler TextChanged;

        public SecretBox()
        {
            var mono = (FontFamily)Application.Current.Resources["MonoFont"];
            _pwd.FontFamily = mono;
            _txt.FontFamily = mono;
            // Le TextBox ne contient la valeur que lorsqu'elle est affichée ; masquée, elle n'est que dans le PasswordBox.
            _pwd.PasswordChanged += (s, e) => { if (!_syncing) TextChanged?.Invoke(this, EventArgs.Empty); };
            _txt.TextChanged += (s, e) => { if (!_syncing) TextChanged?.Invoke(this, EventArgs.Empty); };

            _eye = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = "Afficher / masquer", Focusable = false };
            _eye.Click += (s, e) => Revealed = !Revealed;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(_pwd);
            g.Children.Add(_txt);
            Grid.SetColumn(_eye, 1);
            g.Children.Add(_eye);
            Content = g;
        }

        public string Text
        {
            get => Revealed ? _txt.Text : _pwd.Password;
            set
            {
                if (Revealed) _txt.Text = value ?? "";
                else _pwd.Password = value ?? "";
            }
        }

        public bool Revealed
        {
            get => _txt.Visibility == Visibility.Visible;
            set
            {
                if (value == Revealed) return;
                bool hadFocus = _pwd.IsKeyboardFocusWithin || _txt.IsKeyboardFocusWithin;
                _syncing = true;
                if (value) _txt.Text = _pwd.Password;
                else { _pwd.Password = _txt.Text; _txt.Clear(); }
                _syncing = false;
                _txt.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                _pwd.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                _eye.Content = value ? "" : "";
                if (hadFocus) FocusInput();
            }
        }

        public bool ShowEyeButton { set => _eye.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

        /// <summary>Accès au PasswordBox (pour lire la valeur sans créer de chaîne, via SecurePassword).</summary>
        public PasswordBox PasswordBox => _pwd;

        public void FocusInput()
        {
            if (Revealed) { _txt.Focus(); _txt.CaretIndex = _txt.Text.Length; }
            else _pwd.Focus();
        }

        public void Clear()
        {
            _pwd.Clear();
            _txt.Clear();
        }
    }
}
