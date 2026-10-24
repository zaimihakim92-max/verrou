using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinForms = System.Windows.Forms;

namespace Verrou.Services
{
    /// <summary>
    /// Saisie du mot de passe maître sur un bureau Windows séparé (comme l'option « bureau sécurisé » de KeePass
    /// ou l'écran de l'UAC). Les programmes du bureau normal — enregistreurs de frappe à base de crochets
    /// clavier, captures d'écran, injecteurs de frappe — ne voient ni la fenêtre ni ce qui est tapé.
    /// Ne protège pas contre un logiciel malveillant installé avec des droits administrateur ou dans le noyau.
    /// </summary>
    public static class SecureDesktop
    {
        public sealed class Result
        {
            public string Password { get; set; } = "";
            public string Code { get; set; } = "";
            public string Recovery { get; set; } = "";
        }

        public enum SecondFactor { None, Code, Recovery }

        private const uint GENERIC_ALL = 0x10000000;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDesktop(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SwitchDesktop(IntPtr hDesktop);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr hDesktop);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr hDesktop);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>Affiche la saisie sur un bureau sécurisé. Renvoie null si l'utilisateur annule.</summary>
        public static Task<Result> PromptAsync(string vaultName, SecondFactor factor)
        {
            Bitmap background = CaptureDimmedScreen();
            IntPtr original = GetThreadDesktop(GetCurrentThreadId());
            string name = "Verrou_" + Guid.NewGuid().ToString("N");
            IntPtr desk = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
            if (desk == IntPtr.Zero)
                throw new InvalidOperationException("Impossible de créer le bureau sécurisé (code " + Marshal.GetLastWin32Error() + ").");

            var tcs = new TaskCompletionSource<Result>();
            var thread = new Thread(() =>
            {
                Result result = null;
                Exception error = null;
                try
                {
                    // Le fil doit être rattaché au nouveau bureau avant de créer la moindre fenêtre.
                    if (!SetThreadDesktop(desk)) throw new InvalidOperationException("SetThreadDesktop a échoué (" + Marshal.GetLastWin32Error() + ").");
                    if (!SwitchDesktop(desk)) throw new InvalidOperationException("SwitchDesktop a échoué (" + Marshal.GetLastWin32Error() + ").");
                    using var form = new PromptForm(vaultName, factor, background);
                    WinForms.Application.Run(form);
                    result = form.Result;
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    // Toujours revenir au bureau normal, quoi qu'il arrive.
                    SwitchDesktop(original);
                }
                if (error != null) tcs.TrySetException(error); else tcs.TrySetResult(result);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task.ContinueWith(t =>
            {
                thread.Join(2000);
                CloseDesktop(desk);
                background?.Dispose();
                return t;
            }, TaskScheduler.Default).Unwrap();
        }

        /// <summary>Capture assombrie de l'écran principal, en fond du bureau sécurisé (pour garder le contexte).</summary>
        private static Bitmap CaptureDimmedScreen()
        {
            try
            {
                var bounds = WinForms.Screen.PrimaryScreen.Bounds;
                var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    using var veil = new SolidBrush(Color.FromArgb(215, 0x0B, 0x0F, 0x14));
                    g.FillRectangle(veil, 0, 0, bounds.Width, bounds.Height);
                }
                return bmp;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Fenêtre plein écran (Windows Forms : fonctionne sur un bureau séparé) aux couleurs de Verrou.</summary>
        private sealed class PromptForm : WinForms.Form
        {
            private static readonly Color Bg = Color.FromArgb(0x0B, 0x0F, 0x14), Surface = Color.FromArgb(0x15, 0x1A, 0x21),
                Border = Color.FromArgb(0x2A, 0x32, 0x3D), Text1 = Color.FromArgb(0xE8, 0xED, 0xF2), Muted = Color.FromArgb(0x9A, 0xA5, 0xB4),
                Accent = Color.FromArgb(0x00, 0xC4, 0x8C), Cyan = Color.FromArgb(0x00, 0xB4, 0xD8);

            private readonly WinForms.TextBox _pwd, _second;
            private readonly SecondFactor _factor;
            private readonly WinForms.Timer _timeout = new WinForms.Timer { Interval = 120_000 };
            public Result Result { get; private set; }

            public PromptForm(string vaultName, SecondFactor factor, Bitmap background)
            {
                _factor = factor;
                AutoScaleMode = WinForms.AutoScaleMode.Dpi;
                FormBorderStyle = WinForms.FormBorderStyle.None;
                WindowState = WinForms.FormWindowState.Maximized;
                StartPosition = WinForms.FormStartPosition.Manual;
                Bounds = WinForms.Screen.PrimaryScreen.Bounds;
                BackColor = Bg;
                if (background != null) { BackgroundImage = background; BackgroundImageLayout = WinForms.ImageLayout.Stretch; }
                TopMost = true;
                ShowInTaskbar = false;
                KeyPreview = true;
                Font = new Font("Segoe UI", 10.5f);
                Text = L.T("Verrou — saisie sécurisée");

                var card = new WinForms.Panel { BackColor = Surface, Size = new Size(480, factor == SecondFactor.None ? 330 : 410), Padding = new WinForms.Padding(32) };
                card.Paint += (s, e) => { using var p = new Pen(Border); e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); };
                Controls.Add(card);
                Resize += (s, e) => card.Location = new Point((ClientSize.Width - card.Width) / 2, (ClientSize.Height - card.Height) / 2);

                int y = 28;
                WinForms.Label Lbl(string text, Font f, Color c, int height)
                {
                    var l = new WinForms.Label { Text = text, Font = f, ForeColor = c, AutoSize = false, Location = new Point(32, y), Size = new Size(416, height), BackColor = Color.Transparent };
                    card.Controls.Add(l);
                    y += height;
                    return l;
                }
                WinForms.TextBox Box(bool password, string fontName = "Consolas")
                {
                    var t = new WinForms.TextBox
                    {
                        Location = new Point(32, y), Width = 416, BackColor = Bg, ForeColor = Text1, BorderStyle = WinForms.BorderStyle.FixedSingle,
                        Font = new Font(fontName, 13f), UseSystemPasswordChar = password,
                    };
                    card.Controls.Add(t);
                    y += t.Height + 16;
                    return t;
                }

                // Icône cadenas (police d'icônes Windows) + titre
                card.Controls.Add(new WinForms.Label
                {
                    Text = "\uE72E", Font = new Font("Segoe MDL2 Assets", 16f), ForeColor = Accent, AutoSize = false,
                    Location = new Point(32, y + 4), Size = new Size(34, 32), BackColor = Color.Transparent,
                });
                var title = Lbl(L.T("Saisie sécurisée"), new Font("Segoe UI Semibold", 15f), Text1, 36);
                title.Location = new Point(70, title.Top);
                title.Width = 378;
                Lbl(vaultName, new Font("Segoe UI", 10f), Muted, 26);
                Lbl(L.T("Bureau protégé : les autres programmes ne voient ni cette fenêtre ni ce que vous tapez."), new Font("Segoe UI", 9.5f), Cyan, 42);
                y += 6;
                Lbl(L.T("Mot de passe maître"), new Font("Segoe UI Semibold", 9.5f), Muted, 24);
                _pwd = Box(true);
                if (factor != SecondFactor.None)
                {
                    Lbl(L.T(factor == SecondFactor.Code ? "Code de double authentification" : "Clé de secours"), new Font("Segoe UI Semibold", 9.5f), Muted, 24);
                    _second = Box(false);
                    if (factor == SecondFactor.Code) _second.MaxLength = 7;
                }

                var ok = new WinForms.Button
                {
                    Text = L.T("Déverrouiller"), Location = new Point(32, y + 4), Size = new Size(260, 42), FlatStyle = WinForms.FlatStyle.Flat,
                    BackColor = Accent, ForeColor = Bg, Font = new Font("Segoe UI Semibold", 10.5f), Cursor = WinForms.Cursors.Hand,
                };
                ok.FlatAppearance.BorderSize = 0;
                var cancel = new WinForms.Button
                {
                    Text = L.T("Annuler"), Location = new Point(304, y + 4), Size = new Size(144, 42), FlatStyle = WinForms.FlatStyle.Flat,
                    BackColor = Surface, ForeColor = Text1, Font = new Font("Segoe UI", 10.5f), Cursor = WinForms.Cursors.Hand,
                };
                cancel.FlatAppearance.BorderColor = Color.FromArgb(0x3A, 0x44, 0x52);
                card.Controls.Add(ok);
                card.Controls.Add(cancel);
                card.Height = y + 4 + 42 + 32; // hauteur ajustée au contenu
                ok.Click += (s, e) => Accept();
                cancel.Click += (s, e) => Close();
                AcceptButton = ok;
                CancelButton = cancel;

                // Sécurité : on ne reste jamais bloqué sur le bureau sécurisé (2 minutes sans validation = annulation).
                _timeout.Tick += (s, e) => Close();
                _timeout.Start();
                Shown += (s, e) => { SetForegroundWindow(Handle); Activate(); _pwd.Focus(); };
            }

            private void Accept()
            {
                Result = new Result { Password = _pwd.Text };
                if (_factor == SecondFactor.Code) Result.Code = _second.Text;
                if (_factor == SecondFactor.Recovery) Result.Recovery = _second.Text;
                _pwd.Clear();
                _second?.Clear();
                Close();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _timeout.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
