# Plan de Implementación: Compositor Externo (Arquitectura Definitiva V4)

Este documento refleja la arquitectura final tras resolver la crisis del "Foco de Windows" y el "Raw Input" para cámaras 3D, utilizando una solución elegante de Win32.

## 1. El Paradigma "No pelees por el foco"
El principal riesgo del Compositor Externo era robarle el foco al juego, lo que causaría caídas de FPS, silencio de audio y pérdida de control de cámara.
**La Solución:** El Compositor WPF se creará con los estilos `WS_EX_NOACTIVATE` y `WS_EX_TOPMOST`.
- El Compositor dibujará el escalado FSR por encima de todo.
- El juego **nunca perderá el foco real**. Windows seguirá considerando al juego como la ventana activa en primer plano.
- Esto elimina el 100% de la necesidad de hookear `GetForegroundWindow` o `WM_ACTIVATE`.

## 2. Resolución del Input (Cámara 3D vs UI)
Dado que el juego retiene el foco genuino de Windows:
- **Cámara 3D (Raw Input):** El motor gráfico recibirá sus eventos `WM_INPUT` (deltas del ratón) de forma nativa y directa desde el sistema operativo. No se requiere ningún hook para la cámara. Funciona "gratis".
- **Input del Compositor:** Si el Compositor necesita detectar atajos de teclado (ej. F12 para activar/desactivar FSR), usará un hook global de bajo nivel (`WH_KEYBOARD_LL`) que opera sin importar quién tenga el foco.

## 3. Los Únicos Hooks Win32 Restantes (ClipCursor y SetCursorPos)
Para evitar que el cursor físico del sistema quede atrapado en la diminuta ventana 720p del juego en segundo plano, la DLL en C++ deberá interceptar:
- `ClipCursor`: Se convertirá en un *no-op* (devolverá `TRUE` sin hacer nada) o redirigirá el confinamiento al área del monitor completo.
- `SetCursorPos`: Se interceptará si el motor intenta forzar el recentrado agresivo del cursor, evitando que el ratón físico salte erráticamente.

*(Nota: El Compositor WPF ocultará el cursor real de Windows y dibujará un cursor virtual propio para evitar artefactos visuales).*

## 4. El "Puente" (Textura Compartida en hkPresent)
El ciclo de vida del *frame* será:
1. El juego renderiza su cuadro en su *swapchain* interno (720p).
2. En `hkPresent`, interceptamos la llamada.
3. Copiamos el *backbuffer* de 720p a nuestra Textura Compartida de VRAM (`D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`).
4. Liberamos el *Mutex* para que C# pueda leerla.
5. C# lee la textura, aplica FSR in-process en WPF, y la presenta en 1080p.

## Siguientes Pasos
Si este diseño arquitectónico es aprobado, procederemos a:
1. Eliminar todo el código de spoofing de resolución obsoleto (`WM_WINDOWPOSCHANGING`, `WM_FORCE_FULLSCREEN`, `GetClientRect`, etc.) en la DLL.
2. Implementar los hooks de `ClipCursor` y `SetCursorPos`.
3. Estructurar la sincronización de la Textura Compartida en `hkPresent`.
