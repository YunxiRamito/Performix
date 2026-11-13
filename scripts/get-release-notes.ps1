#Requires -Version 5.1
<#
.SYNOPSIS
    Release notes for one version: the matching CHANGELOG.md section, or a short fallback.

.DESCRIPTION
    docs\RELEASE.md section 5 requires the release body to be prose a user can read. A list of
    commit subjects is neither readable nor scoped to the release (it drags in everything since
    the previous tag, including work that shipped earlier), so notes are never taken from git log.

    This script is the single place that decides where notes come from, so the CI workflow
    (.github\workflows\release.yml) and scripts\publish-release.ps1 cannot drift:

        1. The CHANGELOG.md section for the version. A heading matches when it is
           '## <version> ...', '## v<version> ...' or '## [<version>] ...' at any level; the body
           runs until the next heading of the same or a higher level, with blank lines trimmed
           at both ends.
        2. Fallback when that section is missing or empty: the version, one summary line and the
           CHANGELOG URL. A release never fails because a notes section is missing.

    The notes are written UTF-8 without BOM, because scripts\publish-release.ps1 reads the file
    as UTF-8 and puts the same text into update.json.

    ENGLISH ONLY, UTF-8 WITHOUT BOM. Windows PowerShell 5.1 reads a BOM-less script as ANSI, so a
    single non-ASCII literal would be corrupted before it ever runs - which is why the fallback
    template is English. The notes themselves may be any language: they come from CHANGELOG.md,
    which is decoded as UTF-8 explicitly and is never re-encoded.

.PARAMETER Version
    Release version whose section is wanted, e.g. 0.1.0-alpha.3.

.PARAMETER Changelog
    CHANGELOG.md to read. Default <repo>\CHANGELOG.md.

.PARAMETER OutputFile
    Write the notes here (UTF-8 without BOM) instead of printing them to stdout.

.PARAMETER Summary
    One line used by the fallback template. Default: a pointer at CHANGELOG.md.

.PARAMETER RepositorySlug
    owner/name, used for the CHANGELOG URL in the fallback template.

.PARAMETER Branch
    Branch used for the CHANGELOG URL in the fallback template. Default main.

.EXAMPLE
    pwsh -NoProfile -File scripts\get-release-notes.ps1 -Version 0.1.0-alpha.3 -OutputFile artifacts\release-notes.md

.EXAMPLE
    pwsh -NoProfile -File scripts\get-release-notes.ps1 -Version 0.1.0-alpha.3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Changelog,

    [string]$OutputFile,

    [string]$Summary,

    [string]$RepositorySlug = 'YunxiRamito/Performix',

    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$repo = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Changelog)) {
    $Changelog = Join-Path $repo 'CHANGELOG.md'
}
if ([string]::IsNullOrWhiteSpace($Summary)) {
    $Summary = 'See CHANGELOG.md for the full list of changes in this release.'
}

# Diagnostics go to stderr, never to stdout: callers pipe stdout into a notes variable or a file,
# and Write-Host from a child process lands on that stdout, which would put log lines into the
# release body.
function Write-Note {
    param([string]$Message)
    [Console]::Error.WriteLine('[release-notes] ' + $Message)
}

function Write-Warn {
    param([string]$Message)
    [Console]::Error.WriteLine('[release-notes] WARN: ' + $Message)
}

# Returns the section body for $VersionText, or $null when there is no such heading.
function Get-ChangelogSection {
    param(
        [string[]]$Lines,
        [string]$VersionText)

    $headingPattern = '^\s{0,3}(#{1,6})\s+(?:\[)?v?' + [regex]::Escape($VersionText) + '(?:\])?(?=\s|$)'
    $anyHeadingPattern = '^\s{0,3}(#{1,6})\s+'

    $start = -1
    $level = 0
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $match = [regex]::Match($Lines[$i], $headingPattern)
        if ($match.Success) {
            $start = $i
            $level = $match.Groups[1].Value.Length
            break
        }
    }
    if ($start -lt 0) { return $null }

    $body = New-Object System.Collections.Generic.List[string]
    for ($i = $start + 1; $i -lt $Lines.Count; $i++) {
        $heading = [regex]::Match($Lines[$i], $anyHeadingPattern)
        if ($heading.Success -and $heading.Groups[1].Value.Length -le $level) { break }
        $body.Add($Lines[$i])
    }
    if ($body.Count -eq 0) { return $null }

    $text = ($body -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text
}

$source = 'CHANGELOG.md'
$text = $null

if (Test-Path -LiteralPath $Changelog -PathType Leaf) {
    $raw = $Utf8NoBom.GetString([System.IO.File]::ReadAllBytes($Changelog))
    $raw = $raw.TrimStart([char]0xFEFF)
    $lines = $raw -split "\r?\n"
    $text = Get-ChangelogSection -Lines $lines -VersionText $Version
    if ($null -eq $text) {
        Write-Warn ('no section for ' + $Version + ' in ' + $Changelog + '; using the fallback template')
    }
} else {
    Write-Warn ('no changelog at ' + $Changelog + '; using the fallback template')
}

if ([string]::IsNullOrWhiteSpace($text)) {
    $source = 'fallback template'
    $text = @(
        ('Performix ' + $Version),
        '',
        $Summary,
        '',
        ('CHANGELOG: https://github.com/' + $RepositorySlug + '/blob/' + $Branch + '/CHANGELOG.md')
    ) -join "`n"
}

$output = $text.TrimEnd() + "`n"

if (-not [string]::IsNullOrWhiteSpace($OutputFile)) {
    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFile)
    $parent = Split-Path -Parent $resolved
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($resolved, $output, $Utf8NoBom)
    Write-Note ('notes from ' + $source + ' -> ' + $resolved + ' (' + $text.TrimEnd().Length + ' chars)')
} else {
    Write-Note ('notes from ' + $source + ' (' + $text.TrimEnd().Length + ' chars)')
    Write-Output $text.TrimEnd()
}
exit 0
