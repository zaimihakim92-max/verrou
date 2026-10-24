using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Verrou.Core.Model;
using Verrou.Services;

namespace Verrou.UI
{
    /// <summary>Choix de l'entrée à taper quand plusieurs (ou aucune) correspondent à la fenêtre active.</summary>
    public sealed class AutoTypePickerWindow : Window
    {
        private readonly List<Entry> _all;
        private readonly VaultData _data;
        private readonly TextBox _search = new TextBox { Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(6, 5, 6, 5) };
        private readonly ListBox _list = new ListBox { Height = 300 };

        public Entry Chosen { get; private set; }

        private AutoTypePickerWindow(List<Entry> entries, VaultData data, string windowTitle, bool noMatch)
        {
            _all = entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            _data = data;
            Title = "Verrou — Auto-type";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;
            Style = (Style)Application.Current.Resources["BaseWindow"];
            SetResourceReference(BackgroundProperty, "SurfaceBrush");
            WindowSecurity.Protect(this);

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = noMatch ? "Aucune entrée ne correspond à cette fenêtre. Choisissez celle à taper :" : "Plusieurs entrées correspondent. Laquelle taper ?",
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
            });
            root.Children.Add(new TextBlock
            {
                Text = "Fenêtre : " + windowTitle, Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = windowTitle,
            });
            root.Children.Add(_search);
            root.Children.Add(_list);
            root.Children.Add(new TextBlock
            {
                Text = "Entrée : taper · Échap : annuler · astuce : ajoutez un motif de fenêtre dans l'onglet Auto-type de l'entrée.",
                Style = (Style)Application.Current.Resources["HelpText"],
            });
            Content = root;

            _list.DisplayMemberPath = "Label";
            _search.TextChanged += (s, e) => Filter();
            _search.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.Items.Count - 1); e.Handled = true; }
                if (e.Key == Key.Up && _list.Items.Count > 0) { _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0); e.Handled = true; }
            };
            _list.MouseDoubleClick += (s, e) => Accept();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
            Loaded += (s, e) => { Activate(); _search.Focus(); };
            Filter();
        }

        private sealed class Row
        {
            public Entry Entry { get; set; }
            public string Label { get; set; }
            public override string ToString() => Label; // nom lu par les lecteurs d'écran
        }

        private void Filter()
        {
            string q = _search.Text.Trim();
            _list.ItemsSource = _all.Where(e => EntrySearch.Matches(e, q))
                .Select(e => new Row { Entry = e, Label = $"{e.Title}   —   {e.UserName}   ({_data.PathOf(e.GroupId)})" }).ToList();
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }

        private void Accept()
        {
            if (_list.SelectedItem is Row r) { Chosen = r.Entry; Close(); }
        }

        public static Entry Pick(List<Entry> entries, VaultData data, string windowTitle, bool noMatch)
        {
            if (entries.Count == 0) return null;
            var w = new AutoTypePickerWindow(entries, data, windowTitle, noMatch);
            w.ShowDialog();
            return w.Chosen;
        }
    }
}
