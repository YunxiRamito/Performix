#Requires -Version 5.1
<#
.SYNOPSIS
    One command per release: build, manifest, tag, GitHub Release, commit, read back.

.DESCRIPTION
    Follows docs\RELEASE.md in order. Section numbers below are that file's.

        1. scripts\build-app-zip.ps1     -> artifacts\Performix-win-x64.zip
           scripts\build-installer.ps1   -> artifacts\PerformixSetup.exe   (section 2)
        2. sha256 + size of the zip                                        (section 3)
        3. artifacts\update.json, schema 1, UTF-8 without BOM              (section 4)
        4. git tag + push, then gh release create --prerelease with the zip,
           the installer and the manifest                                  (section 5)
        5. reconcile: download the published asset and, when it is not the file this run built
           (a tag push also runs the release workflow, which rebuilds and --clobber-uploads -
           CI builds are not bit-for-bit identical to a workstation build, docs\RELEASE.md
           section 2.1), rewrite the manifest from the PUBLISHED bytes and re-upload it
                                                                           (section 5.2)
        6. commit the same manifest to the default branch                  (section 7)
           The three GitHub proxies (gh-proxy, ghproxy.net, jsDelivr) read that copy; a release
           without it leaves them at 404 and only the official sources work.
        7. purge the jsDelivr cache and poll the CDN until it serves THIS manifest, then report a
           timeout as a failure, not a warning                              (section 7.1)
        8. read back: download the release asset and compare sha256 + size, then report which
           sources already serve the new version                           (sections 7 and 8)

    Every step is a hard gate: the first failure stops the run and says which step it was.
    Remote steps only run when the local ones succeeded - the order "upload first, then write the
    manifest, then read it back" cannot be reversed (section 0 of the doc).

    JSDELIVR: cdn.jsdelivr.net/gh/<owner>/<repo>@<branch>/<file> is cached per branch reference and
    does NOT notice a commit that only touches update.json. raw.githubusercontent.com and
    gh-proxy.com serve the new file the moment the push lands; jsDelivr keeps the previous bytes
    until the path is purged (https://purge.jsdelivr.net/...), and the purge takes a few more
    seconds to reach every edge. 0.1.0-alpha.2 shipped that way: jsDelivr kept serving a manifest
    that pointed at the previous package, so every user on that source downloaded the wrong bytes
    and failed the sha256 gate. Step 7 purges and then polls until the CDN serves this manifest's
    sha256 (10 x 3 s); a timeout is reported as FAILED in the final verification and makes the run
    exit non-zero, because the users on that source are broken even though the release itself is up.

    NOTES: -NotesFile, else -Notes, else the CHANGELOG.md section for this version (via
    scripts\get-release-notes.ps1, the same rule the release workflow uses). Never a git log
    dump - docs\RELEASE.md section 5.1.

    NOT automated: npm publish (section 6). It needs a registry credential this machine may not
    have, so the script prints the npm commands at the end and the source check reports whether
    the two registries are readable.

    -DryRun runs steps 1-3 and prints the exact commands of steps 4-6 without touching the
    network, git or gh. It still computes the real hashes from real artifacts, so a dry run needs
    either a build (-SkipBuild not set) or existing artifacts (-SkipBuild).

    ENGLISH ONLY, UTF-8 WITHOUT BOM. Windows PowerShell 5.1 reads a BOM-less script as ANSI, so
    a single non-ASCII literal would be corrupted before it ever runs.

.PARAMETER Version
    Release version, e.g. 0.1.0-alpha.3. Written into the build and into the manifest.

.PARAMETER Notes
    Release notes as a string. Mutually exclusive with -NotesFile.

.PARAMETER NotesFile
    File to read the release notes from (UTF-8 or UTF-8 with BOM; a BOM is stripped).

    When neither -Notes nor -NotesFile is given, the notes are the CHANGELOG.md section for this
    version, or a short fallback template (scripts\get-release-notes.ps1 - the same rule the
    release workflow applies, so a local release and a CI release read the same).

.PARAMETER Tag
    Git tag. Default v<Version> (the client strips the leading v).

.PARAMETER Channel
    Manifest channel: alpha (default and the only channel published today), beta, stable.
    The channel decides the manifest file name (section 1): alpha -> update.json,
    everything else -> update-<channel>.json.

.PARAMETER MinVersion
    Optional. Oldest version that may upgrade in place; older builds are told to install
    manually. Omit it and the field is left out of the manifest entirely.

.PARAMETER Branch
    Default branch the manifest is committed to. Default main.

.PARAMETER ArtifactsDir
    Where the artifacts live. Default <repo>\artifacts. Meant for rehearsal runs; the installer
    writes to the documented path regardless.

.PARAMETER DotNet
    Passed through to build-app-zip.ps1 (see that script for the resolution order).

.PARAMETER SkipBuild
    Do not run the two build scripts; use the artifacts that are already there. The manifest is
    still computed from those files, so this is also the CI path (build happened in an earlier
    workflow step).

.PARAMETER StrictVerification
    Treat a source that does not serve the new version yet as a failure. Default: such sources
    are reported as warnings (CDN and registry sync are eventually consistent).

    jsDelivr is the exception: a stale jsDelivr copy means that source hands out a manifest for a
    package that is no longer there, so it is always a failure, with or without this switch.

.PARAMETER PurgeJsDelivrOnly
    CI helper. Purge the jsDelivr copy of the manifest and wait until it serves it - nothing is
    built, tagged, committed or uploaded. The workflow writes the manifest and commits it itself
    (.github\workflows\release.yml), so it calls this mode right after that commit. Exit code 0
    when the CDN serves the manifest, 1 when it does not.

.PARAMETER DryRun
    Local steps only (same as -WhatIf): build, hashes, manifest, then print the plan.

.EXAMPLE
    pwsh -File scripts\publish-release.ps1 -Version 0.1.0-alpha.3 -NotesFile notes.md

.EXAMPLE
    pwsh -File scripts\publish-release.ps1 -Version 0.1.0-alpha.3 -DryRun

.EXAMPLE
    powershell -NoProfile -File scripts\publish-release.ps1 -Version 0.1.0-alpha.3 -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Notes,

    [string]$NotesFile,

    [string]$Tag,

    [ValidateSet('alpha', 'beta', 'stable')]
    [string]$Channel = 'alpha',

    [string]$MinVersion,

    [string]$Branch = 'main',

    [string]$ArtifactsDir,

    [string]$DotNet,

    [switch]$SkipBuild,

    [switch]$StrictVerification,

    [switch]$PurgeJsDelivrOnly,

    [Alias('WhatIf')]
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$ProgressPreference = 'SilentlyContinue'
$nativePref = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
if ($null -ne $nativePref) { Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false }

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$repo = Split-Path -Parent $PSScriptRoot
$RepositorySlug = 'YunxiRamito/Performix'   # UpdateSourceCatalog.Repository
$ZipAssetName = 'Performix-win-x64.zip'
$InstallerAssetName = 'PerformixSetup.exe'

if ([string]::IsNullOrWhiteSpace($ArtifactsDir)) {
    $ArtifactsDir = Join-Path $repo 'artifacts'
}
$ArtifactsDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactsDir)
$ZipPath = Join-Path $ArtifactsDir $ZipAssetName
$InstallerPath = Join-Path $ArtifactsDir $InstallerAssetName

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host ('[publish] == ' + $Message) -ForegroundColor Cyan
}

function Write-Note {
    param([string]$Message)
    Write-Host ('[publish]    ' + $Message) -ForegroundColor DarkGray
}

function Write-Warn {
    param([string]$Message)
    Write-Host ('[publish] WARN: ' + $Message) -ForegroundColor Yellow
    $script:Warnings.Add($Message) | Out-Null
}

function Stop-WithMessage {
    param([string]$Message)
    Write-Host ''
    Write-Host ('[publish] FAILED: ' + $Message) -ForegroundColor Red
    if ($script:Warnings.Count -gt 0) {
        Write-Host '[publish] warnings so far:' -ForegroundColor Yellow
        foreach ($w in $script:Warnings) { Write-Host ('  - ' + $w) -ForegroundColor Yellow }
    }
    exit 1
}

# Manifest file name: alpha keeps the plain update.json the format doc specifies; any other
# channel gets update-<channel>.json (UpdateSourceCatalog.ManifestFileName).
function Get-ManifestFileName {
    param([string]$ChannelName)
    if ($ChannelName -eq 'alpha') { return 'update.json' }
    return ('update-' + $ChannelName + '.json')
}

function ConvertTo-JsonLiteral {
    param([AllowEmptyString()][string]$Text)
    return (ConvertTo-Json -InputObject $Text -Compress)
}

function New-ManifestText {
    param(
        [string]$ManifestChannel,
        [string]$ManifestVersion,
        [string]$PublishedAt,
        [string]$NotesText,
        [string]$MinimumVersion,
        [string]$AssetName,
        [string]$AssetUrl,
        [long]$SizeBytes,
        [string]$Sha256)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('{')
    $lines.Add('  "schema": 1,')
    $lines.Add('  "channel": ' + (ConvertTo-JsonLiteral $ManifestChannel) + ',')
    $lines.Add('  "version": ' + (ConvertTo-JsonLiteral $ManifestVersion) + ',')
    $lines.Add('  "publishedAt": ' + (ConvertTo-JsonLiteral $PublishedAt) + ',')
    $lines.Add('  "notes": ' + (ConvertTo-JsonLiteral $NotesText) + ',')
    if (-not [string]::IsNullOrWhiteSpace($MinimumVersion)) {
        $lines.Add('  "minVersion": ' + (ConvertTo-JsonLiteral $MinimumVersion) + ',')
    }
    $lines.Add('  "assets": [')
    $lines.Add('    {')
    $lines.Add('      "name": ' + (ConvertTo-JsonLiteral $AssetName) + ',')
    $lines.Add('      "url": ' + (ConvertTo-JsonLiteral $AssetUrl) + ',')
    $lines.Add('      "sizeBytes": ' + $SizeBytes.ToString([System.Globalization.CultureInfo]::InvariantCulture) + ',')
    $lines.Add('      "sha256": ' + (ConvertTo-JsonLiteral $Sha256))
    $lines.Add('    }')
    $lines.Add('  ]')
    $lines.Add('}')
    return (($lines -join "`n") + "`n")
}

# Re-read what we just wrote and check it against the rules the client enforces
# (UpdateManifestParser): schema 1, channel match, three-part version, ISO-8601 UTC
# publishedAt, one asset with a whitelisted https url, 1..1 GiB sizeBytes and a 64-char sha256.
function Assert-ManifestFile {
    param(
        [string]$Path,
        [string]$ExpectedChannel,
        [string]$ExpectedVersion,
        [long]$ExpectedSize,
        [string]$ExpectedSha)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-WithMessage ('the manifest was not written: ' + $Path)
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        Stop-WithMessage 'update.json starts with a UTF-8 BOM; some mirrors and JSON parsers keep it as bytes'
    }
    $text = $Utf8NoBom.GetString($bytes)
    try {
        $manifest = $text | ConvertFrom-Json
    } catch {
        Stop-WithMessage ('update.json is not valid JSON: ' + $_.Exception.Message)
    }

    if ($manifest.schema -ne 1) { Stop-WithMessage ('manifest schema is ' + $manifest.schema + ', the client only understands 1') }
    if ($manifest.channel -ne $ExpectedChannel) { Stop-WithMessage ('manifest channel is ' + $manifest.channel + ', expected ' + $ExpectedChannel) }
    if ($manifest.version -ne $ExpectedVersion) { Stop-WithMessage ('manifest version is ' + $manifest.version + ', expected ' + $ExpectedVersion) }
    # Checked against the raw text: PowerShell 7's ConvertFrom-Json turns an ISO-8601 string into
    # a [datetime], so the parsed value cannot be format-checked portably (5.1 keeps it a string).
    if ($text -notmatch '"publishedAt"\s*:\s*"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z"') {
        Stop-WithMessage ('manifest publishedAt is not an ISO-8601 UTC string: ' + $manifest.publishedAt)
    }
    if (-not ($manifest.assets -is [System.Array])) {
        Stop-WithMessage 'manifest assets is not a JSON array; the client rejects the manifest'
    }
    if ($manifest.assets.Count -ne 1) {
        Stop-WithMessage ('manifest carries ' + $manifest.assets.Count + ' assets; the zip must be the only entry (the installer is not an update asset)')
    }
    $asset = $manifest.assets[0]
    if ($asset.name -ne $ZipAssetName) {
        Stop-WithMessage ('asset name is ' + $asset.name + ', expected ' + $ZipAssetName)
    }
    if ($asset.url -notmatch ('^https://github\.com/' + [regex]::Escape($RepositorySlug) + '/releases/download/')) {
        Stop-WithMessage ('asset url is not a GitHub Release https address: ' + $asset.url)
    }
    if ([long]$asset.sizeBytes -ne $ExpectedSize) {
        Stop-WithMessage ('manifest sizeBytes ' + $asset.sizeBytes + ' does not match the zip (' + $ExpectedSize + ')')
    }
    if ($asset.sha256 -ne $ExpectedSha -or $asset.sha256 -notmatch '^[0-9a-f]{64}$') {
        Stop-WithMessage ('manifest sha256 does not match the zip (' + $ExpectedSha + ')')
    }
    return $manifest
}

function Invoke-Tool {
    param(
        [string]$Exe,
        [string[]]$Arguments,
        [switch]$AllowFailure)

    Write-Note ('> ' + $Exe + ' ' + ($Arguments -join ' '))
    $output = & $Exe @Arguments 2>&1
    $code = $LASTEXITCODE
    if ($null -ne $output) {
        foreach ($line in @($output)) { Write-Note ('  ' + [string]$line) }
    }
    if ($code -ne 0 -and -not $AllowFailure) {
        Stop-WithMessage ($Exe + ' ' + ($Arguments -join ' ') + ' failed (exit ' + $code + ')')
    }
    return ($code)
}

function Invoke-BuildScript {
    param([string]$ScriptPath, [string[]]$Arguments)

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        Stop-WithMessage ('missing build script: ' + $ScriptPath)
    }
    # Run it as a child process: these scripts call exit, which would otherwise terminate this
    # run and swallow the step context.
    $engine = (Get-Process -Id $PID).Path
    Write-Note ('> ' + $ScriptPath + ' ' + ($Arguments -join ' '))
    & $engine -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage ('build script failed (exit ' + $LASTEXITCODE + '): ' + $ScriptPath)
    }
}

# Call a build script with -Version only if it declares that parameter: the installer script is
# owned elsewhere and its parameter set may change.
function Get-DeclaredParameters {
    param([string]$ScriptPath)
    try {
        $command = Get-Command -Name $ScriptPath -ErrorAction Stop
        return @($command.Parameters.Keys)
    } catch {
        return @()
    }
}

function Get-WebText {
    param([string]$Url, [int]$TimeoutSec = 60)
    $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec $TimeoutSec
    return [string]$response.Content
}

# sha256 of the text with CRLF folded to LF. The working tree has CRLF (git autocrlf) while the
# blob jsDelivr serves has LF, so a raw byte comparison would call an identical manifest stale.
function Get-NormalizedSha256 {
    param([byte[]]$Bytes)
    $text = $Utf8NoBom.GetString($Bytes)
    $normalized = $text -replace "`r`n", "`n"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Utf8NoBom.GetBytes($normalized))
    } finally {
        $sha.Dispose()
    }
    return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

# jsDelivr caches @<branch> references per file and does not notice a commit that only bumps
# update.json: the CDN keeps serving the previous bytes until the path is purged, and the purge
# needs a few more seconds to reach every edge (docs\RELEASE.md section 7.1). raw.githubusercontent.com
# and gh-proxy.com are fresh the moment the push lands, which is exactly why 0.1.0-alpha.2 looked
# fine on two sources and broken on the third.
#
# Purge, then poll the CDN until it serves the manifest this run published (by content sha256, not
# by the version string: a manifest can carry the new version and the previous package). Returns
# an object with Verified / Attempts / ServedSha256 / Detail / PurgeUrl / CdnUrl / PurgeStatus.
function Invoke-JsDelivrPurge {
    param(
        [string]$OwnerRepo,
        [string]$BranchName,
        [string]$ManifestName,
        [string]$ExpectedSha256,
        [string]$ManifestPath,
        [int]$MaxAttempts = 10,
        [int]$DelaySeconds = 3
    )

    $purgeUrl = 'https://purge.jsdelivr.net/gh/' + $OwnerRepo + '@' + $BranchName + '/' + $ManifestName
    $cdnUrl = 'https://cdn.jsdelivr.net/gh/' + $OwnerRepo + '@' + $BranchName + '/' + $ManifestName
    # No client-side cache in front of the CDN read: the point is to see what the CDN itself has.
    $noCache = @{ 'Cache-Control' = 'no-cache'; 'Pragma' = 'no-cache' }

    $expectedLf = $null
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath) -and (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        $expectedLf = Get-NormalizedSha256 -Bytes ([System.IO.File]::ReadAllBytes($ManifestPath))
    }

    $purgeStatus = 'unknown'
    $purgeDetail = ''
    Write-Note ('> GET ' + $purgeUrl)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $purgeUrl -TimeoutSec 60 -Headers $noCache
        $body = ([string]$response.Content).Trim()
        $purgeDetail = $body
        try {
            $purgeJson = $body | ConvertFrom-Json
            $statusProp = $purgeJson.PSObject.Properties['status']
            if ($null -ne $statusProp) { $purgeStatus = [string]$statusProp.Value } else { $purgeStatus = 'answered' }
        } catch {
            $purgeStatus = 'unparsed'
        }
        Write-Note ('  purge ' + $purgeStatus + ' (HTTP ' + [int]$response.StatusCode + ')')
    } catch {
        # A failed purge request is not fatal on its own - the CDN may already be fresh (nothing to
        # purge). The poll below is the verdict.
        $purgeStatus = 'request-failed'
        $purgeDetail = $_.Exception.Message
        Write-Note ('  purge request failed: ' + $purgeDetail)
    }

    $verified = $false
    $attempts = 0
    $servedSha = ''
    $detail = 'the CDN was never read'
    for ($attempt = 1; $attempt -le $MaxAttempts -and -not $verified; $attempt++) {
        $attempts = $attempt
        # Wait before the first read: jsDelivr answers "finished" long before every edge has it.
        Start-Sleep -Seconds $DelaySeconds
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('performix-jsdelivr-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $cdnUrl -OutFile $tmp -TimeoutSec 45 -Headers $noCache
            $servedBytes = [System.IO.File]::ReadAllBytes($tmp)
            $servedSha = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($servedSha -eq $ExpectedSha256) {
                $verified = $true
                $detail = 'sha256 ' + $servedSha
            } elseif ($null -ne $expectedLf -and (Get-NormalizedSha256 -Bytes $servedBytes) -eq $expectedLf) {
                # Same content, different line endings: content-identical, so accept it and say so.
                $verified = $true
                $detail = 'sha256 ' + $servedSha + ' (content matches after CRLF normalization)'
            } else {
                $servedVersion = ''
                try {
                    $servedJson = ($Utf8NoBom.GetString($servedBytes)) | ConvertFrom-Json
                    $versionProp = $servedJson.PSObject.Properties['version']
                    if ($null -ne $versionProp) { $servedVersion = [string]$versionProp.Value }
                } catch {
                    $servedVersion = ''
                }
                if ([string]::IsNullOrWhiteSpace($servedVersion)) {
                    $detail = 'serves sha256 ' + $servedSha
                } else {
                    # A manifest can carry the new version and the previous package, so print both:
                    # "same version, different bytes" is the failure mode this poll exists to catch.
                    $detail = 'serves sha256 ' + $servedSha + ' (manifest version ' + $servedVersion + ')'
                }
            }
        } catch {
            $detail = 'read failed: ' + $_.Exception.Message
        } finally {
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        }
        if (-not $verified) {
            Write-Note ('  poll ' + $attempt + '/' + $MaxAttempts + ': ' + $detail)
        }
    }
    if ($verified) {
        Write-Note ('  the CDN serves this manifest (' + $detail + ', ' + $attempts + ' check(s))')
    } else {
        Write-Note ('  still stale after ' + $attempts + ' check(s): ' + $detail)
    }

    return [pscustomobject]@{
        Verified    = $verified
        Attempts    = $attempts
        ServedSha256 = $servedSha
        Detail      = $detail
        PurgeUrl    = $purgeUrl
        CdnUrl      = $cdnUrl
        PurgeStatus = $purgeStatus
        PurgeDetail = $purgeDetail
    }
}

# ==================================================================== 1. arguments
Write-Step ('release ' + $Version + ' (' + $Channel + ')')

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.]+)?$') {
    Stop-WithMessage ("version '" + $Version + "' is not a three-part version (e.g. 0.1.0-alpha.3)")
}
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = 'v' + $Version }
if ($Tag -notmatch '^v[0-9]') {
    Stop-WithMessage ("tag '" + $Tag + "' must start with v (the client strips the prefix before comparing: docs\RELEASE.md section 5)")
}
if ($Tag -ne ('v' + $Version)) {
    Write-Warn ('tag ' + $Tag + ' does not match v' + $Version + '; the release will not line up with the manifest version')
}
if (-not [string]::IsNullOrWhiteSpace($Notes) -and -not [string]::IsNullOrWhiteSpace($NotesFile)) {
    Stop-WithMessage '-Notes and -NotesFile are mutually exclusive'
}
if (-not [string]::IsNullOrWhiteSpace($MinVersion) -and $MinVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.]+)?$') {
    Stop-WithMessage ("minVersion '" + $MinVersion + "' is not a three-part version")
}
if (-not [string]::IsNullOrWhiteSpace($MinVersion) -and $Channel -eq 'stable' -and $Version -match '-') {
    Write-Warn 'stable channel with a pre-release version'
}

$manifestName = Get-ManifestFileName $Channel
$manifestPath = Join-Path $ArtifactsDir $manifestName
$notesPath = Join-Path $ArtifactsDir 'release-notes.md'
$assetUrl = 'https://github.com/' + $RepositorySlug + '/releases/download/' + $Tag + '/' + $ZipAssetName

# CI helper (step 7, docs\RELEASE.md section 7.1): the workflow commits the manifest itself, so it
# needs the purge and the read-back without the rest of the run. Nothing is built, tagged, committed
# or uploaded here - the only network calls are the purge request and the CDN reads. Exit 0 when the
# CDN serves this manifest, 1 when it does not (a stale jsDelivr copy breaks every user on it).
if ($PurgeJsDelivrOnly) {
    Write-Step ('jsDelivr purge for ' + $manifestName + ' on ' + $Branch)
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Stop-WithMessage ('the manifest is missing: ' + $manifestPath + ' (the workflow writes it in an earlier step)')
    }
    $manifestSha = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Note ('manifest ' + $manifestPath + ' (' + $manifestSha + ')')
    $jsdelivr = Invoke-JsDelivrPurge -OwnerRepo $RepositorySlug -BranchName $Branch -ManifestName $manifestName `
        -ExpectedSha256 $manifestSha -ManifestPath $manifestPath
    if (-not $jsdelivr.Verified) {
        Stop-WithMessage ('cdn.jsdelivr.net still does not serve ' + $manifestName + ' for ' + $Version + ' after ' +
            $jsdelivr.Attempts + ' check(s): ' + $jsdelivr.Detail + '. ' +
            'Users on the jsDelivr source get the previous manifest and fail the sha256 gate. ' +
            'Purge again and re-read: ' + $jsdelivr.PurgeUrl)
    }
    Write-Host ('[publish] jsDelivr serves ' + $manifestName + ' for ' + $Version + ' (' + $manifestSha + ')') -ForegroundColor Green
    exit 0
}

# Notes: -NotesFile wins when given; an empty note is allowed (the field is optional in the
# manifest) but the release body would be empty, so say so.
$notesText = ''
if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    $resolvedNotes = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($NotesFile)
    if (-not (Test-Path -LiteralPath $resolvedNotes -PathType Leaf)) {
        Stop-WithMessage ('-NotesFile not found: ' + $resolvedNotes)
    }
    $notesText = $Utf8NoBom.GetString([System.IO.File]::ReadAllBytes($resolvedNotes)).TrimStart([char]0xFEFF).TrimEnd()
    Write-Note ('notes from ' + $resolvedNotes + ' (' + $notesText.Length + ' chars)')
} elseif (-not [string]::IsNullOrWhiteSpace($Notes)) {
    $notesText = $Notes.TrimEnd()
    Write-Note ('notes from -Notes (' + $notesText.Length + ' chars)')
} else {
    # Neither given: the same rule CI uses (docs\RELEASE.md section 5.1) - the CHANGELOG.md
    # section for this version, or a short template. Never a git log dump: commit subjects are
    # unreadable on the Release page and drag in work that shipped in an earlier release.
    $notesScript = Join-Path $PSScriptRoot 'get-release-notes.ps1'
    if (-not (Test-Path -LiteralPath $notesScript -PathType Leaf)) {
        Stop-WithMessage ('missing ' + $notesScript + '; it supplies the notes when neither -Notes nor -NotesFile is given')
    }
    $engine = (Get-Process -Id $PID).Path
    $notesTmp = Join-Path ([System.IO.Path]::GetTempPath()) ('performix-notes-' + [guid]::NewGuid().ToString('N') + '.md')
    Write-Note ('> ' + $notesScript + ' -Version ' + $Version)
    try {
        # Through a file, not through stdout: anything the child writes to stdout would otherwise
        # end up inside the release body.
        & $engine -NoProfile -ExecutionPolicy Bypass -File $notesScript -Version $Version `
            -RepositorySlug $RepositorySlug -Branch $Branch -OutputFile $notesTmp
        if ($LASTEXITCODE -ne 0) {
            Stop-WithMessage ('get-release-notes.ps1 failed (exit ' + $LASTEXITCODE + ')')
        }
        if (-not (Test-Path -LiteralPath $notesTmp -PathType Leaf)) {
            Stop-WithMessage ('get-release-notes.ps1 wrote no notes file: ' + $notesTmp)
        }
        $notesText = $Utf8NoBom.GetString([System.IO.File]::ReadAllBytes($notesTmp)).TrimStart([char]0xFEFF).TrimEnd()
    } finally {
        if (Test-Path -LiteralPath $notesTmp) { Remove-Item -LiteralPath $notesTmp -Force -ErrorAction SilentlyContinue }
    }
    if ([string]::IsNullOrWhiteSpace($notesText)) {
        Stop-WithMessage 'get-release-notes.ps1 produced empty notes'
    }
    Write-Note ('notes for ' + $Version + ' (' + $notesText.Length + ' chars, source reported above)')
}

if (-not (Test-Path -LiteralPath $ArtifactsDir -PathType Container)) {
    New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null
}

# ==================================================================== 2. build
if ($SkipBuild) {
    Write-Step '-SkipBuild: using the existing artifacts'
} else {
    Write-Step 'building the app package'
    $zipArgs = @('-Version', $Version, '-ZipPath', $ZipPath)
    if (-not [string]::IsNullOrWhiteSpace($DotNet)) { $zipArgs += @('-DotNet', $DotNet) }
    Invoke-BuildScript -ScriptPath (Join-Path $PSScriptRoot 'build-app-zip.ps1') -Arguments $zipArgs

    Write-Step 'building the installer'
    $installerScript = Join-Path $PSScriptRoot 'build-installer.ps1'
    $installerArgs = @()
    if ((Get-DeclaredParameters $installerScript) -contains 'Version') { $installerArgs += @('-Version', $Version) }
    Invoke-BuildScript -ScriptPath $installerScript -Arguments $installerArgs
}

# ==================================================================== 3. hashes
Write-Step 'hashes'
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    Stop-WithMessage ('the app package is missing: ' + $ZipPath + ' (run without -SkipBuild, or fix the build)')
}
$zipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipSize = (Get-Item -LiteralPath $ZipPath).Length
if ($zipSize -le 0 -or $zipSize -gt (1024L * 1024 * 1024)) {
    Stop-WithMessage ('the zip size ' + $zipSize + ' is outside the 1 byte .. 1 GiB window the manifest accepts')
}
Write-Note ('zip      ' + $ZipPath)
Write-Note ('sizeBytes ' + $zipSize)
Write-Note ('sha256   ' + $zipHash)

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    Stop-WithMessage ('the installer is missing: ' + $InstallerPath + '. Every release ships a PerformixSetup.exe (amd64); see docs\RELEASE.md')
}
$installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$installerSize = (Get-Item -LiteralPath $InstallerPath).Length
Write-Note ('setup    ' + $InstallerPath)
Write-Note ('setup sha256 ' + $installerHash + ' / ' + $installerSize + ' bytes (not part of the manifest)')

# ==================================================================== 4. manifest
Write-Step ('manifest -> ' + $manifestName)
$publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$manifestText = New-ManifestText `
    -ManifestChannel $Channel `
    -ManifestVersion $Version `
    -PublishedAt $publishedAt `
    -NotesText $notesText `
    -MinimumVersion $MinVersion `
    -AssetName $ZipAssetName `
    -AssetUrl $assetUrl `
    -SizeBytes $zipSize `
    -Sha256 $zipHash

[System.IO.File]::WriteAllText($manifestPath, $manifestText, $Utf8NoBom)
$manifest = Assert-ManifestFile -Path $manifestPath -ExpectedChannel $Channel -ExpectedVersion $Version `
    -ExpectedSize $zipSize -ExpectedSha $zipHash
Write-Note ('written  ' + $manifestPath)
Write-Note ('asset url ' + $assetUrl)
Write-Host $manifestText -ForegroundColor DarkGray

# The release body is the same note text; update.json would work as --notes-file but a JSON
# blob is a miserable release page.
[System.IO.File]::WriteAllText($notesPath, $notesText + "`n", $Utf8NoBom)

# ==================================================================== 5. dry run stop
if ($DryRun) {
    Write-Step 'DRY RUN - local steps done, nothing was tagged, pushed or uploaded. A real run would execute:'
    $releaseArgs = @('release', 'create', $Tag, $ZipPath, $InstallerPath, $manifestPath,
        '--title', ('Performix ' + $Version), '--notes-file', $notesPath)
    if ($Channel -ne 'stable') { $releaseArgs += '--prerelease' }
    Write-Host '' -ForegroundColor DarkGray
    Write-Host ('  git tag ' + $Tag) -ForegroundColor DarkGray
    Write-Host ('  git push origin ' + $Tag) -ForegroundColor DarkGray
    Write-Host ('  gh ' + ($releaseArgs -join ' ')) -ForegroundColor DarkGray
    Write-Host ('  download ' + $assetUrl + ', and if it is not this build (' + $zipHash + ', ' + $zipSize + ' bytes)') -ForegroundColor DarkGray
    Write-Host ('             rewrite ' + $manifestName + ' from the published bytes and gh release upload --clobber it') -ForegroundColor DarkGray
    Write-Host ('  Copy-Item ' + $manifestPath + ' ' + (Join-Path $repo $manifestName)) -ForegroundColor DarkGray
    Write-Host ('  git add -- ' + $manifestName + '   (on branch ' + $Branch + ')') -ForegroundColor DarkGray
    Write-Host ('  git commit -m "release: ' + $manifestName + ' for ' + $Version + '" -- ' + $manifestName) -ForegroundColor DarkGray
    Write-Host ('  git push origin ' + $Branch) -ForegroundColor DarkGray
    Write-Host ('  GET https://purge.jsdelivr.net/gh/' + $RepositorySlug + '@' + $Branch + '/' + $manifestName) -ForegroundColor DarkGray
    Write-Host ('      then poll https://cdn.jsdelivr.net/gh/' + $RepositorySlug + '@' + $Branch + '/' + $manifestName) -ForegroundColor DarkGray
    Write-Host ('      until it serves sha256 ' + (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() + ' (10 x 3 s)') -ForegroundColor DarkGray
    Write-Host ('  download ' + $assetUrl + ' and compare sha256 + size (' + $zipHash + ', ' + $zipSize + ' bytes)') -ForegroundColor DarkGray
    Write-Host '  check gh-proxy / ghproxy.net / jsDelivr serve this version, and that both npm registries answer' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host ('[publish] dry run ok. Manifest: ' + $manifestPath) -ForegroundColor Green
    exit 0
}

# ==================================================================== 6. env checks
Write-Step 'toolchain'
$git = Get-Command git -ErrorAction SilentlyContinue
if ($null -eq $git) { Stop-WithMessage 'git is not on PATH' }
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($null -eq $gh) { Stop-WithMessage 'gh (GitHub CLI) is not on PATH; install it or upload the release by hand' }
Invoke-Tool -Exe $gh.Source -Arguments @('auth', 'status') -AllowFailure | Out-Null
$currentBranch = (& git rev-parse --abbrev-ref HEAD)
if ($currentBranch -ne $Branch) {
    Stop-WithMessage ('HEAD is on ' + $currentBranch + ', not ' + $Branch + '. The manifest has to reach the default branch, ' +
        'otherwise gh-proxy / ghproxy.net / jsDelivr cannot see it (docs\RELEASE.md section 1). ' +
        'Switch branch or pass -Branch.')
}

# ==================================================================== 7. tag + release
Write-Step ('tag ' + $Tag)
$existing = (& git tag --list $Tag)
if (-not [string]::IsNullOrWhiteSpace(($existing -join ''))) {
    Stop-WithMessage ('tag ' + $Tag + ' already exists locally. Delete it first: git tag -d ' + $Tag + ' (and git push origin :refs/tags/' + $Tag + ' if it was pushed)')
}
Invoke-Tool -Exe 'git' -Arguments @('tag', $Tag) | Out-Null
Invoke-Tool -Exe 'git' -Arguments @('push', 'origin', $Tag) | Out-Null

Write-Step ('github release ' + $Tag)
$releaseArgs = @('release', 'create', $Tag, $ZipPath, $InstallerPath, $manifestPath,
    '--title', ('Performix ' + $Version), '--notes-file', $notesPath)
if ($Channel -ne 'stable') { $releaseArgs += '--prerelease' }
Invoke-Tool -Exe $gh.Source -Arguments $releaseArgs | Out-Null

# ==================================================================== 8. reconcile with the published asset
# A tag push also runs .github\workflows\release.yml, which rebuilds the package and
# --clobber-uploads it. CI builds are not bit-for-bit identical to a workstation build
# (docs\RELEASE.md section 2.1), so the asset that ends up on the Release can be a different
# file from the one this run built. The published bytes are the truth: the manifest - and the
# copy committed in the next step, which is what gh-proxy / ghproxy.net / jsDelivr serve - has
# to describe what users actually download, otherwise every accelerated download fails the
# sha256 gate. That is exactly what happened to 0.1.0-alpha.2.
Write-Step 'reconcile the manifest with the published asset'
$publishedHash = ''
$publishedSize = 0
$publishedKnown = $false
for ($attempt = 1; $attempt -le 6 -and -not $publishedKnown; $attempt++) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('performix-published-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        Write-Note ('attempt ' + $attempt + ': GET ' + $assetUrl)
        Invoke-WebRequest -UseBasicParsing -Uri $assetUrl -OutFile $tmp -TimeoutSec 300
        $publishedHash = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
        $publishedSize = (Get-Item -LiteralPath $tmp).Length
        $publishedKnown = $true
    } catch {
        Write-Warn ('could not read the published asset back: ' + $_.Exception.Message)
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
    if (-not $publishedKnown -and $attempt -lt 6) { Start-Sleep -Seconds 10 }
}

if (-not $publishedKnown) {
    Write-Warn ('the published asset could not be read; the manifest keeps this build''s hashes (' + $zipHash + '). ' +
        'The read-back step below fails if the asset really differs.')
} elseif ($publishedHash -eq $zipHash -and $publishedSize -eq $zipSize) {
    Write-Note 'the published asset is this build (sha256 and size match)'
} else {
    Write-Warn ('the published asset is NOT this build: this build ' + $zipHash + ' / ' + $zipSize +
        ' bytes, published ' + $publishedHash + ' / ' + $publishedSize + ' bytes.')
    Write-Warn 'the published bytes win - rewriting the manifest from the asset and re-uploading it'
    $zipHash = $publishedHash
    $zipSize = $publishedSize
    $manifestText = New-ManifestText `
        -ManifestChannel $Channel `
        -ManifestVersion $Version `
        -PublishedAt $publishedAt `
        -NotesText $notesText `
        -MinimumVersion $MinVersion `
        -AssetName $ZipAssetName `
        -AssetUrl $assetUrl `
        -SizeBytes $zipSize `
        -Sha256 $zipHash
    [System.IO.File]::WriteAllText($manifestPath, $manifestText, $Utf8NoBom)
    $manifest = Assert-ManifestFile -Path $manifestPath -ExpectedChannel $Channel -ExpectedVersion $Version `
        -ExpectedSize $zipSize -ExpectedSha $zipHash
    Write-Note ('rewritten ' + $manifestPath + ' for the published asset')
    Invoke-Tool -Exe $gh.Source -Arguments @('release', 'upload', $Tag, $manifestPath, '--clobber') | Out-Null
    Write-Note ('re-uploaded ' + $manifestName + ' to the release')
    Write-Note ('this build''s own package stays at ' + $ZipPath + '; it is not what the manifest points at')
}

# ==================================================================== 9. commit the manifest
Write-Step ('commit ' + $manifestName + ' to ' + $Branch)
$rootManifest = Join-Path $repo $manifestName
Copy-Item -LiteralPath $manifestPath -Destination $rootManifest -Force
Write-Note ('copied to ' + $rootManifest)

Invoke-Tool -Exe 'git' -Arguments @('add', '--', $manifestName) | Out-Null
$diffCode = Invoke-Tool -Exe 'git' -Arguments @('diff', '--cached', '--quiet', '--', $manifestName) -AllowFailure
if ($diffCode -eq 0) {
    Write-Warn ('the committed ' + $manifestName + ' is already identical; nothing to commit')
} else {
    Invoke-Tool -Exe 'git' -Arguments @('commit', '-m', ('release: ' + $manifestName + ' for ' + $Version), '--', $manifestName) | Out-Null
    Invoke-Tool -Exe 'git' -Arguments @('push', 'origin', $Branch) | Out-Null
}

# ==================================================================== 10. jsDelivr purge
# The push above makes raw.githubusercontent.com and both gh-proxy hosts serve the new file at once.
# jsDelivr does not: it caches the @<branch> reference per file and keeps the previous bytes until
# the path is purged, and the purge needs a few more seconds to reach every edge. Poll until the
# CDN serves THIS manifest's sha256; a timeout is a failure, not a warning - that source hands out
# a manifest for a package that is no longer there (0.1.0-alpha.2, docs\RELEASE.md section 7.1).
Write-Step ('purge the jsDelivr cache for @' + $Branch + '/' + $manifestName)
$manifestSha = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$jsdelivr = Invoke-JsDelivrPurge -OwnerRepo $RepositorySlug -BranchName $Branch -ManifestName $manifestName `
    -ExpectedSha256 $manifestSha -ManifestPath $manifestPath
if (-not $jsdelivr.Verified) {
    Write-Warn ('cdn.jsdelivr.net still does not serve this ' + $manifestName + ' after ' + $jsdelivr.Attempts +
        ' check(s): ' + $jsdelivr.Detail + ' (purge: ' + $jsdelivr.PurgeUrl + '). ' +
        'Users on the jsDelivr source get the previous manifest and fail the sha256 gate.')
    # Recorded in the verification table below as FAIL, and that makes this run exit non-zero.
}

# ==================================================================== 11. read back
Write-Step 'read back the release asset'
$downloadOk = $false
$downloadHash = ''
$downloadSize = 0
for ($attempt = 1; $attempt -le 6 -and -not $downloadOk; $attempt++) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('performix-release-check-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        Write-Note ('attempt ' + $attempt + ': GET ' + $assetUrl)
        Invoke-WebRequest -UseBasicParsing -Uri $assetUrl -OutFile $tmp -TimeoutSec 300
        $downloadHash = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
        $downloadSize = (Get-Item -LiteralPath $tmp).Length
        $downloadOk = ($downloadHash -eq $zipHash -and $downloadSize -eq $zipSize)
        if (-not $downloadOk) {
            Write-Warn ('downloaded asset differs from the local zip (sha256 ' + $downloadHash + ', ' + $downloadSize + ' bytes)')
        }
    } catch {
        Write-Warn ('download failed: ' + $_.Exception.Message)
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
    if (-not $downloadOk -and $attempt -lt 6) { Start-Sleep -Seconds 10 }
}
if (-not $downloadOk) {
    $script:Failures.Add('the release asset does not match the manifest') | Out-Null
    Stop-WithMessage ('the published asset does not match the manifest (' + $zipHash + ' / ' + $zipSize + ' bytes). ' +
        'Do not announce this release: gh release upload --clobber, or mark the release draft.')
}
Write-Note 'asset download matches sha256 and size'

Write-Step 'sources'
$rawUrl = 'https://raw.githubusercontent.com/' + $RepositorySlug + '/' + $Branch + '/' + $manifestName
# The two proxies and the raw URL are compared by content sha256, not by the version string: a
# manifest can carry the new version and the previous package, which is the failure this whole
# step exists to catch. jsDelivr is checked above (it needs the purge first) and joined in below.
$sourceChecks = @(
    @{ Id = 'gh-proxy';     Url = ('https://gh-proxy.com/' + $rawUrl) },
    @{ Id = 'ghproxy-net';  Url = ('https://ghproxy.net/' + $rawUrl) },
    @{ Id = 'github-raw';   Url = $rawUrl }
)
$manifestNormalizedSha = Get-NormalizedSha256 -Bytes ([System.IO.File]::ReadAllBytes($manifestPath))
$sourceResults = @()
foreach ($check in $sourceChecks) {
    $served = $null
    $detail = ''
    for ($attempt = 1; $attempt -le 4 -and $null -eq $served; $attempt++) {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('performix-source-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $check.Url -OutFile $tmp -TimeoutSec 45 `
                -Headers @{ 'Cache-Control' = 'no-cache'; 'Pragma' = 'no-cache' }
            $bytes = [System.IO.File]::ReadAllBytes($tmp)
            $hash = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -eq $manifestSha) {
                $served = $true
                $detail = 'sha256 ' + $hash
            } elseif ((Get-NormalizedSha256 -Bytes $bytes) -eq $manifestNormalizedSha) {
                # Same content, different line endings - a proxy that re-serializes with CRLF is fine.
                $served = $true
                $detail = 'sha256 ' + $hash + ' (content matches after CRLF normalization)'
            } else {
                $parsed = ($Utf8NoBom.GetString($bytes)) | ConvertFrom-Json
                $served = $false
                $detail = 'serves sha256 ' + $hash + ' (manifest version ' + $parsed.version + ')'
            }
        } catch {
            $detail = $_.Exception.Message
        } finally {
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        }
        if ($null -eq $served -and $attempt -lt 4) { Start-Sleep -Seconds 10 }
    }
    $sourceResults += [pscustomobject]@{ Id = $check.Id; Served = $served; Detail = $detail }
}
# jsDelivr: verdict comes from the purge + poll above, so the table shows what that source really
# serves now. A stale copy here is a FAIL, never a soft warning - see section 10.
$sourceResults += [pscustomobject]@{
    Id     = 'jsdelivr-gh'
    Served = $jsdelivr.Verified
    Detail = $jsdelivr.Detail
}

# npm is the one source family this script does not publish (section 6), so it can legitimately
# still serve the previous version; it only has to be readable.
$registryResults = @()
foreach ($registry in @(
        @{ Id = 'npm-official'; Url = 'https://registry.npmjs.org/performix' },
        @{ Id = 'npmmirror';    Url = 'https://registry.npmmirror.com/performix' })) {
    $reachable = $false
    $detail = ''
    try {
        $meta = Get-WebText -Url $registry.Url -TimeoutSec 30
        $reachable = $true
        $detail = 'readable'
        $parsedMeta = $meta | ConvertFrom-Json
        if ($null -ne $parsedMeta.versions -and $null -ne $parsedMeta.versions.PSObject.Properties[$Version]) {
            $detail = 'readable, carries ' + $Version
        }
    } catch {
        $detail = $_.Exception.Message
    }
    $registryResults += [pscustomobject]@{ Id = $registry.Id; Reachable = $reachable; Detail = $detail }
}

Write-Host ''
Write-Host '================ release verification ================' -ForegroundColor Green
Write-Host ('asset        : ' + $assetUrl)
Write-Host ('sha256       : ' + $downloadHash + '  (local ' + $zipHash + ')')
Write-Host ('sizeBytes    : ' + $downloadSize + '  (local ' + $zipSize + ')')
foreach ($r in $sourceResults) {
    $state = 'WARN'
    if ($r.Served -eq $true) { $state = 'PASS' }
    elseif ($r.Id -eq 'jsdelivr-gh') { $state = 'FAIL' }
    Write-Host ('source ' + $r.Id.PadRight(12) + ': ' + $state + '  ' + $r.Detail)
    if ($r.Served -ne $true) {
        if ($r.Id -eq 'jsdelivr-gh') {
            # Not a warning: this source hands out a manifest for a package that is no longer there.
            $script:Failures.Add('jsDelivr serves a stale ' + $manifestName + ' (' + $r.Detail + ')') | Out-Null
        } else {
            $script:Warnings.Add($r.Id + ' does not serve ' + $Version + ' yet (' + $r.Detail + ')') | Out-Null
        }
    }
}
foreach ($r in $registryResults) {
    $state = 'WARN'
    if ($r.Reachable) { $state = 'PASS' }
    Write-Host ('registry ' + $r.Id.PadRight(12) + ': ' + $state + '  ' + $r.Detail)
    if (-not $r.Reachable) { $script:Warnings.Add($r.Id + ' is not readable: ' + $r.Detail) | Out-Null }
}
Write-Host '=====================================================' -ForegroundColor Green

# ==================================================================== 12. summary
Write-Host ''
Write-Host ('[publish] ' + $Version + ' released as ' + $Tag + ' (' + $Channel + ')') -ForegroundColor Green
Write-Host ('[publish] zip   ' + $ZipAssetName + '  ' + $zipSize + ' bytes  ' + $zipHash + '  (what the manifest and the release carry)')
Write-Host ('[publish] setup ' + $InstallerAssetName + '  ' + $installerSize + ' bytes  ' + $installerHash)
Write-Host ('[publish] manifest ' + $manifestName + ' (release asset + ' + $Branch + ')')
Write-Host '[publish] still manual: npm publish (docs\RELEASE.md section 6) and the one real-machine update test (section 8.4)' -ForegroundColor Yellow

if ($script:Warnings.Count -gt 0) {
    Write-Host ''
    Write-Host '[publish] warnings:' -ForegroundColor Yellow
    foreach ($w in $script:Warnings) { Write-Host ('  - ' + $w) -ForegroundColor Yellow }
    if ($StrictVerification) {
        Stop-WithMessage ('-' + $script:Warnings.Count + ' verification warning(s) and -StrictVerification was given')
    }
}

# Hard failures, unlike the warnings above: the release exists and the asset is fine, but a source
# users can select is serving a manifest that points at a different package. Exit non-zero so a
# scripted release cannot quietly finish on that.
if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host '[publish] FAILED verification:' -ForegroundColor Red
    foreach ($f in $script:Failures) { Write-Host ('  - ' + $f) -ForegroundColor Red }
    Stop-WithMessage ('the release is up but ' + $script:Failures.Count + ' verification check(s) failed (see above). ' +
        'For jsDelivr: re-run the purge and read the CDN back before announcing (docs\RELEASE.md section 7.1).')
}
exit 0
