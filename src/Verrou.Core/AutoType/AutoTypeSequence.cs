using System.Text;
using System.Text.RegularExpressions;
using Verrou.Core.Model;

namespace Verrou.Core.AutoType;

[Flags]
public enum KeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Win = 8 }

public abstract record AutoTypeAction;
/// <summary>Texte tapé caractère par caractère (Unicode).</summary>
public sealed record TypeTextAction(string Text, bool Sensitive = false) : AutoTypeAction;
/// <summary>Touche spéciale (code de touche virtuelle Windows), éventuellement répétée.</summary>
public sealed record KeyAction(ushort VirtualKey, KeyModifiers Modifiers = KeyModifiers.None, int Repeat = 1) : AutoTypeAction;
/// <summary>Caractère tapé avec des modificateurs (^a = Ctrl+A).</summary>
public sealed record ShortcutCharAction(char Char, KeyModifiers Modifiers) : AutoTypeAction;
public sealed record DelayAction(int Milliseconds) : AutoTypeAction;
/// <summary>Délai entre deux frappes pour la suite de la séquence.</summary>
public sealed record SetKeyDelayAction(int Milliseconds) : AutoTypeAction;

public sealed class AutoTypeParseException : Exception
{
    public AutoTypeParseException(string message) : base(message) { }
}

/// <summary>
/// Séquences d'auto-type compatibles KeePass :
/// <list type="bullet">
/// <item>{USERNAME} {PASSWORD} {TITLE} {URL} {NOTES} {S:Nom du champ}</item>
/// <item>{TAB} {ENTER} {SPACE} {BS} {DEL} {ESC} {UP} {DOWN} {LEFT} {RIGHT} {HOME} {END} {PGUP} {PGDN} {INS} {F1}…{F16}</item>
/// <item>répétition : {TAB 3} ; pauses : {DELAY 500} ; délai entre frappes : {DELAY=30}</item>
/// <item>modificateurs devant une touche ou un caractère : ^ = Ctrl, + = Maj, % = Alt (ex. ^a, +{TAB}) ; ~ = Entrée</item>
/// <item>caractères littéraux : {{} {}} {^} {+} {%} {~}</item>
/// </list>
/// Le contenu des champs ({PASSWORD}…) est toujours tapé tel quel, jamais interprété.
/// </summary>
public static class AutoTypeSequence
{
    public const string DefaultSequence = "{USERNAME}{TAB}{PASSWORD}{ENTER}";

    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["SPACE"] = 0x20,
        ["BS"] = 0x08, ["BKSP"] = 0x08, ["BACKSPACE"] = 0x08,
        ["DEL"] = 0x2E, ["DELETE"] = 0x2E, ["INS"] = 0x2D, ["INSERT"] = 0x2D,
        ["ESC"] = 0x1B, ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
        ["HOME"] = 0x24, ["END"] = 0x23, ["PGUP"] = 0x21, ["PGDN"] = 0x22,
        ["CAPSLOCK"] = 0x14, ["APPS"] = 0x5D, ["WIN"] = 0x5B, ["LWIN"] = 0x5B, ["RWIN"] = 0x5C,
    };

    static AutoTypeSequence()
    {
        for (int i = 1; i <= 16; i++) Keys["F" + i] = (ushort)(0x70 + i - 1);
    }

    /// <summary>Vérifie la syntaxe sans entrée (les champs sont remplacés par des valeurs vides).</summary>
    public static void Validate(string sequence) => Parse(sequence, new Entry());

    public static List<AutoTypeAction> Parse(string sequence, EntryFields entry)
    {
        var actions = new List<AutoTypeAction>();
        var text = new StringBuilder();
        var mods = KeyModifiers.None;

        void FlushText()
        {
            if (text.Length > 0) { actions.Add(new TypeTextAction(text.ToString())); text.Clear(); }
        }
        void AddChar(char c)
        {
            if (mods != KeyModifiers.None) { FlushText(); actions.Add(new ShortcutCharAction(c, mods)); mods = KeyModifiers.None; }
            else text.Append(c);
        }
        void AddValue(string? value, bool sensitive = false)
        {
            if (mods != KeyModifiers.None) throw new AutoTypeParseException("Un modificateur (^ + %) ne peut pas précéder un champ.");
            if (!sensitive) { text.Append(value ?? ""); return; }
            // Valeur secrète : action à part, pour pouvoir la brouiller à la frappe.
            FlushText();
            if (!string.IsNullOrEmpty(value)) actions.Add(new TypeTextAction(value, Sensitive: true));
        }

        int i = 0;
        while (i < sequence.Length)
        {
            char c = sequence[i];
            switch (c)
            {
                case '^': mods |= KeyModifiers.Control; i++; continue;
                case '+': mods |= KeyModifiers.Shift; i++; continue;
                case '%': mods |= KeyModifiers.Alt; i++; continue;
                case '~':
                    FlushText(); actions.Add(new KeyAction(0x0D, mods)); mods = KeyModifiers.None; i++; continue;
                case '}': throw new AutoTypeParseException($"« }} » isolé à la position {i + 1} (écrire {{}}}} pour une accolade).");
                case '{':
                    break;
                default:
                    AddChar(c); i++; continue;
            }

            // Jeton entre accolades. Cas particulier : {}} = accolade fermante littérale.
            int close = sequence.Length > i + 2 && sequence[i + 1] == '}' && sequence[i + 2] == '}' ? i + 2 : sequence.IndexOf('}', i + 1);
            if (close < 0) throw new AutoTypeParseException($"Accolade non fermée à la position {i + 1}.");
            string token = sequence.Substring(i + 1, close - i - 1);
            i = close + 1;

            if (token.Length == 1 && "{}^+%~()[]".Contains(token[0])) { AddChar(token[0]); continue; }

            if (token.StartsWith("S:", StringComparison.OrdinalIgnoreCase))
            {
                string name = token[2..];
                var field = entry.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new AutoTypeParseException($"Champ personnalisé inconnu : {name}");
                AddValue(field.Value.Reveal(), sensitive: field.Protected);
                continue;
            }

            string upper = token.Trim().ToUpperInvariant();
            switch (upper)
            {
                case "USERNAME": AddValue(entry.UserName); continue;
                case "PASSWORD": AddValue(entry.Password.Reveal(), sensitive: true); continue;
                case "TITLE": AddValue(entry.Title); continue;
                case "URL": AddValue(entry.Url); continue;
                case "NOTES": AddValue(entry.Notes.Replace("\r\n", "\n")); continue;
            }

            var m = Regex.Match(token.Trim(), @"^DELAY\s*(=)?\s*(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                if (mods != KeyModifiers.None) throw new AutoTypeParseException("Un modificateur ne peut pas précéder {DELAY}.");
                FlushText();
                int ms = Math.Min(int.Parse(m.Groups[2].Value), 60_000);
                actions.Add(m.Groups[1].Success ? new SetKeyDelayAction(ms) : new DelayAction(ms));
                continue;
            }

            m = Regex.Match(token.Trim(), @"^(\S+)(?:\s+(\d+))?$");
            if (m.Success && Keys.TryGetValue(m.Groups[1].Value, out ushort vk))
            {
                FlushText();
                int repeat = m.Groups[2].Success ? Math.Clamp(int.Parse(m.Groups[2].Value), 0, 100) : 1;
                actions.Add(new KeyAction(vk, mods, repeat));
                mods = KeyModifiers.None;
                continue;
            }

            throw new AutoTypeParseException($"Code inconnu : {{{token}}}");
        }

        if (mods != KeyModifiers.None) throw new AutoTypeParseException("La séquence se termine par un modificateur (^ + %).");
        FlushText();
        return actions;
    }
}

/// <summary>Choisit les entrées correspondant au titre de la fenêtre active.</summary>
public static class WindowMatcher
{
    /// <summary>
    /// Une entrée correspond si l'un de ses motifs de fenêtre correspond au titre ; sans motif, si le titre de la
    /// fenêtre contient le titre de l'entrée ou le nom d'hôte de son URL.
    /// </summary>
    public static List<Entry> FindMatches(IEnumerable<Entry> entries, string windowTitle)
    {
        var result = new List<Entry>();
        if (string.IsNullOrWhiteSpace(windowTitle)) return result;
        foreach (var e in entries)
        {
            if (!e.AutoType.Enabled) continue;
            var patterns = e.AutoType.WindowPatterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            bool ok = patterns.Count > 0
                ? patterns.Any(p => PatternMatches(p, windowTitle))
                : (e.Title.Trim().Length > 0 && windowTitle.Contains(e.Title.Trim(), StringComparison.CurrentCultureIgnoreCase))
                  || (HostOf(e.Url) is string host && windowTitle.Contains(host, StringComparison.OrdinalIgnoreCase));
            if (ok) result.Add(e);
        }
        return result;
    }

    public static bool PatternMatches(string pattern, string title)
    {
        pattern = pattern.Trim();
        try
        {
            if (pattern.Length > 4 && pattern.StartsWith("//") && pattern.EndsWith("//"))
                return Regex.IsMatch(title, pattern[2..^2], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            string rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(title, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static string? HostOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string u = url.Contains("://") ? url : "https://" + url;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return host.Contains('.') ? host : null;
    }
}
