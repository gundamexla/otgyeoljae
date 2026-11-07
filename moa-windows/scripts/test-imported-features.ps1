$ErrorActionPreference='Stop'
$moa=Split-Path $PSScriptRoot -Parent
$run=Join-Path $moa ('tests/import-artifacts/'+(Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path (Join-Path $run 'tools/agent-desktop') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $moa 'app/tools/agent-desktop/agent-desktop.exe') -Destination (Join-Path $run 'tools/agent-desktop')
function Port { $l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$l.Start();$p=$l.LocalEndpoint.Port;$l.Stop();return $p }
$config=Get-Content (Join-Path $moa 'engine.example.json') -Raw | ConvertFrom-Json
$config.port=Port
do {$config.backendPort=Port} while($config.port -eq $config.backendPort)
$config.conversationVaultDirectory=Join-Path $run 'vault'
$config | ConvertTo-Json | Set-Content (Join-Path $run 'engine.json') -Encoding utf8
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $run 'bundle-cache'
$engine=Start-Process -FilePath (Join-Path $moa 'app/engine/MoaEngine.exe') -ArgumentList @('--config',('"'+(Join-Path $run 'engine.json')+'"')) -WindowStyle Hidden -PassThru
$url='http://127.0.0.1:'+$config.port
$log=[Collections.Generic.List[string]]::new()
$auth=$null
try {
 for($i=0;$i -lt 60;$i++){try{$status=Invoke-RestMethod "$url/v1/status" -TimeoutSec 1;break}catch{Start-Sleep -Milliseconds 200}}
 if($status.service -ne 'moa-engine'){throw 'Wrong engine'}
 $auth=@{Authorization='Bearer '+[IO.File]::ReadAllText((Join-Path $run 'data/native-token.txt')).Trim()}
 $session=Invoke-RestMethod "$url/v1/sessions" -Method Post -ContentType application/json -Body '{}' -Headers $auth
 function Ask($question,$expected){
  $body=@{sessionId=$session.id;requestId=[Guid]::NewGuid().ToString('N');text=$question}|ConvertTo-Json
  $reply=(Invoke-WebRequest "$url/v1/chat" -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -Headers $auth -TimeoutSec 30).Content
  $answer=''
  foreach($line in ($reply -split "`n")){if($line.StartsWith('data: ')){$packet=$line.Substring(6)|ConvertFrom-Json;if($packet.text){$answer=$packet.text}}}
  if($reply -notmatch 'event: done' -or $reply -match 'event: error' -or ($expected -and $answer -notmatch $expected)){throw "Feature failed: $question"}
  $log.Add('PASS '+$question)
 }
 Ask '/도구' '모아'
 Ask '/계산 (12+3)*4' '60'
 Ask '/기억 저장 이식검사 = 모아 전용' ''
 Ask '/기억 찾기 이식검사' '모아 전용'
 Ask '/메모 모아 기능 이식 검사용 메모' ''
 Ask '/파일 모아' ''
 Ask '사이트 목록' ''
 Ask '/창' ''
 $state=Invoke-RestMethod "$url/v1/status"
 if($state.state -ne 'idle'){throw 'Tool-only calls unexpectedly started model'}
 $log.Add('PASS tool commands leave CPU model unloaded')
 if(-not (Test-Path (Join-Path $run 'data/memory.sqlite3'))){throw 'Private memory database missing'}
 $log.Add('PASS private memory database')
 $compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
 $sources=@('desktop/MoaCompactChat.cs','desktop/MoaPetForm.cs','desktop/MoaRoaming.cs','desktop/MoaStateController.cs','desktop/MoaLocalChat.cs','desktop/MoaLocalRuntime.cs','desktop/MoaControlServer.cs','desktop/MoaEngineControl.cs','tests/CompactLive.cs')|ForEach-Object{Join-Path $moa $_}
 $exe=Join-Path $run 'CompactLive.exe'
 & $compiler /nologo /target:exe /main:CompactLive /codepage:65001 ("/out:"+$exe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll @sources
 if($LASTEXITCODE -ne 0){throw 'Compact test build failed'}
 & $exe (Join-Path $moa 'app/assets')
 if($LASTEXITCODE -ne 0){throw 'Compact real AI test failed'}
 $log.Add('PASS actual local AI in compact window')
} finally {
 if($auth){$null=Invoke-RestMethod "$url/v1/shutdown" -Method Post -ContentType application/json -Body '{}' -Headers $auth -TimeoutSec 5;$auth.Clear();if(-not $engine.WaitForExit(15000)){throw 'Normal test shutdown failed'}}
 $log | Set-Content (Join-Path $run 'results.txt') -Encoding utf8
 $log
 'Artifacts: '+$run
}
