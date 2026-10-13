# Builds Koogle Recorder: recorder\dist\paquete\GameData\KoogleRecorder\... and its zip,
# recorder\dist\KoogleRecorder-<version>.zip, ready to unzip into KSP's folder.
#
# It compiles against KSP's own assemblies (KSP_x64_Data\Managed), with the C# compiler of
# the .NET SDK, the same way as the app's installer. Nothing is downloaded.
#
#   powershell -ExecutionPolicy Bypass -File recorder\construir.ps1
#   powershell -ExecutionPolicy Bypass -File recorder\construir.ps1 -Ksp "D:\Games\Kerbal Space Program"
param([string]$Ksp = (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Kerbal Space Program'))

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$raiz = $PSScriptRoot
$managed = Join-Path $Ksp 'KSP_x64_Data\Managed'
if (-not (Test-Path (Join-Path $managed 'Assembly-CSharp.dll'))) { throw "No encuentro KSP en $Ksp (usa -Ksp con la carpeta del juego)" }

$fuente = Join-Path $raiz 'src\KoogleRecorder.cs'
$version = ([regex]::Match((Get-Content $fuente -Raw), 'Version = "([^"]+)"')).Groups[1].Value
if (-not $version) { throw 'No encuentro la versión en KoogleRecorder.cs' }

$dist = Join-Path $raiz 'dist'
$paquete = Join-Path $dist 'paquete'
$mod = Join-Path $paquete 'GameData\KoogleRecorder'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $mod 'Plugins') | Out-Null

$csc = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\sdk\*\Roslyn\bincore\csc.dll') | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $csc) { throw 'No encuentro el compilador de C# del SDK de .NET' }
$refs = 'mscorlib', 'System', 'System.Core', 'UnityEngine', 'UnityEngine.CoreModule', 'Assembly-CSharp' |
    ForEach-Object { '-r:' + (Join-Path $managed "$_.dll") }
$opciones = @('-nologo', '-noconfig', '-nostdlib+', '-target:library', '-optimize+', '-langversion:7.3', '-utf8output',
              "-out:$(Join-Path $mod 'Plugins\KoogleRecorder.dll')") + $refs + @($fuente)
& dotnet $csc.FullName @opciones
if ($LASTEXITCODE -ne 0) { throw "El compilador falló ($LASTEXITCODE)" }

Copy-Item (Join-Path $raiz 'README.md') (Join-Path $mod 'README.md')
Copy-Item (Join-Path (Split-Path $raiz -Parent) 'LICENSE') (Join-Path $mod 'LICENSE.txt')

$zip = Join-Path $dist "KoogleRecorder-$version.zip"
# entry by entry, with forward slashes: PowerShell 5.1's CreateFromDirectory writes backslashes,
# which other unzippers don't understand as folders
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archivo = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $paquete -Recurse -File | ForEach-Object {
        $nombre = $_.FullName.Substring($paquete.Length + 1).Replace([string][char]92, '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archivo, $_.FullName, $nombre, 'Optimal') | Out-Null
    }
} finally { $archivo.Dispose() }
# a copy without the version in the name, for the release asset the app downloads
Copy-Item $zip (Join-Path $dist 'KoogleRecorder.zip')
'{0}  ({1:N0} KB)' -f $zip, ((Get-Item $zip).Length / 1KB)
