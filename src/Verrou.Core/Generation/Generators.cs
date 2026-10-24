using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Verrou.Core.Generation;

public sealed class PasswordOptions
{
    public int Length { get; set; } = 20;
    public bool Upper { get; set; } = true;
    public bool Lower { get; set; } = true;
    public bool Digits { get; set; } = true;
    public bool Symbols { get; set; } = true;
    /// <summary>Exclut les caractères qui se confondent (I l 1 O 0 o…).</summary>
    public bool ExcludeAmbiguous { get; set; }
    /// <summary>Caractères supplémentaires autorisés.</summary>
    public string Extra { get; set; } = "";
    /// <summary>Caractères interdits.</summary>
    public string Exclude { get; set; } = "";
}

/// <summary>Générateur aléatoire cryptographique (RandomNumberGenerator, tirages sans biais).</summary>
public static class PasswordGenerator
{
    public const string UpperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string LowerChars = "abcdefghijklmnopqrstuvwxyz";
    public const string DigitChars = "0123456789";
    public const string SymbolChars = "!#$%&*+-=?@^_~.,:;()[]{}<>/|";
    private const string Ambiguous = "Il1O0o|`'\"";

    public static string Generate(PasswordOptions o)
    {
        var classes = BuildClasses(o);
        if (classes.Count == 0) throw new ArgumentException("Choisissez au moins un type de caractères.");
        int length = Math.Clamp(o.Length, 4, 512);
        string pool = string.Concat(classes).Distinct().Aggregate(new StringBuilder(), (sb, c) => sb.Append(c)).ToString();
        bool requireAll = length >= classes.Count;

        // Tirage uniforme dans le pool, recommencé tant que chaque type demandé n'apparaît pas (aucun biais de position).
        char[] buf = new char[length];
        for (int attempt = 0; ; attempt++)
        {
            for (int i = 0; i < length; i++) buf[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
            if (!requireAll || attempt > 1000 || classes.All(cls => buf.Any(cls.Contains))) break;
        }
        var s = new string(buf);
        Array.Clear(buf);
        return s;
    }

    public static double EntropyBits(PasswordOptions o)
    {
        int pool = string.Concat(BuildClasses(o)).Distinct().Count();
        return pool <= 1 ? 0 : Math.Clamp(o.Length, 4, 512) * Math.Log2(pool);
    }

    private static List<string> BuildClasses(PasswordOptions o)
    {
        var list = new List<string>();
        void Add(bool on, string chars)
        {
            if (!on) return;
            var filtered = new string(chars.Where(c => !(o.ExcludeAmbiguous && Ambiguous.Contains(c)) && !o.Exclude.Contains(c)).Distinct().ToArray());
            if (filtered.Length > 0) list.Add(filtered);
        }
        Add(o.Upper, UpperChars);
        Add(o.Lower, LowerChars);
        Add(o.Digits, DigitChars);
        Add(o.Symbols, SymbolChars);
        Add(!string.IsNullOrEmpty(o.Extra), o.Extra);
        return list;
    }
}

public sealed class PassphraseOptions
{
    public int WordCount { get; set; } = 6;
    public string Separator { get; set; } = "-";
    public bool Capitalize { get; set; } = true;
    /// <summary>Ajoute un chiffre à la fin d'un mot tiré au hasard.</summary>
    public bool AddDigit { get; set; }
}

public static class PassphraseGenerator
{
    private static string[]? _default;

    /// <summary>Liste EFF « large » (7 776 mots anglais, licence CC BY 3.0) intégrée à l'application.</summary>
    public static string[] DefaultWordlist
    {
        get
        {
            if (_default != null) return _default;
            using var s = typeof(PassphraseGenerator).Assembly.GetManifestResourceStream("Verrou.Core.Wordlist.txt")!;
            using var r = new StreamReader(s, Encoding.UTF8);
            _default = ParseWordlist(r.ReadToEnd());
            return _default;
        }
    }

    /// <summary>Accepte un mot par ligne, ou le format diceware « 11111  mot ».</summary>
    public static string[] ParseWordlist(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Last())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static string Generate(PassphraseOptions o, IReadOnlyList<string>? words = null)
    {
        words ??= DefaultWordlist;
        if (words.Count < 2) throw new ArgumentException("La liste de mots est trop courte.");
        int n = Math.Clamp(o.WordCount, 2, 40);
        var picked = new string[n];
        for (int i = 0; i < n; i++)
        {
            string w = words[RandomNumberGenerator.GetInt32(words.Count)];
            if (o.Capitalize && w.Length > 0) w = char.ToUpperInvariant(w[0]) + w[1..];
            picked[i] = w;
        }
        if (o.AddDigit)
        {
            int i = RandomNumberGenerator.GetInt32(n);
            picked[i] += RandomNumberGenerator.GetInt32(10).ToString();
        }
        return string.Join(o.Separator, picked);
    }

    public static double EntropyBits(PassphraseOptions o, int wordlistSize) =>
        Math.Clamp(o.WordCount, 2, 40) * Math.Log2(Math.Max(2, wordlistSize)) +
        (o.AddDigit ? Math.Log2(10) + Math.Log2(Math.Clamp(o.WordCount, 2, 40)) : 0);
}

/// <summary>Estimation simple de la robustesse d'un mot de passe saisi à la main.</summary>
public static class PasswordStrength
{
    public static double EstimateBits(string? pw)
    {
        if (string.IsNullOrEmpty(pw)) return 0;
        int pool = 0;
        if (pw.Any(char.IsLower)) pool += 26;
        if (pw.Any(char.IsUpper)) pool += 26;
        if (pw.Any(char.IsDigit)) pool += 10;
        if (pw.Any(c => !char.IsLetterOrDigit(c) && c < 128)) pool += 33;
        if (pw.Any(c => c >= 128)) pool += 64;
        // Pénalise les répétitions : on compte les caractères distincts consécutifs « utiles ».
        int effective = 1;
        for (int i = 1; i < pw.Length; i++)
        {
            if (pw[i] == pw[i - 1]) continue;
            if (i >= 2 && pw[i] - pw[i - 1] == pw[i - 1] - pw[i - 2]) continue; // suites abc, 123
            effective++;
        }
        return effective * Math.Log2(Math.Max(pool, 2));
    }

    public static string Label(double bits) => bits switch
    {
        < 40 => "Très faible",
        < 60 => "Faible",
        < 80 => "Moyen",
        < 110 => "Fort",
        _ => "Très fort",
    };
}
