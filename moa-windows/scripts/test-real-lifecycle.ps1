$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process -Name VoltiEngine,llama-server -ErrorAction SilentlyContinue) { throw 'Stop competing engine normally before real tests.' }
$run = Join-Path $moaRoot ('tests/real-lifecycle/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
function FreePort { $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $l.Start(); $p=$l.LocalEndpoint.Port; $l.Stop(); return $p }
$config = Get-Content -LiteralPath (Join-Path $moaRoot 'engine.example.json') -Raw | ConvertFrom-Json
$config.port = FreePort
do { $config.backendPort = FreePort } while ($config.backendPort -eq $config.port)
$config.conversationVaultDirectory = Join-Path $run 'vault'
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'engine.json') -Encoding utf8
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @('desktop/MoaStateController.cs','desktop/MockChatTransport.cs','desktop/MoaChatForm.cs','desktop/MoaLocalChat.cs','desktop/MoaLocalRuntime.cs','tests/RealLifecycle.cs') | ForEach-Object { Join-Path $moaRoot $_ }
& $csc /nologo /target:exe /codepage:65001 /utf8output "/out:$(Join-Path $run 'MoaLifecycle.exe')" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle test build failed.' }
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $run 'bundle-cache'
$engineIds = @()
function Start-TestEngine {
 $p = Start-Process -FilePath (Join-Path $moaRoot 'app/engine/MoaEngine.exe') -ArgumentList @('--config', ('"' + (Join-Path $run 'engine.json') + '"')) -WindowStyle Hidden -PassThru
 for ($i=0; $i -lt 80; $i++) {
  try { $s=Invoke-RestMethod -Uri "http://127.0.0.1:$($config.port)/v1/status" -TimeoutSec 1; if ($s.service -eq 'moa-engine') { return $p } } catch { }
  if ($p.HasExited) { throw 'Test engine failed to start.' }; Start-Sleep -Milliseconds 200
 }
 throw 'Engine startup timed out.'
}
function Stop-TestEngine($p) {
 if ($null -eq $p -or $p.HasExited) { return }
 $auth = @{Authorization='Bearer ' + ([IO.File]::ReadAllText((Join-Path $run 'data/native-token.txt')).Trim())}
 $r = Invoke-RestMethod -Uri "http://127.0.0.1:$($config.port)/v1/shutdown" -Method Post -ContentType 'application/json' -Body '{}' -Headers $auth -TimeoutSec 10
 if (-not $r.stopping -or -not $p.WaitForExit(15000)) { throw 'Normal test engine shutdown did not complete; no force termination attempted.' }
 $auth.Clear()
}
$engine = $null
try {
 foreach ($phase in @('conversation','restore')) {
  $engine = Start-TestEngine
  $engineIds += $engine.Id
  & (Join-Path $run 'MoaLifecycle.exe') (Join-Path $moaRoot 'app/assets') $phase
  if ($LASTEXITCODE -ne 0) { throw "Lifecycle phase failed: $phase" }
  Stop-TestEngine $engine
  $engine.Dispose(); $engine = $null
 }
 if ($engineIds[0] -eq $engineIds[1]) { throw 'Engine process was not replaced.' }
 [pscustomobject]@{Result='PASS';FirstEnginePid=$engineIds[0];RestartedEnginePid=$engineIds[1];ModelCopies=0;ApiPort=$config.port;BackendPort=$config.backendPort} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $run 'summary.json')
} finally { if ($null -ne $engine) { Stop-TestEngine $engine; $engine.Dispose() }; Write-Output "Artifacts: $run" }
