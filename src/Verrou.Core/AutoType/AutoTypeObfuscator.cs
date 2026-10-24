using System.Security.Cryptography;

namespace Verrou.Core.AutoType;

/// <summary>Étape du brouillage : déplacement du curseur, frappe au clavier ou collage depuis le presse-papiers.</summary>
public abstract record ObfuscationStep;
public sealed record CaretHome : ObfuscationStep;
public sealed record CaretEnd : ObfuscationStep;
public sealed record CaretRight(int Count) : ObfuscationStep;
public sealed record CaretLeft(int Count) : ObfuscationStep;
public sealed record TypeChunk(string Text) : ObfuscationStep;
public sealed record PasteChunk(string Text) : ObfuscationStep;

/// <summary>
/// Brouillage de l'auto-type à deux canaux (inspiré du « Two-Channel Auto-Type Obfuscation » de KeePass) :
/// le texte secret est découpé en morceaux, insérés dans un ordre aléatoire (avec des déplacements du curseur),
/// les uns tapés au clavier, les autres collés depuis le presse-papiers.
/// Un enregistreur de frappe ne voit que des fragments désordonnés et des flèches ; un espion du presse-papiers
/// ne voit que d'autres fragments. Il faut les deux, et reconstituer l'ordre, pour retrouver le mot de passe.
/// </summary>
public static class AutoTypeObfuscator
{
    /// <summary>Le brouillage suppose un champ d'une ligne : pas de retour à la ligne, de tabulation ni de caractère hors BMP.</summary>
    public static bool CanObfuscate(string text) =>
        text.Length >= 2 && !text.Any(c => c == '\n' || c == '\r' || c == '\t' || char.IsSurrogate(c));

    public static List<ObfuscationStep> Plan(string text, Func<int, int>? random = null)
    {
        random ??= RandomNumberGenerator.GetInt32;
        if (!CanObfuscate(text)) return new List<ObfuscationStep> { new TypeChunk(text) };

        // 1) Découpage en morceaux de 1 à 3 caractères.
        var chunks = new List<string>();
        for (int i = 0; i < text.Length;)
        {
            int len = Math.Min(1 + random(3), text.Length - i);
            chunks.Add(text.Substring(i, len));
            i += len;
        }
        if (chunks.Count == 1) chunks = new List<string> { text[..1], text[1..] };

        // 2) Canal de chaque morceau ; on garantit que les deux canaux servent.
        var pasted = chunks.Select(_ => random(2) == 0).ToArray();
        if (pasted.All(p => p)) pasted[random(pasted.Length)] = false;
        if (pasted.All(p => !p)) pasted[random(pasted.Length)] = true;

        // 3) Ordre d'insertion aléatoire (mélange de Fisher-Yates).
        int[] order = Enumerable.Range(0, chunks.Count).ToArray();
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = random(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // 4) Pour chaque morceau : placer le curseur au bon endroit, puis taper ou coller.
        var steps = new List<ObfuscationStep>();
        var inserted = new bool[chunks.Count];
        int total = 0;
        foreach (int idx in order)
        {
            int pos = 0;
            for (int j = 0; j < idx; j++) if (inserted[j]) pos += chunks[j].Length;
            int fromEnd = total - pos;
            if (pos <= fromEnd)
            {
                steps.Add(new CaretHome());
                if (pos > 0) steps.Add(new CaretRight(pos));
            }
            else
            {
                steps.Add(new CaretEnd());
                if (fromEnd > 0) steps.Add(new CaretLeft(fromEnd));
            }
            steps.Add(pasted[idx] ? new PasteChunk(chunks[idx]) : new TypeChunk(chunks[idx]));
            inserted[idx] = true;
            total += chunks[idx].Length;
        }
        return steps;
    }

    /// <summary>Rejoue un plan dans un champ texte simulé (sert aux tests).</summary>
    public static string Simulate(IEnumerable<ObfuscationStep> steps)
    {
        var buf = new System.Text.StringBuilder();
        int caret = 0;
        foreach (var s in steps)
        {
            switch (s)
            {
                case CaretHome: caret = 0; break;
                case CaretEnd: caret = buf.Length; break;
                case CaretRight r: caret = Math.Min(buf.Length, caret + r.Count); break;
                case CaretLeft l: caret = Math.Max(0, caret - l.Count); break;
                case TypeChunk t: buf.Insert(caret, t.Text); caret += t.Text.Length; break;
                case PasteChunk p: buf.Insert(caret, p.Text); caret += p.Text.Length; break;
            }
        }
        return buf.ToString();
    }
}
