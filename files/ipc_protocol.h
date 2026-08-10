#pragma once
// ipc_protocol.h
//
// Contrato compartido entre el host (WindowsLikeSteamOS.exe, C#) y el add-on
// que corre inyectado dentro del proceso del juego (esta DLL, C++).
//
// REGLAS DE ORO para no romper esto en el futuro:
//   1. Solo se agregan campos al FINAL del struct. Nunca se insertan en medio
//      ni se reordenan, o rompes el layout con binarios viejos en circulación.
//   2. Cualquier cambio de SIGNIFICADO de un campo existente (no solo agregar
//      campos nuevos) exige subir IPC_PROTOCOL_VERSION.
//   3. Ambos lados DEBEN chequear protocol_version antes de confiar en el
//      resto del struct. Si no matchea, el addon se queda en modo bypass
//      (todo apagado) en vez de intentar interpretar un layout desconocido.
//   4. El host es el único escritor de este struct. El addon es lector +
//      escritor de su PROPIA copia local (ver addon_main.cpp). Nunca dos
//      escritores sobre la misma memoria: es la forma más rápida de meter
//      una race condition imposible de reproducir en debug.

#include <cstdint>

namespace wls
{
    constexpr uint32_t IPC_PROTOCOL_VERSION = 1;

    // Prefijo del nombre de la memoria compartida. El nombre completo se arma
    // como IPC_MAPPING_PREFIX + PID del proceso del juego, para que el host
    // (que ya tiene el PID por su Monitor de Procesos) pueda abrir exactamente
    // la instancia correcta aunque haya varios juegos/instancias en teoría.
    constexpr wchar_t IPC_MAPPING_PREFIX[] = L"Local\\WLSOS_IPC_";

    // Struct compartido. Se lee/escribe mediante un patrón seqlock:
    // el escritor incrementa `seq` a impar ANTES de escribir el payload y a
    // par DESPUÉS. El lector descarta cualquier lectura donde `seq` haya sido
    // impar o haya cambiado durante la copia. Esto evita usar un Mutex/CS
    // real en el hot path del render thread, que sí puede causar stalls o
    // hasta deadlocks si el host se cuelga con el lock tomado.
    struct IpcState
    {
        uint32_t protocol_version;
        uint64_t host_pid;
        uint64_t host_heartbeat;   // el host lo incrementa cada ~250ms
        volatile uint32_t seq;     // seqlock: impar = escritura en curso

        // ---- payload: solo válido si seq es par y no cambió durante la copia ----
        bool  overlay_visible;
        bool  fsr_enabled;
        float fsr_sharpness;      // 0..1
        bool  crt_enabled;
        float crt_intensity;      // 0..1

        // Espacio para crecer sin romper compatibilidad binaria burda.
        // Usar estos antes de agregar campos nuevos "sueltos" cuando el
        // cambio no amerita subir la versión del protocolo.
        uint8_t reserved[64];
    };

    static_assert(sizeof(IpcState) < 4096, "IpcState debe caber holgado en una sola página");
}
