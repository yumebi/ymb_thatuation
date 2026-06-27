#define MyAppName "YMB Thatuation"
#define MyAppVersion "1.1.11"
#define MyAppPublisher "yumebi"
#define MyAppExeName "YmbThatuation.exe"
#define PublishDir "..\src-csharp\YmbThatuation\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{16A514F7-6A93-4AA3-83CE-6B24120664FB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=YmbThatuation-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconDesc}"; GroupDescription: "{cm:AdditionalIconsGroup}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
japanese.DesktopIconDesc=デスクトップにアイコンを作成する
japanese.AdditionalIconsGroup=追加のアイコン:
japanese.WebView2MissingPrompt=WebView2 Runtimeが見つかりません。本アプリの動作に必須です。%nダウンロードページを開きますか?(Windows 10/11でEdgeが入っていれば通常は既に導入済みです)

english.DesktopIconDesc=Create a desktop icon
english.AdditionalIconsGroup=Additional icons:
english.WebView2MissingPrompt=WebView2 Runtime was not found. It is required for this app to work.%nOpen the download page now? (Usually already installed if Edge is present on Windows 10/11)

[Code]
const
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2ClientKeyWow = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

// WebView2 Runtime(Evergreen)の有無を確認する。Win10/11+Edgeなら通常は導入済み。
function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM64, WebView2ClientKeyWow, 'pv', Version) or
    RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', Version) or
    RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', Version);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not IsWebView2Installed) then
  begin
    if MsgBox(CustomMessage('WebView2MissingPrompt'),
      mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
