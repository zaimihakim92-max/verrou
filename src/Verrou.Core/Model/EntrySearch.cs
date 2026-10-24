namespace Verrou.Core.Model;

/// <summary>Recherche : tous les mots saisis doivent apparaître (titre, identifiant, URL, notes, étiquettes, champs non masqués).</summary>
public static class EntrySearch
{
    public static bool Matches(Entry e, string? query, string groupPath = "")
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.All(t => Haystack(e, groupPath).Any(h => h.Contains(t, StringComparison.CurrentCultureIgnoreCase)));
    }

    private static IEnumerable<string> Haystack(Entry e, string groupPath)
    {
        yield return e.Title;
        yield return e.UserName;
        yield return e.Url;
        yield return e.Notes;
        yield return groupPath;
        foreach (var t in e.Tags) yield return t;
        foreach (var f in e.Fields)
        {
            yield return f.Name;
            if (!f.Protected) yield return f.Value.Reveal();
        }
    }
}
