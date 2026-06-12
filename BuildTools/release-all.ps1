param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$Repository = "hieuvipro94x/sx3-scanner-release"
)

$ErrorActionPreference = "Stop"

function Info($Text) { Write-Host $Text -ForegroundColor Cyan }
function Ok($Text) { Write-Host $Text -ForegroundColor Green }
function Warn($Text) { Write-Host $Text -ForegroundColor Yellow }
function Fail($Text) { Write-Host $Text -ForegroundColor Red; throw $Text }
function Write-Step($Text) { Info "`n$Text" }

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
        if (Test-Path -LiteralPath $p) { return $p }
    }

    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    Fail "Khong tim thay Inno Setup 6. Hay cai Inno Setup 6 truoc."
}

function Find-MSBuild {
    $paths = @(
        "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($p in $paths) {
        if (Test-Path -LiteralPath $p) { return $p }
    }

    $cmd = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    Fail "Khong tim thay MSBuild. Hay cai Visual Studio/Build Tools voi .NET desktop build tools."
}

function Find-GitHubCLI {
    $cmd = Get-Command "gh.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $paths = @(
        (Join-Path $env:ProgramFiles "GitHub CLI\gh.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "GitHub CLI\gh.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\GitHub CLI\gh.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\gh.exe")
    )

    foreach ($p in $paths) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }

    Fail "Khong tim thay GitHub CLI (gh.exe). Hay cai GitHub CLI truoc."
}

function Find-Git {
    $cmd = Get-Command "git.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    Fail "Khong tim thay Git. Hay cai Git truoc."
}

function Get-ProjectRoot {
    $scriptDir = $PSScriptRoot

    if ((Split-Path -Leaf $scriptDir) -ieq "kịch bản") {
        $scriptDir = Split-Path -Parent $scriptDir
    }

    if ((Split-Path -Leaf $scriptDir) -ieq "BuildTools") {
        return Split-Path -Parent $scriptDir
    }

    return $scriptDir
}

function Get-GitRoot($ProjectRoot, $GitExe) {
    $gitRoot = & $GitExe -C $ProjectRoot rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot)) {
        Fail "Thu muc project khong phai Git repository: $ProjectRoot"
    }

    return $gitRoot.Trim()
}

function Read-VersionFromAssemblyInfo($ProjectRoot) {
    $assemblyInfo = Get-ChildItem -Path $ProjectRoot -Recurse -File -Filter "AssemblyInfo.cs" |
        Where-Object { $_.FullName -notmatch "\\bin\\|\\obj\\|\\InstallerOutput\\|\\ReleasePackages\\|\\ReleaseOutput\\" } |
        Select-Object -First 1

    if (-not $assemblyInfo) {
        Fail "Khong tim thay Properties\AssemblyInfo.cs de doc version."
    }

    $text = Get-Content -LiteralPath $assemblyInfo.FullName -Raw -Encoding UTF8

    $patterns = @(
        'AssemblyInformationalVersion\("([^"+]+)(?:\+[^"+]+)?"\)',
        'AssemblyFileVersion\("([^"]+)"\)',
        'AssemblyVersion\("([^"]+)"\)'
    )

    foreach ($pattern in $patterns) {
        $match = [regex]::Match($text, $pattern)
        if ($match.Success) {
            $raw = $match.Groups[1].Value.Trim()
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

    if (-not (Test-Path -LiteralPath $notePath)) {
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
        $defaultText | Out-File -LiteralPath $notePath -Encoding UTF8
        Ok "Da tao file release-note.txt: $notePath"
    }

    $noteText = Get-Content -LiteralPath $notePath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($noteText)) {
        Fail "BuildTools\release-note.txt dang rong. Hay nhap noi dung release note."
    }

    return @{
        Path = $notePath
        Text = $noteText.Trim()
    }
}

function Test-GitHubReleaseExists($GitHubCLI, $Repository, $Tag) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $GitHubCLI release view $Tag --repo $Repository *> $null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return $exitCode -eq 0
}

function Test-RemoteTagExists($GitExe, $GitRoot, $Tag) {
    $output = & $GitExe -C $GitRoot ls-remote --tags origin "refs/tags/$Tag" 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        Fail "Khong kiem tra duoc remote tag $Tag. Chi tiet: $($output | Out-String)"
    }

    return -not [string]::IsNullOrWhiteSpace(($output | Out-String))
}

function New-ReleaseManifest($InstallerFile, $ManifestFile, $Version, $Repository) {
    $sha256 = (Get-FileHash -LiteralPath $InstallerFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerName = Split-Path -Leaf $InstallerFile

    $manifest = @"
SX3 Scanner Release Manifest
Version: $Version
Repository: $Repository
Installer: $installerName
SHA256: $sha256
Build time: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

    $manifest | Out-File -LiteralPath $ManifestFile -Encoding UTF8
    return $sha256
}

function Publish-GitHubDraftRelease(
    $GitRoot,
    $GitExe,
    $GitHubCLI,
    $Repository,
    $Version,
    $InstallerFile,
    $ReleaseNoteFile,
    $ManifestFile
) {
    $tag = "v$Version"
    $title = "SX3 Scanner $Version"

    Write-Step "[5/9] Kiem tra release/tag da ton tai..."

    if (Test-GitHubReleaseExists -GitHubCLI $GitHubCLI -Repository $Repository -Tag $tag) {
        Fail "Release $tag da ton tai. Hay tang version trong app."
    }

    if (Test-RemoteTagExists -GitExe $GitExe -GitRoot $GitRoot -Tag $tag) {
        Fail "Git tag $tag da ton tai tren origin. Script KHONG ghi de tag cu. Hay tang version app truoc khi dong goi."
    }

    & $GitExe -C $GitRoot show-ref --verify --quiet "refs/tags/$tag"
    $localTagExitCode = $LASTEXITCODE
    if ($localTagExitCode -eq 0) {
        Fail "Local tag $tag da ton tai. Script KHONG xoa tag cu. Hay tang version app hoac xoa tag local thu cong neu chac chan."
    }
    elseif ($localTagExitCode -ne 1) {
        Fail "Khong kiem tra duoc local tag $tag. ExitCode=$localTagExitCode"
    }

    Write-Step "[6/9] Commit source code neu co thay doi..."
    & $GitExe -C $GitRoot add .
    Assert-LastExitCode "git add"

    & $GitExe -C $GitRoot diff --cached --quiet
    $diffExitCode = $LASTEXITCODE
    if ($diffExitCode -eq 0) {
        Warn "Khong co thay doi de commit. Tiep tuc tao draft release."
    }
    elseif ($diffExitCode -eq 1) {
        & $GitExe -C $GitRoot commit -m "Release $tag"
        Assert-LastExitCode "git commit"
    }
    else {
        Fail "Khong kiem tra duoc thay doi Git. ExitCode=$diffExitCode"
    }

    Write-Step "[7/9] Push source code..."
    & $GitExe -C $GitRoot push
    Assert-LastExitCode "git push"

    Write-Step "[8/9] Tao tag moi $tag..."
    & $GitExe -C $GitRoot tag -a $tag -m "Release $tag"
    Assert-LastExitCode "git tag -a $tag"

    & $GitExe -C $GitRoot push origin $tag
    Assert-LastExitCode "git push origin $tag"

    Write-Step "[9/9] Tao GitHub Draft Release va upload assets..."
    & $GitHubCLI release create $tag `
        $InstallerFile `
        $ReleaseNoteFile `
        $ManifestFile `
        --repo $Repository `
        --title $title `
        --notes-file $ReleaseNoteFile `
        --draft
    Assert-LastExitCode "gh release create $tag --draft"
}

$ProjectRoot = Get-ProjectRoot
$BuildToolsDir = Join-Path $ProjectRoot "BuildTools"
$ReleaseOutputDir = Join-Path $ProjectRoot "ReleaseOutput"
$OutputDir = Join-Path $ProjectRoot "InstallerOutput"
$PackageDir = Join-Path $OutputDir "PackageFiles"
$BinRelease = Join-Path $ProjectRoot "bin\$Platform\$Configuration"
$ProjectFile = Join-Path $ProjectRoot "SX3 SCANER.csproj"
$AnnouncementServerProject = Join-Path $ProjectRoot "AnnouncementServer\SX3.AnnouncementServer.csproj"
$AnnouncementServerPublishDir = Join-Path $ProjectRoot "AnnouncementServer\publish"

$Version = Read-VersionFromAssemblyInfo $ProjectRoot
$ReleaseNote = Get-ReleaseNote -BuildToolsDir $BuildToolsDir -Version $Version

$InstallerFileName = "SX3ScannerSetup-$Version.exe"
$InstallerFile = Join-Path $OutputDir $InstallerFileName
$ReleaseNoteOutputFile = Join-Path $OutputDir "release-note.txt"
$ManifestFile = Join-Path $OutputDir "release-manifest.txt"
$IssFile = Join-Path $OutputDir "SX3ScannerSetup.iss"
$ReleaseUrl = "https://github.com/$Repository/releases/tag/v$Version"

$GitExe = Find-Git
$GitHubCLI = Find-GitHubCLI
$MSBuild = Find-MSBuild
$InnoCompiler = Find-InnoSetup

Ok "Git: $GitExe"
Ok "GitHub CLI: $GitHubCLI"
Ok "MSBuild: $MSBuild"
Ok "Inno Setup: $InnoCompiler"

Info "Kiem tra dang nhap GitHub CLI..."
& $GitHubCLI auth status
if ($LASTEXITCODE -ne 0) {
    Fail "Chua dang nhap GitHub CLI. Hay chay: gh auth login"
}

$GitRoot = Get-GitRoot -ProjectRoot $ProjectRoot -GitExe $GitExe
Ok "Git repository: $GitRoot"

Info "====================================="
Info " SX3 SCANNER X64 BUILD + PACKAGE"
Info " Version from AssemblyInfo: $Version"
Info " GitHub release mode: DRAFT, no delete, no force"
Info "====================================="
Info "ProjectRoot: $ProjectRoot"
Info "Release source: $BinRelease"
Info "Output: $OutputDir"
Info "Repository: $Repository"

Info "`nDon artifact cu truoc khi build..."
foreach ($dir in @($ReleaseOutputDir, $OutputDir)) {
    if (Test-Path -LiteralPath $dir) {
        $resolvedDir = [IO.Path]::GetFullPath($dir)
        $resolvedProjectRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
        if (-not $resolvedDir.StartsWith($resolvedProjectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Tu choi xoa thu muc ngoai project: $resolvedDir"
        }
        Remove-Item -LiteralPath $resolvedDir -Recurse -Force
    }
}

if (Test-Path -LiteralPath $AnnouncementServerPublishDir) {
    $resolvedPublishDir = [IO.Path]::GetFullPath($AnnouncementServerPublishDir)
    $resolvedProjectRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
    if (-not $resolvedPublishDir.StartsWith($resolvedProjectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Fail "Tu choi xoa thu muc publish ngoai project: $resolvedPublishDir"
    }
    Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
}

if (Test-Path -LiteralPath $BinRelease) {
    Get-ChildItem -LiteralPath $BinRelease -Filter "SX3.AnnouncementServer.*" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

Write-Step "[1/9] Build $Configuration|$Platform..."
& $MSBuild $ProjectFile /t:Rebuild /p:Configuration=$Configuration /p:Platform=$Platform /m
Assert-LastExitCode "MSBuild $Configuration|$Platform"

Info "Publish Announcement Server..."
& dotnet publish $AnnouncementServerProject `
    --configuration $Configuration `
    --framework net8.0 `
    --runtime win-x64 `
    --output $AnnouncementServerPublishDir `
    --self-contained true
Assert-LastExitCode "dotnet publish Announcement Server"

$PublishedServerExe = Join-Path $AnnouncementServerPublishDir "AnnouncementServer.exe"
$PublishedServerConfig = Join-Path $AnnouncementServerPublishDir "appsettings.json"
if (-not (Test-Path -LiteralPath $PublishedServerExe)) {
    Fail "Publish Announcement Server thieu EXE: $PublishedServerExe"
}
if (-not (Test-Path -LiteralPath $PublishedServerConfig)) {
    Fail "Publish Announcement Server thieu appsettings.json: $PublishedServerConfig"
}

$Exe = Join-Path $BinRelease "SX3 SCANER.exe"
if (-not (Test-Path -LiteralPath $Exe)) {
    Fail "Khong tim thay file EXE x64: $Exe."
}

$OkSound = Join-Path $BinRelease "Sounds\OK.wav"
$NgSound = Join-Path $BinRelease "Sounds\NG.wav"
if (-not (Test-Path -LiteralPath $OkSound)) {
    Fail "Build thieu am thanh scan OK: $OkSound"
}
if (-not (Test-Path -LiteralPath $NgSound)) {
    Fail "Build thieu am thanh scan NG: $NgSound"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null

Write-Step "[2/9] Copy bin\$Platform\$Configuration vao PackageFiles..."
Copy-Item -Path (Join-Path $BinRelease "*") -Destination $PackageDir -Recurse -Force

$PackagedServerDir = Join-Path $PackageDir "AnnouncementServer"
if (Test-Path -LiteralPath $PackagedServerDir) {
    Remove-Item -LiteralPath $PackagedServerDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PackagedServerDir | Out-Null
Copy-Item -Path (Join-Path $AnnouncementServerPublishDir "*") `
    -Destination $PackagedServerDir `
    -Recurse `
    -Force

$PackagedServer = Join-Path $PackagedServerDir "AnnouncementServer.exe"
$PackagedServerConfig = Join-Path $PackagedServerDir "appsettings.json"
$UnexpectedRootServer = Join-Path $PackageDir "AnnouncementServer.exe"
if (-not (Test-Path -LiteralPath $PackagedServer)) {
    Fail "Khong tim thay Announcement Server trong package: $PackagedServer"
}
if (-not (Test-Path -LiteralPath $PackagedServerConfig)) {
    Fail "Package Announcement Server thieu appsettings.json: $PackagedServerConfig"
}
if (Test-Path -LiteralPath $UnexpectedRootServer) {
    Fail "Package khong hop le: Announcement Server khong duoc nam o root."
}

$PackagedOkSound = Join-Path $PackageDir "Sounds\OK.wav"
$PackagedNgSound = Join-Path $PackageDir "Sounds\NG.wav"
if (-not (Test-Path -LiteralPath $PackagedOkSound)) {
    Fail "Package thieu am thanh scan OK: $PackagedOkSound"
}
if (-not (Test-Path -LiteralPath $PackagedNgSound)) {
    Fail "Package thieu am thanh scan NG: $PackagedNgSound"
}

Write-Step "[3/9] Copy release-note.txt..."
Copy-Item -LiteralPath $ReleaseNote.Path -Destination (Join-Path $PackageDir "release-note.txt") -Force
Copy-Item -LiteralPath $ReleaseNote.Path -Destination (Join-Path $PackageDir "RELEASE_NOTES.txt") -Force
Copy-Item -LiteralPath $ReleaseNote.Path -Destination $ReleaseNoteOutputFile -Force

Write-Step "[4/9] Tao Inno Setup script va dong goi installer..."
$IconLine = ""
$IconPath = Join-Path $ProjectRoot "scan.ico"
if (Test-Path -LiteralPath $IconPath) {
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
AppPublisherURL=https://github.com/$Repository
AppSupportURL=https://github.com/$Repository/issues
AppUpdatesURL=https://github.com/$Repository/releases
DefaultDirName={autopf}\JBZVN\SX3 Scanner
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=$EscapedOutputDir
OutputBaseFilename=SX3ScannerSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UsedUserAreasWarning=no
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCopyright=Copyright © 2026 NAM
$IconLine

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\JBZVN\SX3 Scanner"; Permissions: users-modify
Name: "{commonappdata}\JBZVN\SX3 Scanner\config"; Permissions: users-modify
Name: "{commonappdata}\JBZVN\SX3 Scanner\cache\updates"; Permissions: users-modify

[InstallDelete]
Type: files; Name: "{userstartup}\SX3 SCANER.lnk"
Type: files; Name: "{commonstartup}\SX3 SCANER.lnk"

[Files]
Source: "$EscapedPackageDir\*"; DestDir: "{app}"; Excludes: "database.db,product.db,database.db-wal,database.db-shm,product.db-wal,product.db-shm,database.db-journal,product.db-journal"; Flags: ignoreversion
Source: "$EscapedPackageDir\x64\*"; DestDir: "{app}\x64"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "$EscapedPackageDir\x86\*"; DestDir: "{app}\x86"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "$EscapedPackageDir\AnnouncementServer\*"; DestDir: "{app}\AnnouncementServer"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "$EscapedPackageDir\Sounds\*.wav"; DestDir: "{app}\Sounds"; Flags: ignoreversion

[Icons]
Name: "{group}\SX3 Scanner"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Release note"; Filename: "{app}\release-note.txt"
Name: "{commondesktop}\SX3 Scanner"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /F /TN ""SX3 Scanner"" /TR ""{app}\{#MyAppExeName}"" /SC ONLOGON /RL HIGHEST"; Flags: runhidden waituntilterminated
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
  ShutdownEvent := OpenEvent(EVENT_MODIFY_STATE, False, AnnouncementShutdownEvent);
  if ShutdownEvent <> 0 then
  begin
    SetEvent(ShutdownEvent);
    CloseHandle(ShutdownEvent);
    Sleep(3000);
  end;
end;

procedure KillProcessByName(FileName: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "' + FileName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  KillProcessByName('SX3 SCANER.exe');
  KillProcessByName('AnnouncementServer.exe');
  Sleep(2000);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAnnouncementServer;
  KillProcessByName('SX3 SCANER.exe');
  KillProcessByName('AnnouncementServer.exe');
  Sleep(2000);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopAnnouncementServer;
end;
"@

$IssContent | Out-File -LiteralPath $IssFile -Encoding UTF8

& $InnoCompiler $IssFile
Assert-LastExitCode "Inno Setup build"

if (-not (Test-Path -LiteralPath $InstallerFile)) {
    Fail "Khong tim thay installer: $InstallerFile"
}

$Sha256 = New-ReleaseManifest -InstallerFile $InstallerFile -ManifestFile $ManifestFile -Version $Version -Repository $Repository

Publish-GitHubDraftRelease `
    -GitRoot $GitRoot `
    -GitExe $GitExe `
    -GitHubCLI $GitHubCLI `
    -Repository $Repository `
    -Version $Version `
    -InstallerFile $InstallerFile `
    -ReleaseNoteFile $ReleaseNoteOutputFile `
    -ManifestFile $ManifestFile

Ok "`n====================================="
Ok "DRAFT RELEASE DA DUOC TAO THANH CONG"
Ok "Version: $Version"
Ok "Release URL: $ReleaseUrl"
Ok "Assets:"
Ok "- $InstallerFileName"
Ok "- release-note.txt"
Ok "- release-manifest.txt"
Ok "SHA256: $Sha256"
Ok "====================================="
