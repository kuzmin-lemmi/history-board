param(
    [string]$Executable = (Join-Path $PSScriptRoot 'bin\publish\HistoryBoard.exe'),
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot 'test-results\local')
)
$ErrorActionPreference = 'Stop'
$executablePath = [System.IO.Path]::GetFullPath($Executable)
$resultsPath = [System.IO.Path]::GetFullPath($ResultsDirectory)
if (-not (Test-Path -LiteralPath $executablePath)) { throw 'Сначала соберите приложение: .\build.ps1' }
New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $PSScriptRoot '.bundle-cache'
$env:DOTNET_ROOT = Join-Path $PSScriptRoot 'no-shared-runtime'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$arguments = @('--self-test', ('"' + $resultsPath + '"'))
$process = Start-Process -FilePath $executablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) {
    $failure = Join-Path $resultsPath 'failure.txt'
    if (Test-Path -LiteralPath $failure) { Get-Content -LiteralPath $failure | Write-Error }
    throw 'Проверки приложения завершились с ошибкой.'
}
$report = Get-Content -LiteralPath (Join-Path $resultsPath 'results.json') -Raw | ConvertFrom-Json
if ($report.Passed -lt 50) { throw 'В отчёте отсутствует часть проверок.' }
Write-Host "Проверок пройдено: $($report.Passed)"
