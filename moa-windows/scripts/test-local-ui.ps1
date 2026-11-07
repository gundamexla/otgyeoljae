param([switch]$ExpectBusy, [switch]$Fake)
$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
$run = Join-Path $moaRoot ('tests/local-artifacts/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
function FreePort { $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop(); return $port }
$config = Get-Content -LiteralPath (Join-Path $moaRoot 'engine.example.json') -Raw | ConvertFrom-Json
$config.port = FreePort
do { $config.backendPort = FreePort } while ($config.backendPort -eq $config.port)
$config.conversationVaultDirectory = Join-Path $run 'vault'
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'engine.json') -Encoding utf8
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @('desktop/MoaStateController.cs','desktop/MockChatTransport.cs','desktop/MoaChatForm.cs','desktop/MoaLocalChat.cs','desktop/MoaLocalRuntime.cs','tests/LocalIntegration.cs') | ForEach-Object { Join-Path $moaRoot $_ }
& $csc /nologo /target:exe /codepage:65001 /utf8output "/out:$(Join-Path $run 'MoaLocalSmoke.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Client integration build failed.' }
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $run 'bundle-cache'
$engineFile = Join-Path $moaRoot 'app/engine/MoaEngine.exe'
$engineArgs = @('--config', ('"' + (Join-Path $run 'engine.json') + '"'))
if ($Fake) {
 $engineFile = Join-Path $moaRoot 'engine/tests/bin/Debug/net10.0/MoaEngine.Tests.exe'
 $engineArgs[0] = '--fake-server'
}
$engine = Start-Process -FilePath $engineFile -ArgumentList $engineArgs -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $run 'engine-errors.txt')
try {
 $ready = $false
 for ($i=0; $i -lt 100; $i++) {
   try { $status = Invoke-RestMethod -Uri "http://127.0.0.1:$($config.port)/v1/status" -TimeoutSec 1; if ($status.service -eq 'moa-engine') { $ready = $true; break } } catch { }
   if ($engine.HasExited) { throw 'Test engine exited.' }
   Start-Sleep -Milliseconds 150
 }
 if (-not $ready) { throw 'Test engine did not become ready.' }
 $smokeArgs = @((Join-Path $moaRoot 'app/assets'))
 if ($ExpectBusy) { $smokeArgs += '--expect-busy' }
 & (Join-Path $run 'MoaLocalSmoke.exe') @smokeArgs
 if ($LASTEXITCODE -ne 0) { throw 'Live UI checks failed.' }
} finally {
 if (-not $engine.HasExited) {
  $tokenFile = Join-Path $run 'data/native-token.txt'
  if (Test-Path -LiteralPath $tokenFile) {
   $auth = @{ Authorization = 'Bearer ' + ([IO.File]::ReadAllText($tokenFile).Trim()) }
   try { Invoke-RestMethod -Uri "http://127.0.0.1:$($config.port)/v1/shutdown" -Method Post -ContentType 'application/json' -Body '{}' -Headers $auth | Out-Null } catch { }
  }
  if (-not $engine.WaitForExit(10000)) { $engine.Kill() }
 }
 $engine.Dispose()
 Write-Output "Artifacts: $run"
}
