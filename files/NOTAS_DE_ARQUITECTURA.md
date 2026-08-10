# Notas de arquitectura — Add-on de ReShade + QAM in-game

## Qué cambia respecto a la arquitectura actual

- `SteamOSHooks64.dll` (MinHook manual) se reemplaza por un **add-on de
  ReShade** (`addon_main.cpp`). ReShade hace el hooking de `Present` /
  `SetFullscreenState` por ustedes; el add-on solo reacciona a eventos ya
  resueltos.
- El QAM deja de ser una ventana WPF flotante y pasa a ser un panel **ImGui
  dibujado dentro del framebuffer del juego**, vía `register_overlay`. Mismo
  mecanismo que usa el overlay nativo de ReShade (tecla Home) — por eso
  funciona en pantalla completa exclusiva sin pelear con el compositor de
  Windows.
- El canal de Memoria Compartida que ya tenían (Decisión de arquitectura
  original) se mantiene, pero con un **seqlock** en vez de asumir lecturas
  atómicas de un struct plano.

## Requisito para que esto funcione: build de ReShade con add-ons habilitados

Los builds oficiales firmados de reshade.me **no traen add-ons de terceros
habilitados** (por compatibilidad con anti-cheat). Necesitan compilar
ReShade desde el repo (`crosire/reshade`) con soporte de add-ons activo, y
esa DLL de ReShade (renombrada a `dxgi.dll` / la que corresponda) es la que
va inyectada como proxy — su `SteamOSHooks64.dll` original se retira, o pasa
a ser solo el add-on que carga ReShade.

## Checklist de robustez aplicado en el skeleton

| Riesgo | Mitigación |
|---|---|
| Race entre host (escritor) y addon (lector) en el render thread | Seqlock (`seq` par/impar) en vez de mutex — nunca bloquea Present() |
| Host se cuelga o crashea con el overlay abierto | Heartbeat con timeout de 2s → estado fail-safe (todo apagado) |
| Cambios futuros de layout rompen binarios viejos | `protocol_version` chequeado antes de confiar en el struct + reserva de bytes para crecer |
| I/O bloqueante durante `DllMain` cuelga el juego | Conexión IPC diferida al primer `on_present`, nunca en `DLL_PROCESS_ATTACH` |
| Overhead cuando no hay host conectado | Bypass explícito: sin `g_ipc`, `on_present` retorna casi de inmediato (mismo espíritu que la Decisión B original) |
| Reconectar cada frame si el host tarda en levantar | Backoff de 1s entre intentos de `OpenFileMapping` |
| Device reset / alt-tab / cambio de resolución | Eventos `init_device` / `destroy_device` como puntos explícitos para (re)inicializar cualquier recurso propio del addon |
| Dos escritores sobre el mismo struct (host + addon) | Diseño explícito de un solo escritor por struct; si el panel in-game necesita mandar datos de vuelta, usar un segundo struct con su propio seqlock, no reusar el mismo |
| Excepción cruzando el límite DLL → juego | Nada de `throw`/RTTI cruzando `on_present`/`draw_overlay`; cualquier error se loguea con `reshade::log_message` y se degrada a estado seguro |

## Lado C# (host) — qué falta implementar ahí

1. Al detectar el juego (su Monitor de Procesos ya existente), crear el
   `FileMapping` con nombre `Local\WLSOS_IPC_<PID>` **antes o justo al
   lanzar** el juego, para minimizar la ventana de reintento del addon.
2. Escribir siempre con el patrón seqlock: `seq++` (queda impar) → escribir
   payload → `seq++` (queda par). Nunca escribir el payload sin ese envoltorio.
3. Incrementar `host_heartbeat` cada ~250ms mientras el proceso host esté
   vivo y monitoreando ese juego.
4. Al cerrar el juego o el host, cerrar el `FileMapping` — el addon ya
   maneja el caso "mapping desaparece" al no encontrar heartbeat.

## Estrategia de testing sugerida

- Extraer `read_ipc_snapshot` / la lógica de seqlock a una unidad que se
  pueda testear con un "host falso" en un proceso de test separado,
  simulando: escritura torn, host que deja de latir, versión de protocolo
  distinta. Esto es lo que en la práctica atrapa el 80% de los bugs de este
  tipo de IPC antes de que aparezcan jugando 6 horas seguidas.
- Probar explícitamente: alt-tab repetido, cambio de resolución en vivo,
  cierre abrupto del host (`taskkill /f`) con el juego corriendo, y arranque
  del juego ANTES que el host (para validar el backoff de reconexión).

## Pendiente de diseño (para la próxima iteración)

- Definir si el slider de FSR/CRT en el panel in-game debe reflejarse de
  vuelta en la UI de WPF del host en tiempo real (requiere el segundo canal
  "addon → host" mencionado arriba) o si por ahora el WPF y el ImGui pueden
  divergir sin problema.
- Mapear los shaders de FSR (EASU/RCAS) y el efecto CRT como técnicas
  `.fx` reales cargadas por ReShade, y reemplazar los `TODO` de
  `apply_effect_state` por las llamadas concretas a
  `effect_runtime::set_technique_state`.
