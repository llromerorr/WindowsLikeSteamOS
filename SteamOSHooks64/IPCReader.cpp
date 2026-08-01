#include "IPCReader.h"
#include "Logger.h"
#include <atomic>

namespace IPCReader {

    HANDLE          g_hMapping = nullptr;
    IPCSharedBlock* g_pBlock   = nullptr;

    EffectParams g_LastValidParams = {};
    bool         g_HasValidCache   = false;

    static void SetSafeDefaults(EffectParams& p) {
        memset(&p, 0, sizeof(p));
        p.enablePostProcess = 0;
        p.curvature         = 0.0f;
        p.scanlineIntensity = 0.0f;
        p.fsrSharpness      = 0.5f;
        p.fakeWidth         = 1920;
        p.fakeHeight        = 1080;
    }

    bool IsConnected() {
        return g_pBlock != nullptr;
    }

    bool Initialize() {
        if (g_pBlock) return true;

        g_hMapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, IPC_MMF_NAME);
        if (!g_hMapping) {
            return false;
        }

        g_pBlock = reinterpret_cast<IPCSharedBlock*>(
            MapViewOfFile(g_hMapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, IPC_MMF_SIZE));

        if (!g_pBlock) {
            CloseHandle(g_hMapping);
            g_hMapping = nullptr;
            Logger::Log("[IPC] MapViewOfFile fallo: %lu", GetLastError());
            return false;
        }

        if (g_pBlock->magic != IPC_MAGIC) {
            Logger::Log("[IPC] Magic invalido (0x%08X != 0x%08X). Panel incompatible.",
                g_pBlock->magic, IPC_MAGIC);
            Shutdown();
            return false;
        }
        if (g_pBlock->layoutVersion != IPC_LAYOUT_VERSION) {
            Logger::Log("[IPC] Version de layout %u != %u. Ignorando IPC.",
                g_pBlock->layoutVersion, IPC_LAYOUT_VERSION);
            Shutdown();
            return false;
        }

        SetSafeDefaults(g_LastValidParams);
        Logger::Log("[IPC] Memoria compartida vinculada correctamente.");
        return true;
    }

    void Shutdown() {
        if (g_pBlock)   { UnmapViewOfFile(g_pBlock); g_pBlock = nullptr; }
        if (g_hMapping) { CloseHandle(g_hMapping);   g_hMapping = nullptr; }
        g_HasValidCache = false;
    }

    bool ReadParams(EffectParams& out) {
        if (!g_pBlock) {
            if (!Initialize()) {
                SetSafeDefaults(out);
                return false;
            }
        }

        auto* pSeq = reinterpret_cast<std::atomic<uint32_t>*>(&g_pBlock->sequence);

        for (int attempt = 0; attempt < 4; ++attempt) {
            uint32_t seq1 = pSeq->load(std::memory_order_acquire);

            if (seq1 & 1u) {
                YieldProcessor();
                continue;
            }

            EffectParams temp = g_pBlock->params;

            std::atomic_thread_fence(std::memory_order_acquire);
            uint32_t seq2 = pSeq->load(std::memory_order_acquire);

            if (seq1 == seq2) {
                out = temp;
                g_LastValidParams = temp;
                g_HasValidCache = true;
                return true;
            }
        }

        if (g_HasValidCache) {
            out = g_LastValidParams;
        } else {
            SetSafeDefaults(out);
        }
        return false;
    }

    void WriteTelemetry(uint32_t backend, uint32_t framesRendered, float lastFrameMs) {
        if (!g_pBlock) return;
        g_pBlock->detectedBackend = backend;
        g_pBlock->framesRendered  = framesRendered;
        g_pBlock->lastFrameMs     = lastFrameMs;
    }

    bool WriteParams(const EffectParams& in) {
        if (!g_pBlock) {
            if (!Initialize()) return false;
        }

        auto* pLock = reinterpret_cast<std::atomic<uint32_t>*>(&g_pBlock->writerLock);
        auto* pSeq  = reinterpret_cast<std::atomic<uint32_t>*>(&g_pBlock->sequence);

        uint32_t expected = 0;
        int spins = 0;
        while (!pLock->compare_exchange_weak(expected, 1, std::memory_order_acquire)) {
            expected = 0;
            if (++spins > 10000) {
                Logger::Log("[IPC] WARNING: writerLock forzado tras timeout.");
                pLock->store(0, std::memory_order_release);
                expected = 0;
            }
            YieldProcessor();
        }

        uint32_t seq = pSeq->load(std::memory_order_relaxed);
        pSeq->store(seq + 1, std::memory_order_release);
        std::atomic_thread_fence(std::memory_order_release);

        g_pBlock->params = in;

        std::atomic_thread_fence(std::memory_order_release);
        pSeq->store(seq + 2, std::memory_order_release);

        pLock->store(0, std::memory_order_release);

        g_LastValidParams = in;
        return true;
    }

    bool WriteSharedHandle(HANDLE handle, uint32_t width, uint32_t height, LUID adapterLuid, bool isNtHandle) {
        if (!g_pBlock) return false;
        EffectParams p;
        if (ReadParams(p)) {
            uint64_t hVal = reinterpret_cast<uint64_t>(handle);
            p.reserved0 = static_cast<uint32_t>(hVal & 0xFFFFFFFF);
            p.reserved1 = static_cast<uint32_t>((hVal >> 32) & 0xFFFFFFFF);
            p.reserved2 = width;
            p.reserved3 = height;
            p.reserved4 = adapterLuid.LowPart;
            p.reserved5 = static_cast<uint32_t>(adapterLuid.HighPart);
            p.isNtHandle = isNtHandle ? 1 : 0;
            return WriteParams(p);
        }
        return false;
    }
}
