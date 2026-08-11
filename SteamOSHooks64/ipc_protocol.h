#pragma once
#include <cstdint>
#include <atomic>
#include <cstddef> // offsetof

namespace wls
{
    // =========================================================
    // Versioning / Names
    // =========================================================
    constexpr uint32_t IPC_PROTOCOL_VERSION = 2;

    constexpr wchar_t IPC_MAPPING_PREFIX_HOST_TO_ADDON[] = L"Local\\WLSOS_IPC_H2A_";
    constexpr wchar_t IPC_MAPPING_PREFIX_ADDON_TO_HOST[] = L"Local\\WLSOS_IPC_A2H_";

    constexpr int IPC_SEQLOCK_MAX_RETRIES = 8;

    // =========================================================
    // Enums (agreed)
    // =========================================================
    enum : uint8_t
    {
        AA_OFF  = 0,
        AA_SMAA = 1,
        AA_TAA  = 2,
        AA_CMAA2 = 3
    };

    enum : uint8_t
    {
        SHARPEN_OFF = 0,
        SHARPEN_CAS = 1,
        SHARPEN_RCAS = 2
    };

    // =========================================================
    // Request mask (Addon -> Host)
    // Only apply fields whose bit is set.
    // =========================================================
    enum IpcRequestMask : uint32_t
    {
        REQ_OVERLAY  = 1u << 0,
        REQ_VOLUME   = 1u << 1,
        REQ_FPS_LIMIT= 1u << 2,
        REQ_AA       = 1u << 3,
        REQ_SHARPEN  = 1u << 4,
        REQ_CRT      = 1u << 5,
        REQ_POWER    = 1u << 6,

        // room for future
        REQ_RESERVED7 = 1u << 7
    };

    enum : uint8_t
    {
        POWER_ACTION_NONE = 0,
        POWER_ACTION_SUSPEND = 1,
        POWER_ACTION_HIBERNATE = 2,
        POWER_ACTION_RESTART = 3,
        POWER_ACTION_SHUTDOWN = 4,
        POWER_ACTION_DESKTOP = 5
    };

    // =========================================================
    // Host -> Addon (authoritative effective state)
    // Writer: Host (C#)
    // Reader: Addon (C++)
    // =========================================================
    struct HostToAddonState
    {
        uint32_t protocol_version;      // = IPC_PROTOCOL_VERSION
        uint32_t _pad0;                 // explicit padding to 8-byte alignment

        uint64_t host_pid;
        uint64_t host_heartbeat;        // increments ~250ms

        std::atomic<uint32_t> seq;      // seqlock (host writer)

        // ---- compact flags (exactly 4 bytes) ----
        uint8_t overlay_visible;        // 0/1 (transient, not persisted)
        uint8_t aa_mode;                // enum AA_*
        uint8_t sharpen_mode;           // enum SHARPEN_*
        uint8_t crt_enabled;            // 0/1

        // ---- scalar values (effective) ----
        float    master_volume;         // 0..1 (effective)
        uint32_t fps_limit;             // 0=off, else 30/45/60/...
        float    sharpen_strength;      // 0..1
        float    crt_intensity;         // 0..1

        // AA parameters (only meaningful for certain modes)
        float    taa_jitter;            // 0..1 (TAA)
        float    taa_seeking;           // 0.025..0.250 (TAA)
        float    cmaa2_edge_threshold;  // 0.02..0.15 (CMAA2_beta style; if CMAA2 impl differs, still safe)

        uint8_t  reserved[68];          // keep struct size stable (total 128 bytes)
    };

    // =========================================================
    // Addon -> Host (requests)
    // Writer: Addon (C++)
    // Reader: Host (C#)
    // =========================================================
    struct AddonToHostState
    {
        uint32_t protocol_version;      // = IPC_PROTOCOL_VERSION
        uint32_t _pad0;                 // explicit padding to 8-byte alignment

        uint64_t addon_pid;             // game PID (Host validates)
        uint64_t addon_heartbeat;       // increments periodically (e.g. each present or 250ms)

        std::atomic<uint32_t> seq;      // seqlock (addon writer)

        uint32_t request_epoch;         // increments on any user interaction
        uint32_t request_mask;          // bitmask: which desired_* fields are valid

        // ---- compact desired flags (exactly 4 bytes) ----
        uint8_t desired_overlay_visible; // 0/1 (only if REQ_OVERLAY)
        uint8_t desired_aa_mode;         // enum AA_* (only if REQ_AA)
        uint8_t desired_sharpen_mode;    // enum SHARPEN_* (only if REQ_SHARPEN)
        uint8_t desired_crt_enabled;     // 0/1 (only if REQ_CRT)

        // ---- desired scalars ----
        float    desired_master_volume;   // 0..1 (REQ_VOLUME)
        uint32_t desired_fps_limit;       // 0/off, 30/45/60... (REQ_FPS_LIMIT)
        float    desired_sharpen_strength;// 0..1 (REQ_SHARPEN)
        float    desired_crt_intensity;   // 0..1 (REQ_CRT)

        // AA params
        float    desired_taa_jitter;          // 0..1 (REQ_AA && mode==TAA)
        float    desired_taa_seeking;         // 0.025..0.250 (REQ_AA && mode==TAA)
        float    desired_cmaa2_edge_threshold;// 0.02..0.15 (REQ_AA && mode==CMAA2, if supported)

        uint8_t  requested_power_action;      // POWER_ACTION_*
        uint8_t  reserved[55];           // total 128 bytes
    };

    // =========================================================
    // Compile-time guarantees (layout safety)
    // =========================================================
    static_assert(sizeof(std::atomic<uint32_t>) == sizeof(uint32_t),
        "std::atomic<uint32_t> must be 4 bytes");

    static_assert(offsetof(HostToAddonState, host_pid) % alignof(uint64_t) == 0,
        "HostToAddonState.host_pid must be 8-byte aligned");
    static_assert(offsetof(HostToAddonState, host_heartbeat) % alignof(uint64_t) == 0,
        "HostToAddonState.host_heartbeat must be 8-byte aligned");
    static_assert(offsetof(HostToAddonState, seq) % alignof(uint32_t) == 0,
        "HostToAddonState.seq must be 4-byte aligned");
    static_assert(sizeof(HostToAddonState) == 128, "HostToAddonState size must be 128 bytes");

    static_assert(offsetof(AddonToHostState, addon_pid) % alignof(uint64_t) == 0,
        "AddonToHostState.addon_pid must be 8-byte aligned");
    static_assert(offsetof(AddonToHostState, addon_heartbeat) % alignof(uint64_t) == 0,
        "AddonToHostState.addon_heartbeat must be 8-byte aligned");
    static_assert(offsetof(AddonToHostState, seq) % alignof(uint32_t) == 0,
        "AddonToHostState.seq must be 4-byte aligned");
    static_assert(sizeof(AddonToHostState) == 128, "AddonToHostState size must be 128 bytes");

    static_assert(sizeof(HostToAddonState) < 4096, "HostToAddonState must fit in one memory page");
    static_assert(sizeof(AddonToHostState) < 4096, "AddonToHostState must fit in one memory page");
}
