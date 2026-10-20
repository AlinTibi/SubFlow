; ALMARFELD SubFlow - Windows installer (Inno Setup 6)
; Built by build\package.ps1:  ISCC.exe /DAppVersion=1.2.0 /DSourceDir=<staging folder> /O<output> installer\SubFlow.iss
;
; Installs for the current user by default (no administrator rights). FFmpeg, Ollama and AI
; models are not bundled; SubFlow's Dashboard detects them and offers to install them.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\stage"
#endif

#define AppName "ALMARFELD SubFlow"
#define AppExe "SubFlow.exe"
#define Publisher "ALMARFELD"
#define Website "https://almarfeld.com"

[Setup]
; Keep this AppId forever: it identifies the product for upgrades and uninstall.
AppId={{0CEAC22E-64C6-4503-BB3B-3EECD23F9856}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#Website}
AppSupportURL={#Website}
AppUpdatesURL={#Website}
AppCopyright=Copyright (c) 2026 {#Publisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile={#SourceDir}\LICENSE
SetupIconFile=..\src\ALMARFELD.SubFlow.App\Assets\subflow.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=110
Compression=lzma2/ultra64
SolidCompression=yes
OutputBaseFilename=ALMARFELD-SubFlow-{#AppVersion}-setup-win-x64
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\CONTRIBUTING.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\CODE_OF_CONDUCT.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs
Source: "{#SourceDir}\tools\README.txt"; DestDir: "{app}\tools"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Settings, history and the translation cache live in %APPDATA% and %LOCALAPPDATA%\ALMARFELD\SubFlow
; and are kept on uninstall, so a reinstall continues where the user left off.
