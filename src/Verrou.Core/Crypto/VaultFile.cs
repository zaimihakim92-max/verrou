using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Verrou.Core.Crypto;

public class VaultException : Exception
{
    public VaultException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Ce n'est pas un coffre Verrou (ou version inconnue).</summary>
public sealed class InvalidVaultFormatException : VaultException
{
    public InvalidVaultFormatException(string message) : base(message) { }
}

/// <summary>Mot de passe maître ou fichier-clé incorrect.</summary>
public sealed class WrongCredentialsException : VaultException
{
    public WrongCredentialsException() : base("Mot de passe maître ou fichier-clé incorrect.") { }
}

/// <summary>Ce coffre exige la double authentification (secret 2FA absent de la clé fournie).</summary>
public sealed class TwoFactorRequiredException : VaultException
{
    public TwoFactorRequiredException() : base("Ce coffre est protégé par la double authentification.") { }
}

/// <summary>La clé est bonne mais le contenu chiffré a été modifié ou abîmé.</summary>
public sealed class VaultTamperedException : VaultException
{
    public VaultTamperedException(Exception? inner = null)
        : base("Le fichier du coffre a été modifié ou est endommagé (contrôle d'intégrité échoué).", inner) { }
}

/// <summary>
/// Format binaire d'un coffre .verrou (version 1) :
/// <code>
///   0  "VRRU"            magie (4 octets)
///   4  version           u16 LE = 1
///   6  kdf               u8 = 1 (Argon2id)
///   7  parallélisme      u8
///   8  mémoire (Kio)     u32 LE
///  12  itérations        u32 LE
///  16  sel Argon2        32 octets
///  48  options           u8 (bit 0 = fichier-clé requis, bit 1 = double authentification requise)
///  49  graine            32 octets, nouvelle à chaque enregistrement (sel HKDF de la clé de chiffrement)
///  81  nonce AES-GCM     12 octets
///  93  vérificateur      32 octets = HMAC-SHA256(clé « verify », octets 0..92)
/// 125  données chiffrées AES-256-GCM (JSON compressé)
/// fin  étiquette GCM     16 octets
/// </code>
/// Tout l'en-tête (octets 0..124) est authentifié comme données associées : la moindre modification est détectée.
/// Le vérificateur permet de distinguer « mauvais mot de passe » de « fichier modifié ».
/// </summary>
public static class VaultFile
{
    public static ReadOnlySpan<byte> Magic => "VRRU"u8;
    public const ushort FormatVersion = 1;
    public const int HeaderLength = 125;
    public const int TagLength = 16;
    private const int VerifierOffset = 93;
    private const byte FlagKeyFile = 1, FlagTwoFactor = 2;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, MasterKey key)
    {
        byte[] file = new byte[HeaderLength + plaintext.Length + TagLength];
        Span<byte> h = file.AsSpan(0, HeaderLength);

        Magic.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], FormatVersion);
        h[6] = KdfParameters.Argon2id;
        h[7] = (byte)key.Kdf.Parallelism;
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)key.Kdf.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)key.Kdf.Iterations);
        key.Salt.CopyTo(h[16..48]);
        h[48] = (byte)((key.RequiresKeyFile ? FlagKeyFile : 0) | (key.RequiresTwoFactor ? FlagTwoFactor : 0));
        RandomNumberGenerator.Fill(h[49..81]);   // graine
        RandomNumberGenerator.Fill(h[81..93]);   // nonce

        byte[] verifyKey = key.DeriveSubKey("verify", ReadOnlySpan<byte>.Empty);
        byte[] encKey = key.DeriveSubKey("encrypt", h[49..81]);
        try
        {
            HMACSHA256.HashData(verifyKey, h[..VerifierOffset], h[VerifierOffset..HeaderLength]);
            using var aes = new AesGcm(encKey, TagLength);
            aes.Encrypt(h[81..93], plaintext,
                file.AsSpan(HeaderLength, plaintext.Length),
                file.AsSpan(file.Length - TagLength),
                h);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifyKey);
            CryptographicOperations.ZeroMemory(encKey);
        }
        return file;
    }

    /// <summary>Lit l'en-tête sans rien déchiffrer (pour savoir si un fichier-clé est requis).</summary>
    public static VaultHeaderInfo ReadHeader(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderLength + TagLength || !file[..4].SequenceEqual(Magic))
            throw new InvalidVaultFormatException("Ce fichier n'est pas un coffre Verrou.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (version != FormatVersion)
            throw new InvalidVaultFormatException($"Version de coffre non prise en charge ({version}).");
        if (file[6] != KdfParameters.Argon2id)
            throw new InvalidVaultFormatException("Méthode de dérivation de clé inconnue.");

        uint mem = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint it = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        if (mem > int.MaxValue || it > int.MaxValue)
            throw new InvalidVaultFormatException("Paramètres de dérivation de clé hors limites.");
        var kdf = new KdfParameters((int)mem, (int)it, file[7]);
        kdf.Validate();
        if ((file[48] & ~(FlagKeyFile | FlagTwoFactor)) != 0)
            throw new InvalidVaultFormatException("Options de coffre inconnues (version plus récente de Verrou ?).");
        return new VaultHeaderInfo(kdf, file[16..48].ToArray(), (file[48] & FlagKeyFile) != 0, (file[48] & FlagTwoFactor) != 0);
    }

    /// <summary>
    /// Déchiffre un coffre. Renvoie le contenu en clair et la clé maîtresse (à conserver pour réenregistrer).
    /// </summary>
    /// <exception cref="InvalidVaultFormatException"/>
    /// <exception cref="WrongCredentialsException"/>
    /// <exception cref="VaultTamperedException"/>
    public static byte[] Decrypt(ReadOnlySpan<byte> file, CompositeKey composite, out MasterKey masterKey)
    {
        var info = ReadHeader(file);
        if (info.RequiresKeyFile != composite.HasKeyFile) throw new WrongCredentialsException();
        if (info.RequiresTwoFactor && !composite.HasTwoFactor) throw new TwoFactorRequiredException();
        if (!info.RequiresTwoFactor && composite.HasTwoFactor) throw new WrongCredentialsException();

        var key = MasterKey.Derive(composite, info.Kdf, info.Salt);
        try
        {
            byte[] plaintext = DecryptWithKey(file, key);
            masterKey = key;
            return plaintext;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Déchiffre avec une clé déjà dérivée (même sel).</summary>
    public static byte[] DecryptWithKey(ReadOnlySpan<byte> file, MasterKey key)
    {
        var info = ReadHeader(file);
        if (!info.Salt.AsSpan().SequenceEqual(key.Salt)) throw new WrongCredentialsException();

        ReadOnlySpan<byte> h = file[..HeaderLength];
        byte[] verifyKey = key.DeriveSubKey("verify", ReadOnlySpan<byte>.Empty);
        byte[] encKey = key.DeriveSubKey("encrypt", h[49..81]);
        try
        {
            Span<byte> expected = stackalloc byte[32];
            HMACSHA256.HashData(verifyKey, h[..VerifierOffset], expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, h[VerifierOffset..HeaderLength]))
                throw new WrongCredentialsException();

            int len = file.Length - HeaderLength - TagLength;
            byte[] plaintext = GC.AllocateArray<byte>(len, pinned: true);
            try
            {
                using var aes = new AesGcm(encKey, TagLength);
                aes.Decrypt(h[81..93], file.Slice(HeaderLength, len), file[^TagLength..], plaintext, h);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new VaultTamperedException(ex);
            }
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifyKey);
            CryptographicOperations.ZeroMemory(encKey);
        }
    }
}

public sealed record VaultHeaderInfo(KdfParameters Kdf, byte[] Salt, bool RequiresKeyFile, bool RequiresTwoFactor)
{
    public string TwoFactorId => IdFromSalt(Salt);
    public static string IdFromSalt(byte[] salt) => Convert.ToHexString(SHA256.HashData(salt).AsSpan(0, 16));
}
