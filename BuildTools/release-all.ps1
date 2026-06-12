param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

function Info($Text) { Write-Host $Text -ForegroundColor Cyan }
function Ok($Text) { Write-Host $Text -ForegroundColor Green }
function Warn($Text) { Write-Host $Text -ForegroundColor Yellow }
function Fail($Text) { Write-Host $Text -ForegroundColor Red; throw $Text }

function Assert-LastExitCode($StepName) {
    if ($LASTEXITCODE -ne 0) {
        Fail "$StepName FAILED. ExitCode=$LASTEXITCODE"
    }
}

function Find-InnoSetup {
    $paths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($p in $paths) {
        if (Test-Path $p) { return $p }
    }

    Fail "Khong tim thay Inno Setup 6. Hay cai Inno Setup 6 truoc."
}

function Find-MSBuild {
    $paths = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($p in $paths) {
        if (Test-Path $p) { return $p }
    }

    Fail "Khong tim thay MSBuild. Hay cai Visual Studio/Build Tools voi .NET desktop build tools."
}

function Get-ProjectRoot {
    $scriptDir = Split-Path -Parent $MyInvocation.ScriptName
    if ((Split-Path -Leaf $scriptDir) -ieq "BuildTools") {
        return Split-Path -Parent $scriptDir
    }
    return $scriptDir
}

function Read-VersionFromAssemblyInfo($ProjectRoot) {
    $assemblyInfo = Get-ChildItem -Path $ProjectRoot -Recurse -File -Filter "AssemblyInfo.cs" |
        Where-Object { $_.FullName -notmatch "\\bin\\|\\obj\\|\\InstallerOutput\\|\\ReleasePackages\\" } |
        Select-Object -First 1

    if (-not $assemblyInfo) {
        Fail "Khong tim thay Properties\AssemblyInfo.cs de doc version."
    }

    $text = Get-Content -Path $assemblyInfo.FullName -Raw -Encoding UTF8

    $patterns = @(
        'AssemblyInformationalVersion\("([^"]+)"\)',
        'AssemblyFileVersion\("([^"]+)"\)',
        'AssemblyVersion\("([^"]+)"\)'
    )

    foreach ($pattern in $patterns) {
        $match = [regex]::Match($text, $pattern)
        if ($match.Success) {
            $raw = $match.Groups[1].Value.Trim()
            $raw = $raw.Split("+")[0]
            $parts = $raw.Split(".")
            if ($parts.Length -ge 3) {
                return "$($parts[0]).$($parts[1]).$($parts[2])"
            }
        }
    }

    Fail "Khong doc duoc version tu AssemblyInfo.cs. Can co AssemblyInformationalVersion/AssemblyFileVersion/AssemblyVersion."
}

function Get-ReleaseNote($BuildToolsDir, $Version) {
    $notePath = Join-Path $BuildToolsDir "release-note.txt"

    if (-not (Test-Path $notePath)) {
        $defaultText = @"
SX3 SCANNER RELEASE NOTE

Version: $Version
Build time: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

Noi dung cap nhat:
- Cap nhat phan mem.

Huong dan cai dat:
1. Tat SX3 Scanner neu dang mo.
2. Chay file SX3ScannerSetup-$Version.exe.
3. Chon Next de cai dat.
"@
        New-Item -ItemType Directory -Force -Path $BuildToolsDir | Out-Null
        $defaultText | Out-File -FilePath $notePath -Encoding UTF8
        Ok "Da tao file release-note.txt: $notePath"
    }

    $noteText = Get-Content -Path $notePath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($noteText)) {
        Fail "BuildTools\release-note.txt dang rong. Hay nhap noi dung release note."
    }

    return @{
        Path = $notePath
        Text = $noteText.Trim()
    }
}

$ProjectRoot = Get-ProjectRoot
$BuildToolsDir = Join-Path $ProjectRoot "BuildTools"
$OutputDir = Join-Path $ProjectRoot "InstallerOutput"
$PackageDir = Join-Path $OutputDir "PackageFiles"
$BinRelease = Join-Path $ProjectRoot "bin\$Platform\$Configuration"
$ProjectFile = Join-Path $ProjectRoot "SX3 SCANER.csproj"

$Version = Read-VersionFromAssemblyInfo $ProjectRoot
$ReleaseNote = Get-ReleaseNote -BuildToolsDir $BuildToolsDir -Version $Version

$InstallerFileName = "SX3ScannerSetup-$Version.exe"
$InstallerFile = Join-Path $OutputDir $InstallerFileName
$ReleaseNoteOutputFile = Join-Path $OutputDir "release-note.txt"
$IssFile = Join-Path $OutputDir "SX3ScannerSetup.iss"

Info "====================================="
Info " SX3 SCANNER X64 BUILD + PACKAGE"
Info " Version from AssemblyInfo: $Version"
Info "====================================="
Info "ProjectRoot: $ProjectRoot"
Info "Release source: $BinRelease"
Info "Output: $OutputDir"

$MSBuild = Find-MSBuild
Ok "MSBuild: $MSBuild"

Info "`n[0/5] Build $Configuration|$Platform..."
& $MSBuild $ProjectFile /t:Rebuild /p:Configuration=$Configuration /p:Platform=$Platform /m
Assert-LastExitCode "MSBuild $Configuration|$Platform"

$Exe = Join-Path $BinRelease "SX3 SCANER.exe"
if (-not (Test-Path $Exe)) {
    Fail "Khong tim thay file EXE x64: $Exe."
}

$InnoCompiler = Find-InnoSetup
Ok "Inno Setup: $InnoCompiler"

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null

Info "`n[1/5] Copy bin\Release vao PackageFiles..."
Copy-Item -Path (Join-Path $BinRelease "*") -Destination $PackageDir -Recurse -Force

Info "`n[2/5] Copy release-note.txt..."
Copy-Item -Path $ReleaseNote.Path -Destination (Join-Path $PackageDir "release-note.txt") -Force
Copy-Item -Path $ReleaseNote.Path -Destination (Join-Path $PackageDir "RELEASE_NOTES.txt") -Force
Copy-Item -Path $ReleaseNote.Path -Destination $ReleaseNoteOutputFile -Force

Info "`n[3/4] Tao Inno Setup script..."
$IconLine = ""
$IconPath = Join-Path $ProjectRoot "scan.ico"
if (Test-Path $IconPath) {
    $IconLine = "SetupIconFile=$($IconPath.Replace('"', '""'))"
}

$EscapedOutputDir = $OutputDir.Replace('"', '""')
$EscapedPackageDir = $PackageDir.Replace('"', '""')

$IssContent = @"
#define MyAppName "SX3 Scanner"
#define MyAppVersion "$Version"
#define MyAppPublisher "JBZVN"
#define MyAppExeName "SX3 SCANER.exe"

[Setup]
AppId={{A6E812B1-9F30-4D3C-A9E2-0A0B0C0D0E0F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/hieuvipro94x/sx3-scanner-release
AppSupportURL=https://github.com/hieuvipro94x/sx3-scanner-release/issues
AppUpdatesURL=https://github.com/hieuvipro94x/sx3-scanner-release/releases
DefaultDirName={autopf}\JBZVN\SX3 Scanner
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
DefaultGroupName={#MyAppName}
OutputDir=$EscapedOutputDir
OutputBaseFilename=SX3ScannerSetup-$Version
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UsedUserAreasWarning=no
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
$IconLine

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\JBZVN\SX3 Scanner"; Permissions: users-modify
Name: "{commonappdata}\JBZVN\SX3 Scanner\config"; Permissions: users-modify
Name: "{commonappdata}\JBZVN\SX3 Scanner\cache\updates"; Permissions: users-modify

[InstallDelete]
Type: files; Name: "{userstartup}\SX3 SCANER.lnk"
Type: files; Name: "{commonstartup}\SX3 SCANER.lnk"

[Files]
Source: "$EscapedPackageDir\*"; DestDir: "{app}"; \
  Excludes: "database.db,product.db,*.db,*.db-wal,*.db-shm,*.db-journal"; \
  Flags: ignoreversion recursesubdirs createallsubdirs


[Icons]
Name: "{group}\SX3 Scanner"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Release note"; Filename: "{app}\release-note.txt"
Name: "{commondesktop}\SX3 Scanner"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\AnnouncementServer\SX3.AnnouncementServer.exe"; Flags: nowait skipifsilent
Filename: "{app}\{#MyAppExeName}"; Description: "Open SX3 Scanner"; Flags: nowait postinstall skipifsilent

[Code]
const
  AnnouncementShutdownEvent = 'Local\SX3_AnnouncementServer_Shutdown';
  EVENT_MODIFY_STATE = `$0002;

function OpenEvent(dwDesiredAccess: LongWord; bInheritHandle: Boolean;
  lpName: string): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

procedure StopAnnouncementServer;
var
  ShutdownEvent: THandle;
begin
  ShutdownEvent := OpenEvent(
    EVENT_MODIFY_STATE,
    False,
    AnnouncementShutdownEvent);
  if ShutdownEvent <> 0 then
  begin
    SetEvent(ShutdownEvent);
    CloseHandle(ShutdownEvent);
    Sleep(3000);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAnnouncementServer;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopAnnouncementServer;
end;
"@

$IssContent | Out-File -FilePath $IssFile -Encoding UTF8

Info "`n[4/4] Dong goi installer..."
& $InnoCompiler $IssFile
Assert-LastExitCode "Inno Setup build"

if (-not (Test-Path $InstallerFile)) {
    Fail "Khong tim thay installer: $InstallerFile"
}

$Sha256 = (Get-FileHash -Path $InstallerFile -Algorithm SHA256).Hash.ToLowerInvariant()

Ok "`n====================================="
Ok " DONG GOI THANH CONG"
Ok " Installer:"
Ok " $InstallerFile"
Ok " release-note.txt:"
Ok " $ReleaseNoteOutputFile"
Ok ""
Ok " SHA256:"
Ok " $Sha256"
Ok ""
Ok " Tao GitHub Release tag v$Version va upload asset:"
Ok " - $InstallerFileName"
Ok " Repository: https://github.com/hieuvipro94x/sx3-scanner-release/releases"
Ok "====================================="
