# Verrou

*[Version française](README.fr.md)*

**Verrou** is a password manager for Windows in the spirit of KeePass: your credentials live in **a single encrypted file** on your PC (`.verrou`), nothing is sent over the Internet, every vault is protected by a **master password + two-factor authentication (2FA)**, and **auto-type** fills in sign-in forms for you.

Developed by **Abdel Hakim ZAIMI**. Interface available in **English and French** (Settings › Appearance › Language; defaults to the Windows language), with **light, dark and automatic** themes.

> **Disclaimer**: Verrou has not yet been independently security-audited. Reviews and reports are welcome — see [SECURITY.md](SECURITY.md).

## Installation

- Build the installer with `.\build-installer.ps1` → `installer\Output\Verrou-Setup-<version>.exe` (self-contained, no prerequisites, English or French, per-user or all-users install).
- Or run without installing: `publish\win-x64\Verrou.exe`.

Verrou lives in the notification area: closing the window minimizes it there (auto-type stays available); "Exit" is in the tray icon menu and in "Settings".

## Getting started

1. **Create a new vault**: name, file location, master password (a long passphrase) and optionally a **key file**.
2. **Two-factor authentication** (required for every new vault, shown as "Always on"; can be turned off later per vault): scan the QR code with an authenticator app (Google Authenticator, Microsoft Authenticator, Aegis, 2FAS…), **write down the recovery key** on paper, then type the displayed code to confirm.
3. **+ Entry**: title, username, password (generated automatically, editable), URL, notes, custom fields.
4. On a website's or app's sign-in page, click into the username field and press **Ctrl+Alt+A**: Verrou types the username, Tab, the password, Enter.

> There is **no way to recover** a vault if the master password is lost, if both the phone **and** the recovery key are lost, or if the key file is lost. Keep the recovery key printed and a copy of the key file on a USB drive.

## Two-factor authentication (2FA)

- On every unlock: **master password + 6-digit code** from the phone app (standard TOTP, RFC 6238; 30 s; one step of clock drift tolerated).
- A code can be used **only once** (replay refused); after 5 wrong codes, entry is blocked for 30 s, then twice as long after each new failure (1 h maximum). The lockout is **stored on disk**: restarting Verrou doesn't bypass it.
- **Re-confirmation**: changing the vault's security, exporting, or showing the QR code again asks for the master password **and** a phone code (someone who finds your session unlocked can neither turn off 2FA nor export).
- **Windows Hello (optional, recommended)**: Settings → "Protect 2FA with Windows Hello". The 2FA secret is then encrypted with a key created inside the PC's security chip (TPM) that never leaves it; every unlock additionally asks for your PIN, fingerprint or face. A program running in your session can no longer read the secret without this confirmation. If the Windows PIN is reset, the recovery key still works.
- **Recovery key** (32 characters): "Lost your phone? Use the recovery key" link on the unlock screen. It is also used to open the vault **on another PC** (or after reinstalling Windows): only once, then the phone code is enough.
- **Settings → Master password and two-factor authentication**: turn 2FA on for an older vault, or turn it off. **Settings → Set up a new phone**: shows the QR code again (after re-confirmation).
- How it is protected: the 2FA secret is **part of the encryption key** (like a key file). It is **never written into the vault file**: it stays on this PC, encrypted by Windows (DPAPI, tied to your account) in `%APPDATA%\Verrou\two-factor.json`. Consequence: someone who steals the `.verrou` file **and** guesses your password still cannot open anything without your Windows session or the recovery key. The phone code itself is checked by Verrou on every unlock (an application-level check: someone already logged into your Windows session who knows your password could bypass it with specialized tools — the limit of any serverless 2FA, mitigated by Windows Hello).
- The **encrypted export** produces a copy without 2FA (password only): choose a strong password.

## Auto-type

- **Ctrl+Alt+A** (configurable) in any window: Verrou looks for the matching entry. One match → it is typed; several (or none) → a small window lets you choose. If the vault is locked, Verrou first asks for the master password, then fills in the window.
- Matching: the window title contains the **entry title** or the **domain of its URL**, or one of the entry's **window patterns** (Auto-type tab: `*` `?` wildcards, or a regular expression `//…//`).
- From Verrou: **Ctrl+V** (or the "Auto-type" button) types the selected entry into the last window used.
- **Sequences** (per entry, or a vault-wide default): `{USERNAME}{TAB}{PASSWORD}{ENTER}` by default. Available codes:
  `{USERNAME} {PASSWORD} {TITLE} {URL} {NOTES} {S:Field name}` · keys `{TAB} {ENTER} {SPACE} {BS} {DEL} {ESC} {UP} {DOWN} {LEFT} {RIGHT} {HOME} {END} {PGUP} {PGDN} {INS} {F1}…{F16}` · repeat `{TAB 3}` · pause `{DELAY 500}` · delay between keys `{DELAY=30}` · modifiers `^` Ctrl, `+` Shift, `%` Alt (e.g. `^a`), `~` = Enter · literal braces `{{}` `{}}`.
  Example for a two-page sign-in: `{USERNAME}{ENTER}{DELAY 1500}{PASSWORD}{ENTER}`.
- Text is sent as Unicode: independent of the keyboard layout (AZERTY, dead keys `^` `¨`, accented characters, `€`).
- Safety: if the active window changes while typing, auto-type stops (a password is never typed anywhere but the intended window).
- Windows limitation: auto-type cannot type into an application running "as administrator" unless Verrou is too.

## Anti-spyware protections

- **Secure desktop** (on by default, Settings): the master password and 2FA code are typed on a **separate Windows desktop**, like the UAC prompt or KeePass's equivalent option. Programs on the normal desktop (hook-based keyloggers, screen capture, input injectors) see neither the window nor what is typed. Automatically cancelled after 2 minutes.
- **Auto-type obfuscation** (per-entry option, Auto-type tab): the password is typed in **shuffled pieces**, partly with the keyboard and partly pasted from the clipboard (fragments cleared immediately). A keylogger only sees fragments and arrow keys; a clipboard spy sees other fragments. The field must be empty and accept Ctrl+V (true for web forms and most applications).
- Limitation shared by every password manager (KeePass included): malware running **as administrator** or in the kernel can bypass everything.

## Features

| | |
|---|---|
| Vault | local encrypted file, several vaults possible, change master password / 2FA |
| Protection | master password (typed on a secure desktop) + two-factor authentication (phone TOTP code, recovery key, optional Windows Hello) + optional key file; auto-lock after inactivity (5 min by default), on sleep, on session lock (Win+L), on minimize (option) |
| Clipboard | cleared automatically after 12 s (configurable), excluded from Win+V history and cloud sync |
| Generator | passwords (length, character types, exclusions, ambiguous characters) and passphrases (EFF list of 7,776 words, or your own list); cryptographic randomness, entropy shown |
| Organization | groups and subgroups (drag and drop entries), tags, expiry date, notes, hidden or visible custom fields |
| Search | `Ctrl+F`: every typed word must appear (title, username, URL, notes, tags, group, unhidden fields — never passwords) |
| History | every change keeps the previous version (20 per entry by default); view, restore, delete |
| Import | CSV (KeePass, KeePassXC, Bitwarden, Chrome/Edge, Firefox, 1Password…, `,` or `;` separator) and KeePass XML 2.x |
| Export | encrypted `.verrou` copy with its own password |
| Backups | timestamped encrypted copy before the first change of each session, 10 kept (`Verrou-sauvegardes` folder next to the vault, configurable) |
| Interface | English / French, light / dark / automatic theme |

Window shortcuts: `Ctrl+N` new entry · `Ctrl+B` copy username · `Ctrl+C` copy password · `Ctrl+U` open URL · `Ctrl+V` auto-type · `Enter` edit · `Ctrl+K` duplicate · `Del` delete · `Ctrl+G` generator · `Ctrl+L` lock · `Ctrl+O` open a vault. Global: `Ctrl+Alt+K` shows Verrou.

## Security design

- **Key derivation: Argon2id** (64 MiB, 3 passes, 4 threads by default ≈ 0.5 s; "Hardened" profile 256 MiB). Makes every password guess expensive for an attacker. Implementation checked against the official test vector (RFC 9106).
- **Composite key** = SHA-256(password) ‖ SHA-256(key file) ‖ SHA-256(2FA secret), like KeePass for the first two. The 2FA secret (160 bits) is not in the file: without it, the key cannot be rebuilt.
- **Encryption: AES-256-GCM** (authenticated encryption). A new encryption key (HKDF-SHA256 with a random seed) and a new nonce are drawn on **every save**.
- **Integrity**: the whole file, header included, is authenticated. One modified byte → the vault refuses to open ("file modified or damaged"). An HMAC verifier distinguishes "wrong password" from "damaged file".
- **Atomic writes**: the file is written alongside, read back and verified, then replaces the old one; a crash cannot damage the vault.
- **In memory**: entry passwords stay encrypted (CryptProtectMemory) until displayed or typed; the master key is wiped on lock; the master password is never kept.
- **Screen**: Verrou windows are invisible to screenshots and screen sharing (option).
- Non-secret settings in `%APPDATA%\Verrou\settings.json` (paths, preferences; never a password).

File format (`.verrou`, version 1): `"VRRU"`, version, Argon2id parameters, salt (32 B), options (key file required, 2FA required), seed (32 B), nonce (12 B), HMAC verifier (32 B), then the content (compressed JSON) encrypted with AES-256-GCM and its 16-byte tag. Clearing the "2FA required" option in the header achieves nothing: the header is authenticated and the key depends on the secret. Details in `src/Verrou.Core/Crypto/VaultFile.cs`.

## Design system

Three modes (Settings › Appearance): **Automatic** (follows the Windows light/dark theme, live), **Light**, **Dark**. Changes apply immediately to every window, title bar included. Palettes are in `src/Verrou.App/UI/ThemeManager.cs`; components (`App.xaml`) only use named colors.

Dark theme (original design system):

| Role | Color |
|---|---|
| Background (Deep black), input fields | `#0B0F14` |
| Panels, bars, dialogs — elevation 1 (Anthracite) | `#151A21` |
| Cards, menus — elevation 2 (Slate) | `#1E252F` |
| Borders | `#2A323D` |
| Text (Off-white) / secondary text (Light grey) | `#E8EDF2` / `#9AA5B4` |
| Main accent (Security green): primary buttons, selection, field focus, "strong" meter | `#00C48C` |
| Technical states (Technical cyan): keyboard focus, clipboard countdown, recovery key | `#00B4D8` |
| Alerts (Alert red): errors, deletion, "weak" meter | `#E63946` |

- Typography: **Inter** (SemiBold titles, Regular 14 px body, Medium 12 px labels) and **JetBrains Mono** (passwords, codes, keys), embedded in the app (SIL OFL licence, files in `Assets/Fonts`).
- 4 px grid, 6 / 8 / 12 px radii, shadows only for elevation 3 (lock and welcome cards).
- Light theme: same hues on `#F3F5F8` background / white surfaces, with green, cyan and red **darkened** (`#007A57`, `#0077A3`, `#C62835`) to keep at least 4.5:1 contrast on white; white text on green buttons.
- Deliberate deviation: in the dark theme, text on green buttons is **dark** (`#0B0F14`), not white, to meet the design system's 4.5:1 contrast rule (white on `#00C48C` ≈ 2.3:1, dark ≈ 9:1).

## Why C# / WPF .NET 9

- **Auto-type** needs low-level Windows APIs — global hotkey (`RegisterHotKey`), simulated typing (`SendInput` in Unicode), active window (`GetForegroundWindow`), secure desktop (`CreateDesktop`). C# calls them directly; a web or Electron app would need native modules.
- **Security**: .NET ships AES-GCM, HKDF, HMAC and a cryptographic RNG (via Windows CNG), plus Windows memory protection, DPAPI and Windows Hello. Main external dependency: Argon2id (Konscious.Security.Cryptography, verified by the RFC 9106 test).
- **Distribution**: self-contained executable (.NET runtime included), Inno Setup installer.

## Development

```
src/Verrou.Core     crypto, file format, model, generator, auto-type parsing, imports — no UI
src/Verrou.App      WPF application (windows, global hotkeys, SendInput, clipboard, tray, secure desktop, localization)
tests/Verrou.Tests  xUnit tests (crypto first)
```

- Build and run: `dotnet run --project src\Verrou.App`
- Tests: `dotnet test` (59 tests: encrypt/decrypt, wrong password and wrong key file refused, modified file detected — content, tag, header —, truncated file, malicious Argon2 parameters, fresh nonce on every save, password change, export, backups, Argon2id RFC 9106, TOTP RFC 6238 and Base32 RFC 4648, 2FA vault refused without the secret or with a wrong secret, cleared 2FA flag has no effect, turning 2FA on/off, auto-type obfuscation (text always rebuilt exactly, no channel sees everything), plus auto-type sequences, generator, CSV/XML import, history, search).
- Installer: `.\build-installer.ps1` (runs the tests, publishes, compiles the installer; requires Inno Setup 6).
- Separate data to test without touching your settings: `Verrou.exe --data-dir C:\path\test`.
- Translations: French is the source language; English strings are in `src/Verrou.App/Resources/en.json` (French → English, with `{0}` placeholders for variable parts).

## Licence

[MIT](LICENSE) © 2026 Abdel Hakim ZAIMI.
Passphrase word list: EFF Large Wordlist (Electronic Frontier Foundation, CC BY 3.0). Inter and JetBrains Mono fonts: SIL Open Font License 1.1. QR codes: QRCoder (MIT). Argon2id: Konscious.Security.Cryptography (MIT).
