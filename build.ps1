$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Missing .NET Framework C# compiler.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$assets = Join-Path $PSScriptRoot 'assets'
$iconCompiler = Join-Path $output 'IconAssetGenerator.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warn:4 /out:"$iconCompiler" /reference:System.dll /reference:System.Drawing.dll "$PSScriptRoot\IconArtwork.cs" "$PSScriptRoot\IconAssetGenerator.cs"
if ($LASTEXITCODE -ne 0) { throw 'Icon generator build failed.' }
& $iconCompiler $assets
if ($LASTEXITCODE -ne 0) { throw 'Icon asset generation failed.' }
Remove-Item -LiteralPath $iconCompiler -Force
$distributionIcons = Join-Path $output 'icons'
if (Test-Path -LiteralPath $distributionIcons) { Remove-Item -LiteralPath $distributionIcons -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distributionIcons | Out-Null
Get-ChildItem -LiteralPath $assets -File | Copy-Item -Destination $distributionIcons -Force
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /win32manifest:"$PSScriptRoot\app.manifest" /win32icon:"$assets\PortBridge.ico" /out:"$output\PortBridge-v1.4.1.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "$PSScriptRoot\IconArtwork.cs" "$PSScriptRoot\RelayEngine.cs" "$PSScriptRoot\Program.cs" "$PSScriptRoot\ConnectionTest.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'README.md') -Force
$package = Join-Path $output 'PortBridge-Win11-v1.4.1.zip'
$temporaryPackage = Join-Path $output 'PortBridge-Win11-v1.4.1.tmp.zip'
if (Test-Path -LiteralPath $temporaryPackage) { Remove-Item -LiteralPath $temporaryPackage -Force }
Compress-Archive -Path @((Join-Path $output 'PortBridge-v1.4.1.exe'), (Join-Path $output 'README.md'), (Join-Path $output 'PortBridge.config.xml'), (Join-Path $output 'icons')) -DestinationPath $temporaryPackage -CompressionLevel Optimal
Move-Item -LiteralPath $temporaryPackage -Destination $package -Force
Write-Output "Built: $output\PortBridge-v1.4.1.exe"



