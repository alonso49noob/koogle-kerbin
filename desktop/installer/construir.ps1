# Builds the installer: desktop\dist\KoogleKerbin-Setup-<version>.exe
#
# Puts desktop\dist\KoogleKerbin (what publicar.ps1 leaves) in a zip embedded in the
# installer and compiles it against .NET Framework 4.8 with the C# compiler from the
# .NET 10 SDK. It doesn't download anything.
#
#   powershell -ExecutionPolicy Bypass -File installer\construir.ps1               publish and build
#   powershell -ExecutionPolicy Bypass -File installer\construir.ps1 -SinPublicar  use the existing dist
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

# the application files, in a zip
$zip = Join-Path $obj 'payload.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($dist, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# the version, from the project (1.6.5 becomes 1.6.5.0; 1.6.5.1 stays as is)
$gen = Join-Path $obj 'Version.g.cs'
$numerica = if ($version.Split('.').Count -ge 4) { $version } else { "$version.0" }
@"
using System.Reflection;
[assembly: AssemblyVersion("$numerica")]
[assembly: AssemblyFileVersion("$numerica")]
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
