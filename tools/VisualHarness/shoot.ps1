# Uso: ./shoot.ps1 -Theme Dark|Light -Tag nome [-Size 1160x760] [-Scale 1] [-Pages 0,1] [-Sensors]
param([string]$Theme='Dark',[string]$Tag='dark',[string]$Size='1160x760',[string]$Scale='1',[string]$Pages='',[switch]$Sensors,[switch]$Dialogs,[switch]$Screen,[string]$Out='')
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if(-not $Out){ $Out=Join-Path $root 'artifacts\screens' }
dotnet build "$root\tools\VisualHarness\VisualHarness.csproj" -c Release --nologo -v q | Select-Object -Last 3
$exe="$root\tools\VisualHarness\bin\Release\net10.0-windows\VisualHarness.exe"
$dir=Join-Path $Out $Tag; New-Item -ItemType Directory -Force $dir | Out-Null
$a="--out `"$dir`" --tag $Tag --size $Size --scale $Scale --theme $Theme"
if($Pages){$a+=" --pages $Pages"}; if($Sensors){$a+=" --sensors 1"}; if($Dialogs){$a+=" --dialogs 1"}; if($Screen){$a+=" --screen 1"}
$p=Start-Process $exe -ArgumentList $a -PassThru
if(-not $p.WaitForExit(240000)){ $p.Kill(); 'TIMEOUT' }
if(Test-Path "$dir\harness-error.txt"){ Get-Content "$dir\harness-error.txt" }
(Get-ChildItem $dir -Filter *.png).Count
