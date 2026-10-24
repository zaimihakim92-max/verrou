using System.Buffers.Binary;
using System.Text;
using Konscious.Security.Cryptography;
using Verrou.Core.Crypto;
using Verrou.Core.Model;
using Verrou.Core.Storage;

namespace Verrou.Tests;

/// <summary>Tests de la partie cryptographique. Tous les mots de passe sont factices.</summary>
public sealed class CryptoTests : IDisposable
{
    // Paramètres Argon2id minimaux pour que les tests restent rapides.
    private static readonly KdfParameters Fast = new(8 * 1024, 1, 1);
    private const string Pw = "mot-de-passe-factice-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "verrou-tests-" + Guid.NewGuid().ToString("N"));

    public CryptoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".verrou");

    private static VaultData SampleVault()
    {
        var v = VaultData.CreateNew("Test");
        v.Entries.Add(new Entry
        {
            Title = "Forum exemple",
            UserName = "alice@example.test",
            Password = new SecretString("secret-factice-ÀÉ€-🔑"),
            Url = "https://forum.example.test",
            Notes = "ligne 1\nligne 2",
            GroupId = v.Groups[1].Id,
            Fields = { new CustomField { Name = "PIN", Value = new SecretString("0000"), Protected = true } },
        });
        return v;
    }

    private string CreateVault(out VaultData data, byte[]? keyFile = null, string password = Pw)
    {
        string path = NewPath();
        data = SampleVault();
        using var ck = CompositeKey.Create(password, keyFile);
        using var v = VaultStore.Create(path, data, ck, Fast);
        return path;
    }

    [Fact]
    public void EncryptThenDecrypt_RestoresEverything()
    {
        string path = CreateVault(out var original);

        using var ck = CompositeKey.Create(Pw);
        using var opened = VaultStore.Open(path, ck);

        var e = Assert.Single(opened.Data.Entries);
        var o = original.Entries[0];
        Assert.Equal(o.Title, e.Title);
        Assert.Equal(o.UserName, e.UserName);
        Assert.Equal("secret-factice-ÀÉ€-🔑", e.Password.Reveal());
        Assert.Equal(o.Url, e.Url);
        Assert.Equal(o.Notes, e.Notes);
        Assert.Equal("0000", e.Fields[0].Value.Reveal());
        Assert.True(e.Fields[0].Protected);
        Assert.Equal(original.Groups.Count, opened.Data.Groups.Count);
        Assert.Equal(o.GroupId, e.GroupId);
    }

    [Fact]
    public void FileDoesNotContainPlaintext()
    {
        string path = CreateVault(out _);
        byte[] file = File.ReadAllBytes(path);
        foreach (var needle in new[] { "alice@example.test", "Forum exemple", "secret-factice", "forum.example.test" })
        {
            Assert.Equal(-1, IndexOf(file, Encoding.UTF8.GetBytes(needle)));
            Assert.Equal(-1, IndexOf(file, Encoding.Unicode.GetBytes(needle)));
        }
    }

    [Fact]
    public void WrongPassword_IsRejected()
    {
        string path = CreateVault(out _);
        using var ck = CompositeKey.Create("mauvais-mot-de-passe");
        Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, ck));
    }

    [Fact]
    public void PasswordIsCaseAndCharacterSensitive()
    {
        string path = CreateVault(out _);
        foreach (var bad in new[] { Pw.ToUpperInvariant(), Pw + " ", Pw[..^1], "" + Pw[1..] })
        {
            using var ck = CompositeKey.Create(bad);
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, ck));
        }
    }

    [Fact]
    public void KeyFile_IsRequiredWhenUsed()
    {
        byte[] keyFile = Encoding.ASCII.GetBytes("contenu-de-fichier-cle-factice");
        string path = CreateVault(out _, keyFile);

        Assert.True(VaultStore.RequiresKeyFile(path));

        using (var noKeyFile = CompositeKey.Create(Pw))
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, noKeyFile));

        using (var otherKeyFile = CompositeKey.Create(Pw, Encoding.ASCII.GetBytes("autre-fichier")))
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, otherKeyFile));

        using (var wrongPw = CompositeKey.Create("mauvais", keyFile))
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, wrongPw));

        using var ok = CompositeKey.Create(Pw, keyFile);
        using var opened = VaultStore.Open(path, ok);
        Assert.Single(opened.Data.Entries);
    }

    [Fact]
    public void KeyFileOnly_WithoutPassword_Works()
    {
        byte[] keyFile = new byte[64];
        Random.Shared.NextBytes(keyFile);
        string path = CreateVault(out _, keyFile, password: "");
        using var ck = CompositeKey.Create("", keyFile);
        using var opened = VaultStore.Open(path, ck);
        Assert.Single(opened.Data.Entries);
    }

    [Fact]
    public void ModifiedCiphertext_IsDetected()
    {
        string path = CreateVault(out _);
        byte[] original = File.ReadAllBytes(path);

        // On modifie un bit à plusieurs endroits du contenu chiffré et de l'étiquette d'authentification.
        int[] positions = { VaultFile.HeaderLength, VaultFile.HeaderLength + 7, original.Length / 2, original.Length - VaultFile.TagLength - 1, original.Length - 1 };
        foreach (int pos in positions)
        {
            byte[] bad = (byte[])original.Clone();
            bad[pos] ^= 0x01;
            File.WriteAllBytes(path, bad);
            using var ck = CompositeKey.Create(Pw);
            Assert.Throws<VaultTamperedException>(() => VaultStore.Open(path, ck));
        }
    }

    [Fact]
    public void ModifiedHeader_IsDetected()
    {
        string path = CreateVault(out _);
        byte[] original = File.ReadAllBytes(path);

        // Graine, nonce et vérificateur : n'importe quel octet modifié doit faire échouer l'ouverture.
        for (int pos = 49; pos < VaultFile.HeaderLength; pos += 5)
        {
            byte[] bad = (byte[])original.Clone();
            bad[pos] ^= 0x80;
            File.WriteAllBytes(path, bad);
            using var ck = CompositeKey.Create(Pw);
            Assert.ThrowsAny<VaultException>(() => VaultStore.Open(path, ck));
        }

        // Sel et paramètres Argon2 : la clé dérivée change, l'ouverture échoue.
        foreach (int pos in new[] { 12, 20, 40 })
        {
            byte[] bad = (byte[])original.Clone();
            bad[pos] ^= 0x01;
            File.WriteAllBytes(path, bad);
            using var ck = CompositeKey.Create(Pw);
            Assert.ThrowsAny<VaultException>(() => VaultStore.Open(path, ck));
        }

        // Retirer l'exigence de fichier-clé dans les options : refusé aussi.
        byte[] kf = Encoding.ASCII.GetBytes("fichier-cle-factice");
        string p2 = CreateVault(out _, kf);
        byte[] withKf = File.ReadAllBytes(p2);
        withKf[48] = 0;
        File.WriteAllBytes(p2, withKf);
        using (var ck2 = CompositeKey.Create(Pw))
            Assert.ThrowsAny<VaultException>(() => VaultStore.Open(p2, ck2));
    }

    [Fact]
    public void TruncatedOrForeignFile_IsRejected()
    {
        string path = CreateVault(out _);
        byte[] original = File.ReadAllBytes(path);
        using var ck = CompositeKey.Create(Pw);

        File.WriteAllBytes(path, original[..^1]);
        Assert.ThrowsAny<VaultException>(() => VaultStore.Open(path, ck));

        File.WriteAllBytes(path, original[..50]);
        Assert.Throws<InvalidVaultFormatException>(() => VaultStore.Open(path, ck));

        File.WriteAllText(path, "ceci n'est pas un coffre, juste du texte assez long pour dépasser l'en-tête............................................................................");
        Assert.Throws<InvalidVaultFormatException>(() => VaultStore.Open(path, ck));
    }

    [Fact]
    public void AbsurdKdfParameters_AreRejectedBeforeDerivation()
    {
        string path = CreateVault(out _);
        byte[] bad = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(8), uint.MaxValue); // mémoire demandée gigantesque
        File.WriteAllBytes(path, bad);
        using var ck = CompositeKey.Create(Pw);
        Assert.Throws<InvalidVaultFormatException>(() => VaultStore.Open(path, ck));
    }

    [Fact]
    public void EachSave_UsesFreshNonce()
    {
        string path = CreateVault(out _);
        using var ck = CompositeKey.Create(Pw);
        using var v = VaultStore.Open(path, ck);
        byte[] a = File.ReadAllBytes(path);
        VaultStore.Save(v);
        byte[] b = File.ReadAllBytes(path);
        Assert.NotEqual(a.AsSpan(49, 44).ToArray(), b.AsSpan(49, 44).ToArray()); // graine + nonce
        Assert.Equal(a.AsSpan(16, 32).ToArray(), b.AsSpan(16, 32).ToArray());    // même sel Argon2
    }

    [Fact]
    public void ChangeKey_OldPasswordStopsWorking()
    {
        string path = CreateVault(out _);
        using (var ck = CompositeKey.Create(Pw))
        using (var v = VaultStore.Open(path, ck))
        using (var newCk = CompositeKey.Create("nouveau-mot-de-passe-factice"))
            VaultStore.ChangeKey(v, newCk, Fast);

        using (var old = CompositeKey.Create(Pw))
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, old));
        using var fresh = CompositeKey.Create("nouveau-mot-de-passe-factice");
        using var reopened = VaultStore.Open(path, fresh);
        Assert.Single(reopened.Data.Entries);
    }

    [Fact]
    public void EncryptedExport_HasItsOwnPassword()
    {
        string path = CreateVault(out var data);
        string export = NewPath();
        using (var ek = CompositeKey.Create("mot-de-passe-export-factice"))
            VaultStore.ExportEncrypted(export, data, ek, Fast);

        using (var main = CompositeKey.Create(Pw))
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(export, main));
        using var ok = CompositeKey.Create("mot-de-passe-export-factice");
        using var opened = VaultStore.Open(export, ok);
        Assert.Equal("alice@example.test", opened.Data.Entries[0].UserName);
    }

    [Fact]
    public void LockedKey_CannotBeUsedAnymore()
    {
        using var ck = CompositeKey.Create(Pw);
        var key = MasterKey.Derive(ck, Fast);
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => VaultFile.Encrypt(new byte[10], key));
    }

    [Fact]
    public void Argon2id_MatchesRfc9106TestVector()
    {
        // RFC 9106 §5.3 (Argon2id, version 0x13).
        using var argon = new Argon2id(Enumerable.Repeat((byte)0x01, 32).ToArray())
        {
            Salt = Enumerable.Repeat((byte)0x02, 16).ToArray(),
            KnownSecret = Enumerable.Repeat((byte)0x03, 8).ToArray(),
            AssociatedData = Enumerable.Repeat((byte)0x04, 12).ToArray(),
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };
        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659",
            Convert.ToHexString(argon.GetBytes(32)).ToLowerInvariant());
    }

    [Fact]
    public void SameInputs_GiveSameKey_DifferentSalts_GiveDifferentKeys()
    {
        using var ck = CompositeKey.Create(Pw);
        using var k1 = MasterKey.Derive(ck, Fast);
        using var k2 = MasterKey.Derive(ck, Fast, k1.Salt);
        using var k3 = MasterKey.Derive(ck, Fast);
        byte[] data = Encoding.UTF8.GetBytes("donnée");
        Assert.Equal(data, VaultFile.DecryptWithKey(VaultFile.Encrypt(data, k1), k2));
        Assert.Throws<WrongCredentialsException>(() => VaultFile.DecryptWithKey(VaultFile.Encrypt(data, k1), k3));
    }

    [Fact]
    public void SecretString_RoundTripsAndHidesValue()
    {
        var s = new SecretString("valeur-factice-é🔒");
        Assert.Equal("valeur-factice-é🔒", s.Reveal());
        Assert.Equal("••••••••", s.ToString());
        Assert.True(s.Equals(new SecretString("valeur-factice-é🔒")));
        Assert.False(s.Equals(new SecretString("autre")));
        Assert.Equal("", SecretString.Empty.Reveal());
    }

    [Fact]
    public void Backups_AreCreatedAndPruned()
    {
        string path = CreateVault(out _);
        string backups = Path.Combine(_dir, "sauvegardes");
        for (int i = 0; i < 5; i++) Assert.NotNull(BackupManager.CreateBackup(path, backups, keep: 3));
        var list = BackupManager.List(backups, Path.GetFileNameWithoutExtension(path));
        Assert.Equal(3, list.Count);
        using var ck = CompositeKey.Create(Pw);
        using var restored = VaultStore.Open(list[0].FullName, ck); // une sauvegarde est un coffre normal
        Assert.Single(restored.Data.Entries);
    }

    [Fact]
    public void AtomicSave_LeavesNoTemporaryFile()
    {
        string path = CreateVault(out _);
        Assert.False(File.Exists(path + ".tmp"));
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
