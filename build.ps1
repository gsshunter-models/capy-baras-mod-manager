param([switch]$Test)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = $PSScriptRoot
if ($Test) {
    & dotnet run --project (Join-Path $taskProjectRoot 'Tests\MXSimModManager.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
& dotnet publish (Join-Path $taskProjectRoot 'Desktop\MXSimModManager.csproj') -c Release --no-self-contained -o (Join-Path $taskProjectRoot 'artifacts\CapyBara-Mod-Manager')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'README.md') -Destination (Join-Path $taskProjectRoot 'artifacts\CapyBara-Mod-Manager\README.md') -Force
Write-Output (Join-Path $taskProjectRoot 'artifacts\CapyBara-Mod-Manager\CapyBaraModManager.exe')
