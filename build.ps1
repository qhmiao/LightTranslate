[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\LightTranslate')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'LightTranslate must be built on Windows with PowerShell 7.' }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 and its 64-bit compiler are required.' }
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Security.dll','System.Net.Http.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll','Microsoft.VisualBasic.dll') |
    ForEach-Object { '/r:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') |
    ForEach-Object { '/r:' + (Join-Path $framework ('WPF\' + $_)) }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    ('/out:' + (Join-Path $outputPath 'LightTranslate.exe')) `
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\LightTranslate.manifest')) `
    ('/win32icon:' + (Join-Path $PSScriptRoot 'assets\LightTranslate.ico')) `
    @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$helperReferences = @('System.dll','System.Core.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$helperReferences += @('WindowsBase.dll','UIAutomationClient.dll','UIAutomationTypes.dll') | ForEach-Object { '/r:' + (Join-Path $framework ('WPF\' + $_)) }
& $compiler /nologo /target:exe /platform:x64 /optimize+ ('/out:' + (Join-Path $outputPath 'LightTranslate.SelectionHost.exe')) ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\LightTranslate.manifest')) @helperReferences (Join-Path $PSScriptRoot 'helpers\SelectionHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'Selection helper compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\LightTranslate.exe.config'),
    (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'README.md'),
    (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md') -Destination $outputPath
New-Item -ItemType Directory -Path (Join-Path $outputPath 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\ECDICT-LICENSE.txt'),
    (Join-Path $PSScriptRoot 'licenses\STranslate-LICENSE.txt') -Destination (Join-Path $outputPath 'licenses')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\LightTranslate.ico') -Destination $outputPath
New-Item -ItemType Directory -Path (Join-Path $outputPath 'assets'), (Join-Path $outputPath 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\preview.png') -Destination (Join-Path $outputPath 'assets')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\TESTING.md') -Destination (Join-Path $outputPath 'docs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'settings.example.json') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scripts\startup.ps1') -Destination $outputPath
if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'settings.json'))) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'settings.example.json') -Destination (Join-Path $outputPath 'settings.json')
}
Write-Output ('Built: ' + (Join-Path $outputPath 'LightTranslate.exe'))
