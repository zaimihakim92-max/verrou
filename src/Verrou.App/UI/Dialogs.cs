using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Verrou.Core.Crypto;
using Verrou.Services;

namespace Verrou.UI
{
    public static class Dialogs
    {
        public static void Info(Window owner, string text) =>
            MessageWindow.Show(owner, L.T(text), "Verrou", MessageWindow.Kind.Info, L.T("OK"));

        public static void Warning(Window owner, string text) =>
            MessageWindow.Show(owner, L.T(text), "Verrou", MessageWindow.Kind.Warning, L.T("OK"));

        public static void Error(Window owner, string text) =>
            MessageWindow.Show(owner, L.T(text), "Verrou", MessageWindow.Kind.Error, L.T("OK"));

        public static bool Confirm(Window owner, string text, string title = "Verrou", string yes = "Oui", string no = "Non") =>
            MessageWindow.Show(owner, L.T(text), L.T(title), MessageWindow.Kind.Question, L.T(yes), L.T(no));

        /// <summary>Petite fenêtre de saisie d'un texte.</summary>
        public static string Prompt(Window owner, string title, string label, string initial = "")
        {
            var w = new Window
            {
                Title = title, Owner = owner, Width = 420, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Style = (Style)Application.Current.Resources["BaseWindow"], Background = Theme.Brush("SurfaceBrush"),
            };
            WindowSecurity.Protect(w);
            var tb = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Style = (Style)Application.Current.Resources["PrimaryButton"] };
            var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90 };
            ok.Click += (s, e) => w.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
            root.Children.Add(new TextBlock { Text = label });
            root.Children.Add(tb);
            root.Children.Add(buttons);
            w.Content = root;
            w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            return w.ShowDialog() == true ? tb.Text.Trim() : null;
        }

        /// <summary>Construit une clé composite depuis un PasswordBox sans garder le mot de passe dans une chaîne.</summary>
        public static CompositeKey KeyFrom(PasswordBox box, byte[] keyFile, byte[] twoFactorSecret = null)
        {
            using SecureString ss = box.SecurePassword;
            IntPtr p = Marshal.SecureStringToGlobalAllocUnicode(ss);
            try
            {
                unsafe
                {
                    var span = new ReadOnlySpan<char>((char*)p, ss.Length);
                    return CompositeKey.Create(span, keyFile, twoFactorSecret);
                }
            }
            finally
            {
                Marshal.ZeroFreeGlobalAllocUnicode(p);
            }
        }

        public static TextBlock Icon(string glyph, double size = 14) => new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
