$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
$run = Join-Path $moaRoot ('tests/desktop-artifacts/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @('desktop/Program.cs','desktop/MoaCompactChat.cs','desktop/MoaEngineControl.cs','desktop/MoaControlServer.cs','desktop/MoaDesktopContext.cs','desktop/MoaPetForm.cs','desktop/MoaRoaming.cs','desktop/MoaStateController.cs','desktop/MockChatTransport.cs','desktop/MoaChatForm.cs','desktop/MoaLocalRuntime.cs','desktop/MoaLocalChat.cs','tests/MoaDesktopTests.cs') | ForEach-Object { Join-Path $moaRoot $_ }
$exe = Join-Path $run 'MoaDesktopTests.exe'
& $compiler /nologo /target:exe /main:MoaDesktopTests /codepage:65001 /utf8output "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Desktop test build failed.' }
& $exe (Join-Path $moaRoot 'app/assets') $run
if ($LASTEXITCODE -ne 0) { throw "Desktop tests failed: $run" }
Write-Output "Artifacts: $run"
