using System.Text;
using Verrou.Core.AutoType;
using Verrou.Core.Crypto;
using Verrou.Core.Generation;
using Verrou.Core.Import;
using Verrou.Core.Model;

namespace Verrou.Tests;

public class AutoTypeTests
{
    private static Entry Sample() => new()
    {
        Title = "Banque",
        UserName = "jean.dupont",
        Password = new SecretString("p{a}s+s^%~"),
        Url = "https://www.banque.example.test/login",
        Fields = { new CustomField { Name = "Code client", Value = new SecretString("42") } },
    };

    [Fact]
    public void DefaultSequence_TypesUserTabPasswordEnter()
    {
        var a = AutoTypeSequence.Parse(AutoTypeSequence.DefaultSequence, Sample());
        Assert.Equal(new AutoTypeAction[]
        {
            new TypeTextAction("jean.dupont"), new KeyAction(0x09), new TypeTextAction("p{a}s+s^%~", Sensitive: true), new KeyAction(0x0D),
        }, a);
    }

    [Fact]
    public void FieldValuesAreNeverInterpreted()
    {
        var a = AutoTypeSequence.Parse("{PASSWORD}", Sample());
        Assert.Equal(new TypeTextAction("p{a}s+s^%~", Sensitive: true), Assert.Single(a));
    }

    [Fact]
    public void SupportsRepeatDelayModifiersCustomFieldsAndLiterals()
    {
        var a = AutoTypeSequence.Parse("{S:Code client}{TAB 2}{DELAY 300}{DELAY=20}^a+{TAB}{{}x{}}~", Sample());
        Assert.Equal(new AutoTypeAction[]
        {
            new TypeTextAction("42"), new KeyAction(0x09, KeyModifiers.None, 2), new DelayAction(300), new SetKeyDelayAction(20),
            new ShortcutCharAction('a', KeyModifiers.Control), new KeyAction(0x09, KeyModifiers.Shift),
            new TypeTextAction("{x}"), new KeyAction(0x0D),
        }, a);
    }

    [Theory]
    [InlineData("{INCONNU}")]
    [InlineData("{TAB")]
    [InlineData("abc}")]
    [InlineData("^{PASSWORD}")]
    [InlineData("{USERNAME}^")]
    [InlineData("{S:Absent}")]
    public void InvalidSequences_AreRejected(string seq) =>
        Assert.Throws<AutoTypeParseException>(() => AutoTypeSequence.Parse(seq, Sample()));

    [Fact]
    public void WindowMatching_ByTitleUrlAndPattern()
    {
        var bank = Sample();
        var mail = new Entry { Title = "Messagerie", AutoType = { WindowPatterns = { "*Outlook*" } } };
        var disabled = new Entry { Title = "Banque", AutoType = { Enabled = false } };
        var regex = new Entry { Title = "X", AutoType = { WindowPatterns = { "//^Connexion - (Test|Démo)$//" } } };
        var all = new[] { bank, mail, disabled, regex };

        Assert.Equal(new[] { bank }, WindowMatcher.FindMatches(all, "Espace client - Banque - Edge"));
        Assert.Equal(new[] { bank }, WindowMatcher.FindMatches(all, "banque.example.test - Chrome"));
        Assert.Equal(new[] { mail }, WindowMatcher.FindMatches(all, "Boîte de réception - Outlook"));
        Assert.Equal(new[] { regex }, WindowMatcher.FindMatches(all, "Connexion - Démo"));
        Assert.Empty(WindowMatcher.FindMatches(all, "Bloc-notes"));
    }
}

public class GeneratorTests
{
    [Fact]
    public void Password_RespectsLengthAndClasses()
    {
        var o = new PasswordOptions { Length = 32, Symbols = false, ExcludeAmbiguous = true };
        for (int i = 0; i < 200; i++)
        {
            string p = PasswordGenerator.Generate(o);
            Assert.Equal(32, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.DoesNotContain(p, c => "Il1O0o".Contains(c) || !char.IsLetterOrDigit(c));
        }
        Assert.Equal(1000, Enumerable.Range(0, 1000).Select(_ => PasswordGenerator.Generate(o)).Distinct().Count());
    }

    [Fact]
    public void Passphrase_UsesWordlist()
    {
        Assert.Equal(7776, PassphraseGenerator.DefaultWordlist.Length);
        string p = PassphraseGenerator.Generate(new PassphraseOptions { WordCount = 5, Separator = " ", Capitalize = false });
        var words = p.Split(' ');
        Assert.Equal(5, words.Length);
        Assert.All(words, w => Assert.Contains(w, PassphraseGenerator.DefaultWordlist));
        Assert.InRange(PassphraseGenerator.EntropyBits(new PassphraseOptions { WordCount = 6 }, 7776), 77, 78);
    }
}

public class ModelAndImportTests
{
    [Fact]
    public void History_KeepsPreviousVersions()
    {
        var e = new Entry { Title = "Site", Password = new SecretString("ancien") };
        var upd = e.Snapshot();
        upd.Password = new SecretString("nouveau");
        Assert.True(e.UpdateWithHistory(upd, 2));
        Assert.False(e.UpdateWithHistory(e.Snapshot(), 2)); // rien n'a changé : pas de nouvelle version
        Assert.Equal("ancien", Assert.Single(e.History).Password.Reveal());

        e.RestoreVersion(e.History[0], 2);
        Assert.Equal("ancien", e.Password.Reveal());
        Assert.Equal(2, e.History.Count);
        Assert.Equal("nouveau", e.History[1].Password.Reveal());
    }

    [Fact]
    public void Groups_SubtreePathAndRepair()
    {
        var v = VaultData.CreateNew("Racine");
        var a = v.GetOrCreateGroup(v.RootGroupId, "A");
        var b = v.GetOrCreateGroup(a.Id, "B");
        var c = v.GetOrCreateGroup(b.Id, "C");
        Assert.Equal(new[] { a.Id, b.Id, c.Id }.ToHashSet(), v.Subtree(a.Id));
        Assert.Equal(v.Groups.Count, v.Subtree(v.RootGroupId).Count);
        Assert.Equal("Racine / A / B / C", v.PathOf(c.Id));
        Assert.Same(b, v.GetOrCreateGroup(a.Id, "b"));

        var orphan = new Entry { GroupId = Guid.NewGuid() };
        v.Entries.Add(orphan);
        c.ParentId = Guid.NewGuid();
        v.Repair();
        Assert.Equal(v.RootGroupId, orphan.GroupId);
        Assert.Equal(v.RootGroupId, c.ParentId);
    }

    [Fact]
    public void Search_MatchesAllTermsButNeverPasswords()
    {
        var e = new Entry { Title = "Forum Cuisine", UserName = "chef", Password = new SecretString("motsecret"), Tags = { "loisirs" } };
        Assert.True(EntrySearch.Matches(e, "forum chef"));
        Assert.True(EntrySearch.Matches(e, "LOISIRS"));
        Assert.False(EntrySearch.Matches(e, "forum banque"));
        Assert.False(EntrySearch.Matches(e, "motsecret"));
    }

    [Fact]
    public void CsvImport_BitwardenAndSemicolonFormats()
    {
        var v = VaultData.CreateNew("T");
        string bitwarden = "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
                           "Perso/Jeux,,login,Jeu,\"note sur\ndeux lignes\",,0,https://jeu.example.test,joueur,\"mdp,avec\"\"guillemets\",JBSWY3DPEHPK3PXP\n";
        var r = CsvImporter.Import(v, bitwarden, "Import");
        Assert.Equal(1, r.Entries);
        var e = v.Entries.Single();
        Assert.Equal("Jeu", e.Title);
        Assert.Equal("joueur", e.UserName);
        Assert.Equal("mdp,avec\"guillemets", e.Password.Reveal());
        Assert.Contains("deux lignes", e.Notes);
        Assert.Equal("Import / Perso / Jeux", v.PathOf(e.GroupId)[(v.Name.Length + 3)..]);
        var totp = Assert.Single(e.Fields);
        Assert.Equal("login_totp", totp.Name);
        Assert.True(totp.Protected);

        var v2 = VaultData.CreateNew("T");
        CsvImporter.Import(v2, "Titre;Identifiant;Mot de passe;URL\nMairie;moi;factice;mairie.example.test\n", "Import");
        Assert.Equal("factice", v2.Entries.Single().Password.Reveal());
    }

    [Fact]
    public void KeePassXmlImport_ReadsGroupsFieldsAutoTypeAndHistory()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <KeePassFile><Root><Group><Name>Database</Name>
              <Group><Name>Internet</Name>
                <Entry>
                  <String><Key>Title</Key><Value>Webmail</Value></String>
                  <String><Key>UserName</Key><Value>bob</Value></String>
                  <String><Key>Password</Key><Value Protected="True">factice2</Value></String>
                  <String><Key>URL</Key><Value>https://mail.example.test</Value></String>
                  <String><Key>Question</Key><Value Protected="True">chat</Value></String>
                  <Tags>perso;mail</Tags>
                  <AutoType><Enabled>True</Enabled><DefaultSequence>{USERNAME}{ENTER}{DELAY 500}{PASSWORD}{ENTER}</DefaultSequence>
                    <Association><Window>Webmail*</Window><KeystrokeSequence /></Association></AutoType>
                  <History><Entry>
                    <String><Key>Title</Key><Value>Webmail</Value></String>
                    <String><Key>Password</Key><Value>factice1</Value></String>
                  </Entry></History>
                </Entry>
              </Group>
            </Group></Root></KeePassFile>
            """;
        var v = VaultData.CreateNew("T");
        var r = KeePassXmlImporter.Import(v, new MemoryStream(Encoding.UTF8.GetBytes(xml)), "KeePass");
        Assert.Equal(1, r.Entries);
        var e = v.Entries.Single();
        Assert.Equal("factice2", e.Password.Reveal());
        Assert.Equal(new[] { "perso", "mail" }, e.Tags);
        Assert.Equal("{USERNAME}{ENTER}{DELAY 500}{PASSWORD}{ENTER}", e.AutoType.Sequence);
        Assert.Equal("Webmail*", Assert.Single(e.AutoType.WindowPatterns));
        Assert.True(Assert.Single(e.Fields).Protected);
        Assert.Equal("factice1", Assert.Single(e.History).Password.Reveal());
        Assert.EndsWith("KeePass / Internet", v.PathOf(e.GroupId));
    }

    [Fact]
    public void KeePassXmlImport_RefusesDtd()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><KeePassFile><Root><Group><Name>&e;</Name></Group></Root></KeePassFile>";
        Assert.ThrowsAny<System.Xml.XmlException>(() =>
            KeePassXmlImporter.Import(VaultData.CreateNew("T"), new MemoryStream(Encoding.UTF8.GetBytes(xxe)), "K"));
    }
}
