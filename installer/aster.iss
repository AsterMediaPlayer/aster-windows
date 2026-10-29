; The Windows installer, built by Inno Setup 6 in the release workflow:
;   iscc /DAppVersion=0.10.0-beta /DSourceDir=<publish folder> /DOutputDir=<dist> installer\aster.iss
; It installs for the current user only (no administrator prompt), into
; %LOCALAPPDATA%\Programs\Aster, with a Start menu shortcut and an
; uninstaller. Sign-ins and settings live elsewhere (Credential Manager and
; %LOCALAPPDATA%\Aster), so uninstalling or updating leaves them alone.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Never change: Windows knows the installed app by it, for updates and uninstalling.
AppId={{6C8F2A1E-4B7D-4E39-9A51-0D3C7E2B9F14}
AppName=Aster
AppVersion={#AppVersion}
AppVerName=Aster {#AppVersion}
AppPublisher=Aster Media Player
AppPublisherURL=https://github.com/AsterMediaPlayer/aster-windows
AppSupportURL=https://github.com/AsterMediaPlayer/aster-windows/issues
AppUpdatesURL=https://github.com/AsterMediaPlayer/aster-windows/releases
DefaultDirName={autopf}\Aster
DefaultGroupName=Aster
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809, as the app's own minimum.
MinVersion=10.0.17763
SetupIconFile=..\src\Aster.Desktop\Assets\aster.ico
UninstallDisplayIcon={app}\Aster.Desktop.exe
UninstallDisplayName=Aster
OutputDir={#OutputDir}
OutputBaseFilename=Aster-{#AppVersion}-windows-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; An update closes a running Aster first.
CloseApplications=yes
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; An update replaces the whole folder, so nothing of an older version stays behind.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[Icons]
; The app id Aster gives itself (WindowsAppIdentity), so Windows' media overlay names it.
Name: "{autoprograms}\Aster"; Filename: "{app}\Aster.Desktop.exe"; AppUserModelID: "AsterMediaPlayer.Aster"
Name: "{autodesktop}\Aster"; Filename: "{app}\Aster.Desktop.exe"; Tasks: desktopicon; AppUserModelID: "AsterMediaPlayer.Aster"

[Run]
Filename: "{app}\Aster.Desktop.exe"; Description: "{cm:LaunchProgram,Aster}"; Flags: nowait postinstall skipifsilent
