#pragma once
#include "IPCLayout.h"
#include <windows.h>

namespace IPCReader {
    bool Initialize();
    void Shutdown();
    bool ReadParams(EffectParams& out);
    bool WriteParams(const EffectParams& in);
    void WriteTelemetry(uint32_t backend, uint32_t framesRendered, float lastFrameMs);
}
