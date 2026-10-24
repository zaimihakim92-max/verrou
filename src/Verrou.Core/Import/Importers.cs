using System.Globalization;
using System.Text;
using System.Xml;
using Verrou.Core.Crypto;
using Verrou.Core.Model;

namespace Verrou.Core.Import;

public sealed record ImportResult(int Entries, int Groups, Group Target);

/// <summary>
/// Import CSV : reconnaît les en-têtes de KeePass, KeePassXC, Bitwarden, Chrome/Edge, Firefox, 1Password, Dashlane…
/// Séparateur « , » ou « ; » détecté automatiquement. Les colonnes inconnues deviennent des champs personnalisés.
/// </summary>
public static class CsvImporter
{
    private static readonly string[] TitleCols = { "title", "name", "account", "titre", "nom", "site" };
    private static readonly string[] UserCols = { "username", "login name", "login_username", "user name", "user", "login", "email", "identifiant", "utilisateur", "nom d'utilisateur" };
    private static readonly string[] PassCols = { "password", "login_password", "pass", "mot de passe", "motdepasse" };
    private static readonly string[] UrlCols = { "url", "web site", "website", "login_uri", "uri", "adresse", "site web" };
    private static readonly string[] NotesCols = { "notes", "note", "comments", "comment", "extra", "commentaires" };
    private static readonly string[] GroupCols = { "group", "folder", "groupe", "dossier", "grouping", "category" };
    private static readonly string[] TagCols = { "tags", "étiquettes" };
    private static readonly string[] IgnoredCols = { "favorite", "type", "reprompt", "icon", "last modified", "created", "httprealm", "formactionorigin", "guid", "timecreated", "timelastused", "timepasswordchanged" };

    public static ImportResult Import(VaultData vault, string csvText, string targetGroupName)
    {
        var rows = Parse(csvText);
        if (rows.Count == 0) throw new FormatException("Le fichier CSV est vide.");
        var header = rows[0].Select(h => h.Trim().Trim('﻿').ToLowerInvariant()).ToArray();

        int Find(string[] names) => Array.FindIndex(header, h => names.Contains(h));
        int iTitle = Find(TitleCols), iUser = Find(UserCols), iPass = Find(PassCols), iUrl = Find(UrlCols),
            iNotes = Find(NotesCols), iGroup = Find(GroupCols), iTags = Find(TagCols);
        if (iPass < 0 && iUser < 0)
            throw new FormatException("En-têtes CSV non reconnus : il faut au moins une colonne « password » ou « username ».");
        var known = new HashSet<int> { iTitle, iUser, iPass, iUrl, iNotes, iGroup, iTags };

        var target = vault.GetOrCreateGroup(vault.RootGroupId, targetGroupName);
        int groupsBefore = vault.Groups.Count, count = 0;

        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            string Get(int i) => i >= 0 && i < row.Count ? row[i] : "";

            var e = new Entry
            {
                Title = Get(iTitle),
                UserName = Get(iUser),
                Password = new SecretString(Get(iPass)),
                Url = Get(iUrl),
                Notes = Get(iNotes).Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
                GroupId = target.Id,
            };
            if (string.IsNullOrWhiteSpace(e.Title)) e.Title = WindowHost(e.Url) ?? e.UserName;
            if (iTags >= 0) e.Tags = Get(iTags).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            string groupPath = Get(iGroup);
            if (!string.IsNullOrWhiteSpace(groupPath))
            {
                var g = target;
                var parts = groupPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (parts.Count > 1 && (parts[0].Equals("Root", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Racine", StringComparison.OrdinalIgnoreCase)))
                    parts.RemoveAt(0);
                foreach (var p in parts) g = vault.GetOrCreateGroup(g.Id, p);
                e.GroupId = g.Id;
            }

            for (int c = 0; c < header.Length && c < row.Count; c++)
            {
                if (known.Contains(c) || IgnoredCols.Contains(header[c]) || string.IsNullOrWhiteSpace(row[c])) continue;
                bool secret = header[c].Contains("totp") || header[c].Contains("otp") || header[c].Contains("secret");
                e.Fields.Add(new CustomField { Name = rows[0][c].Trim(), Value = new SecretString(row[c]), Protected = secret });
            }
            vault.Entries.Add(e);
            count++;
        }
        return new ImportResult(count, vault.Groups.Count - groupsBefore, target);
    }

    private static string? WindowHost(string url) => AutoType.WindowMatcher.HostOf(url);

    /// <summary>Analyseur CSV RFC 4180 (guillemets, retours à la ligne dans les champs).</summary>
    public static List<List<string>> Parse(string text)
    {
        char sep = DetectSeparator(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    private static char DetectSeparator(string text)
    {
        int nl = text.IndexOf('\n');
        string first = nl < 0 ? text : text[..nl];
        int commas = first.Count(c => c == ','), semis = first.Count(c => c == ';'), tabs = first.Count(c => c == '\t');
        return tabs > commas && tabs > semis ? '\t' : semis > commas ? ';' : ',';
    }
}

/// <summary>Import d'un export « KeePass XML (2.x) » non chiffré (groupes, champs, auto-type, historique).</summary>
public static class KeePassXmlImporter
{
    private static readonly HashSet<string> StandardKeys = new(StringComparer.Ordinal) { "Title", "UserName", "Password", "URL", "Notes" };

    public static ImportResult Import(VaultData vault, Stream xml, string targetGroupName)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(xml, settings)) doc.Load(reader);

        var rootGroup = doc.SelectSingleNode("/KeePassFile/Root/Group") as XmlElement
                        ?? throw new FormatException("Ce n'est pas un export KeePass XML (balise KeePassFile/Root/Group absente).");

        var target = vault.GetOrCreateGroup(vault.RootGroupId, targetGroupName);
        int groupsBefore = vault.Groups.Count;
        int entries = 0;
        ImportGroupContent(vault, rootGroup, target, ref entries);
        return new ImportResult(entries, vault.Groups.Count - groupsBefore, target);
    }

    private static void ImportGroupContent(VaultData vault, XmlElement xg, Group g, ref int count)
    {
        foreach (XmlElement child in xg.ChildNodes.OfType<XmlElement>())
        {
            if (child.Name == "Entry")
            {
                var e = ReadEntry(child);
                e.GroupId = g.Id;
                vault.Entries.Add(e);
                count++;
            }
            else if (child.Name == "Group")
            {
                string name = child["Name"]?.InnerText ?? "Groupe";
                var sub = new Group { Name = name, ParentId = g.Id, Notes = child["Notes"]?.InnerText ?? "" };
                vault.Groups.Add(sub);
                ImportGroupContent(vault, child, sub, ref count);
            }
        }
    }

    private static Entry ReadEntry(XmlElement xe)
    {
        var e = new Entry();
        ReadFields(xe, e);
        if (ParseTime(xe.SelectSingleNode("Times/CreationTime")?.InnerText) is DateTime c) e.Created = c;
        var history = xe.SelectSingleNode("History");
        if (history != null)
            foreach (XmlElement he in history.ChildNodes.OfType<XmlElement>().Where(n => n.Name == "Entry"))
            {
                var s = new EntrySnapshot();
                ReadFields(he, s);
                e.History.Add(s);
            }
        return e;
    }

    private static void ReadFields(XmlElement xe, EntryFields e)
    {
        foreach (XmlElement s in xe.ChildNodes.OfType<XmlElement>().Where(n => n.Name == "String"))
        {
            string key = s["Key"]?.InnerText ?? "";
            var valueNode = s["Value"];
            string value = valueNode?.InnerText ?? "";
            bool prot = string.Equals(valueNode?.GetAttribute("Protected"), "True", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(valueNode?.GetAttribute("ProtectInMemory"), "True", StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "Title": e.Title = value; break;
                case "UserName": e.UserName = value; break;
                case "Password": e.Password = new SecretString(value); break;
                case "URL": e.Url = value; break;
                case "Notes": e.Notes = value.Replace("\r\n", "\n").Replace("\n", Environment.NewLine); break;
                default:
                    if (key.Length > 0 && !StandardKeys.Contains(key))
                        e.Fields.Add(new CustomField { Name = key, Value = new SecretString(value), Protected = prot });
                    break;
            }
        }
        string tags = xe["Tags"]?.InnerText ?? "";
        e.Tags = tags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var times = xe["Times"];
        if (ParseTime(times?["LastModificationTime"]?.InnerText) is DateTime m) e.Modified = m;
        if (string.Equals(times?["Expires"]?.InnerText, "True", StringComparison.OrdinalIgnoreCase) &&
            ParseTime(times?["ExpiryTime"]?.InnerText) is DateTime x) e.Expires = x;

        var at = xe["AutoType"];
        if (at != null)
        {
            e.AutoType.Enabled = !string.Equals(at["Enabled"]?.InnerText, "False", StringComparison.OrdinalIgnoreCase);
            e.AutoType.Sequence = at["DefaultSequence"]?.InnerText ?? "";
            e.AutoType.Obfuscate = (at["DataTransferObfuscation"]?.InnerText ?? "0").Trim() == "1";
            foreach (XmlElement a in at.ChildNodes.OfType<XmlElement>().Where(n => n.Name == "Association"))
            {
                string w = a["Window"]?.InnerText ?? "";
                if (w.Length > 0) e.AutoType.WindowPatterns.Add(w);
            }
        }
    }

    /// <summary>Dates ISO 8601, ou base64 (secondes depuis l'an 1, format KDBX 4).</summary>
    internal static DateTime? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return d;
        try
        {
            byte[] b = Convert.FromBase64String(s);
            if (b.Length == 8)
            {
                long secs = BitConverter.ToInt64(b, 0);
                return new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs);
            }
        }
        catch (FormatException) { }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }
}
