# Verrou

*[English version](README.md)*

**Verrou** est un gestionnaire de mots de passe pour Windows, dans l'esprit de KeePass : vos identifiants sont dans **un seul fichier chiffré** sur votre PC (`.verrou`), rien ne part sur Internet, chaque coffre est protégé par un **mot de passe maître + double authentification (2FA)**, et l'**auto-type** remplit les formulaires de connexion à votre place.

Développé par **Abdel Hakim ZAIMI**.

## Installation

- Installeur : `.uild-installer.ps1` produit `installer\Output\Verrou-Setup-<version>.exe` (autonome, aucun prérequis, en français ou en anglais, installation pour l'utilisateur ou pour tous).
- Ou sans installer : `publish\win-x64\Verrou.exe`.
- **Langue** : français ou anglais (Paramètres › Apparence › Langue ; par défaut celle de Windows).

Verrou vit dans la zone de notification : fermer la fenêtre la replie (l'auto-type reste disponible), « Quitter » est dans le menu de l'icône ou dans « Paramètres ».

## Premiers pas

1. **Créer un nouveau coffre** : nom, emplacement du fichier, mot de passe maître (une longue phrase de passe), et si vous voulez un **fichier-clé** en plus.
2. **Double authentification** (exigée pour tout nouveau coffre, indiquée « Toujours activée » ; désactivable ensuite coffre par coffre) : scannez le QR code avec une application d'authentification (Google Authenticator, Microsoft Authenticator, Aegis, 2FAS…), **notez la clé de secours** sur papier, puis tapez le code affiché pour confirmer.
3. **+ Entrée** : titre, identifiant, mot de passe (généré automatiquement, modifiable), URL, notes, champs personnalisés.
4. Sur la page de connexion d'un site ou d'une application, cliquez dans le champ identifiant puis appuyez sur **Ctrl+Alt+A** : Verrou tape l'identifiant, Tab, le mot de passe, Entrée.

> Il n'existe **aucun moyen de récupérer** un coffre si le mot de passe maître est perdu, ni si le téléphone **et** la clé de secours sont perdus tous les deux, ni si le fichier-clé est perdu. Gardez la clé de secours imprimée et une copie du fichier-clé sur une clé USB.

## Double authentification (2FA)

- À chaque ouverture : **mot de passe maître + code à 6 chiffres** de l'application du téléphone (TOTP standard, RFC 6238 ; 30 s ; un décalage d'horloge d'un code est toléré).
- Un code ne sert **qu'une fois** (rejeu refusé) ; après 5 codes faux, la saisie est bloquée 30 s, puis le double à chaque nouvel échec (1 h maximum). Le blocage est **mémorisé sur disque** : relancer Verrou ne le contourne pas.
- **Re-confirmation** : modifier la sécurité du coffre, exporter, ou réafficher le QR code demande à nouveau le mot de passe maître **et** un code du téléphone (quelqu'un qui trouve votre session ouverte ne peut pas désactiver la 2FA ni exporter).
- **Windows Hello (option, recommandé)** : Paramètres → « Protéger la 2FA avec Windows Hello ». Le secret 2FA est alors chiffré par une clé créée dans la puce de sécurité du PC (TPM), qui n'en sort jamais ; chaque ouverture demande en plus votre PIN, empreinte ou visage. Un programme de votre session ne peut plus lire le secret sans cette confirmation. Si le PIN Windows est réinitialisé, la clé de secours reste valable.
- **Clé de secours** (32 caractères) : lien « Téléphone perdu ? Utiliser la clé de secours » sur l'écran de déverrouillage. Elle sert aussi à ouvrir le coffre **sur un autre PC** (ou après une réinstallation de Windows) : à faire une seule fois, ensuite le code du téléphone suffit.
- **Paramètres → Mot de passe maître et double authentification** : activer la 2FA sur un ancien coffre, ou la désactiver. **Paramètres → Configurer un nouveau téléphone** : réaffiche le QR code (après confirmation du mot de passe maître).
- Comment c'est protégé : le secret 2FA **fait partie de la clé de chiffrement** (comme un fichier-clé). Il n'est **jamais écrit dans le fichier du coffre** : il est gardé sur ce PC, chiffré par Windows (DPAPI, lié à votre compte), dans `%APPDATA%\Verrou\two-factor.json`. Conséquence : quelqu'un qui vole le fichier `.verrou` **et** devine votre mot de passe ne peut toujours rien ouvrir sans votre session Windows ou la clé de secours. Le code du téléphone, lui, est vérifié par Verrou à chaque ouverture (c'est un contrôle de l'application : une personne déjà connectée à votre session Windows et connaissant votre mot de passe pourrait le contourner avec un outil spécialisé — c'est la limite de toute 2FA sans serveur).
- L'**export chiffré** produit une copie sans 2FA (mot de passe seul) : choisissez un mot de passe fort.

## Auto-type

- **Ctrl+Alt+A** (modifiable) dans n'importe quelle fenêtre : Verrou cherche l'entrée correspondante. Une seule → elle est tapée ; plusieurs (ou aucune) → une petite fenêtre propose de choisir. Si le coffre est verrouillé, Verrou demande d'abord le mot de passe maître puis remplit la fenêtre.
- Correspondance : le titre de la fenêtre contient le **titre de l'entrée** ou le **domaine de son URL**, ou bien un des **motifs de fenêtre** de l'entrée (onglet Auto-type : jokers `*` `?`, ou expression régulière `//…//`).
- Depuis Verrou : **Ctrl+V** (ou le bouton « Auto-type ») tape l'entrée sélectionnée dans la dernière fenêtre utilisée.
- **Séquences** (par entrée, ou par défaut pour tout le coffre) : `{USERNAME}{TAB}{PASSWORD}{ENTER}` par défaut. Codes disponibles :
  `{USERNAME} {PASSWORD} {TITLE} {URL} {NOTES} {S:Nom du champ}` · touches `{TAB} {ENTER} {SPACE} {BS} {DEL} {ESC} {UP} {DOWN} {LEFT} {RIGHT} {HOME} {END} {PGUP} {PGDN} {INS} {F1}…{F16}` · répétition `{TAB 3}` · pause `{DELAY 500}` · délai entre touches `{DELAY=30}` · modificateurs `^` Ctrl, `+` Maj, `%` Alt (ex. `^a`), `~` = Entrée · accolades littérales `{{}` `{}}`.
  Exemple pour un site en deux pages : `{USERNAME}{ENTER}{DELAY 1500}{PASSWORD}{ENTER}`.
- Le texte est envoyé en Unicode : indépendant de la disposition du clavier (AZERTY, touches mortes `^` `¨`, caractères accentués, `€`).
- Sécurité : si la fenêtre active change pendant la frappe, l'auto-type s'arrête (un mot de passe n'est jamais tapé ailleurs que dans la fenêtre visée).
- Limite Windows : l'auto-type ne peut pas taper dans une application lancée « en tant qu'administrateur » si Verrou ne l'est pas.

## Protections contre les logiciels espions

- **Bureau sécurisé** (activé par défaut, Paramètres) : le mot de passe maître et le code 2FA se tapent sur un **bureau Windows séparé**, comme l'écran de l'UAC ou l'option équivalente de KeePass. Les programmes du bureau normal (enregistreurs de frappe à base de crochets clavier, captures d'écran, injecteurs de frappe) ne voient ni la fenêtre ni ce qui est tapé. Annulation automatique au bout de 2 minutes.
- **Brouillage de l'auto-type** (option par entrée, onglet Auto-type) : le mot de passe est tapé par morceaux **dans le désordre**, une partie au clavier, une partie collée depuis le presse-papiers (fragments effacés aussitôt). Un enregistreur de frappe ne voit que des bouts et des flèches ; un espion du presse-papiers d'autres bouts. Le champ doit être vide et accepter Ctrl+V (c'est le cas des formulaires web et de la plupart des applications).
- Limite commune à tous les gestionnaires de mots de passe (KeePass compris) : un logiciel malveillant lancé en **administrateur** ou dans le noyau peut tout contourner.

## Fonctions

| | |
|---|---|
| Coffre | fichier chiffré local, plusieurs coffres possibles, « Changer le mot de passe maître » |
| Protection | mot de passe maître (saisi sur bureau sécurisé) + double authentification (code TOTP du téléphone, clé de secours, Windows Hello en option) + fichier-clé optionnel ; verrouillage auto après inactivité (5 min par défaut), à la mise en veille, au verrouillage de session (Win+L), à la réduction (option) |
| Presse-papiers | effacé automatiquement après 12 s (réglable), exclu de l'historique Win+V et du cloud |
| Générateur | mots de passe (longueur, types de caractères, exclusions, caractères ambigus) et phrases de passe (liste EFF de 7 776 mots, ou votre propre liste) ; tirage cryptographique, entropie affichée |
| Organisation | groupes et sous-groupes (glisser-déposer des entrées), étiquettes, date d'expiration, notes, champs personnalisés masqués ou non |
| Recherche | `Ctrl+F` : tous les mots tapés doivent apparaître (titre, identifiant, URL, notes, étiquettes, groupe, champs non masqués — jamais les mots de passe) |
| Historique | chaque modification garde l'ancienne version (20 par entrée par défaut) ; consultation, restauration, suppression |
| Import | CSV (KeePass, KeePassXC, Bitwarden, Chrome/Edge, Firefox, 1Password…, séparateur `,` ou `;`) et KeePass XML 2.x |
| Export | copie chiffrée `.verrou` avec son propre mot de passe |
| Sauvegardes | copie chiffrée horodatée avant la première modification de chaque session, 10 gardées (dossier `Verrou-sauvegardes` à côté du coffre, réglable) |

Raccourcis dans la fenêtre : `Ctrl+N` nouvelle entrée · `Ctrl+B` copier l'identifiant · `Ctrl+C` copier le mot de passe · `Ctrl+U` ouvrir l'URL · `Ctrl+V` auto-type · `Entrée` modifier · `Ctrl+K` dupliquer · `Suppr` supprimer · `Ctrl+G` générateur · `Ctrl+L` verrouiller · `Ctrl+O` ouvrir un coffre. Global : `Ctrl+Alt+K` affiche Verrou.

## Sécurité : comment c'est fait

- **Dérivation de clé : Argon2id** (64 Mio, 3 passes, 4 fils par défaut ≈ 0,5 s ; profil « Renforcé » 256 Mio). Elle rend chaque essai de mot de passe coûteux pour un attaquant. Implémentation vérifiée contre le vecteur de test officiel (RFC 9106).
- **Clé composite** = SHA-256(mot de passe) ‖ SHA-256(fichier-clé) ‖ SHA-256(secret 2FA), comme KeePass pour les deux premiers. Le secret 2FA (160 bits) n'est pas dans le fichier : sans lui, la clé ne peut pas être reconstituée.
- **Chiffrement : AES-256-GCM** (chiffrement authentifié). Une nouvelle clé de chiffrement (HKDF-SHA256 avec une graine aléatoire) et un nouveau nonce sont tirés à **chaque enregistrement**.
- **Intégrité** : tout le fichier, en-tête compris, est authentifié. Un octet modifié → le coffre refuse de s'ouvrir (« fichier modifié ou endommagé »). Un vérificateur HMAC distingue « mauvais mot de passe » de « fichier abîmé ».
- **Écriture atomique** : le fichier est écrit à côté, relu et vérifié, puis remplace l'ancien ; un plantage ne peut pas abîmer le coffre.
- **En mémoire** : les mots de passe des entrées restent chiffrés (CryptProtectMemory) tant qu'ils ne sont pas affichés ou tapés ; la clé maîtresse est effacée au verrouillage ; le mot de passe maître n'est jamais gardé.
- **Écran** : les fenêtres de Verrou sont invisibles aux captures d'écran et au partage d'écran (option).
- Réglages non secrets dans `%APPDATA%\Verrou\settings.json` (chemins, préférences ; jamais de mot de passe).

Format du fichier (`.verrou`, version 1) : `"VRRU"`, version, paramètres Argon2id, sel (32 o), options (fichier-clé requis, 2FA requise), graine (32 o), nonce (12 o), vérificateur HMAC (32 o), puis le contenu (JSON compressé) chiffré AES-256-GCM et son étiquette de 16 octets. Effacer l'option « 2FA requise » dans l'en-tête ne sert à rien : l'en-tête est authentifié et la clé dépend du secret. Détail dans `src/Verrou.Core/Crypto/VaultFile.cs`.

## Charte graphique

Trois modes (Paramètres › Apparence) : **Automatique** (suit le thème clair/sombre de Windows, en direct), **Clair**, **Sombre**. Le changement s'applique immédiatement à toutes les fenêtres, barre de titre comprise. Les palettes sont dans `src/Verrou.App/UI/ThemeManager.cs` ; les composants (`App.xaml`) n'utilisent que des couleurs nommées.

Thème sombre (charte d'origine) :

| Rôle | Couleur |
|---|---|
| Fond (Noir profond), champs de saisie | `#0B0F14` |
| Panneaux, barres, dialogues — élévation 1 (Anthracite) | `#151A21` |
| Cartes, menus — élévation 2 (Ardoise) | `#1E252F` |
| Bordures | `#2A323D` |
| Texte (Blanc cassé) / texte secondaire (Gris clair) | `#E8EDF2` / `#9AA5B4` |
| Accent principal (Vert sécurité) : boutons principaux, sélection, focus des champs, jauge « fort » | `#00C48C` |
| États techniques (Cyan technique) : focus clavier, compte à rebours du presse-papiers, clé de secours | `#00B4D8` |
| Alertes (Rouge alerte) : erreurs, suppression, jauge « faible » | `#E63946` |

- Typographie : **Inter** (titres SemiBold, corps Regular 14 px, labels Medium 12 px) et **JetBrains Mono** (mots de passe, codes, clés), intégrées à l'application (licence SIL OFL, fichiers dans `Assets/Fonts`).
- Grille de 4 px, rayons 6 / 8 / 12 px, ombres uniquement pour le niveau 3 (cartes de l'écran verrouillé et d'accueil).
- Composants : boutons principal / secondaire / danger, champs avec œil, interrupteurs, cases et radios vertes, jauge de force en 5 segments, barre de titre Windows sombre, boîtes de message et menu de la zone de notification aux mêmes couleurs.
- Thème clair : mêmes teintes sur fond `#F3F5F8` / surfaces blanches, avec le vert, le cyan et le rouge **assombris** (`#007A57`, `#0077A3`, `#C62835`) pour garder un contraste d'au moins 4,5:1 sur fond blanc ; texte blanc sur les boutons verts.
- Écart volontaire : en thème sombre, le texte des boutons verts est **sombre** (`#0B0F14`) et non blanc, pour respecter la règle de contraste 4,5:1 de la charte (blanc sur `#00C48C` ≈ 2,3:1, sombre ≈ 9:1).

## Pourquoi C# / WPF .NET 9

- **Auto-type** : il faut des API Windows bas niveau — raccourci global (`RegisterHotKey`), frappe simulée (`SendInput` en Unicode), fenêtre active (`GetForegroundWindow`). C# les appelle directement, sans couche intermédiaire ; une app web ou Electron devrait passer par des modules natifs.
- **Sécurité** : .NET fournit en standard AES-GCM, HKDF, HMAC et un générateur aléatoire cryptographique (via Windows CNG), plus la protection mémoire Windows. Une seule dépendance externe : Argon2id (Konscious.Security.Cryptography, vérifiée par le test RFC 9106).
- **Distribution** : exécutable autonome (runtime .NET inclus), installeur Inno Setup.

## Développement

```
src/Verrou.Core     crypto, format du fichier, modèle, générateur, auto-type (analyse), imports — sans interface
src/Verrou.App      application WPF (fenêtres, raccourcis globaux, SendInput, presse-papiers, zone de notification)
tests/Verrou.Tests  tests xUnit (crypto en priorité)
```

- Compiler et lancer : `dotnet run --project src\Verrou.App`
- Tests : `dotnet test` (59 tests : chiffrement/déchiffrement, mauvais mot de passe et mauvais fichier-clé refusés, fichier modifié détecté — contenu, étiquette, en-tête —, fichier tronqué, paramètres Argon2 piégés, nonce neuf à chaque enregistrement, changement de mot de passe, export, sauvegardes, Argon2id RFC 9106, TOTP RFC 6238 et Base32 RFC 4648, coffre 2FA refusé sans le secret ou avec un faux secret, drapeau 2FA effacé sans effet, activation/désactivation de la 2FA, brouillage de l'auto-type (le texte est toujours reconstitué à l'identique, aucun canal ne voit tout), et aussi séquences d'auto-type, générateur, imports CSV/XML, historique, recherche).
- Installeur : `.\build-installer.ps1` (lance les tests, publie, compile l'installeur).
- Données séparées pour tester sans toucher à vos réglages : `Verrou.exe --data-dir C:\chemin\test`.

> **Avertissement** : Verrou n'a pas encore fait l'objet d'un audit de sécurité indépendant. Les relectures et signalements sont les bienvenus (voir [SECURITY.md](SECURITY.md)).

Licence MIT. Liste de mots des phrases de passe : EFF Large Wordlist (Electronic Frontier Foundation, CC BY 3.0). Polices Inter et JetBrains Mono : SIL Open Font License 1.1. QR codes : QRCoder (MIT).
