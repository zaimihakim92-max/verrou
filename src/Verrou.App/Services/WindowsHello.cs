using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace Verrou.Services
{
    /// <summary>
    /// Clé dérivée de Windows Hello : une clé RSA créée dans le TPM (elle ne peut pas en sortir) signe un défi
    /// aléatoire ; la signature (déterministe) donne une clé AES. Chaque utilisation exige le PIN, l'empreinte
    /// ou le visage. Même technique que le « déverrouillage rapide » de KeePassXC.
    /// </summary>
    public static class WindowsHello
    {
        private const string CredentialName = "Verrou 2FA";

        public sealed class HelloException : Exception
        {
            public HelloException(string message) : base(message) { }
        }

        public static async Task<bool> IsAvailableAsync()
        {
            try { return await KeyCredentialManager.IsSupportedAsync(); }
            catch (Exception) { return false; }
        }

        public static async Task<byte[]> DeriveKeyAsync(byte[] challenge, bool createIfMissing = false)
        {
            if (!await IsAvailableAsync())
                throw new HelloException("Windows Hello n'est pas configuré sur ce PC (Paramètres Windows › Comptes › Options de connexion).");

            KeyCredential credential;
            var opened = await KeyCredentialManager.OpenAsync(CredentialName);
            if (opened.Status == KeyCredentialStatus.Success) credential = opened.Credential;
            else if (createIfMissing && opened.Status == KeyCredentialStatus.NotFound)
            {
                BringPromptToFront();
                var created = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.FailIfExists);
                if (created.Status != KeyCredentialStatus.Success) throw new HelloException(Describe(created.Status));
                credential = created.Credential;
            }
            else throw new HelloException(Describe(opened.Status));

            BringPromptToFront();
            var signed = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
            if (signed.Status != KeyCredentialStatus.Success) throw new HelloException(Describe(signed.Status));
            CryptographicBuffer.CopyToByteArray(signed.Result, out byte[] signature);
            try
            {
                return HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, challenge, System.Text.Encoding.ASCII.GetBytes("Verrou Hello 2FA v1"));
            }
            finally { CryptographicOperations.ZeroMemory(signature); }
        }

        private static string Describe(KeyCredentialStatus s) => s switch
        {
            KeyCredentialStatus.UserCanceled => "Windows Hello annulé.",
            KeyCredentialStatus.UserPrefersPassword => "Windows Hello refusé.",
            KeyCredentialStatus.NotFound => "La clé Windows Hello de Verrou est introuvable (PIN réinitialisé ?). Utilisez la clé de secours.",
            KeyCredentialStatus.SecurityDeviceLocked => "Le module de sécurité est verrouillé (trop d'essais de PIN).",
            _ => "Windows Hello indisponible (" + s + ").",
        };

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

        /// <summary>La fenêtre de Windows Hello s'ouvre parfois derrière l'application : on la ramène devant.</summary>
        private static void BringPromptToFront()
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 40; i++)
                {
                    IntPtr h = FindWindow("Credential Dialog Xaml Host", null);
                    if (h != IntPtr.Zero) { SetForegroundWindow(h); return; }
                    await Task.Delay(100);
                }
            });
        }
    }
}
