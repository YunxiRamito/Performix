# Performix no-driver fork — build script
# Usage:  pwsh -File scripts\build-fork.ps1            # build Release + publish to dist\
#         pwsh -File scripts\build-fork.ps1 -Test      # additionally run the test project
#
# SDK resolution: $env:PERFORMIX_DOTNET, else <repo>\tools\dotnet-sdk\dotnet.exe
# (the portable SDK is ~818 MB and is NOT committed; tools/ is git-ignored).
# The system-wide .NET installation is never touched.
param(
    [switch]$Test,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'

$scriptsDir = $PSScriptRoot                                  # <repo>\scripts
$repoDir = Split-Path -Parent $scriptsDir                    # <repo>

function Resolve-DotNet {
    if ($env:PERFORMIX_DOTNET) {
        $explicit = $env:PERFORMIX_DOTNET
        if (-not (Test-Path $explicit)) {
            throw "PERFORMIX_DOTNET 指向的文件不存在：$explicit"
        }
        return (Resolve-Path $explicit).Path
    }
    $default = Join-Path $repoDir 'tools\dotnet-sdk\dotnet.exe'
    if (-not (Test-Path $default)) {
        throw @"
找不到 .NET 10 SDK。二选一：
  1) 设置环境变量 PERFORMIX_DOTNET 指向 dotnet.exe（推荐）
  2) 把便携 SDK 放到 $default（tools\ 已被 .gitignore 忽略，不会提交）
"@
    }
    return $default
}

$dotnet = Resolve-DotNet
$sdkDir = Split-Path -Parent $dotnet

$env:DOTNET_ROOT = $sdkDir
$env:PATH = "$sdkDir;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$solution = Join-Path $repoDir 'Performix.slnx'
$frontend = Join-Path $repoDir 'src\Performix.Legacy.Wpf\Performix.Legacy.Wpf.csproj'
$probe = Join-Path $repoDir 'src\Performix.Probe\Performix.Probe.csproj'
$dist = Join-Path $repoDir 'dist'
$tests = Join-Path $repoDir 'tests\Performix.Tests\Performix.Tests.csproj'

Write-Host "SDK: $(& $dotnet --version)  ($dotnet)" -ForegroundColor Cyan
& $dotnet build $solution -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $NoPublish) {
    & $dotnet publish $frontend -c Release -o $dist
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    # Read-only sensor self-check (pipe / NVML / ACPI + fan-curve dry run), no GUI needed.
    & $dotnet publish $probe -c Release -o $dist
    if ($LASTEXITCODE -ne 0) { throw "probe publish failed" }

    Write-Host "published   -> $dist\Predex.exe（WPF 回滚版）" -ForegroundColor Green
    Write-Host "self-check  -> $dist\PerformixProbe.exe" -ForegroundColor Green
}

if ($Test) {
    & $dotnet test $tests -c Release --no-build
}
