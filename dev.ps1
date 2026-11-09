#requires -Version 7.0
param(
    [ValidateSet('build', 'run', 'test', 'release')]
    [string]$Task = 'build',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$localSdk = Join-Path $PSScriptRoot '.tools-home\dotnet'
if (Test-Path -LiteralPath (Join-Path $localSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localSdk
    $env:PATH = $localSdk + ';' + $env:PATH
}
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools-home'
$env:APPDATA = Join-Path $PSScriptRoot '.tools-home\appdata'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) {
    throw 'Нужен .NET SDK 10. Установите его или распакуйте в .tools-home\dotnet.'
}

if ($Task -eq 'release') {
    if ($Offline) { throw 'Для автономной проверки используйте -Task test -Offline.' }
    & (Join-Path $PSScriptRoot 'build.ps1')
    return
}

$configuration = if ($Task -eq 'test') { 'Release' } else { 'Debug' }
$restoreArguments = @('restore', '.\HistoryBoard.csproj', '--configfile', '.\NuGet.Config', '-r', 'win-x64')
if ($Task -eq 'test') { $restoreArguments += @('-p:SelfContained=true', '-p:PublishSingleFile=true') }
if ($Offline) {
    $feed = Join-Path $PSScriptRoot '.tools-home\nuget-feed'
    if (-not (Test-Path -LiteralPath $feed)) { throw 'Локальный источник пакетов .tools-home\nuget-feed отсутствует.' }
    $restoreArguments += @('--source', $feed)
}
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить зависимости.' }

if ($Task -eq 'test') {
    & dotnet publish .\HistoryBoard.csproj -c $configuration -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\bin\publish
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать автономное приложение.' }
    & (Join-Path $PSScriptRoot 'test-app.ps1')
} else {
    & dotnet build .\HistoryBoard.csproj -c $configuration -r win-x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
    if ($Task -eq 'run') {
        $dataDirectory = Join-Path $PSScriptRoot '.tools-home\dev-data'
        & dotnet .\bin\Debug\net10.0-windows\win-x64\HistoryBoard.dll --data-dir $dataDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Приложение завершилось с ошибкой.' }
    }
}
