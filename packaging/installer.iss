; FlashDraft Windows installer.
; Wizard flow: Welcome > License > Destination > Program group / shortcuts >
; Install > Finished (with optional "Launch FlashDraft").
[Setup]
AppName=FlashDraft
AppVersion=0.1.1
AppPublisher=rainbowcrr-sys
DefaultDirName={autopf}\FlashDraft
DefaultGroupName=FlashDraft
OutputBaseFilename=FlashDraft-Windows-x64-Setup
OutputDir=..
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
LicenseFile=EULA.rtf
DisableDirPage=no
DisableProgramGroupPage=no
UninstallDisplayIcon={app}\FlashDraft.Desktop.exe
PrivilegesRequired=admin

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\assets\flashdraft-icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "EULA.rtf"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut / 创建桌面快捷方式"; GroupDescription: "Shortcuts / 快捷方式"; Flags: unchecked

[Icons]
Name: "{group}\FlashDraft"; Filename: "{app}\FlashDraft.Desktop.exe"
Name: "{autodesktop}\FlashDraft"; Filename: "{app}\FlashDraft.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FlashDraft.Desktop.exe"; Description: "Launch FlashDraft / 启动 FlashDraft"; Flags: nowait postinstall skipifsilent
