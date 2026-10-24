using Verrou.Core.Crypto;

namespace Verrou.Core.Model;

/// <summary>Contenu déchiffré d'un coffre.</summary>
public sealed class VaultData
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Mon coffre";
    public Guid RootGroupId { get; set; }
    public List<Group> Groups { get; set; } = new();
    public List<Entry> Entries { get; set; } = new();
    public VaultOptions Options { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public static VaultData CreateNew(string name, IEnumerable<string>? groupNames = null)
    {
        var root = new Group { Name = name };
        var v = new VaultData { Name = name, RootGroupId = root.Id };
        v.Groups.Add(root);
        foreach (var n in groupNames ?? new[] { "Internet", "E-mail", "Banque", "Réseau", "Divers" })
            v.Groups.Add(new Group { Name = n, ParentId = root.Id });
        return v;
    }

    public Group Root => Groups.First(g => g.Id == RootGroupId);
    public Group? FindGroup(Guid id) => Groups.FirstOrDefault(g => g.Id == id);
    public IEnumerable<Group> ChildrenOf(Guid id) => Groups.Where(g => g.ParentId == id).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Le groupe et tous ses descendants.</summary>
    public HashSet<Guid> Subtree(Guid id)
    {
        var set = new HashSet<Guid> { id };
        var stack = new Stack<Guid>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            Guid current = stack.Pop();
            foreach (var c in Groups.Where(g => g.ParentId == current))
                if (set.Add(c.Id)) stack.Push(c.Id);
        }
        return set;
    }

    /// <summary>Chemin lisible « Racine / Internet / Forums ».</summary>
    public string PathOf(Guid groupId)
    {
        var parts = new List<string>();
        var g = FindGroup(groupId);
        int guard = 0;
        while (g != null && guard++ < 100)
        {
            parts.Insert(0, g.Name);
            g = g.ParentId is Guid p ? FindGroup(p) : null;
        }
        return string.Join(" / ", parts);
    }

    /// <summary>Trouve ou crée le sous-groupe de ce nom sous <paramref name="parentId"/>.</summary>
    public Group GetOrCreateGroup(Guid parentId, string name)
    {
        var g = Groups.FirstOrDefault(x => x.ParentId == parentId && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (g != null) return g;
        g = new Group { Name = name, ParentId = parentId };
        Groups.Add(g);
        return g;
    }

    /// <summary>Corrige les incohérences (groupes orphelins, entrées sans groupe) après chargement ou import.</summary>
    public void Repair()
    {
        if (!Groups.Any(g => g.Id == RootGroupId))
        {
            var root = new Group { Name = Name };
            Groups.Add(root);
            RootGroupId = root.Id;
        }
        var ids = Groups.Select(g => g.Id).ToHashSet();
        foreach (var g in Groups)
            if (g.Id != RootGroupId && (g.ParentId is not Guid p || !ids.Contains(p) || p == g.Id)) g.ParentId = RootGroupId;
        Groups.First(g => g.Id == RootGroupId).ParentId = null;
        foreach (var e in Entries)
            if (!ids.Contains(e.GroupId)) e.GroupId = RootGroupId;
    }
}

public sealed class VaultOptions
{
    /// <summary>Nombre de versions précédentes gardées par entrée.</summary>
    public int HistoryMaxItems { get; set; } = 20;
    /// <summary>Séquence d'auto-type par défaut.</summary>
    public string DefaultAutoTypeSequence { get; set; } = AutoType.AutoTypeSequence.DefaultSequence;
}

public sealed class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public string Notes { get; set; } = "";
    public bool IsExpanded { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;
}

public sealed class CustomField
{
    public string Name { get; set; } = "";
    public SecretString Value { get; set; } = SecretString.Empty;
    /// <summary>Masqué à l'écran (la valeur est de toute façon chiffrée dans le fichier).</summary>
    public bool Protected { get; set; }

    public CustomField Clone() => new() { Name = Name, Value = Value, Protected = Protected };
}

public sealed class AutoTypeSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Séquence propre à l'entrée ; vide = séquence par défaut du coffre.</summary>
    public string Sequence { get; set; } = "";
    /// <summary>Titres de fenêtre cibles (jokers * et ?, ou //expression régulière//).</summary>
    public List<string> WindowPatterns { get; set; } = new();
    /// <summary>
    /// Brouillage à deux canaux (comme KeePass) : le mot de passe est tapé par morceaux dans le désordre,
    /// une partie au clavier et une partie par le presse-papiers. Un enregistreur de frappe ou un espion du
    /// presse-papiers n'en voit qu'une partie. Ne fonctionne que dans un champ vide qui accepte le collage.
    /// </summary>
    public bool Obfuscate { get; set; }

    public AutoTypeSettings Clone() => new() { Enabled = Enabled, Sequence = Sequence, WindowPatterns = new List<string>(WindowPatterns), Obfuscate = Obfuscate };
}

/// <summary>Champs d'une entrée (partagés entre l'entrée et ses versions d'historique).</summary>
public class EntryFields
{
    public string Title { get; set; } = "";
    public string UserName { get; set; } = "";
    public SecretString Password { get; set; } = SecretString.Empty;
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public List<CustomField> Fields { get; set; } = new();
    public AutoTypeSettings AutoType { get; set; } = new();
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public DateTime? Expires { get; set; }

    /// <summary>Copie profonde des champs vers <paramref name="t"/>.</summary>
    public void CopyFieldsTo(EntryFields t)
    {
        t.Title = Title;
        t.UserName = UserName;
        t.Password = Password;
        t.Url = Url;
        t.Notes = Notes;
        t.Tags = new List<string>(Tags);
        t.Fields = Fields.Select(f => f.Clone()).ToList();
        t.AutoType = AutoType.Clone();
        t.Modified = Modified;
        t.Expires = Expires;
    }

    public string? GetField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Value.Reveal();

    /// <summary>Vrai si les champs visibles par l'utilisateur sont identiques.</summary>
    public bool SameContentAs(EntryFields o) =>
        Title == o.Title && UserName == o.UserName && Password.Equals(o.Password) && Url == o.Url && Notes == o.Notes &&
        Expires == o.Expires && Tags.SequenceEqual(o.Tags) &&
        Fields.Count == o.Fields.Count &&
        Fields.Zip(o.Fields).All(p => p.First.Name == p.Second.Name && p.First.Protected == p.Second.Protected && p.First.Value.Equals(p.Second.Value)) &&
        AutoType.Enabled == o.AutoType.Enabled && AutoType.Sequence == o.AutoType.Sequence &&
        AutoType.WindowPatterns.SequenceEqual(o.AutoType.WindowPatterns) && AutoType.Obfuscate == o.AutoType.Obfuscate;
}

public sealed class EntrySnapshot : EntryFields
{
}

public sealed class Entry : EntryFields
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<EntrySnapshot> History { get; set; } = new();

    public EntrySnapshot Snapshot()
    {
        var s = new EntrySnapshot();
        CopyFieldsTo(s);
        return s;
    }

    /// <summary>Copie des champs de <paramref name="source"/> dans cette entrée (sans toucher à l'historique).</summary>
    public void ApplyFields(EntryFields source) => source.CopyFieldsTo(this);

    /// <summary>
    /// Met à jour l'entrée avec de nouveaux champs en archivant la version actuelle dans l'historique
    /// (seulement si quelque chose a changé). Renvoie vrai si l'entrée a été modifiée.
    /// </summary>
    public bool UpdateWithHistory(EntryFields updated, int maxHistory)
    {
        if (SameContentAs(updated)) return false;
        History.Add(Snapshot());
        while (History.Count > Math.Max(0, maxHistory)) History.RemoveAt(0);
        ApplyFields(updated);
        Modified = DateTime.UtcNow;
        return true;
    }

    /// <summary>Restaure une ancienne version (la version actuelle part elle aussi dans l'historique).</summary>
    public void RestoreVersion(EntrySnapshot version, int maxHistory)
    {
        var copy = new EntrySnapshot();
        version.CopyFieldsTo(copy);
        UpdateWithHistory(copy, maxHistory);
    }

    public Entry Duplicate()
    {
        var e = new Entry { GroupId = GroupId };
        e.ApplyFields(this);
        e.Title = Title + " (copie)";
        e.Modified = DateTime.UtcNow;
        return e;
    }
}
