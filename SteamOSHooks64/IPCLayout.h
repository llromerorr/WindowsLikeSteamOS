#pragma once
#include <cstdint>

#pragma pack(push, 1)

constexpr uint32_t IPC_MAGIC          = 0x53544D53; // 'STMS' = SteamOS
constexpr uint32_t IPC_LAYOUT_VERSION = 2;

struct EffectParams {
    uint32_t enablePostProcess;
    uint32_t enableCRT;
    float    curvature;
    float    scanlineIntensity;
    uint32_t enableFSR;
    float    fsrSharpness;
    uint32_t enableResolutionSpoof;
    uint32_t fakeWidth;
    uint32_t fakeHeight;
    uint32_t showOverlay;
    uint32_t reserved0; // Handle Low
    uint32_t reserved1; // Handle High
    uint32_t reserved2; // Texture Width
    uint32_t reserved3; // Texture Height
    uint32_t reserved4; // Adapter LUID Low
    uint32_t reserved5; // Adapter LUID High
    uint32_t isNtHandle;
    uint8_t  reserved[32];
};

struct IPCSharedBlock {
    uint32_t     magic;
    uint32_t     layoutVersion;
    uint32_t     sequence;
    uint32_t     writerLock;
    EffectParams params;
    uint32_t     detectedBackend;
    uint32_t     framesRendered;
    float        lastFrameMs;
    uint32_t     _reservedTelemetry[13];
};

#pragma pack(pop)

static_assert(sizeof(IPCSharedBlock) == 180, "IPCSharedBlock size mismatch! Must be exactly 180 bytes.");

constexpr const wchar_t* IPC_MMF_NAME = L"Local\\SteamOSHooks_IPC_v2";
constexpr size_t         IPC_MMF_SIZE = 4096;
