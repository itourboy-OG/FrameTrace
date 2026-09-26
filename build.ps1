<# Builds a self-contained Windows x64 app and an Inno Setup installer. Dependencies stay pinned and local to the build. #>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$presentMon = Join-Path $projectRoot 'vendor/PresentMon.exe'
$expectedHash = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
if (-not (Test-Path -LiteralPath $presentMon)) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' -OutFile $presentMon
            break
        } catch {
            if ($attempt -eq 3) { throw }
            Write-Warning ('PresentMon download failed; retry {0}/3. Details: {1}' -f $attempt, $_.Exception.Message)
            Start-Sleep -Seconds 2
        }
    }
}
if ((Get-FileHash -LiteralPath $presentMon -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'PresentMon SHA-256 mismatch. Verify vendor/PresentMon.exe against the official v2.6.0 release before building.'
}
$publishDirectory = Join-Path $projectRoot 'artifacts/app-0.7.14'
dotnet publish (Join-Path $projectRoot 'src/Frameglass.csproj') -c Release -r win-x64 --self-contained true -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "Install Inno Setup 6 to build the installer. The portable application is already built at $publishDirectory." }
& $compiler (Join-Path $projectRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
$desktopReleases = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Frame Trace'
New-Item -ItemType Directory -Path $desktopReleases -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot '../FrameTrace-0.7.14-Setup.exe') -Destination $desktopReleases
Write-Output "Installer copied to $desktopReleases"
