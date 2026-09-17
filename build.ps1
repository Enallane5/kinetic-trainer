# Debug build of everything + unit tests.
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-Location $PSScriptRoot
dotnet test tests\KineticTrainer.Tests\KineticTrainer.Tests.csproj --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build src\KineticTrainer.App\KineticTrainer.App.csproj -c Debug -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nRun: src\KineticTrainer.App\bin\x64\Debug\net10.0-windows10.0.19041.0\KineticTrainer.exe"
