param([string]$Version = '')
$ErrorActionPreference = 'Stop'
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Номер версии должен иметь вид 1.2.0.' }
Set-Location -LiteralPath $PSScriptRoot
& (Join-Path $PSScriptRoot 'GenerateIcon.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
dotnet restore .\HistoryBoard.csproj --configfile .\NuGet.Config -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw 'Не удалось загрузить компоненты сборки.' }
$publishArguments = @('publish', '.\HistoryBoard.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-o', '.\bin\publish')
if ($Version) { $publishArguments += "-p:Version=$Version" }
dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать приложение.' }
# Подпись Authenticode (необязательно). Сертификат передаётся в переменных окружения или секретах GitHub:
# SIGN_CERT_PFX_BASE64 — файл .pfx в Base64, SIGN_CERT_PASSWORD — пароль, SIGN_TIMESTAMP_URL — сервер метки времени.
# REQUIRE_SIGNING=true запрещает выпуск без подписи. Контрольная сумма ниже считается уже для подписанного файла.
$publishedExe = Join-Path $PSScriptRoot 'bin\publish\HistoryBoard.exe'
if ($env:SIGN_CERT_PFX_BASE64) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signtool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'Не найден signtool.exe из Windows SDK.' }
    $pfx = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.pfx')
    try {
        [System.IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_CERT_PFX_BASE64))
        $timestamp = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
        & $signtool.FullName sign /fd SHA256 /td SHA256 /tr $timestamp /f $pfx /p $env:SIGN_CERT_PASSWORD /d 'Доска истории' $publishedExe
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось подписать приложение.' }
        & $signtool.FullName verify /pa $publishedExe
        if ($LASTEXITCODE -ne 0) { throw 'Подпись приложения не прошла проверку.' }
    }
    finally { Remove-Item -LiteralPath $pfx -Force -ErrorAction SilentlyContinue }
} elseif ($env:REQUIRE_SIGNING -eq 'true') {
    throw 'Для выпуска нужна подпись: задайте SIGN_CERT_PFX_BASE64 и SIGN_CERT_PASSWORD.'
} else {
    Write-Warning 'Приложение не подписано: Windows SmartScreen может предупреждать при запуске установщика.'
}
$releaseDirectory = Join-Path $PSScriptRoot 'Готовое приложение'
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\publish\HistoryBoard.exe') -Destination (Join-Path $releaseDirectory 'Установить доску.exe') -Force
$releaseInstaller = Join-Path $releaseDirectory 'HistoryBoard-Setup-win-x64.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\publish\HistoryBoard.exe') -Destination $releaseInstaller -Force
$digest = (Get-FileHash -LiteralPath $releaseInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($releaseInstaller + '.sha256') -Value ($digest + '  HistoryBoard-Setup-win-x64.exe') -Encoding ascii
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $releaseDirectory 'Как пользоваться.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\app.ico') -Destination (Join-Path $releaseDirectory 'Иконка программы.ico') -Force
Write-Host "Готово: $releaseDirectory"
