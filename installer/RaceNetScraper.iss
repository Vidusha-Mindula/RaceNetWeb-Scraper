; Inno Setup script for RaceNet Meetings Scraper.
; Built via installer\build-racenet-installer.ps1, which publishes the app to
; installer\publish-racenet first and then invokes ISCC on this script.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; S3 access/secret key baked into a fresh install's default settings.json (see [Code] below) —
; passed in at build time via ISCC's /D flag from build-racenet-installer.ps1, never hardcoded
; here since this .iss file is public. Left blank, a built installer just ships with no default
; (same as before this existed) — the user fills them in via the app themselves.
#ifndef S3AccessKey
  #define S3AccessKey ""
#endif
#ifndef S3SecretKey
  #define S3SecretKey ""
#endif

#define AppName "RaceNet Meetings Scraper"
#define AppExeName "RaceNetScraper.App.exe"
#define AppPublisher "Troyendata"

[Setup]
AppId={{AE8C1827-543D-462F-BA88-53D2A42C931C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={userpf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=RaceNetScraperSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "publish-racenet\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\playwright.ps1"" install chromium"; \
    StatusMsg: "Downloading the Chromium browser used for scraping (this needs an internet connection and can take a minute)..."; \
    Flags: runhidden waituntilterminated
; No "skipifsilent" here on purpose: the in-app updater runs this installer with /SILENT (see
; UpdateChecker.LaunchInstaller), and the whole point of that flow is that the app comes back up
; on its own afterward with zero clicks - skipifsilent would suppress exactly that relaunch.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall

[Code]
// Overwrites settings.json (same shape AppSettings.cs itself saves) with the baked-in defaults
// on every install AND every update — deliberately, not just on a fresh install. A machine's
// settings.json can drift (e.g. someone typing in a different bucket name, or keys going stale)
// and silently keep failing update after update since nothing ever reset it; resetting on every
// version keeps every machine on known-good config rather than accumulating per-machine drift.
// This wipes ALL saved preferences on that machine (download folder, auto-export toggle, etc.),
// not just the S3 fields — intentional, so there's exactly one reset path to reason about.
procedure WriteDefaultSettings;
var
  SettingsDir, SettingsPath, Json: string;
begin
  SettingsDir := ExpandConstant('{localappdata}\RaceNetScraper');
  SettingsPath := SettingsDir + '\settings.json';

  if not DirExists(SettingsDir) then
    ForceDirectories(SettingsDir);

  Json := '{"DownloadFolder":"","AutoExportAfterScrape":false,"UploadToS3":false,' +
    '"S3Endpoint":"https://s3.troyendata.com","S3AccessKey":"{#S3AccessKey}",' +
    '"S3SecretKey":"{#S3SecretKey}","S3BucketName":"troyen-gen-prod","S3Folder":"pending",' +
    '"LastSeenNoticeId":""}';

  SaveStringToFile(SettingsPath, Json, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteDefaultSettings;
end;
