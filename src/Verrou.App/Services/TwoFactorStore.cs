using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Verrou.Services
{
    /// <summary>
    /// Secrets de double authentification des coffres, gardés sur ce PC. Le secret n'est jamais écrit dans le
    /// fichier du coffre : un coffre volé avec son mot de passe reste illisible sans ce PC ou la clé de secours.
    /// Protection : DPAPI (lié au compte Windows) et, en option, Windows Hello (clé matérielle du TPM + PIN ou
    /// biométrie à chaque utilisation). Mémorise aussi les échecs de code (blocage qui survit au redémarrage).
    /// Fichier : %APPDATA%\Verrou\two-factor.json.
    /// </summary>
    public sealed class TwoFactorStore
    {
        private sealed class Item
        {
            /// <summary>DPAPI( secret ) ou, si protégé par Windows Hello, DPAPI( nonce ‖ AES-GCM(secret) ‖ tag ).</summary>
            public string Secret { get; set; } = "";
            public string Protection { get; set; } = "dpapi";
            public string HelloChallenge { get; set; } = "";
            public long LastStep { get; set; } = -1;
            public int FailedAttempts { get; set; }
            public DateTime? LockedUntilUtc { get; set; }
        }

        private sealed class FileModel
        {
            public Dictionary<string, Item> Vaults { get; set; } = new Dictionary<string, Item>();
        }

        private static string FilePath => Path.Combine(AppSettings.DataDir, "two-factor.json");
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };
        private readonly FileModel _model;

        public TwoFactorStore() => _model = Load();

        private static FileModel Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), Json) ?? new FileModel();
            }
            catch (Exception) { }
            return new FileModel();
        }

        private void Save()
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(_model, Json));
            File.Move(FilePath + ".tmp", FilePath, true);
        }

        private static byte[] Entropy(string id) => Encoding.UTF8.GetBytes("Verrou 2FA " + id);

        public bool Has(string id) => _model.Vaults.ContainsKey(id);
        public bool IsHelloProtected(string id) => _model.Vaults.TryGetValue(id, out var i) && i.Protection == "hello";

        /// <summary>
        /// Secret du coffre, ou null s'il n'est pas sur ce PC ou illisible (autre compte Windows).
        /// Si la protection Windows Hello est active, Windows demande le PIN ou la biométrie.
        /// </summary>
        /// <exception cref="WindowsHello.HelloException">Windows Hello annulé ou indisponible.</exception>
        public async Task<byte[]> GetAsync(string id)
        {
            if (!_model.Vaults.TryGetValue(id, out var item)) return null;
            byte[] inner;
            try { inner = ProtectedData.Unprotect(Convert.FromBase64String(item.Secret), Entropy(id), DataProtectionScope.CurrentUser); }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException) { return null; }
            if (item.Protection != "hello") return inner;

            byte[] key = await WindowsHello.DeriveKeyAsync(Convert.FromBase64String(item.HelloChallenge));
            try { return Unwrap(inner, key, id); }
            catch (CryptographicException) { throw new WindowsHello.HelloException("La clé Windows Hello ne correspond plus (PIN réinitialisé ?). Utilisez la clé de secours."); }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(inner); }
        }

        /// <summary>Enregistre le secret (DPAPI, et Windows Hello si demandé : Windows demande alors le PIN).</summary>
        public async Task SetAsync(string id, byte[] secret, bool helloProtect = false)
        {
            var old = _model.Vaults.TryGetValue(id, out var o) ? o : null;
            var item = new Item { LastStep = old?.LastStep ?? -1 };
            byte[] inner = secret;
            if (helloProtect)
            {
                byte[] challenge = RandomNumberGenerator.GetBytes(32);
                byte[] key = await WindowsHello.DeriveKeyAsync(challenge, createIfMissing: true);
                try { inner = Wrap(secret, key, id); }
                finally { CryptographicOperations.ZeroMemory(key); }
                item.Protection = "hello";
                item.HelloChallenge = Convert.ToBase64String(challenge);
            }
            item.Secret = Convert.ToBase64String(ProtectedData.Protect(inner, Entropy(id), DataProtectionScope.CurrentUser));
            _model.Vaults[id] = item;
            Save();
        }

        public void Remove(string id)
        {
            if (id != null && _model.Vaults.Remove(id)) Save();
        }

        // ---- Anti-rejeu ----
        public long LastStep(string id) => _model.Vaults.TryGetValue(id, out var i) ? i.LastStep : -1;

        public void SetLastStep(string id, long step)
        {
            if (!_model.Vaults.TryGetValue(id, out var i)) return;
            i.LastStep = step;
            Save();
        }

        // ---- Blocage après des codes faux (mémorisé sur disque) ----

        /// <summary>Secondes de blocage restantes (0 = saisie autorisée).</summary>
        public int LockedSeconds(string id)
        {
            if (!_model.Vaults.TryGetValue(id, out var i) || i.LockedUntilUtc is not DateTime until) return 0;
            double left = (until - DateTime.UtcNow).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : 0;
        }

        /// <summary>Compte un code faux. À partir du 5e : blocage de 30 s, doublé à chaque nouvel échec (1 h maximum).</summary>
        public int RegisterFailure(string id)
        {
            if (!_model.Vaults.TryGetValue(id, out var i)) return 0;
            i.FailedAttempts++;
            int seconds = 0;
            if (i.FailedAttempts >= 5)
            {
                seconds = (int)Math.Min(3600, 30 * Math.Pow(2, i.FailedAttempts - 5));
                i.LockedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            }
            Save();
            return seconds;
        }

        public int FailedAttempts(string id) => _model.Vaults.TryGetValue(id, out var i) ? i.FailedAttempts : 0;

        public void ResetFailures(string id)
        {
            if (!_model.Vaults.TryGetValue(id, out var i) || (i.FailedAttempts == 0 && i.LockedUntilUtc == null)) return;
            i.FailedAttempts = 0;
            i.LockedUntilUtc = null;
            Save();
        }

        // ---- Chiffrement par la clé Windows Hello ----
        private static byte[] Wrap(byte[] secret, byte[] key, string id)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12), ct = new byte[secret.Length], tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, secret, ct, tag, Entropy(id));
            byte[] blob = new byte[12 + ct.Length + 16];
            nonce.CopyTo(blob, 0); ct.CopyTo(blob, 12); tag.CopyTo(blob, 12 + ct.Length);
            return blob;
        }

        private static byte[] Unwrap(byte[] blob, byte[] key, string id)
        {
            int len = blob.Length - 28;
            byte[] pt = new byte[len];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, len), blob.AsSpan(12 + len, 16), pt, Entropy(id));
            return pt;
        }
    }
}
