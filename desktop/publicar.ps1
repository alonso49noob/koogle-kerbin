# Builds the ready-to-use version in desktop\dist\KoogleKerbin:
# KoogleKerbin.exe with its KoogleKerbin.dll + data\ with the maps and catalogs.
# It isn't published as a single file or for a specific RID: that requires downloading the
# runtime packages from NuGet, and an app that uses the installed runtime doesn't need them.
# Needs the .NET 10 SDK. The .exe needs the .NET 10 desktop runtime on the machine
# where it runs (the installer takes care of that).
param([string]$Salida = (Join-Path $PSScriptRoot 'dist\KoogleKerbin'))

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (Test-Path $Salida) { Remove-Item $Salida -Recurse -Force }

dotnet publish (Join-Path $PSScriptRoot 'KerbinMaps.csproj') `
  -c Release --self-contained false `
  -p:DebugType=none -p:GenerateDocumentationFile=false `
  -o $Salida
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo ($LASTEXITCODE)" }

Get-ChildItem $Salida -Recurse -File | ForEach-Object {
  '{0,10:N0} KB  {1}' -f ($_.Length / 1KB), $_.FullName.Substring($Salida.Length + 1)
}
