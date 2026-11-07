param([switch]$Shutdown, [switch]$Compare)
$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
$voltiRoot = Join-Path (Split-Path $moaRoot -Parent) 'volti-pet/desktop'
$reportDir = Join-Path $moaRoot 'tests/live-verification'
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$configPath = Join-Path $voltiRoot 'engine.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$dataPath = [IO.Path]::GetFullPath((Join-Path $voltiRoot $config.dataDirectory))
$vault = $config.conversationVaultDirectory
if (-not $vault) { $vault = Join-Path $dataPath 'conversations' }
function Digest($lines) {
 $sha = [Security.Cryptography.SHA256]::Create()
 try { return [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))) } finally { $sha.Dispose() }
}
function Snapshot {
 $dataFiles = @(Get-ChildItem -LiteralPath $dataPath -File | Where-Object Name -ne 'instance.lock' | Sort-Object Name)
 $notes = @(Get-ChildItem -LiteralPath $vault -Filter '*.md' -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName)
 [pscustomobject]@{
  ConfigHash = (Get-FileHash -LiteralPath $configPath).Hash
  DataFileCount = $dataFiles.Count
  DataDigest = Digest @($dataFiles | ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash })
  NoteCount = $notes.Count
  NoteDigest = Digest @($notes | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash })
 }
}
$snapshot = Snapshot
$baselinePath = Join-Path $reportDir 'volti-baseline.json'
if ($Compare) {
 $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
 $equal = $true
 foreach ($field in @('ConfigHash','DataFileCount','DataDigest','NoteCount','NoteDigest')) { if ($baseline.$field -ne $snapshot.$field) { $equal = $false } }
 [pscustomobject]@{Preserved=$equal;DataFiles=$snapshot.DataFileCount;ConversationNotes=$snapshot.NoteCount} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $reportDir 'volti-comparison.json')
 if (-not $equal) { throw 'Volti snapshot differs. No files have been restored or overwritten.' }
 exit
}
if (Test-Path -LiteralPath $baselinePath) { throw 'A preservation baseline already exists; compare instead of overwriting it.' }
$snapshot | ConvertTo-Json | Set-Content -LiteralPath $baselinePath -Encoding utf8
if ($Shutdown) {
 $baseUrl = "http://127.0.0.1:$($config.port)"
 $status = $null
 try { $status = Invoke-RestMethod -Uri "$baseUrl/v1/status" -TimeoutSec 4 } catch { }
 if ($status) {
  if ($status.service -ne 'volti-engine') { throw 'Unexpected service on Volti port; no shutdown sent.' }
  $auth = @{ Authorization = 'Bearer ' + ([IO.File]::ReadAllText((Join-Path $dataPath 'native-token.txt')).Trim()) }
  $response = Invoke-RestMethod -Uri "$baseUrl/v1/shutdown" -Method Post -ContentType 'application/json' -Body '{}' -Headers $auth -TimeoutSec 10
  if (-not $response.stopping) { throw 'Normal shutdown was not acknowledged.' }
  $auth.Clear()
  for ($i = 0; $i -lt 80; $i++) { if (-not (Get-Process -Name VoltiEngine -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 250 }
  if (Get-Process -Name VoltiEngine -ErrorAction SilentlyContinue) { throw 'Volti has not exited; no force termination attempted.' }
  'Volti normal shutdown acknowledged and process exited.' | Tee-Object -FilePath (Join-Path $reportDir 'shutdown.txt')
 } else {
  if (Get-Process -Name VoltiEngine -ErrorAction SilentlyContinue) { throw 'Volti process exists but API is unavailable. No force termination attempted.' }
  'Volti was already stopped when checked; no shutdown or data mutation needed.' | Tee-Object -FilePath (Join-Path $reportDir 'shutdown.txt')
 }
 if (Get-Process -Name llama-server -ErrorAction SilentlyContinue) { throw 'A CPU model process is still running; tests must not start.' }
}
