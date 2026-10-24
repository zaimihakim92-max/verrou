using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Verrou.Core.Generation;

namespace Verrou.Services
{
    /// <summary>Réglages non secrets, dans %APPDATA%\Verrou\settings.json (ou --data-dir).</summary>
    public sealed class AppSettings
    {
        public string LastVaultPath { get; set; } = "";
        public List<string> RecentVaults { get; set; } = new List<string>();
        /// <summary>Chemin du fichier-clé mémorisé par coffre (jamais son contenu).</summary>
        public Dictionary<string, string> KeyFilePaths { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool RememberKeyFilePath { get; set; } = true;

        public int AutoLockMinutes { get; set; } = 5;
        public bool LockOnMinimize { get; set; }
        public bool LockOnSessionLock { get; set; } = true;
        public int ClipboardClearSeconds { get; set; } = 12;

        public string AutoTypeHotkey { get; set; } = "Ctrl+Alt+A";
        public string ShowWindowHotkey { get; set; } = "Ctrl+Alt+K";
        public int AutoTypeKeyDelayMs { get; set; } = 8;

        public int BackupCount { get; set; } = 10;
        /// <summary>Vide = dossier « Verrou-sauvegardes » à côté du coffre.</summary>
        public string BackupFolder { get; set; } = "";

        public bool ProtectFromScreenCapture { get; set; } = true;
        /// <summary>Mot de passe maître et code 2FA saisis sur un bureau Windows séparé (protège des enregistreurs de frappe).</summary>
        public bool UseSecureDesktop { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        /// <summary>Thème : "auto" (comme Windows), "light" ou "dark".</summary>
        public string Theme { get; set; } = "auto";
        /// <summary>Langue : "auto" (celle de Windows), "fr" ou "en". Appliquée au démarrage.</summary>
        public string Language { get; set; } = "auto";

        public PasswordOptions Generator { get; set; } = new PasswordOptions();
        public PassphraseOptions Passphrase { get; set; } = new PassphraseOptions();
        public bool GeneratorPassphraseMode { get; set; }

        // ---- Persistance ----
        public static string DataDir { get; private set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Verrou");

        private static string FilePath => Path.Combine(DataDir, "settings.json");
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static void UseDataDir(string dir) => DataDir = Path.GetFullPath(dir);

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            }
            catch (Exception) { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, Json));
                File.Move(FilePath + ".tmp", FilePath, true);
            }
            catch (Exception) { }
        }

        public void AddRecent(string path)
        {
            RecentVaults.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentVaults.Insert(0, path);
            if (RecentVaults.Count > 8) RecentVaults.RemoveRange(8, RecentVaults.Count - 8);
            LastVaultPath = path;
        }

        public string BackupDirFor(string vaultPath) =>
            string.IsNullOrWhiteSpace(BackupFolder)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(vaultPath)) ?? DataDir, "Verrou-sauvegardes")
                : BackupFolder;
    }
}
