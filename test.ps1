$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /out:"$PSScriptRoot\dist\Tests.exe" /reference:"$PSScriptRoot\dist\PortBridge-v1.4.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "$PSScriptRoot\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
Push-Location $PSScriptRoot
try { & "$PSScriptRoot\dist\Tests.exe"; if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' } } finally { Pop-Location }



