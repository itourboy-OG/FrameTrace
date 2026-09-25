<# Builds an isolated Preview app and installer without changing release artifacts. #>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$publishDirectory = Join-Path $projectRoot 'artifacts/preview-0.7.10'
$installerScript = Join-Path $projectRoot 'installer-preview.iss'
$desktopReleases = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Frame Trace'

dotnet publish (Join-Path $projectRoot 'src/Frameglass.csproj') -c Release -r win-x64 --self-contained true -p:PreviewBuild=true -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw "Preview publish failed with exit code $LASTEXITCODE." }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory

$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "Install Inno Setup 6 to build the Preview installer. The portable Preview app is already built at $publishDirectory." }
& $compiler $installerScript
if ($LASTEXITCODE -ne 0) { throw "Preview installer build failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Path $desktopReleases -Force | Out-Null
$installer = Join-Path $projectRoot 'artifacts/preview/FrameTracePreview-0.7.10-Setup.exe'
Copy-Item -LiteralPath $installer -Destination $desktopReleases
Write-Output "Preview installer copied to $(Join-Path $desktopReleases 'FrameTracePreview-0.7.10-Setup.exe')"
