param([switch]$Real)
$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $moaRoot 'build/cli'
$env:NUGET_PACKAGES = Join-Path $moaRoot 'build/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$fallback = Join-Path (Split-Path $moaRoot -Parent) 'volti-pet/engine/.build-cache/packages'
$fixtureDir = Join-Path $moaRoot 'tests/fixtures'
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
foreach ($name in @('VoltiEngine','OwnedModel')) {
 & $csc /nologo /target:exe "/out:$(Join-Path $fixtureDir "$name.exe")" (Join-Path $moaRoot 'tests/Fixture.cs')
 if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
}
$arguments = @('run', '--project', (Join-Path $moaRoot 'engine/tests/MoaEngine.Tests.csproj'), "-p:RestoreFallbackFolders=$fallback", '--')
if ($Real) { $arguments += '--real' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Moa engine checks failed.' }
