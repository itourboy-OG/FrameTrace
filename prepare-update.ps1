param([Parameter(Mandatory = $true)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$appPaths = @((Join-Path $installRoot 'FrameTrace.exe'), (Join-Path $installRoot 'Frameglass.exe'))
$capturePath = Join-Path $installRoot 'vendor/PresentMon.exe'
$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
try {
    foreach ($instance in @(Get-CimInstance Win32_Process -Filter "Name='FrameTrace.exe' OR Name='Frameglass.exe'")) {
        if ($instance.ExecutablePath -notin $appPaths) { continue }
        $app = [Diagnostics.Process]::GetProcessById($instance.ProcessId)
        try {
            if ($app.MainModule.FileName -notin $appPaths) { throw 'Application identity changed during update preparation.' }
            if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(20000)) {
                throw "Close Frame Trace before updating. Process $($instance.ProcessId) has not finished shutting down."
            }
        } finally { $app.Dispose() }
    }
    foreach ($instance in @(Get-CimInstance Win32_Process -Filter "Name='PresentMon.exe'")) {
        if (-not [string]::Equals($instance.ExecutablePath, $capturePath, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($instance.CommandLine -notmatch '--session_name\s+(Frame(?:glass|Trace)-\d+-[a-fA-F0-9]{32})(?:\s|$)') { continue }
        $sessionName = $Matches[1]
        $capture = [Diagnostics.Process]::GetProcessById($instance.ProcessId)
        try {
            if (-not [string]::Equals($capture.MainModule.FileName, $capturePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Capture identity changed during update preparation.' }
            $stop = Start-Process -FilePath $capturePath -ArgumentList @('--session_name', $sessionName, '--terminate_existing_session') -WindowStyle Hidden -PassThru
            try {
                if (-not $stop.WaitForExit(8000)) { $stop.Kill(); throw "Timed out stopping capture session $sessionName." }
                if ($stop.ExitCode -ne 0) { throw "Cannot stop capture session $sessionName (exit $($stop.ExitCode))." }
            } finally { $stop.Dispose() }
            if (-not $capture.WaitForExit(3000)) { $capture.Kill(); $capture.WaitForExit() }
            Write-Output "Stopped Frame Trace capture PID $($instance.ProcessId)."
        } finally { $capture.Dispose() }
    }
    foreach ($path in $appPaths + @($capturePath)) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $file.Dispose()
    }
    exit 0
} catch {
    Write-Error -Message ("Cannot prepare Frame Trace update in '{0}': {1}" -f $installRoot, $_.Exception.Message) -ErrorAction Continue
    if (-not $isAdministrator) { exit 5 }
    exit 1
}
