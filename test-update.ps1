param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
function Stop-TestSession {
    param([Parameter(Mandatory = $true)][string]$Binary, [Parameter(Mandatory = $true)][string]$Session)
    $stop = Start-Process -FilePath $Binary -ArgumentList '--session_name', $Session, '--terminate_existing_session' -WindowStyle Hidden -PassThru
    try {
        if (-not $stop.WaitForExit(8000)) { $stop.Kill(); throw "Test session stop timed out: $Session" }
        if ($stop.ExitCode -ne 0) { throw "Test session stop failed: $Session, exit $($stop.ExitCode)" }
    } finally { $stop.Dispose() }
}

$testRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$release = Join-Path $PSScriptRoot 'artifacts/app-0.7.13'
$ready = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.ready')
$probe = Start-Process -FilePath (Join-Path $release 'FrameTrace.exe') -ArgumentList @('--capture-lifetime-probe', ('"{0}"' -f $ready)) -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($probe.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Capture lifetime probe did not start.' }
        Start-Sleep -Milliseconds 100
    }
    $child = @(Get-CimInstance Win32_Process -Filter "Name='PresentMon.exe'" | Where-Object ParentProcessId -eq $probe.Id)
    if ($child.Count -ne 1) { throw 'Expected exactly one real capture child.' }
    $capture = [Diagnostics.Process]::GetProcessById($child[0].ProcessId)
    try {
        $probe.Kill(); $probe.WaitForExit()
        if (-not $capture.WaitForExit(5000)) { throw 'Capture child survived dashboard termination.' }
    } finally { $capture.Dispose() }
    $session = [regex]::Match($child[0].CommandLine, '--session_name\s+(\S+)').Groups[1].Value
    & logman.exe query $session -ets | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Crash probe did not leave a Windows trace session to validate recovery.' }
    $orphanReclaimed = $false
    $recoveryReady = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.ready')
    $recovery = Start-Process -FilePath (Join-Path $release 'FrameTrace.exe') -ArgumentList @('--capture-lifetime-probe', ('"{0}"' -f $recoveryReady)) -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        while (-not (Test-Path -LiteralPath $recoveryReady)) {
            if ($recovery.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Crash recovery capture did not start.' }
            Start-Sleep -Milliseconds 100
        }
        & logman.exe query $session -ets | Out-Null
        if ($LASTEXITCODE -eq 0) { throw 'Restarting Frame Trace did not reclaim the abandoned trace session.' }
        $orphanReclaimed = $true
        if (-not $recovery.WaitForExit(35000)) { throw 'Recovery probe failed to shut down.' }
        if ($recovery.ExitCode -ne 0) { throw "Recovery probe failed: $($recovery.ExitCode)." }
    } finally {
        if (-not $recovery.HasExited) { $recovery.Kill(); $recovery.WaitForExit() }
        $recovery.Dispose()
        if (-not $orphanReclaimed) { Stop-TestSession -Binary (Join-Path $release 'vendor/PresentMon.exe') -Session $session }
    }
} finally {
    if (-not $probe.HasExited) { $probe.Kill(); $probe.WaitForExit() }
    $probe.Dispose()
}
$owners = @()
try {
    foreach ($directory in @('installation', 'other-installation')) {
        $vendor = Join-Path $testRoot "$directory/vendor"
        New-Item -ItemType Directory -Path $vendor -Force | Out-Null
        $binary = Join-Path $vendor 'PresentMon.exe'
        Copy-Item -LiteralPath (Join-Path $release 'vendor/PresentMon.exe') -Destination $binary
        $session = 'FrameTrace-' + $PID + '-' + [Guid]::NewGuid().ToString('N')
        $capture = Start-Process -FilePath $binary -ArgumentList @('--session_name', $session, '--no_csv', '--no_console_stats') -WindowStyle Hidden -PassThru
        $owners += [PSCustomObject]@{ Process = $capture; Session = $session; Binary = $binary }
    }
    Start-Sleep -Seconds 2
    if ($owners[0].Process.HasExited -or $owners[1].Process.HasExited) { throw 'Real upgrade capture probes did not remain running.' }
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'prepare-update.ps1') -InstallDirectory (Join-Path $testRoot 'installation')
    if ($LASTEXITCODE -ne 0) { throw "Upgrade preparation failed with exit $LASTEXITCODE." }
    if (-not $owners[0].Process.HasExited) { throw 'Upgrade helper was not stopped.' }
    if ($owners[1].Process.HasExited) { throw 'Upgrade stopped a different installation.' }
    $file = [IO.File]::Open($owners[0].Binary, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $file.Dispose()
    'PASS: abrupt dashboard exit stops its real capture child; next startup reclaims its abandoned Windows trace session; update preparation stops only the target installation and releases its executable.' | Set-Content -LiteralPath (Join-Path $testRoot 'result.txt')
} finally {
    foreach ($owner in $owners) {
        if (-not $owner.Process.HasExited) {
            Stop-TestSession -Binary $owner.Binary -Session $owner.Session
            if (-not $owner.Process.WaitForExit(5000)) { $owner.Process.Kill(); $owner.Process.WaitForExit() }
        }
        $owner.Process.Dispose()
    }
}
