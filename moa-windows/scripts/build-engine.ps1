$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
$projectRoot = Split-Path $moaRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $moaRoot 'build/cli'
$env:NUGET_PACKAGES = Join-Path $moaRoot 'build/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $moaRoot 'build/http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$fallback = Join-Path $projectRoot 'volti-pet/engine/.build-cache/packages'
& dotnet publish (Join-Path $moaRoot 'engine/MoaEngine.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:RestoreFallbackFolders=$fallback" -o (Join-Path $moaRoot 'app/engine') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Moa engine build failed.' }
New-Item -ItemType Directory -Path (Join-Path $moaRoot 'app/engine/licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $moaRoot 'engine/data/README.txt') -Destination (Join-Path $moaRoot 'app/engine/licenses/GeoNames.txt')
