# Produces a self-contained, unpackaged Release build in dist\KineticTrainer-win-x64\ plus a zip.
# If Inno Setup (ISCC.exe) is installed, also builds dist\KineticTrainer-Setup.exe.
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'dist\KineticTrainer-win-x64'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet test tests\KineticTrainer.Tests\KineticTrainer.Tests.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish src\KineticTrainer.App\KineticTrainer.App.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $out --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item README.md, docs -Destination $out -Recurse -Force
Copy-Item tools\bridge-client.py -Destination $out -Force

# Compress-Archive is far too slow for a self-contained publish folder (thousands of files).
Add-Type -AssemblyName System.IO.Compression.FileSystem
function New-Zip([string] $sourceDir, [string] $zipPath) {
  if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
  [System.IO.Compression.ZipFile]::CreateFromDirectory($sourceDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}

$zip = Join-Path $PSScriptRoot 'dist\KineticTrainer-win-x64.zip'
New-Zip $out $zip
Write-Host "`nFolder: $out"
Write-Host "Zip:    $zip"

$iscc = @(
  "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
  & $iscc /Q installer\KineticTrainer.iss
  if ($LASTEXITCODE -eq 0) { Write-Host "Setup:  $(Join-Path $PSScriptRoot 'dist\KineticTrainer-Setup.exe')" }
} else {
  Write-Host "Inno Setup not found; skipped KineticTrainer-Setup.exe (the folder/zip above runs as-is)."
}

# One download for moving to another PC: a folder with the installer, a portable copy and instructions.
$version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$bundleName = "KineticTrainer-$version-Windows"
$bundle = Join-Path $PSScriptRoot "dist\$bundleName"
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Path $bundle | Out-Null
$setup = Join-Path $PSScriptRoot 'dist\KineticTrainer-Setup.exe'
if (Test-Path $setup) { Copy-Item $setup -Destination $bundle }
Copy-Item $out -Destination (Join-Path $bundle 'Portable') -Recurse
Copy-Item (Join-Path $PSScriptRoot 'installer\READ ME FIRST.txt') -Destination $bundle
$bundleZip = "$bundle.zip"
New-Zip $bundle $bundleZip
Write-Host "Bundle: $bundleZip"
