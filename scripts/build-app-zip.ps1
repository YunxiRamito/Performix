#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the release application package: <repo>\artifacts\Performix-win-x64.zip.

.DESCRIPTION
    The updater downloads exactly one zip per release, so this script is the single place that
    decides what "the app package" is. It follows docs\RELEASE.md section 2 and 3:

        1. dotnet publish src\Performix.App\Performix.App.csproj -c Release -p:Platform=x64
             -r win-x64 -p:Version=<Version> -p:SelfContained=false
             -p:WindowsAppSDKSelfContained=true
           Unchanged deployment shape: the app is framework-dependent on the .NET 10 desktop
           runtime but carries the Windows App SDK runtime in its own folder.
        2. Hard gates on the publish output: Performix.exe, resources.pri and
           scripts\invoke-update.ps1 must all be present. A running exe cannot overwrite itself,
           so a package without invoke-update.ps1 could never install itself.
        3. Zip the CONTENTS of the output directory (baseDirectory=false) so Performix.exe sits
           at the zip root - that is where scripts\invoke-update.ps1 looks first. The container
           is deterministic: ordinal-sorted entries, one fixed timestamp, no directory entries,
           fixed attributes, fixed compression level (see the zip step for why).
        4. Re-open the zip and assert Performix.exe and scripts\invoke-update.ps1 are entries,
           so a packaging change cannot silently ship an uninstallable update.
        5. Print sha256 and sizeBytes, which are the two hard gates of the manifest.

    REPRODUCIBILITY - what this script does and does not give you.

    Deterministic here (container only):
        * entry order: relative paths, '/' separated, sorted with an ORDINAL comparison. A
          culture-aware sort (Sort-Object) would order differently on a different locale, and
          ZipFile.CreateFromDirectory takes the order straight from the file system.
        * entry timestamps: one constant value for every entry. Without this the DOS timestamp
          field carries each file's LastWriteTime, so two builds of identical content still
          produce different bytes.
        * no directory entries (they hold nothing and their order is file-system dependent),
          external attributes fixed to 0, CompressionLevel.Optimal for every entry.

    NOT deterministic - the content itself, so the hash is not portable between machines:
        * the .NET SDK patch version: 10.0.1xx vs 10.0.2xx ships a different compiler and
          runtime pack. CI uses whatever actions/setup-dotnet '10.0.x' resolves to that day;
          a workstation uses its installed SDK (or tools\dotnet-sdk\ via $env:PERFORMIX_DOTNET).
        * the Windows App SDK NuGet version and the whole restore graph (floating versions,
          NuGet cache state, fallback folders).
        * absolute source paths baked into the .pdb files in the publish output. The CI checkout
          (D:\a\Performix\Performix) and a local checkout (anywhere) differ in length, which is
          enough to change the bytes - and the 0.1.0-alpha.2 release showed exactly that:
          33490261 bytes / a8d5e3... locally vs 33489786 bytes / 3d5048... on CI.
        * PublishReadyToRun / PGO / ILLink: any of them changes the code that is compiled.
          (This script does not pass them; keep it that way unless the release process changes.)
        * MSBuild node reuse, incremental state, and anything else that makes a build depend on
          what ran before it in the same workspace.

    Consequence, and the rule the release process runs on (docs\RELEASE.md section 2.1):
    the sha256 printed below is valid FOR THIS BUILD ONLY. The CI-built asset is the one that
    ships; a local build is for verifying that the release works, never for producing the
    hashes that go into the manifest.

    The script writes exactly two places: the publish output directory and the zip. It never
    touches git and never touches the network.

    ENGLISH ONLY, UTF-8 WITHOUT BOM. Windows PowerShell 5.1 reads a BOM-less script as ANSI, so
    a single non-ASCII literal would be corrupted before it ever runs.

.PARAMETER Version
    Version injected into the build, e.g. 0.1.0-alpha.3. Becomes AssemblyInformationalVersion,
    which is what the update page displays.

.PARAMETER Configuration
    Build configuration. Default Release (the only configuration a release may use).

.PARAMETER OutputDir
    Publish output directory. Default <repo>\artifacts\publish\win-x64.

.PARAMETER ZipPath
    Output zip. Default <repo>\artifacts\Performix-win-x64.zip. The manifest and docs key off
    this file name; change it only if the manifest changes too.

.PARAMETER DotNet
    dotnet.exe to use. Resolution order: this parameter, $env:PERFORMIX_DOTNET,
    <repo>\tools\dotnet-sdk\dotnet.exe, then the dotnet on PATH.

.PARAMETER SkipPublish
    Pack whatever is already in -OutputDir (no dotnet publish). The gates still run.

.PARAMETER DryRun
    Plan only (same as -WhatIf): resolve the SDK, print the exact publish command, write
    nothing. A missing SDK is a warning here, not a failure - the point is to check the plan.

.EXAMPLE
    pwsh -File scripts\build-app-zip.ps1 -Version 0.1.0-alpha.3

.EXAMPLE
    powershell -NoProfile -File scripts\build-app-zip.ps1 -Version 0.1.0-alpha.3 -WhatIf
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$OutputDir,

    [string]$ZipPath,

    [string]$DotNet,

    [switch]$SkipPublish,

    [Alias('WhatIf')]
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$ProgressPreference = 'SilentlyContinue'
# Native stderr must not become a terminating error (PS 7.3+ behaviour) - git/dotnet write
# warnings and progress to stderr on a perfectly successful run.
$nativePref = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
if ($null -ne $nativePref) { Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false }

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Performix.App\Performix.App.csproj'
$updateScriptSource = Join-Path $PSScriptRoot 'invoke-update.ps1'
$assetName = 'Performix-win-x64.zip'

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repo 'artifacts\publish\win-x64'
}
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $repo ('artifacts\' + $assetName)
}

function Write-Step {
    param([string]$Message)
    Write-Host ('[build-app-zip] ' + $Message) -ForegroundColor Cyan
}

function Write-Warn {
    param([string]$Message)
    Write-Host ('[build-app-zip] WARN: ' + $Message) -ForegroundColor Yellow
}

function Stop-WithMessage {
    param([string]$Message)
    Write-Host ('[build-app-zip] FAILED: ' + $Message) -ForegroundColor Red
    exit 1
}

function Resolve-DotNetExe {
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($DotNet)) { $candidates.Add($DotNet) }
    if (-not [string]::IsNullOrWhiteSpace($env:PERFORMIX_DOTNET)) { $candidates.Add($env:PERFORMIX_DOTNET) }
    $candidates.Add((Join-Path $repo 'tools\dotnet-sdk\dotnet.exe'))

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath
        }
    }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $onPath) { return $onPath.Source }
    return $null
}

# ------------------------------------------------------------------ 0. arguments
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.]+)?$') {
    Stop-WithMessage ("version '" + $Version + "' is not a three-part version (e.g. 0.1.0-alpha.3)")
}
if ($Configuration -ne 'Release') {
    Write-Warn ('configuration is ' + $Configuration + '; a release must be Release (docs\RELEASE.md section 2)')
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    Stop-WithMessage ('the WinUI project is missing: ' + $project)
}

$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$ZipPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ZipPath)

Write-Step ('version ' + $Version + ' / configuration ' + $Configuration)
Write-Step ('publish output  ' + $OutputDir)
Write-Step ('zip            ' + $ZipPath)

# ------------------------------------------------------------------ 1. SDK + publish
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-p:Platform=x64',
    ('-p:Version=' + $Version),
    '-r', 'win-x64',
    '-p:SelfContained=false',
    '-p:WindowsAppSDKSelfContained=true',
    '-nodeReuse:false',
    '-o', $OutputDir
)

$dotnetExe = Resolve-DotNetExe
$sdkOk = $false
if ($null -eq $dotnetExe) {
    if (-not $DryRun) {
        Stop-WithMessage @'
no dotnet.exe found. Set $env:PERFORMIX_DOTNET, put the portable SDK in tools\dotnet-sdk\,
or install the .NET 10 SDK. .NET 8/9 cannot target net10.0-windows.
'@
    }
    Write-Warn 'no dotnet.exe found - printing the plan without an SDK check'
} else {
    Write-Step ('dotnet         ' + $dotnetExe)
    $sdkList = & $dotnetExe --list-sdks 2>&1
    $sdkExit = $LASTEXITCODE
    $tenSdks = @()
    if ($sdkExit -eq 0) { $tenSdks = @($sdkList | Where-Object { $_ -match '^\s*10\.' }) }
    $sdkSummary = '(none)'
    if ($null -ne $sdkList -and @($sdkList).Count -gt 0) { $sdkSummary = (@($sdkList) -join ' | ') }
    if ($tenSdks.Count -gt 0) {
        $sdkOk = $true
        Write-Step ('SDK            ' + ($tenSdks -join ', '))
    } elseif ($DryRun) {
        Write-Warn ('.NET 10 SDK not found - the real run would stop here (installed: ' + $sdkSummary + ')')
    } else {
        Stop-WithMessage ('.NET 10 SDK not found (dotnet --list-sdks exit ' + $sdkExit + '). Installed: ' +
            $sdkSummary + '. .NET 8/9 cannot target net10.0-windows.')
    }
}

if ($DryRun) {
    Write-Step 'DRY RUN - nothing was built, nothing was written. The real run would execute:'
    if ($sdkOk -and $null -ne $dotnetExe) {
        Write-Host ('  ' + $dotnetExe + ' ' + ($publishArgs -join ' ')) -ForegroundColor DarkGray
    } else {
        Write-Host ('  dotnet ' + ($publishArgs -join ' ')) -ForegroundColor DarkGray
    }
    Write-Host ('  gate: ' + (Join-Path $OutputDir 'Performix.exe') + ' exists') -ForegroundColor DarkGray
    Write-Host ('  gate: ' + (Join-Path $OutputDir 'resources.pri') + ' exists') -ForegroundColor DarkGray
    Write-Host ('  gate: ' + (Join-Path $OutputDir 'scripts\invoke-update.ps1') + ' exists') -ForegroundColor DarkGray
    Write-Host ('  zip : deterministic container ' + $OutputDir + ' -> ' + $ZipPath) -ForegroundColor DarkGray
    Write-Host '        (ordinal-sorted entries, fixed 1980-01-01 timestamp, no directory entries,' -ForegroundColor DarkGray
    Write-Host '         fixed attributes, CompressionLevel.Optimal; Performix.exe at the root)' -ForegroundColor DarkGray
    Write-Host '  then print sha256 + sizeBytes for the manifest (valid for this build only)' -ForegroundColor DarkGray
    exit 0
}

if ($SkipPublish) {
    Write-Step '-SkipPublish: keeping the existing publish output'
    if (-not (Test-Path -LiteralPath $OutputDir -PathType Container)) {
        Stop-WithMessage ('-SkipPublish was given but the publish output does not exist: ' + $OutputDir)
    }
} else {
    # A clean output directory, otherwise a rename/drop between versions leaves stale files in
    # the package. Guarded: only ever delete under <repo>\artifacts.
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
    $fullOut = [System.IO.Path]::GetFullPath($OutputDir)
    if ($fullOut.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $fullOut -PathType Container)) {
        Write-Step ('cleaning ' + $fullOut)
        Remove-Item -LiteralPath $fullOut -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath $OutputDir -PathType Container)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    if ($null -ne $dotnetExe) {
        $sdkDir = Split-Path -Parent $dotnetExe
        $env:DOTNET_ROOT = $sdkDir
        $env:PATH = $sdkDir + ';' + $env:PATH
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

    Write-Step 'dotnet publish ...'
    & $dotnetExe @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage ('dotnet publish failed (exit ' + $LASTEXITCODE + ')')
    }
}

# ------------------------------------------------------------------ 2. hard gates
$exePath = Join-Path $OutputDir 'Performix.exe'
$priPath = Join-Path $OutputDir 'resources.pri'
$packagedUpdateScript = Join-Path $OutputDir 'scripts\invoke-update.ps1'
$stalePri = Join-Path $OutputDir 'Performix.pri'

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Stop-WithMessage ('Performix.exe is missing from the publish output: ' + $exePath)
}
if (-not (Test-Path -LiteralPath $priPath -PathType Leaf)) {
    Stop-WithMessage ('resources.pri is missing from the publish output: ' + $priPath + '. ' +
        'An unpackaged WinUI 3 app cannot start without it; check EnableMsixTooling=true in ' +
        'src\Performix.App\Performix.App.csproj and the build log.')
}
if (Test-Path -LiteralPath $stalePri -PathType Leaf) {
    # MRM prefers <exe name>.pri; a leftover Performix.pri was the root cause of a ~50% startup
    # crash (docs, csproj comment). Never ship it.
    Write-Warn ('removing stale ' + $stalePri + ' (MRM would load the wrong PRI)')
    Remove-Item -LiteralPath $stalePri -Force
}

if (-not (Test-Path -LiteralPath $packagedUpdateScript -PathType Leaf)) {
    # The csproj copies it (Content + CopyToOutputDirectory), but a package without it cannot
    # install itself, so restore it here rather than ship a broken update path.
    if (-not (Test-Path -LiteralPath $updateScriptSource -PathType Leaf)) {
        Stop-WithMessage ('scripts\invoke-update.ps1 is missing from the repository; the package ' +
            'could not update itself')
    }
    Write-Warn 'scripts\invoke-update.ps1 was not copied by the build; adding it to the package'
    $scriptDir = Split-Path -Parent $packagedUpdateScript
    if (-not (Test-Path -LiteralPath $scriptDir -PathType Container)) {
        New-Item -ItemType Directory -Path $scriptDir -Force | Out-Null
    }
    Copy-Item -LiteralPath $updateScriptSource -Destination $packagedUpdateScript -Force
}

# ------------------------------------------------------------------ 3. zip (deterministic container)
$zipParent = Split-Path -Parent $ZipPath
if (-not (Test-Path -LiteralPath $zipParent -PathType Container)) {
    New-Item -ItemType Directory -Path $zipParent -Force | Out-Null
}
if (Test-Path -LiteralPath $ZipPath -PathType Leaf) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

# ZipFile.CreateFromDirectory was the whole problem: it takes entry order from the file system,
# each entry's DOS timestamp from the file's LastWriteTime, and writes directory entries too - so
# two builds of byte-identical content produced different archives. Pin every one of those inputs
# here instead. This makes the CONTAINER deterministic; it does not make the content reproducible
# (see the REPRODUCIBILITY block in the header).
$rootFull = [System.IO.Path]::GetFullPath($OutputDir).TrimEnd('\', '/')
$entryNames = New-Object System.Collections.Generic.List[string]
foreach ($file in (Get-ChildItem -LiteralPath $OutputDir -Recurse -File -Force)) {
    $full = [System.IO.Path]::GetFullPath($file.FullName)
    if ($full.Length -le ($rootFull.Length + 1)) { continue }
    # Zip entry names always use '/', whatever the platform separator is.
    $entryNames.Add($full.Substring($rootFull.Length + 1).Replace('\', '/'))
}
$names = $entryNames.ToArray()
# Ordinal, not Sort-Object: a culture-aware comparison orders 'A' / 'a' / '_' differently
# depending on the machine locale, and that alone changes the bytes.
[Array]::Sort($names, [System.StringComparer]::Ordinal)

# Kind=Local on purpose: .NET converts a UTC DateTime to local time before writing the DOS time
# field, which would put the runner's time zone back into the archive. A fixed wall clock keeps
# the field identical everywhere. 1980-01-01 is the earliest value the DOS format can hold.
$fixedStamp = [datetime]::new(1980, 1, 1, 0, 0, 0, [System.DateTimeKind]::Local)

Write-Step ('compressing ' + $names.Count + ' entries (deterministic container) ...')
$zipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $archive = [System.IO.Compression.ZipArchive]::new(
        $zipStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        foreach ($name in $names) {
            $sourcePath = [System.IO.Path]::Combine($rootFull, $name.Replace('/', '\'))
            $entry = $archive.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedStamp
            $entry.ExternalAttributes = 0
            $entryStream = $entry.Open()
            try {
                $sourceStream = [System.IO.File]::OpenRead($sourcePath)
                try {
                    $sourceStream.CopyTo($entryStream)
                } finally {
                    $sourceStream.Dispose()
                }
            } finally {
                $entryStream.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
} finally {
    $zipStream.Dispose()
}
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    Stop-WithMessage ('the zip was not created: ' + $ZipPath)
}

# ------------------------------------------------------------------ 4. verify the zip layout
$archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
} finally {
    $archive.Dispose()
}
$rootExe = @($entries | Where-Object { $_ -eq 'Performix.exe' -or $_ -eq '.\Performix.exe' })
$rootUpdateScript = @($entries | Where-Object { $_ -match '^\.?[\\/]?scripts[\\/]invoke-update\.ps1$' })
if ($rootExe.Count -eq 0) {
    Stop-WithMessage ('Performix.exe is not at the zip root (entries: ' + (($entries | Select-Object -First 8) -join ', ') + '). ' +
        'scripts\invoke-update.ps1 looks for it at the root first.')
}
if ($rootUpdateScript.Count -eq 0) {
    Stop-WithMessage 'scripts\invoke-update.ps1 is not inside the zip; the package could not update itself'
}

# ------------------------------------------------------------------ 5. manifest inputs
$hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $ZipPath).Length
if ($size -le 0 -or $size -gt (1024L * 1024 * 1024)) {
    Stop-WithMessage ('the zip size ' + $size + ' is outside the 1 byte .. 1 GiB window the manifest accepts')
}

Write-Host ''
Write-Host '================ app package ================' -ForegroundColor Green
Write-Host ('zip        : ' + $ZipPath)
Write-Host ('sizeBytes  : ' + $size)
Write-Host ('sha256     : ' + $hash)
Write-Host ('entries    : ' + $entries.Count + ' (Performix.exe at root, scripts\invoke-update.ps1 present)')
Write-Host '============================================' -ForegroundColor Green
Write-Host ''
Write-Host 'REPRODUCIBILITY - read this before using the hash:' -ForegroundColor Yellow
Write-Host '  deterministic   : the zip CONTAINER (ordinal-sorted entries, one fixed 1980-01-01' -ForegroundColor DarkGray
Write-Host '                    timestamp, no directory entries, fixed attributes, Optimal level)' -ForegroundColor DarkGray
Write-Host '  NOT deterministic: the CONTENT - .NET SDK patch version, Windows App SDK / NuGet' -ForegroundColor DarkGray
Write-Host '                    restore graph, ReadyToRun/PGO/ILLink, and the absolute source paths' -ForegroundColor DarkGray
Write-Host '                    baked into the .pdb files all change the bytes.' -ForegroundColor DarkGray
Write-Host '  => THIS SHA256 IS VALID FOR THIS BUILD ONLY. The same commit built on the CI runner' -ForegroundColor Yellow
Write-Host '     produces a different sha256. The CI-built asset is the one that ships; a local build' -ForegroundColor Yellow
Write-Host '     is for verifying the release, never for the numbers in the manifest' -ForegroundColor Yellow
Write-Host '     (docs\RELEASE.md section 2.1).' -ForegroundColor Yellow
Write-Host ''
Write-Host 'scripts\publish-release.ps1 writes these values into the manifest; when the tag also' -ForegroundColor DarkGray
Write-Host 'fires the release workflow it reconciles the manifest with the PUBLISHED asset first.' -ForegroundColor DarkGray
exit 0
