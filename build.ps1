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
