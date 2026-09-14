# Genera la version lista para usar en desktop\dist\KoogleKerbin:
# KoogleKerbin.exe con su KoogleKerbin.dll + data\ con los mapas y catalogos.
# No se publica como fichero unico ni para un RID concreto: eso obliga a bajar de
# NuGet los paquetes del runtime, y para una app que usa el runtime instalado no hacen falta.
# Necesita el SDK de .NET 10. El .exe necesita el runtime de escritorio de .NET 10
# en el equipo donde se ejecute (el instalador se encarga de eso).
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
