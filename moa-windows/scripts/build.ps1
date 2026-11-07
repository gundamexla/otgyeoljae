$ErrorActionPreference = 'Stop'
$moaRoot = Split-Path $PSScriptRoot -Parent
$projectRoot = Split-Path $moaRoot -Parent
$appDir = Join-Path $moaRoot 'app'
$assetDir = Join-Path $appDir 'assets'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler not found.' }
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null
$sources = @('Program.cs', 'MoaCompactChat.cs', 'MoaEngineControl.cs', 'MoaControlServer.cs', 'MoaDesktopContext.cs', 'MoaPetForm.cs', 'MoaRoaming.cs', 'MoaStateController.cs', 'MockChatTransport.cs', 'MoaChatForm.cs', 'MoaLocalRuntime.cs', 'MoaLocalChat.cs') | ForEach-Object { Join-Path $moaRoot "desktop/$_" }
$output = Join-Path $appDir 'MoaLocal.exe'
& $compiler /nologo /target:winexe /main:Moa.Program /optimize+ /codepage:65001 /utf8output "/out:$output" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Moa build failed.' }
foreach ($motion in @('idle','listen','work','done','confirm')) {
    $source = Join-Path $projectRoot "hackathon-archive/public/assets/moa-$motion.gif"
    $target = Join-Path $assetDir "moa-$motion.gif"
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $target).Hash) { continue }
    Copy-Item -LiteralPath $source -Destination $assetDir
}
Write-Output $output
Copy-Item -LiteralPath (Join-Path $moaRoot 'assets/moa-walk.png') -Destination (Join-Path $assetDir 'moa-walk.png') -Force
$toolDir = Join-Path $appDir 'tools/agent-desktop'
New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
foreach ($name in @('agent-desktop.exe','LICENSE','checksums.txt')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('volti-pet/desktop/tools/agent-desktop/' + $name)) -Destination $toolDir -Force
}
