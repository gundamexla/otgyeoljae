$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
$testDir = Join-Path $moaRoot 'tests/artifacts'
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @('desktop/MoaStateController.cs','desktop/MockChatTransport.cs','desktop/MoaChatForm.cs','desktop/MoaLocalRuntime.cs','desktop/MoaLocalChat.cs','tests/MoaTests.cs') | ForEach-Object { Join-Path $moaRoot $_ }
$testExe = Join-Path $testDir 'MoaTests.exe'
& $compiler /nologo /target:exe /codepage:65001 /utf8output "/out:$testExe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Moa test build failed.' }
& $testExe (Join-Path $moaRoot 'app/assets') $testDir
if ($LASTEXITCODE -ne 0) { throw 'Moa tests failed. See tests/artifacts/test-results.txt.' }
