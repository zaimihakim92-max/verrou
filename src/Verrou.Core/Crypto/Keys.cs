using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Verrou.Core.Crypto;

/// <summary>Paramètres de la dérivation de clé Argon2id, stockés en clair dans l'en-tête du coffre.</summary>
public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    public const byte Argon2id = 1;

    // Bornes acceptées à l'ouverture : évite qu'un fichier piégé bloque la machine (mémoire géante)
    // ou affaiblisse la protection (paramètres ridicules).
    public const int MinMemoryKiB = 8 * 1024, MaxMemoryKiB = 4 * 1024 * 1024;
    public const int MinIterations = 1, MaxIterations = 100;
    public const int MinParallelism = 1, MaxParallelism = 64;

    /// <summary>64 Mio, 3 passes, 4 fils : environ une demi-seconde sur un PC récent.</summary>
    public static KdfParameters Standard => new(64 * 1024, 3, 4);
    /// <summary>256 Mio, 4 passes, 4 fils : plus lent, plus résistant.</summary>
    public static KdfParameters Strong => new(256 * 1024, 4, 4);

    public void Validate()
    {
        if (MemoryKiB < MinMemoryKiB || MemoryKiB > MaxMemoryKiB ||
            Iterations < MinIterations || Iterations > MaxIterations ||
            Parallelism < MinParallelism || Parallelism > MaxParallelism ||
            MemoryKiB < 8 * Parallelism)
            throw new InvalidVaultFormatException("Paramètres de dérivation de clé hors limites.");
    }

    public override string ToString() => $"Argon2id — {MemoryKiB / 1024} Mio, {Iterations} passes, {Parallelism} fils";
}

/// <summary>
/// Clé composite = SHA-256(mot de passe maître) ‖ SHA-256(fichier-clé, optionnel) ‖ SHA-256(secret 2FA, optionnel).
/// Ne garde que les empreintes, jamais le mot de passe lui-même.
/// </summary>
public sealed class CompositeKey : IDisposable
{
    private readonly byte[] _material;

    public bool HasKeyFile { get; }
    /// <summary>Le secret de double authentification (TOTP) fait partie de la clé.</summary>
    public bool HasTwoFactor { get; }

    private CompositeKey(byte[] material, bool hasKeyFile, bool hasTwoFactor)
    {
        _material = material;
        HasKeyFile = hasKeyFile;
        HasTwoFactor = hasTwoFactor;
    }

    public static CompositeKey Create(ReadOnlySpan<char> password, byte[]? keyFileBytes = null, byte[]? twoFactorSecret = null)
    {
        if (password.IsEmpty && keyFileBytes == null)
            throw new ArgumentException("Il faut un mot de passe maître ou un fichier-clé.");

        bool hasKeyFile = keyFileBytes != null, hasTwoFactor = twoFactorSecret != null;
        byte[] material = GC.AllocateArray<byte>(32 * (1 + (hasKeyFile ? 1 : 0) + (hasTwoFactor ? 1 : 0)), pinned: true);

        byte[] utf8 = GC.AllocateArray<byte>(Encoding.UTF8.GetMaxByteCount(password.Length), pinned: true);
        try
        {
            int n = Encoding.UTF8.GetBytes(password, utf8);
            SHA256.HashData(utf8.AsSpan(0, n), material.AsSpan(0, 32));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }

        int pos = 32;
        if (hasKeyFile)
        {
            SHA256.HashData(keyFileBytes, material.AsSpan(pos, 32));
            pos += 32;
        }
        if (hasTwoFactor)
        {
            // Préfixe de domaine : le secret 2FA ne peut pas être confondu avec un fichier-clé.
            byte[] tagged = new byte[16 + twoFactorSecret!.Length];
            Encoding.ASCII.GetBytes("Verrou 2FA v1").CopyTo(tagged, 0); // 13 octets + 3 zéros
            twoFactorSecret.CopyTo(tagged, 16);
            SHA256.HashData(tagged, material.AsSpan(pos, 32));
            CryptographicOperations.ZeroMemory(tagged);
        }

        return new CompositeKey(material, hasKeyFile, hasTwoFactor);
    }

    internal ReadOnlySpan<byte> Material => _material;

    public void Dispose() => CryptographicOperations.ZeroMemory(_material);
}

/// <summary>
/// Clé maîtresse (32 octets) issue d'Argon2id. Gardée en mémoire (tableau épinglé) tant que le coffre est
/// déverrouillé, pour pouvoir réenregistrer sans redemander le mot de passe ; effacée au verrouillage.
/// </summary>
public sealed class MasterKey : IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;

    public KdfParameters Kdf { get; }
    public byte[] Salt { get; }
    public bool RequiresKeyFile { get; }
    public bool RequiresTwoFactor { get; }

    /// <summary>Identifiant stable du coffre pour retrouver son secret 2FA sur ce PC (dérivé du sel, change avec la clé).</summary>
    public string TwoFactorId => VaultHeaderInfo.IdFromSalt(Salt);

    private MasterKey(byte[] key, KdfParameters kdf, byte[] salt, bool requiresKeyFile, bool requiresTwoFactor)
    {
        _key = key;
        Kdf = kdf;
        Salt = salt;
        RequiresKeyFile = requiresKeyFile;
        RequiresTwoFactor = requiresTwoFactor;
    }

    /// <summary>Dérive la clé. Sans sel fourni, un nouveau sel aléatoire de 32 octets est tiré.</summary>
    public static MasterKey Derive(CompositeKey composite, KdfParameters kdf, byte[]? salt = null)
    {
        kdf.Validate();
        salt ??= RandomNumberGenerator.GetBytes(32);
        byte[] key = GC.AllocateArray<byte>(32, pinned: true);
        byte[] input = composite.Material.ToArray();
        try
        {
            using var argon = new Argon2id(input)
            {
                Salt = salt,
                MemorySize = kdf.MemoryKiB,
                Iterations = kdf.Iterations,
                DegreeOfParallelism = kdf.Parallelism,
            };
            byte[] derived = argon.GetBytes(32);
            derived.CopyTo(key, 0);
            CryptographicOperations.ZeroMemory(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
        return new MasterKey(key, kdf, salt, composite.HasKeyFile, composite.HasTwoFactor);
    }

    /// <summary>Sous-clé indépendante (HKDF-SHA256) pour un usage donné.</summary>
    internal byte[] DeriveSubKey(string purpose, ReadOnlySpan<byte> salt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] output = GC.AllocateArray<byte>(32, pinned: true);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, output, salt, Encoding.ASCII.GetBytes("Verrou v1 " + purpose));
        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}
