using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Verrou.UI
{
    /// <summary>
    /// Thème clair, sombre ou automatique (comme Windows). Les couleurs sont des ressources dynamiques :
    /// les remplacer met à jour toutes les fenêtres ouvertes immédiatement.
    /// </summary>
    public static class ThemeManager
    {
        public const string Auto = "auto", Light = "light", Dark = "dark";

        public static string Mode { get; private set; } = Auto;
        public static bool IsLight { get; private set; }
        public static event Action Changed;

        // Charte sombre (Noir profond, Anthracite, Ardoise, Vert sécurité, Cyan technique, Rouge alerte).
        private static readonly Dictionary<string, string> DarkPalette = new Dictionary<string, string>
        {
            ["BgBrush"] = "#FF0B0F14", ["SurfaceBrush"] = "#FF151A21", ["Surface2Brush"] = "#FF1E252F", ["Surface3Brush"] = "#FF262E39",
            ["BorderBrush"] = "#FF2A323D", ["BorderStrongBrush"] = "#FF3A4452",
            ["TextBrush"] = "#FFE8EDF2", ["MutedTextBrush"] = "#FF9AA5B4", ["DisabledTextBrush"] = "#FF5F6A79",
            ["AccentBrush"] = "#FF00C48C", ["AccentHoverBrush"] = "#FF1AD39C", ["AccentDarkBrush"] = "#FF00A878", ["AccentSoftBrush"] = "#2600C48C",
            ["OnAccentBrush"] = "#FF0B0F14", ["CyanBrush"] = "#FF00B4D8", ["CyanSoftBrush"] = "#2600B4D8",
            ["DangerBrush"] = "#FFE63946", ["DangerSoftBrush"] = "#26E63946", ["DangerTextBrush"] = "#FFFF6B76", ["DangerHoverBrush"] = "#40E63946",
            ["WarningBrush"] = "#FFF0A52B", ["SelectionBrush"] = "#3300C48C", ["HoverBrush"] = "#FF1A2029",
            ["ButtonHoverBorderBrush"] = "#FF4A5566", ["ScrollThumbBrush"] = "#FF3A4452", ["ScrollThumbHoverBrush"] = "#FF566173", ["SwitchKnobBrush"] = "#FFFFFFFF",
        };

        // Déclinaison claire : mêmes teintes, assombries pour garder un contraste ≥ 4,5:1 sur fond blanc.
        private static readonly Dictionary<string, string> LightPalette = new Dictionary<string, string>
        {
            ["BgBrush"] = "#FFF3F5F8", ["SurfaceBrush"] = "#FFFFFFFF", ["Surface2Brush"] = "#FFEEF1F5", ["Surface3Brush"] = "#FFE2E7EE",
            ["BorderBrush"] = "#FFD6DCE4", ["BorderStrongBrush"] = "#FFBAC3CF",
            ["TextBrush"] = "#FF0B0F14", ["MutedTextBrush"] = "#FF525D6E", ["DisabledTextBrush"] = "#FF9AA5B4",
            ["AccentBrush"] = "#FF007A57", ["AccentHoverBrush"] = "#FF008F66", ["AccentDarkBrush"] = "#FF006548", ["AccentSoftBrush"] = "#2400C48C",
            ["OnAccentBrush"] = "#FFFFFFFF", ["CyanBrush"] = "#FF0077A3", ["CyanSoftBrush"] = "#1A00B4D8",
            ["DangerBrush"] = "#FFC62835", ["DangerSoftBrush"] = "#14E63946", ["DangerTextBrush"] = "#FFB3202C", ["DangerHoverBrush"] = "#24E63946",
            ["WarningBrush"] = "#FFB26B00", ["SelectionBrush"] = "#2600C48C", ["HoverBrush"] = "#FFF0F3F7",
            ["ButtonHoverBorderBrush"] = "#FF9AA5B4", ["ScrollThumbBrush"] = "#FFC3CBD6", ["ScrollThumbHoverBrush"] = "#FF9AA5B4", ["SwitchKnobBrush"] = "#FFFFFFFF",
        };

        // Anciens noms utilisés par le code.
        private static readonly (string Alias, string Target)[] Aliases =
        {
            ("InkBrush", "TextBrush"), ("PanelBrush", "BgBrush"), ("ChromeBrush", "SurfaceBrush"),
            ("ChromeHoverBrush", "Surface2Brush"), ("ChromeTextBrush", "TextBrush"),
        };

        /// <summary>Le thème « clair » de Windows (Paramètres › Personnalisation › Couleurs) est-il actif ?</summary>
        public static bool WindowsUsesLightTheme()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v ? v != 0 : true;
            }
            catch (Exception) { return true; }
        }

        public static void Apply(string mode)
        {
            Mode = mode == Light || mode == Dark ? mode : Auto;
            bool light = Mode == Light || (Mode == Auto && WindowsUsesLightTheme());
            IsLight = light;

            var res = Application.Current.Resources;
            var palette = light ? LightPalette : DarkPalette;
            foreach (var kv in palette)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                b.Freeze();
                res[kv.Key] = b;
            }
            foreach (var (alias, target) in Aliases) res[alias] = res[target];

            var shadow = new DropShadowEffect
            {
                BlurRadius = light ? 24 : 28, ShadowDepth = light ? 3 : 6, Direction = 270,
                Opacity = light ? 0.10 : 0.45, Color = light ? Color.FromRgb(0x1E, 0x25, 0x2F) : Colors.Black,
            };
            shadow.Freeze();
            res["Elevation3"] = shadow;

            Services.WindowSecurity.ApplyToAll();
            Changed?.Invoke();
        }

        /// <summary>À appeler quand Windows change de thème : n'a d'effet qu'en mode automatique.</summary>
        public static void OnSystemThemeChanged()
        {
            if (Mode != Auto) return;
            bool light = WindowsUsesLightTheme();
            if (light != IsLight) Apply(Auto);
        }

        public static string Label(string mode) => mode switch
        {
            Light => "Clair",
            Dark => "Sombre",
            _ => "Automatique (comme Windows)",
        };
    }
}
