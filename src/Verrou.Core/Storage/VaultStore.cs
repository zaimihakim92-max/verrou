using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Verrou.Core.Crypto;
using Verrou.Core.Model;

namespace Verrou.Core.Storage;

/// <summary>Sérialisation du contenu (JSON compressé gzip) — avant chiffrement / après déchiffrement.</summary>
public static class VaultSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static byte[] Serialize(VaultData data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, data, Options);
        return ms.ToArray();
    }

    public static VaultData Deserialize(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        var data = JsonSerializer.Deserialize<VaultData>(gz, Options)
                   ?? throw new InvalidVaultFormatException("Contenu du coffre vide.");
        data.Repair();
        return data;
    }
}

/// <summary>Coffre ouvert : contenu + clé maîtresse (pour réenregistrer).</summary>
public sealed class OpenedVault : IDisposable
{
    public OpenedVault(string path, VaultData data, MasterKey key)
    {
        Path = path;
        Data = data;
        Key = key;
    }

    public string Path { get; set; }
    public VaultData Data { get; }
    public MasterKey Key { get; private set; }

    public void ReplaceKey(MasterKey newKey)
    {
        var old = Key;
        Key = newKey;
        old.Dispose();
    }

    public void Dispose() => Key.Dispose();
}

public static class VaultStore
{
    public static OpenedVault Create(string path, VaultData data, CompositeKey composite, KdfParameters kdf)
    {
        var key = MasterKey.Derive(composite, kdf);
        var v = new OpenedVault(path, data, key);
        Save(v);
        return v;
    }

    public static OpenedVault Open(string path, CompositeKey composite)
    {
        byte[] file = File.ReadAllBytes(path);
        byte[] plain = VaultFile.Decrypt(file, composite, out var key);
        try
        {
            VaultData data;
            try { data = VaultSerializer.Deserialize(plain); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                key.Dispose();
                throw new VaultTamperedException(ex);
            }
            return new OpenedVault(path, data, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static bool RequiresKeyFile(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] buf = new byte[VaultFile.HeaderLength + VaultFile.TagLength];
        fs.ReadExactly(buf);
        return VaultFile.ReadHeader(buf).RequiresKeyFile;
    }

    public static void Save(OpenedVault vault) => SaveAs(vault.Path, vault.Data, vault.Key);

    /// <summary>
    /// Écriture atomique : fichier temporaire écrit et vidé sur disque, relu et vérifié, puis remplacement.
    /// Un plantage en cours d'écriture ne peut donc pas abîmer le coffre existant.
    /// </summary>
    public static void SaveAs(string path, VaultData data, MasterKey key)
    {
        data.Modified = DateTime.UtcNow;
        byte[] plain = VaultSerializer.Serialize(data);
        byte[] file;
        try { file = VaultFile.Encrypt(plain, key); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        // Vérification avant de remplacer : on doit pouvoir relire ce qu'on écrit.
        byte[] check = VaultFile.DecryptWithKey(file, key);
        CryptographicOperations.ZeroMemory(check);

        string full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string tmp = full + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(file);
            fs.Flush(true);
        }
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>Change le mot de passe / fichier-clé / paramètres : nouveau sel, nouvelle clé maîtresse.</summary>
    public static void ChangeKey(OpenedVault vault, CompositeKey composite, KdfParameters kdf)
    {
        var newKey = MasterKey.Derive(composite, kdf);
        try { SaveAs(vault.Path, vault.Data, newKey); }
        catch { newKey.Dispose(); throw; }
        vault.ReplaceKey(newKey);
    }

    /// <summary>Exporte une copie chiffrée avec ses propres identifiants.</summary>
    public static void ExportEncrypted(string path, VaultData data, CompositeKey composite, KdfParameters kdf)
    {
        using var key = MasterKey.Derive(composite, kdf);
        SaveAs(path, data, key);
    }
}

/// <summary>Sauvegardes automatiques (copies chiffrées horodatées du fichier du coffre).</summary>
public static class BackupManager
{
    public static string? CreateBackup(string vaultPath, string backupDir, int keep)
    {
        if (!File.Exists(vaultPath) || keep <= 0) return null;
        Directory.CreateDirectory(backupDir);
        string name = System.IO.Path.GetFileNameWithoutExtension(vaultPath);
        string dest = System.IO.Path.Combine(backupDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.verrou");
        int n = 1;
        while (File.Exists(dest))
            dest = System.IO.Path.Combine(backupDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}-{n++}.verrou");
        File.Copy(vaultPath, dest);
        Prune(backupDir, name, keep);
        return dest;
    }

    public static IReadOnlyList<FileInfo> List(string backupDir, string vaultName) =>
        Directory.Exists(backupDir)
            ? new DirectoryInfo(backupDir).GetFiles(vaultName + "-*.verrou").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : new List<FileInfo>();

    private static void Prune(string backupDir, string name, int keep)
    {
        foreach (var f in List(backupDir, name).Skip(keep))
            try { f.Delete(); } catch (IOException) { }
    }
}
