# User-scope Windows installer for the Native AOT zip. Compatible with Windows PowerShell 5.1.
param(
    [string]$Version,
    [string]$Archive,
    [string]$Checksum,
    [string]$ReleaseUrl,
    [string]$StateDir,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$Force,
    [int]$ParentPid = 0
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Fail([string]$Message) { throw "atf installer: $Message" }
function Download([string]$Url, [string]$Destination, [string]$Name) {
    try { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Destination | Out-Null }
    catch { Fail "could not download $Name from $Url`: $($_.Exception.Message)" }
}
function Ensure-Path([string]$Directory) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $userPath) { $userPath = '' }
    if (@($userPath.Split(';') | Where-Object { $_.TrimEnd('\') -ieq $Directory.TrimEnd('\') }).Count -eq 0) {
        [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $Directory).TrimStart(';'), 'User')
    }
    if (@($env:Path.Split(';') | Where-Object { $_.TrimEnd('\') -ieq $Directory.TrimEnd('\') }).Count -eq 0) { $env:Path += ";$Directory" }
}

$homeDir = [Environment]::GetFolderPath('UserProfile')
if (-not $homeDir) { Fail 'user profile directory is unavailable' }
$root = Join-Path $homeDir '.local\share\agentteamforge'
$releases = Join-Path $root 'releases'
$bin = Join-Path $root 'bin'
$binary = Join-Path $bin 'atf.exe'
if (-not $StateDir) {
    $stateBase = if ($env:XDG_STATE_HOME) { $env:XDG_STATE_HOME } else { Join-Path $homeDir '.local\state' }
    $StateDir = Join-Path $stateBase 'agentteamforge'
}
if (-not [IO.Path]::IsPathRooted($StateDir)) { Fail 'state directory must be absolute' }

if ($Uninstall) {
    if ($Version -or $Archive -or $Checksum -or $ReleaseUrl) { Fail 'uninstall cannot be combined with download options' }
    if ($Purge -and (-not (Test-Path (Join-Path $StateDir 'profile.json') -PathType Leaf) -or
        -not (Test-Path (Join-Path $StateDir 'operator.key') -PathType Leaf))) { Fail 'state directory has no ATF profile and key' }
    if (Test-Path $binary -PathType Leaf) {
        if (-not (Test-Path (Join-Path $bin '.atf-version') -PathType Leaf)) { Fail "refusing unmanaged $bin" }
        if (Test-Path $StateDir -PathType Container) {
            & $binary stop --state-dir $StateDir
            if ($LASTEXITCODE -ne 0) { Fail 'daemon did not stop; installation left unchanged' }
        }
        if ($Force) { & $binary uninstall --teardown-only --state-dir $StateDir --force }
        else { & $binary uninstall --teardown-only --state-dir $StateDir }
        if ($LASTEXITCODE -ne 0) { Fail 'client teardown failed; installation left unchanged' }
    }
    if ($ParentPid -gt 0) {
        if (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) {
            try { Wait-Process -Id $ParentPid -Timeout 30 -ErrorAction Stop }
            catch {
                if (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) {
                    Fail 'parent atf process did not exit; installation left unchanged'
                }
            }
        }
    }
    if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
    if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath) {
        $kept = @($userPath.Split(';') | Where-Object { $_.TrimEnd('\') -ine $bin.TrimEnd('\') })
        [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
    }
    if ($Purge) { Remove-Item $StateDir -Recurse -Force; Write-Output "purged state: $StateDir" }
    else { Write-Output "state kept: $StateDir" }
    Write-Output 'atf uninstalled'
    return
}
if ($Purge -or $Force -or $ParentPid -gt 0) { Fail 'purge, force and parent PID are only valid with uninstall' }

$architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
switch ($architecture) {
    'AMD64' { $rid = 'win-x64' }
    'ARM64' { $rid = 'win-arm64' }
    default { Fail "unsupported Windows architecture: $architecture" }
}
$sumsName = "SHA256SUMS-$rid"
$repo = if ($env:ATF_RELEASES_URL) { $env:ATF_RELEASES_URL.TrimEnd('/') } else { 'https://github.com/PRFactory-app/agent-team-forge/releases' }

if ($Version) { $Version = $Version.TrimStart('v') }
if ($Archive) {
    if (-not (Test-Path $Archive -PathType Leaf)) { Fail "archive not found: $Archive" }
    $Archive = (Resolve-Path $Archive).Path
    if (-not $Version) {
        $archiveName = [IO.Path]::GetFileName($Archive)
        if ($archiveName -notmatch "^atf-(.+)-$rid\.zip$") { Fail 'use -Version with this archive name' }
        $Version = $Matches[1]
    }
    if (-not $Checksum) { $Checksum = Join-Path (Split-Path $Archive -Parent) $sumsName }
    if (-not (Test-Path $Checksum -PathType Leaf)) { Fail "checksum not found: $Checksum" }
} else {
    if (-not $Version) {
        try { $latest = Invoke-WebRequest -UseBasicParsing -Uri "$repo/latest" -MaximumRedirection 5 }
        catch { Fail "could not resolve latest release: $($_.Exception.Message)" }
        # Windows PowerShell exposes ResponseUri; PowerShell 7 exposes RequestMessage.
        $response = $latest.BaseResponse
        $latestUrl = if ($response.ResponseUri) { $response.ResponseUri.AbsoluteUri } else { $response.RequestMessage.RequestUri.AbsoluteUri }
        if ($latestUrl -notmatch '/tag/v([0-9][0-9A-Za-z.+-]*)$') { Fail 'no release is published yet' }
        $Version = $Matches[1]
    }
    if (-not $ReleaseUrl) { $ReleaseUrl = "$repo/download/v$Version" }
}
if ($Version -notmatch '^[0-9][0-9A-Za-z.+-]*$') { Fail 'invalid version' }
$name = "atf-$Version-$rid.zip"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $scratch | Out-Null
$stage = Join-Path $root ([IO.Path]::GetRandomFileName())
$backup = Join-Path $root ([IO.Path]::GetRandomFileName())
$extract = $null
try {
    if (-not $Archive) {
        $Archive = Join-Path $scratch $name
        $Checksum = Join-Path $scratch $sumsName
        Download "$ReleaseUrl/$name" $Archive $name
        Download "$ReleaseUrl/$sumsName" $Checksum $sumsName
    }
    $entry = Get-Content $Checksum | Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+$([regex]::Escape($name))$" } | Select-Object -First 1
    if (-not $entry) { Fail "checksum entry missing for $name" }
    $expected = [regex]::Match($entry, '^([0-9a-fA-F]{64})').Groups[1].Value
    $actual = (Get-FileHash -Algorithm SHA256 -Path $Archive).Hash
    if ($actual -ne $expected) { Fail 'archive checksum mismatch' }

    $target = Join-Path $releases $Version
    if (Test-Path $target) {
        if (-not (Test-Path (Join-Path $target 'atf.exe') -PathType Leaf)) { Fail "version directory is unmanaged: $target" }
        if ((& (Join-Path $target 'atf.exe') --version) -ne "atf $Version") { Fail 'installed version failed verification' }
    } else {
        New-Item -ItemType Directory -Force -Path $releases | Out-Null
        # Extract beside the target: Move-Item cannot move directories across volumes.
        $extract = Join-Path $root ([IO.Path]::GetRandomFileName())
        Expand-Archive -LiteralPath $Archive -DestinationPath $extract
        if (-not (Test-Path (Join-Path $extract 'atf.exe') -PathType Leaf)) { Fail 'archive missing atf.exe' }
        if ((& (Join-Path $extract 'atf.exe') --version) -ne "atf $Version") { Fail 'archive version mismatch' }
        Move-Item $extract $target
    }
    if (Test-Path $bin) {
        if (-not (Test-Path (Join-Path $bin '.atf-version') -PathType Leaf)) { Fail "refusing unmanaged $bin" }
        if ((Get-Content (Join-Path $bin '.atf-version') -Raw).Trim() -eq $Version) {
            if ((& $binary --version) -ne "atf $Version") { Fail 'installed binary failed verification' }
            Ensure-Path $bin
            Write-Output "atf $Version already installed: $binary"
            return
        }
    }
    Copy-Item $target $stage -Recurse
    Set-Content (Join-Path $stage '.atf-version') $Version -Encoding ascii
    if (Test-Path $bin) {
        if (Test-Path $StateDir -PathType Container) {
            & $binary stop --state-dir $StateDir
            if ($LASTEXITCODE -ne 0) { Fail 'daemon did not stop; installation left unchanged' }
        }
        try { Move-Item $bin $backup }
        catch { Fail "cannot replace $bin; close agent clients using atf (MCP bridges) and rerun: $($_.Exception.Message)" }
    }
    try { Move-Item $stage $bin }
    catch {
        if (Test-Path $backup) { Move-Item $backup $bin }
        throw
    }
    if (Test-Path $backup) {
        try { Remove-Item $backup -Recurse -Force }
        catch { Write-Warning "old binary copy kept at $backup`: $($_.Exception.Message)" }
    }
    Ensure-Path $bin
    Write-Output "installed atf $Version`: $binary"
    Write-Output 'Run: atf setup --mode wt'
    Write-Output 'Open a new terminal if atf is not yet on PATH; reload agent clients after setup.'
} finally {
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if ($extract -and (Test-Path $extract)) { Remove-Item $extract -Recurse -Force }
}
