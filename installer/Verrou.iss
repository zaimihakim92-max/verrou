; Installeur Verrou — compilé avec Inno Setup 6 par build-installer.ps1
; Le binaire autonome (runtime .NET inclus) est publié dans ..\publish\win-x64 avant compilation.

#define AppName "Verrou"
#define AppPublisher "Abdel Hakim ZAIMI"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "Verrou.exe"

[Setup]
AppId={{FC3FE9CC-94D7-417F-AA2C-B85394E808A6
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=© 2026 {#AppPublisher}
AppComments=Encrypted password manager with auto-type and two-factor authentication
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=© 2026 {#AppPublisher}
VersionInfoDescription=Installation de {#AppName}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installation sans droits administrateur possible (pour l'utilisateur courant) ou pour tous.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=Verrou-Setup-{#AppVersion}
SetupIconFile=..\src\Verrou.App\Assets\Verrou.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
ShowLanguageDialog=auto
Compression=lzma2/ultra64
SolidCompression=yes
; L'installeur demande de fermer Verrou s'il est en cours d'exécution.
AppMutex=Verrou.Running
CloseApplications=yes
ChangesAssociations=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
en.TaskDesktop=Create a desktop shortcut
fr.TaskDesktop=Créer un raccourci sur le Bureau
en.GroupShortcuts=Shortcuts:
fr.GroupShortcuts=Raccourcis :
en.TaskStartup=Start Verrou with Windows (in the notification area, for auto-type)
fr.TaskStartup=Lancer Verrou au démarrage de Windows (dans la zone de notification, pour l'auto-type)
en.GroupStartup=Startup:
fr.GroupStartup=Démarrage :
en.TaskAssoc=Open .verrou vaults with Verrou
fr.TaskAssoc=Ouvrir les coffres .verrou avec Verrou
en.GroupFiles=Files:
fr.GroupFiles=Fichiers :
en.IconComment=Password manager
fr.IconComment=Gestionnaire de mots de passe
en.VaultType=Verrou vault
fr.VaultType=Coffre Verrou
en.LaunchApp=Launch Verrou
fr.LaunchApp=Lancer Verrou

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupShortcuts}"
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:GroupStartup}"
Name: "assoc"; Description: "{cm:TaskAssoc}"; GroupDescription: "{cm:GroupFiles}"

[Files]
Source: "..\publish\win-x64\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.fr.md"; DestDir: "{app}"; DestName: "LISEZMOI.md"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\src\Verrou.App\Assets\Fonts\OFL-Inter.txt"; DestDir: "{app}\licences"; Flags: ignoreversion
Source: "..\src\Verrou.App\Assets\Fonts\OFL-JetBrainsMono.txt"; DestDir: "{app}\licences"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:IconComment}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Lancement au démarrage (même clé que l'option des Paramètres de Verrou).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Verrou"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: startup
; Association de fichiers.
Root: HKA; Subkey: "Software\Classes\.verrou"; ValueType: string; ValueName: ""; ValueData: "Verrou.Vault"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Verrou.Vault"; ValueType: string; ValueName: ""; ValueData: "{cm:VaultType}"; Flags: uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Verrou.Vault\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Verrou.Vault\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

; Les coffres (.verrou), leurs sauvegardes, les réglages et les secrets 2FA (%APPDATA%\Verrou) sont conservés à la désinstallation.
