#pragma once
#include "IPCLayout.h"
#include <windows.h>

namespace IPCReader {
    bool IsConnected();
    bool Initialize();
    void Shutdown();
    bool ReadParams(EffectParams& out);
    bool WriteParams(const EffectParams& in);
    void WriteTelemetry(uint32_t backend, uint32_t framesRendered, float lastFrameMs);
    bool WriteSharedHandle(HANDLE handle, uint32_t width, uint32_t height, LUID adapterLuid, bool isNtHandle);
}
