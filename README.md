# WLSteamOS (WindowsLikeSteamOS) 🎮✨

**WLSteamOS** transforma cualquier PC con Windows 10/11 en una consola de videojuegos dedicada, replicando la experiencia fluida e integrada de **SteamOS (Steam Big Picture)** en una cuenta de usuario aislada, con optimizaciones automáticas de hardware, control de pantallas, emulación de mandos a bajo nivel y un sistema reactivo de recuperación.

---

## 📌 ¿Qué es WLSteamOS?

WLSteamOS consta de dos partes integradas en un único ejecutable C#/.NET 8.0 autocontenido:

1. **Configurador Administrativo (GUI):** Interfaz gráfica moderna con estética dark-mode y glassmorphism que gestiona la instalación del entorno, dependencias en red (RivaTuner RTSS / Steam CDN), creación de la cuenta local de Windows y calibración de hardware.
2. **Master Shell Extensivo:** Reemplazo directo y liviano del entorno `explorer.exe` registrado para el usuario local dedicado (`SteamOS`). Actúa como un hipervisor del ciclo de vida de Steam y del sistema operativo sin cargar elementos innecesarios de Windows.

---

## 📖 Manual de Uso e Instalación Paso a Paso

### 📋 Requisitos del Sistema
- Windows 10 o Windows 11 (64-bit).
- Permisos de Administrador en el PC.
- Mando de videojuegos (Xbox, PlayStation, o mando genérico DirectInput).
- Steam instalado (si no está instalado, la app puede descargarlo automáticamente).

---

### 🚀 1. Instalación Inicial (Crear el Entorno Gaming)

1. Descarga el ejecutable ejecutable compilado `WindowsLikeSteamOS.exe` desde la sección de **Releases** de GitHub.
2. Ejecuta `WindowsLikeSteamOS.exe` haciendo **clic derecho -> Ejecutar como Administrador**.
3. En la pantalla del Configurador:
   - **Monitor Principal:** Selecciona la pantalla donde quieres jugar (TV, Monitor Gaming).
   - **Resolución y Refresco:** Configura la resolución deseada (ej. `1920x1080`) y la tasa de refresco (ej. `60Hz`, `120Hz`, `144Hz`).
   - **Dispositivo de Audio:** Elige por dónde se reproducirá el sonido del juego.
   - **Rendimiento:** (Opcional) Activa el límite de FPS vía RivaTuner (RTSS) o Nvidia Fast Sync.
   - **Mapear Mando:** (Opcional) Si usas un mando genérico, puedes mapear los botones directamente en la app.
4. Haz clic en **`INSTALAR STEAMKIOSK`**.
   - *El sistema creará automáticamente el usuario local `SteamOS` en Windows, configurará las optimizaciones de registro y preparará el entorno de consola.*

---

### 🎮 2. Uso Diario de la Consola

1. Cierra sesión en tu cuenta actual de Windows o reinicia el equipo.
2. En la pantalla de inicio de sesión de Windows, selecciona la nueva cuenta llamada **`SteamOS`**.
3. El equipo iniciará sesión directamente en **Steam Big Picture Mode** en pantalla completa. Sin escritorio de Windows, barras de tareas ni ventanas molestas.
4. Puedes navegar por toda la interfaz de Steam utilizando tu mando (D-Pad, Joysticks y Botón A).

---

### 🛠️ 3. Menú de Recuperación (Herramientas de Emergencia)

Si Steam se congela, falla un juego o quieres salir al escritorio de Windows tradicional o cerrar sesión, puedes abrir en cualquier momento el **Menú de Recuperación**:

#### 🕹️ Cómo abrir el Menú de Recuperación:
- **Desde el Mando:** Presiona la combinación **`Start + Select + LB + RB`** al mismo tiempo.
- **Desde el Teclado:** Presiona **`Ctrl + Shift + Alt + R`**.
- **Atajo de Salida Rápida al Escritorio:** Presiona **`Ctrl + Shift + Alt + S`**.

#### 📋 Opciones disponibles en el Menú de Recuperación:
- ** Reintentar Inicio de Steam:** Fuerza el cierre de procesos congelados de Steam y relanza la interfaz Big Picture.
- ** Salir a Modo Escritorio:** Detiene la sesión de consola, activa de nuevo las pantallas secundarias y abre el escritorio tradicional de Windows (`explorer.exe`).
- ** Cerrar Sesión de Windows:** Cierra la cuenta `SteamOS` y te devuelve a la pantalla de selección de usuarios de Windows.
- ** Reparar Steam desde CDN:** Descarga y reinstala la versión más reciente de Steam directamente desde los servidores oficiales de Valve si se dañó algún archivo.
- ** Restaurar Entorno de Pantallas:** Restablece la configuración de monitores y tasas de refresco originales.

*Nota: La interfaz del menú de recuperación se controla 100% con el mando (Arriba/Abajo con D-Pad o Joystick y A para Seleccionar).*

---

### 🗑️ 4. Desinstalación y Purga del Entorno

Si deseas eliminar la cuenta de consola y dejar tu Windows tal como estaba originalmente:

1. Inicia sesión en tu cuenta de usuario normal de Administrador en Windows.
2. Abre `WindowsLikeSteamOS.exe` como Administrador.
3. Haz clic en el botón **`DESINSTALAR`** o **`PURGAR`**.
4. La interfaz deshabilitará todos los botones y mostrará una barra de progreso mientras elimina de forma segura el usuario `SteamOS`, limpia las claves de registro y restaura la configuración de inicio de Windows.
5. Al recibir la confirmación de éxito, el entorno habrá sido totalmente desinstalado.

---

## 🔍 Arquitectura Técnica de Bajo Nivel

Para los desarrolladores y usuarios avanzados, aquí se detalla el funcionamiento interno de WLSteamOS:

### 1. 🖥️ Aislamiento de Pantallas y Topología Win32
- **Enumeración Quirúrgica:** Usa `EnumDisplayDevices` y `EnumDisplaySettings` para leer los adaptadores físicos activos, sus coordenadas espaciales y modos de refresco soportados.
- **Aislamiento Físico:** Desactiva físicamente todos los monitores secundarios escribiendo una configuración temporal de `DEVMODE` con campos de ancho y altura en cero (`dmPelsWidth = 0`, `dmPelsHeight = 0`) y aplicando los cambios de topología con `ChangeDisplaySettingsEx`. Esto apaga las pantallas adicionales y evita que el foco de renderizado se pierda.
- **Tasa de Refresco Estricta:** Fuerza el monitor principal a los Hz seleccionados en la configuración antes de que Steam despierte, minimizando el lag de entrada y parpadeos en paneles VRR (G-Sync/FreeSync).

### 2. ⚡ Integración de NVAPI y GPU Scaling (`nvapi64.dll`)
- **QueryInterface Dinámico:** NVAPI consulta dinámicamente las funciones nativas `NvAPI_Initialize`, `NvAPI_Disp_GetDisplayConfig` y `NvAPI_Disp_SetDisplayConfig` usando punteros de función y delegados (`UnmanagedFunctionPointer` en C#).
- **Fuerza Bruta de Escalado:** Modifica las propiedades internas de escalado para forzar **GPU Scaling a Full Panel (Stretch)**, evitando que el monitor intente reescalar la señal (causando latencia de visualización) y forzando a la GPU a realizar el escalado bilineal nativo.
- **Protección de Memoria:** Estructuras unmanaged alojadas en el heap mediante `Marshal.AllocHGlobal`, con limpieza garantizada en bloques `finally`.

### 3. ⌨️ Captura de Teclado y Hook Global (`SetWindowsHookEx`)
- **Hook de Bajo Nivel:** Se registra un hook global de teclado de tipo `WH_KEYBOARD_LL` (13) mediante `SetWindowsHookEx` en el thread del Shell.
- **Prevención de Crashes en DirectX 11:** El monitor de procesos detecta activamente cuándo un juego pasa a primer plano y suspende dinámicamente el hook de teclado, reactivándolo de inmediato cuando el juego se cierra para prevenir cuelgues.

### 4. 🔄 Ciclo de Vida y Máquina de Estados Reactiva de Steam
- **Monitoreo de Ventanas:** Lee constantemente el texto y procesos visibles del sistema. Distingue entre las pantallas de actualización de Steam (`"updating steam"`, `"bootstrapper"`), la pantalla de login (`"steam login"`) y la interfaz Big Picture (`"Gamepad UI"`).
- **Gestión de Actualizaciones:** Si el usuario actualiza Steam y este se cierra, la máquina de estados detecta la desaparición de las ventanas, espera una ventana de gracia reactiva de 4 segundos y reconecta el Shell al nuevo proceso relanzado con `-gamepadui`.
- **Redirección Multimonitor (`steamwebhelper`):** La aplicación escanea la jerarquía de ventanas de los procesos hijos `steamwebhelper.exe` (CEF) para reposicionar y redimensionar forzadamente la ventana principal en las coordenadas principales `(0,0)`.

### 5. 🔊 Ruteo de Audio y Limpieza COM
- El audio se gestiona instanciando interfaces COM de Windows Core Audio (WASAPI).
- Al cambiar el dispositivo de audio por defecto para la sesión de juego, se garantiza la liberación inmediata de los objetos unmanaged usando bloques `using` en C#.

### 6. ⏻ Gestión de Suspensión y Energía (Power Management)
- **Plan de Alto Rendimiento:** Modifica el plan de energía activo de Windows a "Alto rendimiento" (`8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`) usando `powercfg` al arrancar el Kiosco, y restaura el plan original al salir de la sesión.
- **Bloqueo de Reposo Físico:** Llama a `SetThreadExecutionState` con banderas de ejecución continua para deshabilitar temporalmente la suspensión de la pantalla y del procesador.
- **Intercepción de Suspensión Nativa (`WM_POWERBROADCAST`):** Captura el mensaje `WM_POWERBROADCAST` (0x0218) y sus eventos `PBT_APMSUSPEND` y `PBT_APMRESUMESUSPEND`. Al suspender el sistema, detiene de forma limpia los mapeadores y hooks. Al despertar, reconstruye reactivamente el aislamiento de pantallas, refresco de pantalla, volumen de audio y mapeo de mandos físicos.

---

## 🎮 Emulación y Traducción de Mandos (Nivel Kernel)

- **Driver Virtual:** Utiliza `ViGEmBus` (Virtual Gamepad Emulation Bus) para crear una instancia de control de Xbox 360 directo a nivel de controlador de dispositivo del kernel.
- **Aislamiento de Mando Físico (`HidHide`):** Se conecta con el servicio `HidHideControlService` para ocultar el ID del dispositivo físico a nivel de hardware, evitando el problema de "doble entrada" en los juegos.
- **Atajos Físicos Avanzados:** Mapea la combinación `Select + Start` para activar el botón Guía de Xbox y abrir el menú overlay de Steam Big Picture.

---

## 🛠️ Compilación desde el Código Fuente

Para generar el ejecutable autocontenido y optimizado sin dependencias externas:

```bash
make publish
```

*(O ejecutando el comando dotnet directamente):*
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

El ejecutable compilado estará ubicado en:  
`bin\Release\net8.0-windows\win-x64\publish\WindowsLikeSteamOS.exe`

---

## ⚖️ Licencia

Este proyecto se distribuye bajo la licencia **MIT**. Siéntete libre de modificar, auditar y contribuir al proyecto.