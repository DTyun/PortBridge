$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Missing .NET Framework C# compiler.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /win32manifest:"$PSScriptRoot\app.manifest" /out:"$output\PortBridge-v1.4.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "$PSScriptRoot\RelayEngine.cs" "$PSScriptRoot\Program.cs" "$PSScriptRoot\ConnectionTest.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $output\PortBridge-v1.4.exe"



