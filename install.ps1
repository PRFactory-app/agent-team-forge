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
$explicitVersion = [bool]($Version -or $Archive)
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Fail([string]$Message) { throw "atf installer: $Message" }
function Invoke-Atf([string]$Executable, [string[]]$Arguments) {
    try {
        & $Executable @Arguments
        $script:LASTEXITCODE = $LASTEXITCODE
    } catch {
        Fail "could not start '$Executable': $($_.Exception.Message). Windows security may have blocked it. For Defender ASR, allow the install folder '$root\' (%USERPROFILE%\.local\share\agentteamforge\) in Windows Security and rerun. Smart App Control has no path allow: it must be off or the binary signed; retrying alone will not fix a SAC block."
    }
}
function Compare-VersionPart([string]$Left, [string]$Right) {
    $leftNumber = $Left -match '^[0-9]+$'
    $rightNumber = $Right -match '^[0-9]+$'
    if ($leftNumber -and $rightNumber) { return ([decimal]$Left).CompareTo([decimal]$Right) }
    if ($leftNumber) { return -1 }
    if ($rightNumber) { return 1 }
    # Tags are named rc1..rcN, so compare a shared prefix numerically (rc10 after rc9).
    $leftParts = [regex]::Match($Left, '^(.*[^0-9])([0-9]+)$')
    $rightParts = [regex]::Match($Right, '^(.*[^0-9])([0-9]+)$')
    if ($leftParts.Success -and $rightParts.Success -and $leftParts.Groups[1].Value -ceq $rightParts.Groups[1].Value) {
        return ([decimal]$leftParts.Groups[2].Value).CompareTo([decimal]$rightParts.Groups[2].Value)
    }
    return [Math]::Sign([string]::CompareOrdinal($Left, $Right))
}
function Compare-VersionList([string]$Left, [string]$Right) {
    $leftItems = $Left.Split('.')
    $rightItems = $Right.Split('.')
    for ($index = 0; $index -lt [Math]::Min($leftItems.Length, $rightItems.Length); $index++) {
        $result = Compare-VersionPart $leftItems[$index] $rightItems[$index]
        if ($result -ne 0) { return $result }
    }
    return [Math]::Sign($leftItems.Length - $rightItems.Length)
}
# SemVer precedence: true when $Older sorts before $Newer (build metadata ignored).
function Test-VersionOlder([string]$Older, [string]$Newer) {
    $first = $Older.Split('+')[0]
    $second = $Newer.Split('+')[0]
    $firstCore = $first; $firstPre = ''
    $dash = $first.IndexOf('-')
    if ($dash -ge 0) { $firstCore = $first.Substring(0, $dash); $firstPre = $first.Substring($dash + 1) }
    $secondCore = $second; $secondPre = ''
    $dash = $second.IndexOf('-')
    if ($dash -ge 0) { $secondCore = $second.Substring(0, $dash); $secondPre = $second.Substring($dash + 1) }
    $result = Compare-VersionList $firstCore $secondCore
    if ($result -eq 0) {
        if ($firstPre -eq '' -and $secondPre -eq '') { $result = 0 }
        elseif ($firstPre -eq '') { $result = 1 }
        elseif ($secondPre -eq '') { $result = -1 }
        else { $result = Compare-VersionList $firstPre $secondPre }
    }
    return $result -lt 0
}
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

$backup = Join-Path $root 'bin.previous'
New-Item -ItemType Directory -Force -Path $root | Out-Null
try { $installLock = [IO.File]::Open((Join-Path $root 'install.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
catch [IO.IOException] { Fail "another installer is running (cannot acquire $root\install.lock): $($_.Exception.Message)" }
try {
if (-not (Test-Path $bin) -and (Test-Path $backup)) {
    if ((Test-Path (Join-Path $backup 'atf.exe') -PathType Leaf) -and
        (Test-Path (Join-Path $backup '.atf-version') -PathType Leaf)) {
        Move-Item -LiteralPath $backup -Destination $bin
    } elseif (-not $Uninstall) { Fail "incomplete backup at $backup; installation left unchanged" }
}

if ($Uninstall) {
    if ($Version -or $Archive -or $Checksum -or $ReleaseUrl) { Fail 'uninstall cannot be combined with download options' }
    if ($Purge -and (-not (Test-Path (Join-Path $StateDir 'profile.json') -PathType Leaf) -or
        -not (Test-Path (Join-Path $StateDir 'operator.key') -PathType Leaf))) { Fail 'state directory has no ATF profile and key' }
    if (Test-Path $binary -PathType Leaf) {
        if (-not (Test-Path (Join-Path $bin '.atf-version') -PathType Leaf)) { Fail "refusing unmanaged $bin" }
        if (Test-Path $StateDir -PathType Container) {
            Invoke-Atf $binary @('stop', '--state-dir', $StateDir)
            if ($LASTEXITCODE -ne 0) { Fail 'daemon did not stop; installation left unchanged' }
        }
        if ($Force) { Invoke-Atf $binary @('uninstall', '--teardown-only', '--state-dir', $StateDir, '--force') }
        else { Invoke-Atf $binary @('uninstall', '--teardown-only', '--state-dir', $StateDir) }
        if ($LASTEXITCODE -ne 0) { Fail 'client teardown failed; installation left unchanged' }
        # Teardown asks claude for its registration; that health check runs `atf mcp`, which starts the daemon again.
        if (Test-Path $StateDir -PathType Container) {
            Invoke-Atf $binary @('stop', '--state-dir', $StateDir) | Out-Null
            if ($LASTEXITCODE -ne 0) { Fail 'daemon restarted during client teardown and did not stop; binaries left in place' }
        }
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
    foreach ($leftover in @((Join-Path $root 'bin.staging'), $backup)) {
        if (Test-Path $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force }
    }
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
# Rerunning the one-liner resolves "latest", which can be older than an installed prerelease.
$activeVersionFile = Join-Path $bin '.atf-version'
if (-not $explicitVersion -and (Test-Path $activeVersionFile -PathType Leaf)) {
    $activeVersion = (Get-Content $activeVersionFile -Raw).Trim()
    if ($activeVersion -ne $Version -and (Test-VersionOlder $Version $activeVersion)) {
        Write-Output "atf $activeVersion is active and newer than the latest release $Version; nothing changed."
        Write-Output "To switch to $Version anyway, rerun with -Version $Version."
        return
    }
}
$name = "atf-$Version-$rid.zip"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $scratch | Out-Null
$stage = $null
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
        if ((Invoke-Atf (Join-Path $target 'atf.exe') @('--version')) -ne "atf $Version") { Fail 'installed version failed verification' }
    } else {
        New-Item -ItemType Directory -Force -Path $releases | Out-Null
        # Extract beside the target: Move-Item cannot move directories across volumes.
        $extract = Join-Path $releases "$Version.staging"
        if (Test-Path $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
        Expand-Archive -LiteralPath $Archive -DestinationPath $extract
        if (-not (Test-Path (Join-Path $extract 'atf.exe') -PathType Leaf)) { Fail 'archive missing atf.exe' }
        $extractedBinary = Join-Path $extract 'atf.exe'
        $extractedVersion = Invoke-Atf $extractedBinary @('--version')
        if ($extractedVersion -ne "atf $Version") { Fail 'archive version mismatch' }
        Move-Item $extract $target
    }
    if (Test-Path $bin) {
        if (-not (Test-Path (Join-Path $bin '.atf-version') -PathType Leaf)) { Fail "refusing unmanaged $bin" }
        if ((Get-Content (Join-Path $bin '.atf-version') -Raw).Trim() -eq $Version) {
            if ((Invoke-Atf $binary @('--version')) -ne "atf $Version") { Fail 'installed binary failed verification' }
            Ensure-Path $bin
            Write-Output "atf $Version already installed: $binary"
            return
        }
    }
    $stage = Join-Path $root 'bin.staging'
    if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if ((Test-Path $bin) -and (Test-Path $backup)) {
        try { Remove-Item -LiteralPath $backup -Recurse -Force }
        catch { Fail "cannot remove old binary copy at $backup; close processes using that folder and rerun: $($_.Exception.Message)" }
    }
    Copy-Item $target $stage -Recurse
    Set-Content (Join-Path $stage '.atf-version') $Version -Encoding ascii
    if (Test-Path $bin) {
        if (Test-Path $StateDir -PathType Container) {
            Invoke-Atf $binary @('stop', '--state-dir', $StateDir)
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
    if ($stage -and (Test-Path $stage)) { Remove-Item $stage -Recurse -Force }
    if ($extract -and (Test-Path $extract)) { Remove-Item $extract -Recurse -Force }
}
} finally {
    $installLock.Dispose()
}
