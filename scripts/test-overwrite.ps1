[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\LightTranslate'))
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$exe = Join-Path $outputPath 'LightTranslate.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the independent candidate first.' }
$test = Start-Process -FilePath $exe -ArgumentList '--self-test-overwrite' -WindowStyle Hidden -PassThru
if (-not $test.WaitForExit(30000)) { throw ('Test still running; inspect PID ' + $test.Id) }
if ($test.ExitCode -ne 0) { throw ('Overwrite test failed; see ' + (Join-Path $outputPath 'status.log')) }
Get-Content -LiteralPath (Join-Path $outputPath 'clipboard-overwrite-test.json') -Raw -Encoding utf8
