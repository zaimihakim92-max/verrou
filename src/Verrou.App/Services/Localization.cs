using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Verrou.Services
{
    /// <summary>
    /// Traduction de l'interface (français → anglais). Le français est la langue source : chaque texte affiché est
    /// cherché dans Resources/en.json, y compris les messages avec variables (clés « … {0} … »).
    /// Les fenêtres sont traduites automatiquement à leur ouverture (titres, libellés, boutons, infobulles, menus) ;
    /// les données de l'utilisateur (zones de texte, éléments marqués Tag="data") ne sont jamais touchées.
    /// </summary>
    public static class L
    {
        public const string Auto = "auto", French = "fr", English = "en";
        public const string DataTag = "data";

        private static Dictionary<string, string> _exact = new Dictionary<string, string>();
        private static readonly List<(Regex Rx, string Format)> Patterns = new List<(Regex, string)>();

        public static bool IsEnglish { get; private set; }

        public static string Resolve(string pref) =>
            pref == French || pref == English ? pref
                : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? French : English;

        public static void Init(string pref)
        {
            IsEnglish = Resolve(pref) == English;
            if (IsEnglish)
            {
                using var s = typeof(L).Assembly.GetManifestResourceStream("Verrou.Lang.en.json");
                _exact = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>();
                foreach (var kv in _exact.Where(k => Regex.IsMatch(k.Key, @"\{\d+\}")))
                {
                    // « Coffre ouvert : {0} entrée(s). » -> ^Coffre ouvert : (.+?) entrée\(s\)\.$
                    string rx = "^" + Regex.Replace(Regex.Escape(kv.Key), @"\\\{(\d+)}", "(?<p$1>.+?)") + "$";
                    Patterns.Add((new Regex(rx, RegexOptions.Singleline | RegexOptions.CultureInvariant), kv.Value));
                }
            }

            // Traduction automatique de toute fenêtre ouverte et de tout menu contextuel affiché.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((o, e) => Translate(o as DependencyObject)));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((o, e) => Translate(o as DependencyObject)));
        }

        /// <summary>Traduit un texte (inchangé en français, ou s'il est inconnu).</summary>
        public static string T(string fr)
        {
            if (!IsEnglish || string.IsNullOrEmpty(fr)) return fr;
            if (_exact.TryGetValue(fr, out var en)) return en;
            foreach (var (rx, format) in Patterns)
            {
                var m = rx.Match(fr);
                if (!m.Success) continue;
                // Les morceaux variables sont eux-mêmes traduits s'ils sont connus (ex. « Mot de passe copié. »).
                return Regex.Replace(format, @"\{(\d+)\}", mm =>
                {
                    var g = m.Groups["p" + mm.Groups[1].Value];
                    return g.Success ? T(g.Value) : mm.Value;
                });
            }
            return fr;
        }

        public static string T(string frFormat, params object[] args) => string.Format(T(frFormat), args);

        /// <summary>Traduit les textes d'interface d'une fenêtre ou d'un élément (arbre logique).</summary>
        public static void Translate(DependencyObject root)
        {
            if (!IsEnglish || root == null) return;
            var seen = new HashSet<DependencyObject>();
            Walk(root, seen);
        }

        private static void Walk(DependencyObject d, HashSet<DependencyObject> seen)
        {
            if (d == null || !seen.Add(d)) return;
            if (d is FrameworkElement fe && Equals(fe.Tag, DataTag)) return;

            switch (d)
            {
                case Window w: w.Title = T(w.Title); break;
                case TextBox _: case PasswordBox _: return; // données saisies : jamais traduites
                case TextBlock tb:
                    // Copie de la liste : modifier un Run peut réorganiser la collection en cours de parcours.
                    var runs = tb.Inlines.OfType<Run>().ToList();
                    if (runs.Count == 1 && tb.Inlines.Count == 1) Set(tb.Text, v => tb.Text = v);
                    else foreach (var run in runs) Set(run.Text, v => run.Text = v);
                    break;
            }
            if (d is HeaderedContentControl hc && hc.Header is string h) Set(h, v => hc.Header = v);
            if (d is HeaderedItemsControl hi && hi.Header is string h2) Set(h2, v => hi.Header = v);
            if (d is ContentControl cc && !(d is Window) && cc.Content is string c) Set(c, v => cc.Content = v);
            if (d is MenuItem mi && !string.IsNullOrEmpty(mi.InputGestureText)) Set(mi.InputGestureText, v => mi.InputGestureText = v);
            if (d is FrameworkElement f2)
            {
                if (f2.ToolTip is string tt) Set(tt, v => f2.ToolTip = v);
                string name = AutomationProperties.GetName(f2);
                if (!string.IsNullOrEmpty(name)) Set(name, v => AutomationProperties.SetName(f2, v));
            }
            if (d is ListView lv && lv.View is GridView gv)
                foreach (var col in gv.Columns)
                    if (col.Header is string ch) { var cc2 = col; Set(ch, v => cc2.Header = v); }

            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().ToList())
                Walk(child, seen);
        }

        /// <summary>
        /// Traduit automatiquement une propriété texte à chaque changement (barre d'état, messages d'erreur,
        /// boutons dont le libellé change…), sans avoir à modifier chaque affectation dans le code.
        /// </summary>
        public static void Track(DependencyObject o, DependencyProperty p)
        {
            if (!IsEnglish || o == null) return;
            var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(p, o.GetType());
            bool busy = false;
            void Apply()
            {
                if (busy || !(o.GetValue(p) is string v)) return;
                string t = T(v);
                if (t == v) return;
                busy = true;
                o.SetValue(p, t);
                busy = false;
            }
            dpd.AddValueChanged(o, (s, e) => Apply());
            Apply();
        }

        public static void TrackText(params TextBlock[] blocks) { foreach (var b in blocks) Track(b, TextBlock.TextProperty); }
        public static void TrackContent(params ContentControl[] controls) { foreach (var c in controls) Track(c, ContentControl.ContentProperty); }

        private static void Set(string current, Action<string> apply)
        {
            string t = T(current);
            if (!ReferenceEquals(t, current) && t != current) apply(t);
        }
    }
}
