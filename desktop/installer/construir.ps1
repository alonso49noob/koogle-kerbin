# Genera el instalador: desktop\dist\KoogleKerbin-Setup-<version>.exe
#
# Mete desktop\dist\KoogleKerbin (lo que deja publicar.ps1) en un zip incrustado en el
# instalador y lo compila contra .NET Framework 4.8 con el compilador de C# del SDK de
# .NET 10. No descarga nada.
#
#   powershell -ExecutionPolicy Bypass -File installer\construir.ps1               publica y construye
#   powershell -ExecutionPolicy Bypass -File installer\construir.ps1 -SinPublicar  usa el dist que ya hay
param([switch]$SinPublicar)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$raiz = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $raiz 'dist\KoogleKerbin'
[xml]$proyecto = Get-Content (Join-Path $raiz 'KerbinMaps.csproj')
$version = @($proyecto.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'No encuentro <Version> en KerbinMaps.csproj' }

if (-not $SinPublicar) { & (Join-Path $raiz 'publicar.ps1') | Out-Null }
if (-not (Test-Path (Join-Path $dist 'KoogleKerbin.exe'))) { throw "Falta $dist\KoogleKerbin.exe: ejecuta publicar.ps1" }

$obj = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

# los ficheros de la aplicacion, en un zip
$zip = Join-Path $obj 'payload.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($dist, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# la version, desde el proyecto
$gen = Join-Path $obj 'Version.g.cs'
@"
using System.Reflection;
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
namespace KoogleKerbinSetup { static class Build { public const string Version = "$version"; } }
"@ | Set-Content -Encoding UTF8 $gen

$csc = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\sdk\*\Roslyn\bincore\csc.dll') | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $csc) { throw 'No encuentro el compilador de C# del SDK de .NET' }
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.IO.Compression' |
    ForEach-Object { '-r:' + (Join-Path $fw "$_.dll") }

$salida = Join-Path $raiz "dist\KoogleKerbin-Setup-$version.exe"
$ico = Join-Path $raiz 'assets\app.ico'
$opciones = @(
    '-nologo', '-noconfig', '-nostdlib+', '-target:winexe', '-platform:anycpu', '-optimize+', '-langversion:7.3', '-utf8output',
    "-out:$salida", "-win32icon:$ico", "-win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')",
    "-resource:$zip,payload.zip", "-resource:$ico,app.ico", "-resource:$(Join-Path $raiz 'assets\app.png'),app.png"
) + $refs + @((Join-Path $PSScriptRoot 'Instalador.cs'), $gen)

& dotnet $csc.FullName @opciones
if ($LASTEXITCODE -ne 0) { throw "El compilador fallo ($LASTEXITCODE)" }

$tam = (Get-Item $salida).Length
'{0}  ({1:N1} MB)' -f $salida, ($tam / 1MB)
