using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verrou.Core.Crypto;

/// <summary>
/// Chaîne secrète (mot de passe d'une entrée…) gardée chiffrée en mémoire tant qu'elle n'est pas affichée ou tapée.
/// Sous Windows : CryptProtectMemory (clé liée au processus). Ailleurs : masque XOR aléatoire.
/// Immuable : pour changer la valeur, on crée une nouvelle instance.
/// </summary>
[JsonConverter(typeof(SecretStringJsonConverter))]
public sealed class SecretString : IEquatable<SecretString>
{
    private const int BlockSize = 16; // CRYPTPROTECTMEMORY_BLOCK_SIZE
    private const uint SameProcess = 0;

    public static readonly SecretString Empty = new(string.Empty);

    private readonly byte[] _data;   // octets UTF-16 protégés, complétés à un multiple de 16
    private readonly byte[]? _pad;   // masque XOR (repli hors Windows)
    private readonly int _byteLength;

    public SecretString(string? value)
    {
        value ??= string.Empty;
        _byteLength = value.Length * 2;
        int padded = Math.Max(BlockSize, (_byteLength + BlockSize - 1) / BlockSize * BlockSize);
        _data = GC.AllocateArray<byte>(padded, pinned: true);
        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(_data);

        if (OperatingSystem.IsWindows() && CryptProtectMemory(_data, (uint)_data.Length, SameProcess))
            return;

        _pad = RandomNumberGenerator.GetBytes(padded);
        for (int i = 0; i < padded; i++) _data[i] ^= _pad[i];
    }

    public int Length => _byteLength / 2;
    public bool IsEmpty => _byteLength == 0;

    /// <summary>Renvoie la valeur en clair. À n'appeler qu'au moment de l'utiliser.</summary>
    public string Reveal()
    {
        if (_byteLength == 0) return string.Empty;
        byte[] tmp = GC.AllocateArray<byte>(_data.Length, pinned: true);
        try
        {
            _data.CopyTo(tmp, 0);
            if (_pad == null)
            {
                if (!CryptUnprotectMemory(tmp, (uint)tmp.Length, SameProcess))
                    throw new CryptographicException("Impossible de déchiffrer une valeur protégée en mémoire.");
            }
            else
            {
                for (int i = 0; i < tmp.Length; i++) tmp[i] ^= _pad[i];
            }
            return new string(MemoryMarshal.Cast<byte, char>(tmp.AsSpan(0, _byteLength)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tmp);
        }
    }

    public bool Equals(SecretString? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(Reveal().AsSpan()), MemoryMarshal.AsBytes(other.Reveal().AsSpan()));
    }

    public override bool Equals(object? obj) => Equals(obj as SecretString);
    public override int GetHashCode() => _byteLength;
    public override string ToString() => IsEmpty ? string.Empty : "••••••••";

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectMemory(byte[] pData, uint cbData, uint dwFlags);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectMemory(byte[] pData, uint cbData, uint dwFlags);
}

/// <summary>Écrit la valeur en clair : n'est utilisé qu'à l'intérieur de la charge utile chiffrée du coffre.</summary>
public sealed class SecretStringJsonConverter : JsonConverter<SecretString>
{
    public override SecretString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? SecretString.Empty : new SecretString(reader.GetString());

    public override void Write(Utf8JsonWriter writer, SecretString value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Reveal());
}
