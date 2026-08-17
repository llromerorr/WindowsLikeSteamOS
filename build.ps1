# =========================================================
# Script de Compilación y Publicación Modular de SteamOS
# =========================================================
Write-Host "Compilando ecosistema modular SteamOS..." -ForegroundColor Cyan

$releaseDir = Join-Path $PSScriptRoot "bin\Release"
if (Test-Path $releaseDir) {
    Remove-Item -Path $releaseDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# 1. Compilar y Publicar SteamOS.Shell (Windowless Console Shell)
Write-Host "`n[1/3] Publicando SteamOS_Shell (Modo Consola)..." -ForegroundColor Yellow
dotnet publish src\SteamOS.Shell\SteamOS.Shell.csproj -c Release -r win-x64 --no-self-contained -o $releaseDir
if ($LASTEXITCODE -ne 0) { Write-Error "Error compilando SteamOS.Shell"; exit 1 }

# 2. Compilar y Publicar SteamOS.Config (WPF Settings Panel)
Write-Host "`n[2/3] Publicando SteamOS_Config (Panel de Configuración)..." -ForegroundColor Yellow
dotnet publish src\SteamOS.Config\SteamOS.Config.csproj -c Release -r win-x64 --no-self-contained -o $releaseDir
if ($LASTEXITCODE -ne 0) { Write-Error "Error compilando SteamOS.Config"; exit 1 }

# 3. Compilar y Publicar SteamOS.Installer (Standalone Setup Wizard)
Write-Host "`n[3/3] Publicando SteamOS_Setup (Asistente de Instalación)..." -ForegroundColor Yellow
dotnet publish src\SteamOS.Installer\SteamOS.Installer.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o $releaseDir
if ($LASTEXITCODE -ne 0) { Write-Error "Error compilando SteamOS.Installer"; exit 1 }

# Copiar recursos complementarios
Copy-Item "src\SteamOS.Core\icon.ico" -Destination $releaseDir -Force -ErrorAction SilentlyContinue

Write-Host "`n========================================================" -ForegroundColor Green
Write-Host " ¡Compilación completada con éxito en bin\Release! " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Get-ChildItem -Path $releaseDir -Filter "*.exe" | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
