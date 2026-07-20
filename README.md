# WindowsLikeSteamOS 🎮✨

Transforma tu PC con Windows en una consola de videojuegos dedicada, replicando la experiencia fluida, reactiva e integrada de SteamOS (Steam Big Picture) en una cuenta de usuario aislada, con optimizaciones automáticas de hardware y control de APIs a bajo nivel.

Este proyecto consta de dos partes integradas en un único ejecutable C#/.NET 8.0 autocontenido:
1. **Configurador Administrativo (GUI):** Interfaz WPF moderna con estética dark-mode y glassmorphism que gestiona la instalación del entorno, dependencias en red (Winget / CDN oficial de Valve), creación de perfiles y personalización del hardware.
2. **Master Shell Extensivo:** Reemplazo directo y liviano del entorno `explorer.exe` registrado para el usuario local dedicado. Actúa como un hipervisor del ciclo de vida de Steam y del sistema operativo.

---

## 🔍 Arquitectura Técnica de Bajo Nivel

Para los desarrolladores y usuarios avanzados, aquí se detalla lo que ocurre bajo el capó al iniciar la sesión:

### 1. 🖥️ Aislamiento de Pantallas y Topología Win32
En lugar de depender de abstracciones de alto nivel de Windows Forms o WPF, la aplicación interactúa directamente con el subsistema gráfico de Windows mediante **P/Invokes nativos de `user32.dll`**:
* **Enumeración Quirúrgica:** Usa `EnumDisplayDevices` y `EnumDisplaySettings` para leer los adaptadores físicos activos, sus coordenadas espaciales y modos de refresco soportados.
* **Aislamiento Físico:** Desactiva físicamente todos los monitores secundarios escribiendo una configuración temporal de `DEVMODE` con campos de ancho y altura en cero (`dmPelsWidth = 0`, `dmPelsHeight = 0`) y aplicando los cambios de topología con `ChangeDisplaySettingsEx`. Esto apaga las pantallas adicionales y evita que el ratón o el foco de renderizado se escapen.
* **Tasa de Refresco Estricta:** Fuerza el monitor principal a los Hz seleccionados en la configuración antes de que Steam despierte, minimizando la latencia de entrada y los parpadeos en paneles con VRR (G-Sync/FreeSync).

### 2. ⚡ Integración de NVAPI y GPU Scaling (`nvapi64.dll`)
Para juegos antiguos o competitivos que se ejecutan a resoluciones inferiores a la nativa en pantalla completa exclusiva, la aplicación se comunica con el driver de NVIDIA:
* **QueryInterface Dinámico:** NVAPI no exporta la mayoría de sus funciones directamente; solo expone la interfaz unificada `nvapi_QueryInterface`. La aplicación consulta dinámicamente las funciones nativas `NvAPI_Initialize`, `NvAPI_Disp_GetDisplayConfig` y `NvAPI_Disp_SetDisplayConfig` usando punteros de función y delegados (`UnmanagedFunctionPointer` en C#).
* **Fuerza Bruta de Escalado:** Modifica las propiedades internas de escalado de la pantalla activa para forzar **GPU Scaling a Full Panel (Stretch)**, evitando que el monitor intente reescalar la señal (lo que causa latencia de visualización) y forzando a la GPU a realizar el escalado bilineal nativo ultra rápido.
* **Protección del Heap:** Las estructuras complejas de NVAPI son alocadas directamente en el heap unmanaged mediante `Marshal.AllocHGlobal`, inicializadas a cero con buffers temporales (`Marshal.Copy`) para evitar punteros basura en las rutas de excepción, y liberadas en bloques `finally` a nivel de bytes para prevenir fugas de memoria.

### 3. ⌨️ Captura de Teclado y Hook Global (`SetWindowsHookEx`)
Para ofrecer una experiencia de consola real, es crucial que el usuario no pueda abrir accidentalmente atajos del sistema (como la tecla Windows, `Alt+Tab`, `Alt+Esc` o `Ctrl+Esc`):
* **Hook de Bajo Nivel:** Se registra un hook global de teclado de tipo `WH_KEYBOARD_LL` (13) mediante `SetWindowsHookEx` en el thread del Shell.
* **Prevención de Crashes en DirectX 11 (Suspensión en Gameplay):** Muchos juegos antiguos basados en DirectX 11 entran en conflicto con hooks de teclado globales cuando cambian de estado de renderizado o resolución de pantalla, provocando cuelgues (`crashes`) repentinos. Para solucionarlo, el monitor de procesos detecta activamente cuándo el juego pasa a primer plano y **suspende dinámicamente el hook de teclado**, reactivándolo de inmediato solo cuando el juego se cierra.

### 4. 🔄 Ciclo de Vida y Máquina de Estados Reactiva de Steam
El Shell no realiza esperas ciegas basadas en tiempos fijos; implementa un monitor de eventos reactivo:
* **Monitoreo de Ventanas:** Lee constantemente el texto y procesos visibles del sistema. Distingue entre las pantallas de actualización de Steam (`"updating steam"`, `"bootstrapper"`), la pantalla de login (`"steam login"`) y la interfaz Big Picture (`"Gamepad UI"`).
* **Gestión de Actualizaciones:** Si el usuario actualiza Steam y este se cierra para reiniciarse, la máquina de estados detecta la desaparición de las ventanas, espera una ventana de gracia reactiva de **4 segundos** (con verificación de persistencia para ignorar sincronizaciones rápidas en la nube) y reconecta el Shell al nuevo proceso relanzado con `-gamepadui`.
* **Redirección Multimonitor (`steamwebhelper`):** La interfaz Big Picture moderna es renderizada por los procesos hijos `steamwebhelper.exe` (Chromium Embedded Framework) y no por `steam.exe`. La aplicación escaneará la jerarquía de ventanas de ambos procesos para reposicionar y redimensionar de forma forzada la ventana principal en las coordenadas principales `(0,0)` apenas se inicializa.

### 5. 🔊 Ruteo de Audio y Limpieza COM
* El audio se gestiona instanciando interfaces COM de Windows Core Audio (WASAPI).
* Al cambiar el dispositivo de audio por defecto para la sesión de juego, se garantiza la liberación inmediata de los objetos unmanaged usando bloques `using` en C#, previniendo fugas de descriptores COM en el message pump del Shell.

### 6. ⏻ Gestión de Suspensión y Energía (Power Management)
* **Plan de Alto Rendimiento:** Modifica el plan de energía activo de Windows a "Alto rendimiento" (`8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`) usando `powercfg` al arrancar el Kiosco, y restaura el plan original al salir de la sesión.
* **Bloqueo de Reposo Físico:** Llama a `SetThreadExecutionState` con banderas de ejecución continua para deshabilitar temporalmente la suspensión de la pantalla y el procesador.
* **Intercepción de Suspensión Nativa (`WM_POWERBROADCAST`):** Captura el mensaje de Windows `WM_POWERBROADCAST` (0x0218) y sus eventos `PBT_APMSUSPEND` y `PBT_APMRESUMESUSPEND`. Al suspender el sistema, detiene de forma limpia los mapeadores y hooks. Al despertar de la suspensión (resume), reconstruye reactivamente el aislamiento de pantallas, refresco de pantalla, volumen de audio y mapeo de mandos físicos en milisegundos.

---

## 🎮 Emulación y Traducción de Mandos (Nivel Kernel)

La aplicación incluye un emulador integrado para mandos DirectInput a Xbox 360:
* **Driver Virtual:** Utiliza `ViGEmBus` (Virtual Gamepad Emulation Bus) para crear una instancia de control de Xbox 360 directo a nivel de controlador de dispositivo del kernel.
* **Aislamiento de Mando Físico (`HidHide`):** Para evitar el problema de "doble entrada" (donde el juego lee al mismo tiempo el mando físico genérico y el mando virtualizado de Xbox 360), el Shell se conecta con el servicio `HidHideControlService`. Inserta la ruta del ejecutable en la lista blanca de aplicaciones y oculta el ID del dispositivo físico a nivel de hardware, de modo que Windows y los juegos solo "ven" el mando de Xbox 360 emulado.
* **Atajos Físicos Avanzados:** Mapea el botón **Xbox Guide** combinando `Select + Start`, permitiendo abrir el menú superpuesto de Steam Big Picture sin tener un teclado a la mano.

---

## 🚀 Compilación y Distribución

Para generar un ejecutable autocontenido, optimizado y sin dependencias externas (Single File Executable):

```bash
make publish
```

*(O ejecutando la directiva de compilación directa):*
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

Esto generará el ejecutable compilado en:  
`bin\Release\net8.0-windows\win-x64\publish\WindowsLikeSteamOS.exe`

---

## 🛑 Atajo de Emergencia
Si estás en una sesión de juego dedicada y necesitas forzar el retorno al escritorio clásico de Windows, presiona en cualquier momento:
* ⌨️ **`Ctrl + Alt + Shift + S`**

Este atajo realiza un bypass total: enciende las pantallas secundarias, restaura la topología original, destruye los procesos de Steam del espacio de memoria y levanta `explorer.exe` de inmediato sin necesidad de cerrar sesión.

---

## ⚖️ Licencia
Este proyecto se distribuye bajo la licencia MIT. Siéntete libre de modificar, auditar y expandir sus capacidades gráficas y de hardware.