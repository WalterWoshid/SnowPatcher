#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef AppDirectory
  #error AppDirectory is required
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory is required
#endif

[Setup]
AppId={{E1901F8E-EE43-45DA-A340-F12ED7381DD1}
AppName=SnowPatcher
AppVersion={#AppVersion}
AppPublisher=WalterWoshid
AppPublisherURL=https://github.com/WalterWoshid/SnowPatcher
AppSupportURL=https://github.com/WalterWoshid/SnowPatcher/issues
AppUpdatesURL=https://github.com/WalterWoshid/SnowPatcher/releases/latest
DefaultDirName={localappdata}\Programs\SnowPatcher
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableDirPage=auto
WizardStyle=classic
SetupIconFile={#AppDirectory}\Assets\SnowPatcher.ico
UninstallDisplayIcon={app}\SnowPatcher.exe
OutputDir={#ReleaseDirectory}
OutputBaseFilename=SnowPatcher-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
AppMutex=Local\SnowPatcher.Desktop
SetupMutex=Local\SnowPatcher.Setup
CloseApplications=no
RestartApplications=no
UsePreviousTasks=yes

[Tasks]
Name: startmenu; Description: "Add to the Start menu"; GroupDescription: "Shortcuts:"
Name: desktop; Description: "Add a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: startup; Description: "Start SnowPatcher when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#AppDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SnowPatcher"; Filename: "{app}\SnowPatcher.exe"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{userdesktop}\SnowPatcher"; Filename: "{app}\SnowPatcher.exe"; WorkingDir: "{app}"; Tasks: desktop

[InstallDelete]
Type: files; Name: "{userprograms}\SnowPatcher.lnk"; Tasks: not startmenu
Type: files; Name: "{userdesktop}\SnowPatcher.lnk"; Tasks: not desktop

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SnowPatcher"; ValueData: """{app}\SnowPatcher.exe"" --startup"; Tasks: startup

[Run]
Filename: "{app}\SnowPatcher.exe"; Parameters: "--show"; WorkingDir: "{app}"; Description: "Launch SnowPatcher"; Flags: nowait postinstall skipifsilent

[Code]
const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
var
  StartupChoiceLoaded: Boolean;

function OwnsStartupEntry: Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(HKCU, StartupKey, 'SnowPatcher', Command) and
    ((CompareText(Command, '"' + ExpandConstant('{app}\SnowPatcher.exe') + '" --startup') = 0) or
     (CompareText(Command, '"' + ExpandConstant('{app}\SnowPatcher.App.exe') + '" --startup') = 0));
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Command: String;
begin
  { Preferences may have changed since the last setup. Explicit /TASKS wins. }
  if (CurPageID = wpSelectTasks) and not StartupChoiceLoaded then
  begin
    StartupChoiceLoaded := True;
    if (ExpandConstant('{param:TASKS|unset}') = 'unset') and
       (ExpandConstant('{param:MERGETASKS|unset}') = 'unset') then
    begin
      if RegQueryStringValue(HKCU, StartupKey, 'SnowPatcher', Command) then
        WizardSelectTasks('startup')
      else
        WizardSelectTasks('!startup');
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { Keep the previous executable until installation has succeeded. }
    DeleteFile(ExpandConstant('{app}\SnowPatcher.App.exe'));
    DeleteFile(ExpandConstant('{app}\SnowPatcher.App.dll'));
    DeleteFile(ExpandConstant('{app}\SnowPatcher.App.deps.json'));
    DeleteFile(ExpandConstant('{app}\SnowPatcher.App.runtimeconfig.json'));
    if not WizardIsTaskSelected('startup') and OwnsStartupEntry then
      RegDeleteValue(HKCU, StartupKey, 'SnowPatcher');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { A portable copy may have taken over startup since installation. }
  if (CurUninstallStep = usUninstall) and OwnsStartupEntry then
    RegDeleteValue(HKCU, StartupKey, 'SnowPatcher');
end;
