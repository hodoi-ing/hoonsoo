param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if (Test-Path $Dotnet) {
        $dotnetPath = (Resolve-Path $Dotnet).Path
        $dotnetDir = Split-Path $dotnetPath -Parent
        $env:DOTNET_ROOT = $dotnetDir
        $env:PATH = "$dotnetDir;$env:PATH"
        $Dotnet = $dotnetPath
    }
    $testProj = Join-Path $repo 'tests/hoonsoo.Tests/hoonsoo.Tests.csproj'
    $srcProj = Join-Path $repo 'src/hoonsoo/hoonsoo.csproj'
    $release = Join-Path $repo 'outputs/hoonsoo-win-x64'
    $testDll = Join-Path $repo 'tests/hoonsoo.Tests/bin/Release/net10.0-windows10.0.19041.0/hoonsoo.Tests.dll'

    # Direct invocation instead of Start-Process -Wait: with this script's stdout redirected (WSL, CI,
    # any non-console host) Start-Process -NoNewWindow -Wait can block forever with no child to show for
    # it — the run looked alive for minutes while nothing was writing. Calling dotnet directly streams the
    # same output, keeps exit codes honest, and passes paths with spaces as single arguments.
    function Invoke-Dotnet([string[]]$arguments) {
        & $Dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
    }
    Invoke-Dotnet @('build', $testProj, '-c', 'Release', '-v:minimal')
    Invoke-Dotnet @('publish', $srcProj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $release, '-p:DebugType=None', '-p:DebugSymbols=false', '-v:minimal')
    $started = Get-Date
    Invoke-Dotnet @($testDll)
    $resultsFile = Join-Path (Split-Path $testDll) 'test-results.json'
    if ((Get-Item $resultsFile).LastWriteTime -lt $started) { throw 'No fresh test results' }
    $results = Get-Content $resultsFile -Raw | ConvertFrom-Json
    if (@($results).Count -eq 0 -or @($results | Where-Object { !$_.passed }).Count -gt 0) { throw 'Tests failed' }

    Copy-Item (Join-Path $repo 'README.md') (Join-Path $release 'README.ko.md') -Force
    Copy-Item (Join-Path $repo 'scripts/Start-hoonsoo.cmd') $release -Force
    $prerequisites = Join-Path $release 'prerequisites'
    New-Item -ItemType Directory -Force $prerequisites | Out-Null
    $redist = Join-Path $prerequisites 'vc_redist.x64.exe'
    if (!(Test-Path $redist)) { Invoke-WebRequest https://aka.ms/vs/17/release/vc_redist.x64.exe -OutFile $redist }
    if ((Get-AuthenticodeSignature $redist).Status -ne 'Valid') { throw 'Microsoft prerequisite signature validation failed' }
    Copy-Item (Join-Path $repo 'tests/hoonsoo.Tests/bin/Release/net10.0-windows10.0.19041.0/test-results.json') (Join-Path $release 'test-results.json') -Force
    Compress-Archive -Path "$release\*" -DestinationPath (Join-Path $repo 'outputs/hoonsoo-win-x64.zip') -Force
    Write-Host "Release ready: $release"
} finally { Pop-Location }
