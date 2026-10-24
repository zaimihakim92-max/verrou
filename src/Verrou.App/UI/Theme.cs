using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Verrou.Core.Generation;
using Verrou.Services;

namespace Verrou.UI
{
    public static class Theme
    {
        public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        public static Style Style(string key) => (Style)Application.Current.Resources[key];

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Barre de titre Windows sombre (Noir profond), bordure couleur « Bordure ».</summary>
        public static void ApplyTitleBar(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            bool light = ThemeManager.IsLight;
            int on = light ? 0 : 1;
            int caption = light ? 0x00FFFFFF : 0x00140F0B;   // COLORREF = 0x00BBGGRR
            int border = light ? 0x00E4DCD6 : 0x003D322A;
            int text = light ? 0x00140F0B : 0x00F2EDE8;
            DwmSetWindowAttribute(h, 20, ref on, 4);       // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(h, 35, ref caption, 4);  // DWMWA_CAPTION_COLOR (Windows 11)
            DwmSetWindowAttribute(h, 34, ref border, 4);   // DWMWA_BORDER_COLOR
            DwmSetWindowAttribute(h, 36, ref text, 4);     // DWMWA_TEXT_COLOR
        }
    }

    /// <summary>Jauge de force en 5 segments (rouge alerte → ambre → vert sécurité), comme dans la charte.</summary>
    public sealed class StrengthMeter : UserControl
    {
        private readonly Border[] _segments = new Border[5];
        private readonly TextBlock _label = new TextBlock { FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

        public StrengthMeter()
        {
            var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 5; i++)
            {
                _segments[i] = new Border { Width = 28, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0), Background = Theme.Brush("Surface3Brush") };
                bars.Children.Add(_segments[i]);
            }
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(bars);
            Grid.SetColumn(_label, 1);
            row.Children.Add(_label);
            Content = row;
        }

        /// <summary>Affiche la force correspondant à une entropie en bits (texte facultatif à la suite).</summary>
        public void Show(double bits, string detail = null, bool empty = false)
        {
            int level = empty ? 0 : bits < 40 ? 1 : bits < 60 ? 2 : bits < 80 ? 3 : bits < 110 ? 4 : 5;
            Brush on = level <= 2 ? Theme.Brush("DangerBrush") : level == 3 ? Theme.Brush("WarningBrush") : Theme.Brush("AccentBrush");
            for (int i = 0; i < 5; i++) _segments[i].Background = i < level ? on : Theme.Brush("Surface3Brush");
            _label.Foreground = empty ? Theme.Brush("MutedTextBrush") : on;
            _label.Inlines.Clear();
            if (!empty) _label.Inlines.Add(new System.Windows.Documents.Run(Services.L.T(PasswordStrength.Label(bits))) { FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(detail))
                _label.Inlines.Add(new System.Windows.Documents.Run((empty ? "" : "  ·  ") + Services.L.T(detail)) { Foreground = Theme.Brush("MutedTextBrush") });
        }
    }

    /// <summary>Boîte de message aux couleurs de Verrou (remplace la MessageBox blanche de Windows).</summary>
    public sealed class MessageWindow : Window
    {
        public enum Kind { Info, Error, Question, Warning }
        private bool _result;

        private MessageWindow(Window owner, string text, string title, Kind kind, string yes, string no)
        {
            Title = title;
            Style = Theme.Style("BaseWindow");
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = owner == null;
            Topmost = owner == null;
            if (owner != null && owner.IsVisible) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowSecurity.Protect(this);

            string glyph = kind switch { Kind.Error => "", Kind.Warning => "", Kind.Question => "", _ => "" };
            string color = kind switch { Kind.Error => "DangerBrush", Kind.Warning => "WarningBrush", Kind.Question => "CyanBrush", _ => "AccentBrush" };

            var grid = new Grid { Margin = new Thickness(24, 24, 24, 20) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var icon = Dialogs.Icon(glyph, 22);
            icon.Foreground = Theme.Brush(color);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 0, 16, 0);
            grid.Children.Add(icon);
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = Theme.Brush("TextBrush") };
            Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, -4, 0) };
            var ok = new Button { Content = yes, MinWidth = 96, IsDefault = true, Style = Theme.Style(kind == Kind.Error ? "DangerButton" : "PrimaryButton") };
            ok.Click += (s, e) => { _result = true; Close(); };
            buttons.Children.Add(ok);
            if (no != null)
            {
                var cancel = new Button { Content = no, MinWidth = 96, IsCancel = true };
                cancel.Click += (s, e) => Close();
                buttons.Children.Add(cancel);
                ok.IsDefault = false; // pour une question, Entrée ne confirme pas par mégarde
                cancel.IsDefault = true;
            }
            else ok.IsCancel = true;
            Grid.SetRow(buttons, 1);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);
            Content = new Border { Background = Theme.Brush("SurfaceBrush"), Child = grid };
            Loaded += (s, e) => (no != null ? (UIElement)buttons.Children[1] : ok).Focus();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { try { Clipboard.SetText(text); } catch { } } };
        }

        public static bool Show(Window owner, string text, string title, Kind kind, string yes = "OK", string no = null)
        {
            var w = new MessageWindow(owner, text, title, kind, yes, no);
            w.ShowDialog();
            return w._result;
        }
    }
}
