$ErrorActionPreference = "Stop"

Write-Host "0. Deteniendo procesos en ejecución..." -ForegroundColor Cyan
Get-Process -Name "WindowsLikeSteamOS", "DiagnosticoRTSS" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

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

Write-Host "2. Compilando Shaders FSR..." -ForegroundColor Cyan
$fxc = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe"
if (Test-Path $fxc) {
    & $fxc /nologo /T ps_5_0 /E main /Fo "WindowsLikeSteamOS\Shaders\FSR_EASU_PS.cso" "WindowsLikeSteamOS\Shaders\FSR_EASU_PS.hlsl"
    if ($LASTEXITCODE -ne 0) { throw "Error compilando FSR_EASU_PS.hlsl" }
    
    & $fxc /nologo /T ps_5_0 /E main /Fo "WindowsLikeSteamOS\Shaders\FSR_RCAS_PS.cso" "WindowsLikeSteamOS\Shaders\FSR_RCAS_PS.hlsl"
    if ($LASTEXITCODE -ne 0) { throw "Error compilando FSR_RCAS_PS.hlsl" }
    
    & $fxc /nologo /T vs_5_0 /E main /Fo "WindowsLikeSteamOS\Shaders\FSR_VS.cso" "WindowsLikeSteamOS\Shaders\FSR_VS.hlsl"
    if ($LASTEXITCODE -ne 0) { throw "Error compilando FSR_VS.hlsl" }
} else {
    Write-Host "ADVERTENCIA: fxc.exe no encontrado, saltando compilación de shaders." -ForegroundColor Yellow
}

Write-Host "3. Compilando WindowsLikeSteamOS (C#)..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Error compilando la aplicación en C#" }

Write-Host "3. Copiando SteamOSHooks64.dll al directorio final..." -ForegroundColor Cyan
$outDir = "bin\Release\net8.0-windows\win-x64\publish"
Copy-Item "SteamOSHooks64\build\Release\SteamOSHooks64.dll" -Destination $outDir -Force

Write-Host "=======================================================" -ForegroundColor Green
Write-Host "¡Construcción completada con éxito!" -ForegroundColor Green
Write-Host "Todo el sistema (EXE y DLL) está listo en: $outDir" -ForegroundColor Yellow
Write-Host "=======================================================" -ForegroundColor Green
