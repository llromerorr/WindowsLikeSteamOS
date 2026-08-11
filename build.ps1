param(
    [string]$GameDir = "C:\Games\Dark Souls 3\Game"
)

$ErrorActionPreference = "Stop"
$DeployDir = "C:\ProgramData\SteamOS"

Write-Host "0. Deteniendo procesos en ejecución y limpiando memoria..." -ForegroundColor Cyan
$procesos = @("WindowsLikeSteamOS", "WLSOS_Shell", "DiagnosticoRTSS", "DarkSoulsIII")
foreach ($p in $procesos) {
    Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force
}
Start-Sleep -Seconds 2

Write-Host "0.1 Limpiando binarios antiguos..." -ForegroundColor Cyan
if (Test-Path $DeployDir) {
    Get-ChildItem -Path $DeployDir -Include *.exe, *.dll, *.pdb, *.addon -File | Remove-Item -Force -ErrorAction SilentlyContinue
}
if (Test-Path "$GameDir\dxgi.dll") {
    Remove-Item -Path "$GameDir\dxgi.dll" -Force -ErrorAction SilentlyContinue
}
if (Test-Path "$GameDir\SteamOSHooks64.dll") {
    Remove-Item -Path "$GameDir\SteamOSHooks64.dll" -Force -ErrorAction SilentlyContinue
}

Write-Host "1. Compilando SteamOSHooks64.dll (C++)..." -ForegroundColor Cyan
$cmake = "C:\Program Files\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) { $cmake = "cmake" }

& $cmake -B SteamOSHooks64\build -S SteamOSHooks64
if ($LASTEXITCODE -ne 0) { throw "Error configurando CMake" }
& $cmake --build SteamOSHooks64\build --config Release
if ($LASTEXITCODE -ne 0) { throw "Error compilando la DLL en C++" }

Write-Host "2. Compilando Shaders FSR..." -ForegroundColor Cyan
$fxc = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe"
if (Test-Path $fxc) {
    & $fxc /nologo /T ps_5_0 /E main /Fo "WindowsLikeSteamOS\Shaders\FSR_EASU_PS.cso" "WindowsLikeSteamOS\Shaders\FSR_EASU_PS.hlsl"
    & $fxc /nologo /T ps_5_0 /E main /Fo "WindowsLikeSteamOS\Shaders\FSR_RCAS_PS.cso" "WindowsLikeSteamOS\Shaders\FSR_RCAS_PS.hlsl"
    & $fxc /nologo /T vs_5_0 /E main /Fo "WindowsLikeSteamOS\Shaders\FSR_VS.cso" "WindowsLikeSteamOS\Shaders\FSR_VS.hlsl"
} else {
    Write-Host "ADVERTENCIA: fxc.exe no encontrado, saltando compilación de shaders." -ForegroundColor Yellow
}

Write-Host "3. Compilando WindowsLikeSteamOS (C#)..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Error compilando la aplicación en C#" }

Write-Host "4. Desplegando archivos..." -ForegroundColor Cyan
$publishDir = "bin\Release\net8.0-windows\win-x64\publish"

if (-not (Test-Path $DeployDir)) { New-Item -ItemType Directory -Path $DeployDir | Out-Null }
Copy-Item "$publishDir\*" -Destination $DeployDir -Recurse -Force

# Crear copia separada para el Shell
Copy-Item "$DeployDir\WindowsLikeSteamOS.exe" -Destination "$DeployDir\WLSOS_Shell.exe" -Force -ErrorAction SilentlyContinue

# Copiar WLSOS.addon
$addonSource = "SteamOSHooks64\build\Release\WLSOS.addon"
if (Test-Path $addonSource) {
    Copy-Item $addonSource -Destination "$DeployDir\WLSOS.addon" -Force
}

# Desplegar en el juego si existe
if (Test-Path $GameDir) {
    # 1. Copiar ReShade64.dll como dxgi.dll para inyección Direct3D/DXGI
    $reshadeSource = "C:\Users\Luis Romero\GitHub\reshade\bin\x64\Release\ReShade64.dll"
    if (Test-Path $reshadeSource) {
        Copy-Item $reshadeSource -Destination "$GameDir\dxgi.dll" -Force
        Write-Host "-> ReShade DLL copiada al juego: $GameDir\dxgi.dll" -ForegroundColor Green
    } else {
        Write-Host "ADVERTENCIA: No se encontró ReShade64.dll en $reshadeSource" -ForegroundColor Yellow
    }

    # 2. Copiar WLSOS.addon en reshade-addons
    $gameAddonDir = "$GameDir\reshade-addons"
    if (-not (Test-Path $gameAddonDir)) { New-Item -ItemType Directory -Path $gameAddonDir | Out-Null }
    Copy-Item $addonSource -Destination "$gameAddonDir\WLSOS.addon" -Force
    Write-Host "-> Addon copiado al juego: $gameAddonDir\WLSOS.addon" -ForegroundColor Green

    # 3. Desplegar shaders (WLSOS_Pipeline.fx y ReShade.fxh) en reshade-shaders
    $shadersSourceDir = "reshade-shaders\Shaders"
    $gameShadersDir = "$GameDir\reshade-shaders\Shaders"
    if (Test-Path $shadersSourceDir) {
        if (-not (Test-Path $gameShadersDir)) { New-Item -ItemType Directory -Path $gameShadersDir -Force | Out-Null }
        Copy-Item "$shadersSourceDir\*" -Destination $gameShadersDir -Recurse -Force
        
        # También copiar WLSOS_Pipeline.fx directamente en Shaders\ para máxima compatibilidad
        if (Test-Path "$shadersSourceDir\WLSOS\WLSOS_Pipeline.fx") {
            Copy-Item "$shadersSourceDir\WLSOS\WLSOS_Pipeline.fx" -Destination "$gameShadersDir\WLSOS_Pipeline.fx" -Force
        }
        Write-Host "-> Shaders y headers (ReShade.fxh, WLSOS_Pipeline.fx) copiados a: $gameShadersDir" -ForegroundColor Green
    }

    # 4. Asegurar que ReShade.ini tiene la ruta de shaders de WLSOS
    $reshadeIni = "$GameDir\ReShade.ini"
    if (Test-Path $reshadeIni) {
        $iniContent = Get-Content $reshadeIni -Raw
        $wlsosShaderPath = ".\reshade-shaders\Shaders"
        if ($iniContent -notmatch [regex]::Escape($wlsosShaderPath)) {
            $iniContent = $iniContent -replace '(?m)^(EffectSearchPaths=.*)$', "`$1,$wlsosShaderPath"
            Set-Content $reshadeIni -Value $iniContent -NoNewline
            Write-Host "-> ReShade.ini actualizado: EffectSearchPaths ahora incluye $wlsosShaderPath" -ForegroundColor Green
        } else {
            Write-Host "-> ReShade.ini ya contiene la ruta de shaders WLSOS" -ForegroundColor Green
        }
    }
} else {
    Write-Host "ADVERTENCIA: Directorio del juego '$GameDir' no encontrado." -ForegroundColor Yellow
}

Write-Host "=======================================================" -ForegroundColor Green
Write-Host "¡Construcción y despliegue completados con éxito!" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Green
