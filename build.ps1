# =========================================================
# Script de Compilación y Publicación de SteamOS
# Genera el instalador único autónomo SteamOS_Setup.exe
# =========================================================
Write-Host "Iniciando compilación del ecosistema modular SteamOS..." -ForegroundColor Cyan

$releaseDir = Join-Path $PSScriptRoot "bin\Release"
if (Test-Path $releaseDir) {
    Remove-Item -Path $releaseDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# 1. Compilar binarios internos (Shell y Config)
Write-Host "`n[1/3] Compilando SteamOS_Shell (Modo Consola)..." -ForegroundColor Yellow
dotnet build src\SteamOS.Shell\SteamOS.Shell.csproj -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "Error compilando SteamOS.Shell"; exit 1 }

Write-Host "`n[2/3] Compilando SteamOS_Config (Panel de Configuración)..." -ForegroundColor Yellow
dotnet build src\SteamOS.Config\SteamOS.Config.csproj -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "Error compilando SteamOS.Config"; exit 1 }

# 2. Publicar SteamOS_Setup.exe empaquetando todo adentro
Write-Host "`n[3/3] Publicando Instalador Autónomo SteamOS_Setup.exe..." -ForegroundColor Yellow
dotnet publish src\SteamOS.Installer\SteamOS.Installer.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o $releaseDir
if ($LASTEXITCODE -ne 0) { Write-Error "Error publicando SteamOS.Installer"; exit 1 }

# Limpieza de archivos secundarios en bin/Release para dejar únicamente el instalador
Get-ChildItem -Path $releaseDir | Where-Object { $_.Name -ne "SteamOS_Setup.exe" } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n========================================================" -ForegroundColor Green
Write-Host " ¡Instalador autónomo generado con éxito en bin\Release! " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Get-ChildItem -Path $releaseDir -Filter "*.exe" | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
