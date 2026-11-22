[CmdletBinding()]
param(
    [ValidateSet('run', 'test', 'stop')][string]$Mode = 'run',
    [ValidateRange(1, 65535)][int]$Port,
    [switch]$NoBrowser,
    [switch]$NoNode
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$hash = [Security.Cryptography.SHA256]::Create()
try { $repoId = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToUpperInvariant()))) }
finally { $hash.Dispose() }
$stopName = "Local\Portfel-$repoId"
if ($Mode -eq 'stop') {
    $signal = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting($stopName, [ref]$signal)) {
        try { [void]$signal.Set() }
        finally { $signal.Dispose() }
        Write-Host 'Stop requested.'
    }
    else { Write-Host 'No local instance is running.' }
    return
}

$testing = $Mode -eq 'test'
if (-not $Port) { $Port = if ($testing) { 5081 } else { 5080 } }
$origin = "http://127.0.0.1:$Port"
$data = if ($testing) { Join-Path $repo ".portfel/tests/$([guid]::NewGuid().ToString('N'))" }
        else { Join-Path $repo '.portfel/local' }
$artifacts = Join-Path $data 'artifacts'
$frontend = if ($NoNode) { Join-Path $repo 'frontend/dist' } else { Join-Path $data 'dist' }

function Invoke-Tool($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $exe) failed with exit code $LASTEXITCODE." }
}

function Start-ServiceProcess($name, [string[]]$extra = @()) {
    $arguments = @(
        (Join-Path $artifacts "bin/$name/debug/$name.dll")
        '--environment=Development'
        "--Storage:DatabasePath=$(Join-Path $data 'portfel.db')"
    ) + $extra
    # Start-Process joins its arguments into a Windows command line.
    Start-Process -FilePath $dotnet -ArgumentList ($arguments | ForEach-Object { '"' + $_ + '"' }) `
        -WorkingDirectory (Join-Path $repo "src/$name") -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $data "$name.log") -RedirectStandardError (Join-Path $data "$name.err.log")
}

function Assert-Running($process, $name) {
    if ($process.HasExited) {
        throw "$name exited ($($process.ExitCode)). See $name.log and $name.err.log in $data."
    }
}

function Write-ApiLog([IO.StreamReader[]]$readers) {
    foreach ($reader in $readers) {
        while ($null -ne ($line = $reader.ReadLine())) { Write-Host "[api] $line" }
    }
}

$api = $lock = $signal = $null
$apiLogs = @()
$passed = $false
$previousE2eUrl = $env:PORTFEL_E2E_URL
$previousPath = $env:Path
Push-Location -LiteralPath $repo
try {
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { throw 'Install the .NET SDK specified in global.json.' }
    if ($NoNode) {
        if (-not (Test-Path -LiteralPath (Join-Path $frontend 'index.html') -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $frontend 'assets') -PathType Container)) {
            throw 'No frontend bundle at frontend/dist. Build it once with pnpm --dir frontend build or unpack a ready bundle there.'
        }
    }
    else {
        $toolDirs = @(Join-Path $repo '.portfel/tools')
        $git = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue).Source
        if ($git -and (Test-Path -LiteralPath (Join-Path $repo '.git'))) {
            $commonDir = & $git rev-parse --path-format=absolute --git-common-dir 2>$null
            if ($LASTEXITCODE -eq 0 -and $commonDir) {
                $toolDirs += Join-Path (Split-Path -Parent $commonDir) '.portfel/tools'
            }
        }
        foreach ($tools in $toolDirs) {
            if ((Test-Path -LiteralPath (Join-Path $tools 'node.exe') -PathType Leaf) -and
                (Test-Path -LiteralPath (Join-Path $tools 'pnpm.cmd') -PathType Leaf)) {
                $env:Path = "$tools;$env:Path"
                break
            }
        }
        $pnpm = (Get-Command pnpm -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
        if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue) -or -not $pnpm) {
            throw 'To build the frontend, install Node.js 24.22.3 and pnpm 11.25.0. To use a ready frontend/dist bundle, run local.cmd -NoNode.'
        }
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() }
    catch { throw "Cannot listen at $origin. Stop the other server or choose a different -Port." }
    finally { $listener.Stop() }

    New-Item -ItemType Directory -Path $data -Force | Out-Null
    if (-not $testing) {
        try { $lock = [IO.File]::Open((Join-Path $data 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch { throw "Cannot lock $data. Stop the other local instance if it is running. $_" }
        $signal = [Threading.EventWaitHandle]::new($false, 'ManualReset', $stopName)
    }
    Write-Host "Data and logs: $data"

    Invoke-Tool $dotnet @('restore', 'dotnetCrptPortfel.slnx', '--locked-mode', '--artifacts-path', $artifacts)
    Invoke-Tool $dotnet @('build', 'dotnetCrptPortfel.slnx', '--no-restore', '-warnaserror', '-c', 'Debug', '--artifacts-path', $artifacts)
    if (-not $NoNode) {
        Invoke-Tool $pnpm @('--dir', 'frontend', 'install', '--frozen-lockfile')
        Invoke-Tool $pnpm @('--dir', 'frontend', 'build', '--outDir', $frontend, '--emptyOutDir')
    }

    if ($testing) {
        foreach ($project in 'DotnetCrptPortfel.Tests', 'App.Tests') {
            Invoke-Tool $dotnet @('run', '--project', "tests/$project", '--no-build', '-c', 'Debug', '--artifacts-path', $artifacts)
        }
        if (-not $NoNode -and -not $env:PORTFEL_E2E_CHANNEL) {
            Invoke-Tool $pnpm @('--dir', 'frontend', 'exec', 'playwright', 'install', 'chromium')
        }
    }

    $api = Start-ServiceProcess 'App.Api' @(
        "--urls=$origin"
        "--Frontend:Directory=$frontend"
        "--Security:KeyDirectory=$(Join-Path $data 'keys')"
    )
    if (-not $testing) {
        foreach ($file in 'App.Api.log', 'App.Api.err.log') {
            $stream = [IO.File]::Open((Join-Path $data $file), 'Open', 'Read', 'ReadWrite')
            $apiLogs += [IO.StreamReader]::new($stream)
        }
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        Assert-Running $api 'App.Api'
        try { $ready = (Invoke-RestMethod "$origin/api/v1/health/ready" -TimeoutSec 2).status -eq 'ready' }
        catch { $ready = $false }
        if (-not $ready) { Start-Sleep -Milliseconds 250 }
    } until ($ready -or $timer.Elapsed.TotalSeconds -ge 30)
    if (-not $ready) { throw "API not ready at $origin. See the App.Api logs in $data." }

    if ($testing) {
        if ($NoNode) {
            $page = Invoke-WebRequest "$origin/" -UseBasicParsing -TimeoutSec 5
            if ($page.StatusCode -ne 200 -or $page.Content -cne [IO.File]::ReadAllText((Join-Path $frontend 'index.html'))) {
                throw 'The API did not serve the prebuilt frontend.'
            }
            Write-Host 'PASS prebuilt frontend served by ASP.NET Core. Playwright skipped (-NoNode).'
        }
        else {
            $env:PORTFEL_E2E_URL = $origin
            Invoke-Tool $pnpm @('--dir', 'frontend', 'test:e2e', '--output', (Join-Path $data 'playwright'))
        }
        $passed = $true
    }
    else {
        Write-Host "Portfolio: $origin (run local.cmd stop in another terminal to stop)"
        if (-not $NoBrowser) {
            try { Start-Process $origin }
            catch { Write-Warning "Open $origin in your browser." }
        }
        do {
            Write-ApiLog $apiLogs
            Assert-Running $api 'App.Api'
        } until ($signal.WaitOne(250))
    }
}
finally {
    $env:PORTFEL_E2E_URL = $previousE2eUrl
    $env:Path = $previousPath
    if ($api) {
        if (-not $api.HasExited) { Stop-Process -InputObject $api -ErrorAction SilentlyContinue }
        [void]$api.WaitForExit(5000)
        $api.Dispose()
    }
    try { Write-ApiLog $apiLogs }
    finally { foreach ($reader in $apiLogs) { $reader.Dispose() } }
    if ($signal) { $signal.Dispose() }
    if ($lock) { $lock.Dispose() }
    Pop-Location
    if ($passed) {
        Remove-Item -LiteralPath $data -Recurse -Force
    }
}
