param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipTests,
    [switch]$Installer,
    [string]$OAuthJson
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Run([string]$what, [scriptblock]$cmd) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)" }
}

Run "dotnet restore"        { dotnet restore MultiDriveCloud.sln }
Run "dotnet build"          { dotnet build MultiDriveCloud.sln -c $Configuration --no-restore }
if (-not $SkipTests) {
    $results = Join-Path $root "dist\test-results"
    Run "dotnet test"       { dotnet test MultiDriveCloud.sln -c $Configuration --no-build --logger "trx;LogFileName=results.trx" --results-directory $results }
    $trx = Join-Path $results "results.trx"
    if (-not (Test-Path $trx)) { throw "Aucun rapport de test produit : les tests n'ont pas pu s'exécuter." }
    $c = ([xml](Get-Content $trx -Raw)).TestRun.ResultSummary.Counters
    Write-Host ("Tests : {0} exécutés, {1} réussis, {2} en échec" -f $c.executed, $c.passed, $c.failed) -ForegroundColor Cyan
    if ([int]$c.executed -eq 0 -or [int]$c.failed -gt 0) { throw "Tests non concluants (exécutés=$($c.executed), échecs=$($c.failed))." }
}

$dist = Join-Path $root "dist"
$publishDir = Join-Path $dist "MultiDriveCloud"
if (Test-Path $publishDir) {
    try { Remove-Item $publishDir -Recurse -Force -ErrorAction Stop }
    catch {
        $publishDir = Join-Path $dist ("MultiDriveCloud-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
        Write-Warning "Le dossier dist\MultiDriveCloud est verrouillé (application ouverte ?). Publication dans $publishDir"
    }
}

Run "dotnet publish ($Runtime, self-contained, single file)" {
    dotnet publish src/MultiDriveCloud.App/MultiDriveCloud.App.csproj -c $Configuration -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $publishDir
}

$oauthSource = if ($OAuthJson) { $OAuthJson } else { Join-Path $root "config\google-oauth.json" }
if (Test-Path $oauthSource) {
    Copy-Item $oauthSource (Join-Path $publishDir "google-oauth.json") -Force
    Write-Host "Identité Google intégrée : les utilisateurs n'auront qu'à cliquer sur « Ajouter un compte »." -ForegroundColor Green
} else {
    Write-Warning "Aucune identité Google fournie (config\google-oauth.json ou -OAuthJson) : chaque utilisateur devra suivre l'assistant de configuration."
}

$exe = Join-Path $publishDir "MultiDriveCloud.exe"
if (-not (Test-Path $exe)) { throw "L'exécutable n'a pas été produit : $exe" }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Set-Content -Path (Join-Path $dist "SHA256SUMS.txt") -Value "$hash  MultiDriveCloud/MultiDriveCloud.exe" -Encoding ascii
Write-Host ("Exécutable : {0} ({1:N1} Mo)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning "Inno Setup 6 introuvable : installateur non généré (https://jrsoftware.org/isinfo.php)."
    } else {
        Run "Inno Setup" { & $iscc "/DSourceDir=$publishDir" "/DOutputDir=$dist" (Join-Path $root "installer\MultiDriveCloud.iss") }
        Write-Host "Installateur : $dist\MultiDriveCloud-Setup.exe" -ForegroundColor Green
    }
}

Write-Host "Terminé. Sortie dans $dist" -ForegroundColor Green
