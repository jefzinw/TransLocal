<#
  Publica o TransLocal no GitHub:
    1. cria o repositório público (se ainda não existir) e envia o código;
    2. cria uma Release com o instalador e o executável único.

  Requer o GitHub CLI autenticado uma vez:   gh auth login
  Uso:
    powershell -ExecutionPolicy Bypass -File tools\publish-github.ps1
    powershell -ExecutionPolicy Bypass -File tools\publish-github.ps1 -Version 1.1.0
#>
param(
    [string]$Name = 'TransLocal',
    [string]$Version = '1.0.0',
    [string]$Changes = '',
    [switch]$SkipRelease
)

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Fail([string]$msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

$ghCmd = Get-Command gh -ErrorAction SilentlyContinue
$gh = if ($ghCmd) { $ghCmd.Source } else {
    # Instalado pelo winget, mas o terminal foi aberto antes da atualização do PATH.
    Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\GitHub.cli_*\bin\gh.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $gh -or -not (Test-Path $gh)) { Fail "GitHub CLI não encontrado. Instale com: winget install GitHub.cli" }

& $gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Faça login no GitHub primeiro:  `"$gh`" auth login" }

# O repositório git precisa ser a pasta do projeto, nunca uma pasta acima dela.
$top = (git rev-parse --show-toplevel 2>$null)
if (-not $top -or ([IO.Path]::GetFullPath($top) -ne [IO.Path]::GetFullPath($root))) {
    Fail "O repositório git deveria ser $root, mas é '$top'."
}

$login = (& $gh api user --jq .login).Trim()
$full = "$login/$Name"
$description = 'Transmita a tela de um computador para outro por cabo (rede, USB4/Thunderbolt, bridge USB) ou exiba um notebook via placa de captura HDMI. App nativo para Windows.'

& $gh repo view $full 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    # Primeira publicação: autor = e-mail privado do GitHub (noreply), para não expor o e-mail pessoal
    # no histórico público. O commit inicial ainda não foi enviado, então pode ser ajustado.
    $id = (& $gh api user --jq .id).Trim()
    $displayName = (& $gh api user --jq '.name // .login').Trim()
    $noreply = "$id+$login@users.noreply.github.com"
    git config user.name $displayName
    git config user.email $noreply
    if ((git rev-list --count HEAD) -eq '1' -and (git log -1 --format=%ae) -ne $noreply) {
        git commit --amend --reset-author --no-edit | Out-Null
    }

    Write-Host "==> Criando repositório público $full" -ForegroundColor Cyan
    & $gh repo create $Name --public --description $description --source $root --remote origin --push
    if ($LASTEXITCODE -ne 0) { Fail "Falha ao criar o repositório." }
} else {
    Write-Host "==> Repositório $full já existe; enviando o código" -ForegroundColor Cyan
    $remotes = git remote 2>$null
    if (-not $remotes) { git remote add origin "https://github.com/$full.git" }
    git push -u origin HEAD
    if ($LASTEXITCODE -ne 0) { Fail "Falha no git push." }
}

& $gh repo edit $full --add-topic windows --add-topic screen-sharing --add-topic screen-mirroring `
    --add-topic hdmi-capture --add-topic usb4 --add-topic dotnet --add-topic winforms --add-topic desktop-duplication | Out-Null

if (-not $SkipRelease) {
    $setup = Join-Path $root "dist\TransLocal-Setup-$Version.exe"
    $exe = Join-Path $root 'dist\win-x64\TransLocal.exe'
    if (-not (Test-Path $setup) -or -not (Test-Path $exe)) {
        Write-Host "==> Gerando binários $Version" -ForegroundColor Cyan
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -Version $Version
        if ($LASTEXITCODE -ne 0) { Fail "Falha no build." }
    }

    $changesBlock = if ($Changes) { "`n**Novidades**`n$Changes`n" } else { '' }
    $notes = @"
## TransLocal $Version

Transmita a tela de um computador para outro por cabo, ou exiba um notebook ligado por HDMI a uma placa de captura.
$changesBlock
**Downloads**
- **TransLocal-Setup-$Version.exe**: instalador (atalho no Menu Iniciar, regra no Firewall do Windows, início automático opcional).
- **TransLocal.exe**: executável único, roda sem instalar.

Instale nos dois computadores. Não é preciso ter o .NET instalado. Para atualizar, basta executar o instalador novo (ele fecha a versão antiga sozinho).

> O executável não tem assinatura digital: na primeira execução o Windows pode mostrar “O Windows protegeu o computador”. Clique em **Mais informações › Executar assim mesmo**.

**Ligações que funcionam:** cabo de rede (RJ45) direto, USB-C com USB4/Thunderbolt nos dois computadores, cabo bridge USB 3.0 em modo rede, Wi-Fi, ou HDMI → placa de captura USB.
**Não funcionam:** cabo USB-A ↔ USB-A comum e HDMI ↔ HDMI direto (as duas portas são saídas).

Veja o README para instruções e solução de problemas.
"@
    $notesFile = Join-Path ([IO.Path]::GetTempPath()) "translocal-notes-$Version.md"
    [IO.File]::WriteAllText($notesFile, $notes, (New-Object Text.UTF8Encoding($false)))

    $assets = @("$setup#Instalador para Windows (x64)", "$exe#Executável único, sem instalar (Windows x64)")
    $apk = Join-Path $root 'dist\TransLocal-Android.apk'
    if (Test-Path $apk) { $assets += "$apk#App para celular Android (aprova acessos)" }

    & $gh release view "v$Version" --repo $full 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "==> Release v$Version já existe; atualizando os arquivos" -ForegroundColor Cyan
        & $gh release upload "v$Version" @($assets | ForEach-Object { $_.Split('#')[0] }) --repo $full --clobber
    } else {
        Write-Host "==> Criando a Release v$Version" -ForegroundColor Cyan
        & $gh release create "v$Version" @assets --repo $full --title "TransLocal $Version" --notes-file $notesFile --target main
    }
    if ($LASTEXITCODE -ne 0) { Fail "Falha ao publicar a Release." }
    Remove-Item $notesFile -ErrorAction SilentlyContinue
}

Write-Host "`nPublicado: https://github.com/$full" -ForegroundColor Green
