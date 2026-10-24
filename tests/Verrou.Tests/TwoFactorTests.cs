using System.Text;
using Verrou.Core.Crypto;
using Verrou.Core.Model;
using Verrou.Core.Storage;

namespace Verrou.Tests;

/// <summary>Double authentification (TOTP) : algorithme et intégration au coffre. Valeurs factices uniquement.</summary>
public sealed class TwoFactorTests : IDisposable
{
    private static readonly KdfParameters Fast = new(8 * 1024, 1, 1);
    private const string Pw = "mot-de-passe-factice-2fa";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "verrou-2fa-" + Guid.NewGuid().ToString("N"));

    public TwoFactorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // RFC 6238, annexe B (SHA-1, secret ASCII « 12345678901234567890 », 8 chiffres).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Totp_MatchesRfc6238Vectors(long unixTime, string expected)
    {
        byte[] secret = Encoding.ASCII.GetBytes("12345678901234567890");
        long step = Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(unixTime));
        Assert.Equal(expected, Totp.Compute(secret, step, 8));
        Assert.Equal(expected[2..], Totp.Compute(secret, step)); // 6 chiffres = les 6 derniers
    }

    [Fact]
    public void Totp_AcceptsClockDriftOfOnePeriod_AndRejectsReplay()
    {
        byte[] secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        long step = Totp.TimeStep(now);

        Assert.True(Totp.Verify(secret, Totp.Compute(secret, step), now, -1, out long used));
        Assert.Equal(step, used);
        Assert.True(Totp.Verify(secret, Totp.Compute(secret, step - 1), now, -1, out _));
        Assert.True(Totp.Verify(secret, Totp.Compute(secret, step + 1), now, -1, out _));
        Assert.False(Totp.Verify(secret, Totp.Compute(secret, step - 2), now, -1, out _));
        Assert.False(Totp.Verify(secret, Totp.Compute(secret, step + 2), now, -1, out _));

        // Un code déjà utilisé (même pas de temps) est refusé.
        Assert.False(Totp.Verify(secret, Totp.Compute(secret, step), now, lastUsedStep: step, out _));
        // Formats tolérés : espaces ; refusés : longueur incorrecte.
        string code = Totp.Compute(secret, step);
        Assert.True(Totp.Verify(secret, code[..3] + " " + code[3..], now, -1, out _));
        Assert.False(Totp.Verify(secret, code[..5], now, -1, out _));
        Assert.False(Totp.Verify(secret, "", now, -1, out _));
    }

    [Fact]
    public void Base32_Rfc4648VectorsAndRoundTrip()
    {
        Assert.Equal("MZXW6YTBOI", Base32.Encode(Encoding.ASCII.GetBytes("foobar")));
        Assert.Equal("MZXW6", Base32.Encode(Encoding.ASCII.GetBytes("foo")));
        Assert.Equal("foobar", Encoding.ASCII.GetString(Base32.Decode("mzxw 6ytb-oi")));
        byte[] s = Totp.GenerateSecret();
        Assert.Equal(s, Base32.Decode(Base32.Group(Base32.Encode(s))));
        Assert.Throws<FormatException>(() => Base32.Decode("ABC1"));
    }

    [Fact]
    public void Uri_IsAuthenticatorCompatible()
    {
        string uri = Totp.BuildUri(Encoding.ASCII.GetBytes("12345678901234567890"), "Mon coffre");
        Assert.StartsWith("otpauth://totp/Verrou:Mon%20coffre?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Verrou", uri);
    }

    private string CreateTwoFactorVault(byte[] secret)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".verrou");
        var data = VaultData.CreateNew("2FA");
        data.Entries.Add(new Entry { Title = "Exemple", Password = new SecretString("factice"), GroupId = data.RootGroupId });
        using var ck = CompositeKey.Create(Pw, null, secret);
        using var v = VaultStore.Create(path, data, ck, Fast);
        Assert.True(v.Key.RequiresTwoFactor);
        return path;
    }

    [Fact]
    public void TwoFactorVault_NeedsPasswordAndSecret()
    {
        byte[] secret = Totp.GenerateSecret();
        string path = CreateTwoFactorVault(secret);

        var header = VaultFile.ReadHeader(File.ReadAllBytes(path));
        Assert.True(header.RequiresTwoFactor);
        Assert.False(header.RequiresKeyFile);

        using (var pwOnly = CompositeKey.Create(Pw))
            Assert.Throws<TwoFactorRequiredException>(() => VaultStore.Open(path, pwOnly));
        using (var wrongSecret = CompositeKey.Create(Pw, null, Totp.GenerateSecret()))
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, wrongSecret));
        using (var wrongPw = CompositeKey.Create("mauvais", null, secret))
            Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, wrongPw));

        using var ok = CompositeKey.Create(Pw, null, secret);
        using var opened = VaultStore.Open(path, ok);
        Assert.Equal("factice", opened.Data.Entries.Single().Password.Reveal());
        Assert.Equal(header.TwoFactorId, opened.Key.TwoFactorId);
    }

    [Fact]
    public void RemovingTwoFactorFlag_DoesNotBypassIt()
    {
        byte[] secret = Totp.GenerateSecret();
        string path = CreateTwoFactorVault(secret);
        byte[] f = File.ReadAllBytes(path);
        f[48] = 0; // on efface « 2FA requise » dans l'en-tête
        File.WriteAllBytes(path, f);
        using var pwOnly = CompositeKey.Create(Pw);
        Assert.Throws<WrongCredentialsException>(() => VaultStore.Open(path, pwOnly)); // la clé dépend du secret
        using var withSecret = CompositeKey.Create(Pw, null, secret);
        Assert.ThrowsAny<VaultException>(() => VaultStore.Open(path, withSecret));
    }

    [Fact]
    public void UnknownHeaderFlags_AreRejected()
    {
        string path = CreateTwoFactorVault(Totp.GenerateSecret());
        byte[] f = File.ReadAllBytes(path);
        f[48] |= 0x80;
        Assert.Throws<InvalidVaultFormatException>(() => VaultFile.ReadHeader(f));
    }

    [Fact]
    public void TwoFactor_CanBeEnabledAndDisabledByChangingKey()
    {
        string path = Path.Combine(_dir, "evolution.verrou");
        using (var ck = CompositeKey.Create(Pw))
        using (VaultStore.Create(path, VaultData.CreateNew("T"), ck, Fast)) { }

        byte[] secret = Totp.GenerateSecret();
        string oldId = VaultFile.ReadHeader(File.ReadAllBytes(path)).TwoFactorId;
        using (var ck = CompositeKey.Create(Pw))
        using (var v = VaultStore.Open(path, ck))
        using (var with2fa = CompositeKey.Create(Pw, null, secret))
            VaultStore.ChangeKey(v, with2fa, Fast);

        var h = VaultFile.ReadHeader(File.ReadAllBytes(path));
        Assert.True(h.RequiresTwoFactor);
        Assert.NotEqual(oldId, h.TwoFactorId); // nouveau sel => nouvel identifiant
        using (var pwOnly = CompositeKey.Create(Pw))
            Assert.Throws<TwoFactorRequiredException>(() => VaultStore.Open(path, pwOnly));

        using (var ck = CompositeKey.Create(Pw, null, secret))
        using (var v = VaultStore.Open(path, ck))
        using (var without = CompositeKey.Create(Pw))
            VaultStore.ChangeKey(v, without, Fast);
        using var plain = CompositeKey.Create(Pw);
        using var reopened = VaultStore.Open(path, plain);
        Assert.False(reopened.Key.RequiresTwoFactor);
    }
}
