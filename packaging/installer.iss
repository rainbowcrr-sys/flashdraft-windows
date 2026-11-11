; FlashDraft Windows installer.
; Wizard flow: Welcome > License > Destination > Program group / shortcuts >
; Install > Finished (with optional "Launch FlashDraft").
[Setup]
AppName=FlashDraft
AppVersion=0.1.2
AppPublisher=rainbowcrr-sys
DefaultDirName={autopf}\FlashDraft
DefaultGroupName=FlashDraft
OutputBaseFilename=FlashDraft-Windows-x64-Setup
OutputDir=..
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
UninstallDisplayIcon={app}\FlashDraft.Desktop.exe
PrivilegesRequired=admin
; Native Inno Setup language dialog appears BEFORE step 1 of the wizard.
; No custom WizardImageFile / WizardSmallImageFile: all controls, radio
; buttons, checkboxes and buttons use the stock Inno Setup 6 modern style.
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"; LicenseFile: "EULA-en.rtf"
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"; LicenseFile: "EULA-zh.rtf"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.ShortcutsGroup=Shortcuts
chinesesimplified.ShortcutsGroup=快捷方式
english.LaunchApp=Launch FlashDraft
chinesesimplified.LaunchApp=启动 FlashDraft

; The installer writes the chosen language into the app directory. The app
; reads it as its default and the Settings page can override it later.
[INI]
Filename: "{app}\locale.ini"; Section: "FlashDraft"; Key: "Language"; String: "{code:LangCode}"

[Code]
function LangCode(Param: String): String;
begin
  if ActiveLanguage = 'chinesesimplified' then
    Result := 'zh-CN'
  else
    Result := 'en-US';
end;

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\assets\flashdraft-icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "EULA-en.rtf"; DestDir: "{app}"; Flags: ignoreversion
Source: "EULA-zh.rtf"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:ShortcutsGroup}"; Flags: unchecked

[Icons]
Name: "{group}\FlashDraft"; Filename: "{app}\FlashDraft.Desktop.exe"
Name: "{autodesktop}\FlashDraft"; Filename: "{app}\FlashDraft.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FlashDraft.Desktop.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
