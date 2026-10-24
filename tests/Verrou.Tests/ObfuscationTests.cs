using Verrou.Core.AutoType;
using Verrou.Core.Crypto;
using Verrou.Core.Model;

namespace Verrou.Tests;

public class ObfuscationTests
{
    [Theory]
    [InlineData("ab")]
    [InlineData("Factice!{Pass}^+%~é€2026")]
    [InlineData("motdepasse-tres-long-factice-pour-tester-le-brouillage-0123456789")]
    public void Plan_AlwaysRebuildsTheExactText(string secret)
    {
        for (int i = 0; i < 300; i++)
            Assert.Equal(secret, AutoTypeObfuscator.Simulate(AutoTypeObfuscator.Plan(secret)));
    }

    [Fact]
    public void Plan_UsesBothChannels_AndNeitherSeesTheWholeSecret()
    {
        const string secret = "Factice-Brouillage-2026!";
        int scrambled = 0;
        for (int i = 0; i < 300; i++)
        {
            var steps = AutoTypeObfuscator.Plan(secret);
            string keys = string.Concat(steps.OfType<TypeChunk>().Select(t => t.Text));
            string clip = string.Concat(steps.OfType<PasteChunk>().Select(p => p.Text));
            Assert.NotEmpty(keys);
            Assert.NotEmpty(clip);
            Assert.Equal(secret.Length, keys.Length + clip.Length);
            Assert.DoesNotContain(secret, keys);
            Assert.DoesNotContain(secret, clip);
            if (steps.Any(s => s is CaretLeft || s is CaretRight)) scrambled++;
        }
        Assert.True(scrambled > 250); // l'ordre d'insertion est presque toujours mélangé
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ligne1\nligne2")]
    [InlineData("tab\tulation")]
    [InlineData("emoji🔑")]
    public void Plan_FallsBackToPlainTyping_WhenObfuscationIsUnsafe(string text)
    {
        Assert.Equal(new ObfuscationStep[] { new TypeChunk(text) }, AutoTypeObfuscator.Plan(text));
    }

    [Fact]
    public void Parser_MarksPasswordAndProtectedFieldsAsSensitive()
    {
        var e = new Entry
        {
            UserName = "u", Password = new SecretString("pw-factice"),
            Fields = { new CustomField { Name = "PIN", Value = new SecretString("1234"), Protected = true },
                       new CustomField { Name = "Client", Value = new SecretString("42"), Protected = false } },
        };
        var a = AutoTypeSequence.Parse("{USERNAME}{PASSWORD}{S:PIN}{S:Client}", e);
        Assert.Equal(new AutoTypeAction[]
        {
            new TypeTextAction("u"), new TypeTextAction("pw-factice", true), new TypeTextAction("1234", true), new TypeTextAction("42"),
        }, a);
    }
}
