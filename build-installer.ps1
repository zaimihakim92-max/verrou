# Construit l'installeur Verrou : tests, publication autonome (runtime .NET inclus) puis compilation Inno Setup.
# Usage : .\build-installer.ps1 [-Version 1.0.0] [-SkipTests]
param([string]$Version = "", [switch]$SkipTests)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$proj = "src\Verrou.App\Verrou.App.csproj"

if (-not $Version) {
    $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Verrou $Version" -ForegroundColor Cyan

if (-not $SkipTests) {
    dotnet test tests\Verrou.Tests\Verrou.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Les tests ont échoué : installeur non construit." }
}

dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o publish\win-x64
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 est introuvable (winget install JRSoftware.InnoSetup)." }

& $iscc "/DAppVersion=$Version" installer\Verrou.iss
if ($LASTEXITCODE -ne 0) { throw "La compilation de l'installeur a échoué." }

Get-Item "installer\Output\Verrou-Setup-$Version.exe"
