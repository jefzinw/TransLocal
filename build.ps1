<#
  Gera o TransLocal:
    dist\win-x64\TransLocal.exe            executável único (não precisa instalar o .NET)
    dist\TransLocal-Setup-<versão>.exe     instalador (requer Inno Setup 6)

  Uso:
    powershell -ExecutionPolicy Bypass -File build.ps1
    powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.1.0 -IncludeArm64
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipInstaller,
    [switch]$IncludeArm64
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\TransLocal\TransLocal.csproj'

function Find-First([string[]]$candidates) {
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

# Usa o primeiro dotnet que tenha um SDK instalado (só o runtime não compila).
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = $null
foreach ($candidate in @(
        $(if ($cmd) { $cmd.Source } else { $null }),
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe")) {
    if (-not $candidate -or -not (Test-Path $candidate)) { continue }
    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $candidate; break }
}
if (-not $dotnet) { throw "SDK do .NET 10 não encontrado. Instale em https://dot.net" }
Write-Host "Usando $dotnet"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not (Test-Path (Join-Path $root 'assets\TransLocal.ico'))) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icon.ps1')
}

$rids = @('win-x64')
if ($IncludeArm64) { $rids += 'win-arm64' }

foreach ($rid in $rids) {
    $out = Join-Path $root "dist\$rid"
    Write-Host "==> Publicando $rid em $out" -ForegroundColor Cyan
    & $dotnet publish $project -c Release -r $rid -p:Version=$Version -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $rid" }
    Get-ChildItem $out -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}

# App Android (aprovador): só se o componente Android do .NET estiver instalado.
$androidProj = Join-Path $root 'android\TransLocal.Android\TransLocal.Android.csproj'
$workloads = & $dotnet workload list 2>$null
if ((Test-Path $androidProj) -and ($workloads -match '^\s*android\s')) {
    Write-Host "==> Gerando o app Android" -ForegroundColor Cyan
    & $dotnet publish $androidProj -c Release -f net10.0-android -p:ApplicationDisplayVersion=$Version --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar o app Android" }
    $apk = Get-ChildItem (Join-Path $root 'android\TransLocal.Android\bin\Release\net10.0-android\publish') -Filter '*-Signed.apk' | Select-Object -First 1
    Copy-Item $apk.FullName (Join-Path $root 'dist\TransLocal-Android.apk') -Force
} else {
    Write-Warning "Componente Android do .NET não instalado; app de celular não gerado (dotnet workload install android)."
}

if (-not $SkipInstaller) {
    $iscc = Find-First @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    if (-not $iscc) {
        Write-Warning "Inno Setup 6 não encontrado; instalador não gerado. Instale com: winget install JRSoftware.InnoSetup"
    } else {
        Write-Host "==> Gerando instalador" -ForegroundColor Cyan
        & $iscc /Q "/DAppVersion=$Version" (Join-Path $root 'installer\TransLocal.iss')
        if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar o instalador" }
    }
}

Write-Host "`nPronto:" -ForegroundColor Green
Get-ChildItem (Join-Path $root 'dist') -Recurse -Include *.exe, *.apk | ForEach-Object {
    "  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)
}
