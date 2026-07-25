$ErrorActionPreference = "Stop"

Write-Host "1. Compilando SteamOSHooks64.dll (C++)..." -ForegroundColor Cyan
# Asegurarnos de usar el cmake recién instalado
$cmake = "C:\Program Files\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) {
    $cmake = "cmake"
}

& $cmake -B SteamOSHooks64\build -S SteamOSHooks64
if ($LASTEXITCODE -ne 0) { throw "Error configurando CMake" }

& $cmake --build SteamOSHooks64\build --config Release
if ($LASTEXITCODE -ne 0) { throw "Error compilando la DLL en C++" }

Write-Host "2. Compilando WindowsLikeSteamOS (C#)..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Error compilando la aplicación en C#" }

Write-Host "3. Copiando SteamOSHooks64.dll al directorio final..." -ForegroundColor Cyan
$outDir = "bin\Release\net8.0-windows\win-x64\publish"
Copy-Item "SteamOSHooks64\build\Release\SteamOSHooks64.dll" -Destination $outDir -Force

Write-Host "=======================================================" -ForegroundColor Green
Write-Host "¡Construcción completada con éxito!" -ForegroundColor Green
Write-Host "Todo el sistema (EXE y DLL) está listo en: $outDir" -ForegroundColor Yellow
Write-Host "=======================================================" -ForegroundColor Green
