# Historial del Proyecto "Windows Like SteamOS"

## Estado Actual
El proyecto se encuentra en la versión **v4.2.0 (Estabilidad Crítica y Refactor de Navegación)**. 
Hemos estabilizado exitosamente el entorno de inyección de juegos y corregido los problemas de foco en el menú "Juegos" usando control por mando.

## Logros y Funciones Implementadas
* **Interfaz QAM Rediseñada:** 
  * Se implementó un rediseño de UI inspirado en Steam Deck con botones redondeados (píldoras sin bordes).
  * Dashboard de hardware simétrico y auto-ocultamiento de batería en PCs de escritorio.
  * Botón para abrir directamente el Administrador de Tareas.
* **Integración del Mando Mejorada:** 
  * El Panel QAM ahora se navega de forma nativa e impecable usando el D-PAD.
  * La pestaña de juegos permite recorrer la lista con el D-PAD y usar el botón `(A) Seleccionar` para enfocar la instalación/desinstalación de las DLLs (DXGI.dll / D3D12.dll) sin perder el foco ni quedarse atrapado.
* **Refactor de Inyección (SteamOSHooks64):**
  * Se reemplazaron los problemáticos "Wrappers COM" (Double Hooking) por una inyección directa y limpia de la vTable (vía `MinHook`).
  * Se soporta la intercepción de teclado (bloqueo Alt+F4) de manera nativa sin interrumpir el SwapChain de DirectX 11 o DirectX 12.
* **Estabilidad Crítica Asegurada (Resolución de Crashes Severos en DX11/DX12):**
  * Se eliminó la inyección instantánea (`InjectionDelay=0`) de RTSS que causaba colapsos de Access Violation (The memory could not be written) al iniciar juegos exclusivos de DX11 (como Dark Souls 3). Ahora el delay por defecto de 15 segundos (`15000`) asegura compatibilidad.
  * Se eliminó el redimensionamiento agresivo de ventanas de juegos (`SetWindowLong WS_POPUP` y `SetWindowPos`) desde el panel, ya que esto rompía el flujo del renderizador de juegos a pantalla completa.
  * Se detuvo la aplicación forzada del `DISABLEDXMAXIMIZEDWINDOWEDMODE` en el Registro de Windows (AppCompatFlags\Layers), evitando corromper la pantalla completa del sistema operativo.

## Historial de Iteraciones Críticas y Lecciones Aprendidas (¡Importante para el Futuro!)

* **Fase de Kiosco:** El proyecto inició intentando forzar a Windows a comportarse como un kiosco. Se cambió al enfoque actual (Monitor de Procesos y hooks seguros) debido a limitaciones de permisos.
* **Crashes por RTSS (Zombie Process):** Si se modifica la configuración global de RTSS (`Config` y `Global`), es vital verificar que procesos en segundo plano como `RTSSHooksLoader64.exe` se reinicien. Si no, seguirán inyectando con la configuración antigua de forma agresiva.
* **InjectionDelay = 0 NUNCA MÁS:** Forzar a RTSS a inyectarse en el milisegundo 0 para "evitar tirones del OSD" causa un *Crash* fatal (Access Violation) en juegos que están creando su dispositivo Direct3D (ej. Dark Souls 3). El valor debe quedarse en su default recomendado (15000ms).
* **Interferencia en Pantalla Completa:** NUNCA usar P/Invoke (`SetWindowLong`, `SetWindowPos`) desde C# hacia el *Handle* (`HWND`) de un juego en ejecución en Fullscreen Exclusivo para "forzarlo" a modo ventana sin bordes. El motor gráfico colapsará inmediatamente al presentar (Present) el frame.
* **Manejo de Foco en ListBox de WPF:** Para navegar una lista con mando, es un error hacer que el ListBox atrape el foco si sus elementos internos cambian dinámicamente. La solución correcta (implementada en v4.2.0) es interceptar el comando del mando (`NavUp/NavDown`) a nivel del contenedor principal y modificar la propiedad `SelectedIndex` manualmente, trasladando el foco lógico al botón de acción (`btnGestionarJuego`) sólo cuando se presiona `NavSelect`.

## Arquitectura General
* **App.xaml.cs:** Modifica configuraciones globales y gestiona el lanzamiento del entorno (RTSS, Steam -gamepadui).
* **VentanaRecuperacion.xaml:** El panel QAM (Quick Access Menu) en WPF.
* **SteamService.cs:** Maneja el ciclo de vida de los procesos, monitorea cuándo un juego se abre para aislarlo, y oculta Steam.
* **WindowWatcherService.cs:** Detecta mediante Eventos de Windows (`SetWinEventHook`) qué ventana tiene el primer plano y activa triggers (ej. perfiles por juego o bloqueo de teclas).
* **Mando:** InputSimulatorPlus es usado para traducir las entradas físicas del mando en el panel a navegación nativa (`Tab`, `Shift+Tab`, `Space`, etc.).
* **SteamOSHooks64 (C++):** Librería inyectable opcional compilada como `dxgi.dll` o `d3d12.dll` que intercepta D3D, Win32 (Mouse/Teclado) y XInput para brindar overlays directos y bloqueo de combinaciones de sistema sin latencia extra.
