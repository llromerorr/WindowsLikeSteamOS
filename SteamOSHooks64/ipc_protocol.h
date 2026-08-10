#pragma once
// ipc_protocol.h

#include <cstdint>
#include <atomic>

namespace wls
{
    constexpr uint32_t IPC_PROTOCOL_VERSION = 1;
    constexpr wchar_t IPC_MAPPING_PREFIX_HOST_TO_ADDON[] = L"Local\\WLSOS_IPC_H2A_";
    constexpr wchar_t IPC_MAPPING_PREFIX_ADDON_TO_HOST[] = L"Local\\WLSOS_IPC_A2H_";
    constexpr int      IPC_SEQLOCK_MAX_RETRIES = 8;

    // Canal 1: Host -> Addon (Estado Base)
    struct HostToAddonState {
        uint32_t protocol_version;
        uint32_t _pad0;               // <-- explicit padding
        uint64_t host_pid;
        uint64_t host_heartbeat;      // C# lo incrementa cada 250ms (fail-safe)
        std::atomic<uint32_t> seq;    // Seqlock del Host (release/acquire)

        uint8_t  overlay_visible;
        uint8_t  fsr_enabled;
        uint8_t  crt_enabled;
        uint8_t  _pad1;               // <-- explicit padding
        float    fsr_sharpness;
        float    crt_intensity;
        uint8_t  reserved[64];
    };

    // Acciones del sistema solicitadas desde el QAM de ReShade
    enum PowerAction : uint8_t {
        POWER_ACTION_NONE = 0,
        POWER_ACTION_SUSPEND = 1,
        POWER_ACTION_HIBERNATE = 2,
        POWER_ACTION_RESTART = 3,
        POWER_ACTION_SHUTDOWN = 4,
        POWER_ACTION_DESKTOP = 5
    };

    // Canal 2: Addon -> Host (Peticiones de cambio)
    struct AddonToHostState {
        uint32_t protocol_version;
        uint32_t _pad0;                // <-- explicit padding
        uint64_t addon_pid;            // PID real del proceso del juego, para validación de origen
        uint64_t addon_heartbeat;      // Fail-safe inverso: Host detecta Addon caído
        std::atomic<uint32_t> seq;     // Seqlock del Addon (release/acquire)

        uint32_t request_epoch;        // Se incrementa en cada interacción del usuario en ImGui
        uint8_t  desired_fsr_mode;     // 0 = OFF, 1 = 720p (FSR), 2 = 900p (FSR)
        uint8_t  desired_fps_limit;    // 0 = OFF, 30, 45, 60
        uint8_t  requested_volume;     // 0 a 100%
        uint8_t  requested_power_action; // enum PowerAction
        float    desired_fsr_sharpness;
        float    desired_crt_intensity;
        uint8_t  reserved[64];
    };

    static_assert(sizeof(std::atomic<uint32_t>) == sizeof(uint32_t), "std::atomic<uint32_t> debe tener el mismo tamano que uint32_t");
    static_assert(offsetof(HostToAddonState, host_pid) % alignof(uint64_t) == 0, "host_pid debe estar alineado a 8 bytes");
    static_assert(offsetof(AddonToHostState, addon_pid) % alignof(uint64_t) == 0, "addon_pid debe estar alineado a 8 bytes");
    static_assert(offsetof(HostToAddonState, seq) % alignof(uint32_t) == 0, "seq debe estar alineado a 4 bytes");
    static_assert(offsetof(HostToAddonState, fsr_sharpness) % alignof(float) == 0, "fsr_sharpness debe estar alineado a 4 bytes");
    static_assert(sizeof(HostToAddonState) < 4096, "HostToAddonState debe caber en una pagina");
    static_assert(sizeof(AddonToHostState) < 4096, "AddonToHostState debe caber en una pagina");
}
