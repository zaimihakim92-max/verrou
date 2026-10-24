using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Verrou.Core.Crypto;

/// <summary>
/// Codes à usage unique basés sur le temps (TOTP, RFC 6238 : HMAC-SHA1, 6 chiffres, 30 s), compatibles avec
/// Google Authenticator, Microsoft Authenticator, Aegis, 2FAS, Bitwarden Authenticator…
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    public const int SecretLength = 20; // 160 bits, recommandé par la RFC 4226

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    public static long TimeStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        int mod = (int)Math.Pow(10, digits);
        return (binary % mod).ToString(new string('0', digits));
    }

    /// <summary>
    /// Vérifie un code en tolérant un décalage d'horloge de ±1 période. Refuse un pas de temps déjà utilisé
    /// (<paramref name="lastUsedStep"/>) pour empêcher qu'un code intercepté soit rejoué.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastUsedStep, out long matchedStep, int window = 1)
    {
        matchedStep = -1;
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != Digits) return false;
        long current = TimeStep(now);
        bool found = false;
        for (long s = current - window; s <= current + window; s++)
        {
            // Comparaison à temps constant, et on parcourt toute la fenêtre.
            bool eq = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, s)), Encoding.ASCII.GetBytes(code));
            if (eq && !found && s > lastUsedStep) { matchedStep = s; found = true; }
        }
        return found;
    }

    /// <summary>Adresse otpauth:// à mettre dans un QR code pour l'application d'authentification.</summary>
    public static string BuildUri(ReadOnlySpan<byte> secret, string account, string issuer = "Verrou")
    {
        string label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }
}

/// <summary>Base32 RFC 4648 (alphabet A–Z 2–7), sans remplissage, tolérant espaces/tirets/minuscules au décodage.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '=').ToArray();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char c in clean)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException($"Caractère invalide dans la clé : « {c} ».");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    /// <summary>Présentation lisible par groupes de 4 : « ABCD EFGH … ».</summary>
    public static string Group(string s) =>
        string.Join(" ", Enumerable.Range(0, (s.Length + 3) / 4).Select(i => s.Substring(i * 4, Math.Min(4, s.Length - i * 4))));
}
