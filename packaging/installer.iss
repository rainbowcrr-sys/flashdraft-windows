; Inno Setup script for the Windows installer.
; Produces FlashDraft-Windows-x64-Setup.exe, a directly installable package.
[Setup]
AppName=FlashDraft
AppVersion=0.1.0
AppPublisher=rainbowcrr-sys
DefaultDirName={autopf}\FlashDraft
DefaultGroupName=FlashDraft
OutputBaseFilename=FlashDraft-Windows-x64-Setup
OutputDir=..
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\FlashDraft.Desktop.exe
WizardStyle=modern

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\assets\flashdraft-icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\FlashDraft"; Filename: "{app}\FlashDraft.Desktop.exe"
Name: "{autodesktop}\FlashDraft"; Filename: "{app}\FlashDraft.Desktop.exe"
