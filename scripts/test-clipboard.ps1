[CmdletBinding()]
param([switch]$Native)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\dist\LightTranslate\LightTranslate.exe'
$argument = if ($Native) { '--self-test-clipboard-native' } else { '--self-test-clipboard' }
$test = Start-Process -FilePath $exe -ArgumentList $argument -WindowStyle Hidden -PassThru
if (-not $test.WaitForExit(30000)) {
    Stop-Process -Id $test.Id -Force
    throw 'Clipboard test timed out.'
}
$test.Refresh()
if ($test.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path (Split-Path $exe) 'status.log') -Tail 12
    throw 'Clipboard test failed.'
}
$name = if ($Native) { 'clipboard-native-test.json' } else { 'clipboard-test.json' }
Get-Content -LiteralPath (Join-Path (Split-Path $exe) $name)
