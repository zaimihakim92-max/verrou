# Security policy

Verrou is a personal project that has **not yet been independently audited**. Careful reviews are very welcome.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private vulnerability reporting instead (repository **Security** tab → **Report a vulnerability**), with:

- a description of the issue and its impact,
- steps to reproduce (use dummy vaults and passwords only),
- the Verrou version (Settings › About).

You will get an answer as soon as possible; fixes are published with a note in the release.

## Scope

In scope: the vault file format and cryptography (`src/Verrou.Core`), two-factor authentication, secure desktop, auto-type, clipboard handling, memory handling.

Out of scope: attacks that require administrator or kernel access to the machine, or physical access to an unlocked session — no local password manager can defend against those.
