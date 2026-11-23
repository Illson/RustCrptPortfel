[CmdletBinding()]
param(
    [ValidateSet('run', 'test', 'stop')][string]$Mode = 'run',
    [ValidateRange(1, 65535)][int]$Port,
    [switch]$NoBrowser,
    [switch]$ChatGptLab
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
    else { Write-Host 'No local demo is running.' }
    return
}

$testing = $Mode -eq 'test'
if (-not $Port) { $Port = if ($testing) { 5081 } else { 5080 } }
$origin = "http://127.0.0.1:$Port"
$data = if ($testing) { Join-Path $repo ".portfel/tests/$([guid]::NewGuid().ToString('N'))" }
        else { Join-Path $repo '.portfel/local' }
$artifacts = Join-Path $data 'artifacts'
$frontend = Join-Path $data 'dist'

function Invoke-Tool($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $exe) failed with exit code $LASTEXITCODE." }
}

function Start-ServiceProcess($name, [string[]]$extra = @()) {
    $arguments = @(
        (Join-Path $artifacts "bin/$name/debug/$name.dll")
        '--environment=Development'
        '--Demo:Enabled=true'
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

$api = $worker = $lock = $signal = $null
$apiLogs = @()
$passed = $false
$previousE2eUrl = $env:PORTFEL_E2E_URL
$previousLabE2e = $env:PORTFEL_E2E_CHATGPT_LAB
$previousPath = $env:Path
Push-Location -LiteralPath $repo
try {
    $tools = Join-Path $repo '.portfel/tools'
    if (Test-Path -LiteralPath (Join-Path $tools 'node.exe') -PathType Leaf) { $env:Path = "$tools;$env:Path" }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue).Source
    $pnpm = (Get-Command pnpm -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $dotnet) { throw 'Install the .NET SDK specified in global.json.' }
    if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue) -or -not $pnpm) {
        throw 'Install Node.js 24.22.3 and pnpm 11.25.0 (npm.cmd install -g pnpm@11.25.0), then reopen PowerShell.'
    }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() }
    catch { throw "Cannot listen at $origin. Stop the other server or choose a different -Port." }
    finally { $listener.Stop() }

    New-Item -ItemType Directory -Path $data -Force | Out-Null
    if (-not $testing) {
        try { $lock = [IO.File]::Open((Join-Path $data 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch { throw "Cannot lock $data. Stop the other local demo if it is running. $_" }
        $signal = [Threading.EventWaitHandle]::new($false, 'ManualReset', $stopName)
    }
    Write-Host "Data and logs: $data"

    Invoke-Tool $dotnet @('restore', 'dotnetCrptPortfel.slnx', '--locked-mode', '--artifacts-path', $artifacts)
    Invoke-Tool $dotnet @('build', 'dotnetCrptPortfel.slnx', '--no-restore', '-warnaserror', '-c', 'Debug', '--artifacts-path', $artifacts)
    Invoke-Tool $pnpm @('--dir', 'frontend', 'install', '--frozen-lockfile')
    Invoke-Tool $pnpm @('--dir', 'frontend', 'build', '--outDir', $frontend, '--emptyOutDir')

    if ($testing) {
        foreach ($project in 'DotnetCrptPortfel.Tests', 'App.Tests') {
            Invoke-Tool $dotnet @('run', '--project', "tests/$project", '--no-build', '-c', 'Debug', '--artifacts-path', $artifacts)
        }
        if (-not $env:PORTFEL_E2E_CHANNEL) {
            Invoke-Tool $pnpm @('--dir', 'frontend', 'exec', 'playwright', 'install', 'chromium')
        }
    }

    $apiArgs = @(
        "--Frontend:Directory=$frontend"
        "--Security:KeyDirectory=$(Join-Path $data 'keys')"
    )
    if ($ChatGptLab) {
        $apiArgs += @('--ChatGptLab:Enabled=true', "--ChatGptLab:Port=$Port", "--ChatGptLab:Directory=$(Join-Path $data 'chatgpt-lab')")
    }
    else { $apiArgs += "--urls=$origin" }
    $api = Start-ServiceProcess 'App.Api' $apiArgs
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

    # Let the API finish the SQLite migration before starting the worker.
    $worker = Start-ServiceProcess 'App.Worker'
    Assert-Running $worker 'App.Worker'
    if ($testing) {
        $env:PORTFEL_E2E_URL = $origin
        if ($ChatGptLab) { $env:PORTFEL_E2E_CHATGPT_LAB = '1' }
        Invoke-Tool $pnpm @('--dir', 'frontend', 'test:e2e', '--output', (Join-Path $data 'playwright'))
        $passed = $true
    }
    else {
        Write-Host "Demo: $origin (run local.cmd stop in another terminal to stop)"
        if (-not $NoBrowser) {
            try { Start-Process $(if ($ChatGptLab) { "$origin/chatgpt-lab" } else { $origin }) }
            catch { Write-Warning "Open $origin in your browser." }
        }
        do {
            Write-ApiLog $apiLogs
            Assert-Running $api 'App.Api'
            Assert-Running $worker 'App.Worker'
        } until ($signal.WaitOne(250))
    }
}
finally {
    $env:PORTFEL_E2E_URL = $previousE2eUrl
    $env:PORTFEL_E2E_CHATGPT_LAB = $previousLabE2e
    $env:Path = $previousPath
    foreach ($process in @($worker, $api)) {
        if ($process) {
            if (-not $process.HasExited) { Stop-Process -InputObject $process -ErrorAction SilentlyContinue }
            [void]$process.WaitForExit(5000)
            $process.Dispose()
        }
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
