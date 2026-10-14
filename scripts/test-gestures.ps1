[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\LightTranslate'))
$ErrorActionPreference = 'Stop'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$test = Start-Process -FilePath (Join-Path $outputPath 'LightTranslate.exe') -ArgumentList '--self-test-gestures' -WindowStyle Hidden -PassThru
if (-not $test.WaitForExit(30000)) {
    Stop-Process -Id $test.Id -Force
    throw 'Gesture integration test timed out.'
}
if ($test.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $outputPath 'status.log') -Tail 10
    throw 'Gesture integration test failed.'
}
Get-Content -LiteralPath (Join-Path $outputPath 'gesture-integration-test.json')
