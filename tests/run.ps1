$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path tests/out | Out-Null
& $compiler /nologo /target:exe /codepage:65001 /out:tests\out\Tests.exe src\DiagnosticLog.cs src\DeviceBridge.cs src\CompanionClient.cs src\H264Decoder.cs src\MediaPlayback.cs src\PcmPlayer.cs tests\Tests.cs tests\CompanionTests.cs tests\VideoTests.cs
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& .\tests\out\Tests.exe
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
