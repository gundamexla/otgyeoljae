param([switch]$Worker,[string]$RunDirectory,[string]$EnginePath,[switch]$Natural,[switch]$Context,[switch]$Clinic,[switch]$Variations,[switch]$OfferReplies,[switch]$Scenario)
$ErrorActionPreference='Stop'
$moa=Split-Path $PSScriptRoot -Parent
if(-not $Worker){
 $run=Join-Path $moa ('tests/compact-tools/'+(Get-Date -Format yyyyMMdd-HHmmss))
 New-Item -ItemType Directory -Path (Join-Path $run 'tools/agent-desktop') -Force | Out-Null
 Copy-Item -LiteralPath (Join-Path $moa 'app/tools/agent-desktop/agent-desktop.exe') -Destination (Join-Path $run 'tools/agent-desktop')
 function Port { $l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$l.Start();$p=$l.LocalEndpoint.Port;$l.Stop();$p }
 $config=Get-Content (Join-Path $moa 'engine.example.json') -Raw | ConvertFrom-Json
 $config.port=Port
 do{$config.backendPort=Port}while($config.port -eq $config.backendPort)
 $config.conversationVaultDirectory=Join-Path $run 'vault'
 $config|ConvertTo-Json|Set-Content (Join-Path $run 'engine.json') -Encoding utf8
 if(-not $EnginePath){$EnginePath=Join-Path $moa 'app/engine/MoaEngine.exe'}
 (Resolve-Path -LiteralPath $EnginePath).Path|Set-Content (Join-Path $run 'engine-path.txt')
 if($Natural){'natural'|Set-Content (Join-Path $run 'test-mode.txt')}
 if($Clinic){'clinic'|Set-Content (Join-Path $run 'test-mode.txt')}
 if($Variations){'variations'|Set-Content (Join-Path $run 'test-mode.txt')}
 if($OfferReplies){'offer-replies'|Set-Content (Join-Path $run 'test-mode.txt')}
 if($Scenario){'scenario'|Set-Content (Join-Path $run 'test-mode.txt')}
 if($Context){
  if(Get-Process VoltiEngine,llama-server -ErrorAction SilentlyContinue){throw 'A competing CPU engine is running; context inference check not started'}
  'context'|Set-Content (Join-Path $run 'test-mode.txt')
 }
 $compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
 $sources=@('desktop/MoaCompactChat.cs','desktop/MoaPetForm.cs','desktop/MoaRoaming.cs','desktop/MoaStateController.cs','desktop/MoaLocalChat.cs','desktop/MoaLocalRuntime.cs','desktop/MoaControlServer.cs','desktop/MoaEngineControl.cs','tests/CompactToolsLive.cs')|ForEach-Object{Join-Path $moa $_}
 & $compiler /nologo /target:winexe /main:CompactToolsLive /codepage:65001 ("/out:"+(Join-Path $run 'CompactToolsLive.exe')) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll @sources
 if($LASTEXITCODE -ne 0){throw 'Compact tool harness build failed'}
 $shell=New-Object -ComObject Shell.Application
 $exe=(Get-Process -Id $PID).Path
 $shell.ShellExecute($exe,('-NoProfile -File "'+$PSCommandPath+'" -Worker -RunDirectory "'+$run+'"'),$moa,'open',0)
 Write-Output "Started verification in Explorer desktop: $run"
 exit
}
$run=[IO.Path]::GetFullPath($RunDirectory)
if(-not $run.StartsWith((Join-Path $moa 'tests/compact-tools/') ,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected test directory'}
$config=Get-Content (Join-Path $run 'engine.json') -Raw | ConvertFrom-Json
$url='http://127.0.0.1:'+$config.port
$engine=$null
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $run 'bundle-cache'
function Stop-TestEngine {
 if($null -eq $script:engine -or $script:engine.HasExited){return}
 $auth=@{Authorization='Bearer '+[IO.File]::ReadAllText((Join-Path $run 'data/native-token.txt')).Trim()}
 try{$null=Invoke-RestMethod "$url/v1/shutdown" -Method Post -ContentType application/json -Body '{}' -Headers $auth -TimeoutSec 10;if(-not $script:engine.WaitForExit(15000)){throw 'Normal shutdown timed out'}}finally{$auth.Clear()}
 $script:engine.Dispose();$script:engine=$null
}
try{
 $engineIds=@()
 $mode=if(Test-Path -LiteralPath (Join-Path $run 'test-mode.txt')){(Get-Content (Join-Path $run 'test-mode.txt') -Raw).Trim()}else{''}
 $phases=if($mode -eq 'scenario'){@('scenario-actions','scenario-restore')}elseif($mode -eq 'context'){@('context-actions','context-restore')}elseif($mode -eq 'offer-replies'){@('offer-replies-actions','offer-replies-restore')}elseif($mode -eq 'variations'){@('variations-actions','variations-restore')}elseif($mode -eq 'clinic'){@('clinic-actions','clinic-restore')}elseif($mode -eq 'natural'){@('natural-actions','natural-restore')}else{@('actions','restore')}
 foreach($phase in $phases){
  ('Starting '+$phase)|Set-Content (Join-Path $run 'status.txt')
  $engine=Start-Process -FilePath ((Get-Content (Join-Path $run 'engine-path.txt') -Raw).Trim()) -ArgumentList @('--config',('"'+(Join-Path $run 'engine.json')+'"')) -WindowStyle Hidden -PassThru
  $engineIds+=$engine.Id
  $ready=$false
  for($i=0;$i -lt 100;$i++){try{$s=Invoke-RestMethod "$url/v1/status" -TimeoutSec 1;if($s.service -eq 'moa-engine'){$ready=$true;break}}catch{};if($engine.HasExited){throw 'Engine exited at startup'};Start-Sleep -Milliseconds 200}
  if(-not $ready){throw 'Engine startup timed out'}
  $client=Start-Process -FilePath (Join-Path $run 'CompactToolsLive.exe') -ArgumentList @(('"'+(Join-Path $moa 'app/assets')+'"'),$phase) -PassThru
  $client.WaitForExit()
  if($client.ExitCode -ne 0){throw ('Compact '+$phase+' failed; see phase results')}
  Stop-TestEngine
 }
 [pscustomobject]@{Result='PASS';EnginePids=$engineIds;ApiPort=$config.port;BackendPort=$config.backendPort}|ConvertTo-Json|Set-Content (Join-Path $run 'summary.json')
 'PASS'|Set-Content (Join-Path $run 'status.txt')
}catch{$_|Out-String|Set-Content (Join-Path $run 'failure.txt');'FAIL'|Set-Content (Join-Path $run 'status.txt')}
finally{try{Stop-TestEngine}catch{$_|Out-String|Set-Content (Join-Path $run 'cleanup-failure.txt')}}
