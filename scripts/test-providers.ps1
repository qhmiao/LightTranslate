[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\LightTranslate'))

$ErrorActionPreference = 'Stop'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$testProcess = Start-Process -FilePath (Join-Path $outputPath 'LightTranslate.exe') -ArgumentList '--self-test-providers' -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(30000)) {
    Stop-Process -Id $testProcess.Id -Force
    throw 'Provider self-test exceeded 30 seconds.'
}
if ($testProcess.ExitCode -ne 0) { throw 'Provider self-test failed; see status.log in the test output directory.' }
Get-Content -LiteralPath (Join-Path $outputPath 'provider-test.json')
