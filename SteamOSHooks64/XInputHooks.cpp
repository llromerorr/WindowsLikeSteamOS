#define NOMINMAX
#include "XInputHooks.h"
#include "Hooking.h"
#include "Logger.h"

#include <MinHook.h>
#include <atomic>
#include <chrono>
#include <algorithm>
#include <intrin.h>
#include <psapi.h>

using XInputGetState_t = DWORD(WINAPI*)(DWORD dwUserIndex, XINPUT_STATE* pState);
using XInputGetStateEx_t = DWORD(WINAPI*)(DWORD dwUserIndex, XINPUT_STATE* pState);

constexpr const wchar_t* XINPUT_DLLS[] = {
    L"xinput1_4.dll",
    L"xinput1_3.dll",
    L"xinput9_1_0.dll"
};

static std::atomic<bool> g_HooksInitialized{ false };
static std::atomic<bool> g_overlay_active{ false };
static std::atomic<int>  g_cooldown_frames{ 0 };
static bool              g_last_overlay_state = false;

// Captura thread-safe del estado físico de XInput (sin invocar ImGuiIO desde hilos arbitrarios)
static XINPUT_STATE          g_captured_state = { 0 };
static std::atomic<uint32_t> g_captured_seq{ 0 };

static XInputGetState_t   oXInputGetState   = nullptr;
static XInputGetStateEx_t oXInputGetStateEx = nullptr;

static const XINPUT_STATE EMPTY_STATE = { 0 };

static DWORD WINAPI hkXInputGetState(DWORD dwUserIndex, XINPUT_STATE* pState) {
    if (!pState) return ERROR_BAD_ARGUMENTS;

    // 1. SIEMPRE llamar a la función ORIGINAL primero para leer el hardware real
    DWORD result = ERROR_DEVICE_NOT_CONNECTED;
    if (oXInputGetState) {
        result = oXInputGetState(dwUserIndex, pState);
    }
    if (result != ERROR_SUCCESS) return result;

    // 2. Almacenar el estado real de forma thread-safe con seqlock acq_rel/release
    g_captured_seq.fetch_add(1, std::memory_order_acq_rel); // Impar (writer activo)
    g_captured_state = *pState;
    g_captured_seq.fetch_add(1, std::memory_order_release); // Par (escritura publicada)

    // 3. Verificar si el Overlay está activo o en frames de transición/desaparición
    bool overlayActive = g_overlay_active.load(std::memory_order_relaxed);
    int cooldown = g_cooldown_frames.load(std::memory_order_relaxed);

    if (overlayActive || cooldown > 0) {
        if (cooldown > 0) {
            g_cooldown_frames.fetch_sub(1, std::memory_order_relaxed);
        }

        // Bypassing para ReShade: si la llamada proviene de ReShade (dxgi.dll, d3d11.dll, etc.)
        // permitimos que lea el input real para navegar por ImGui nativo.
        PVOID caller = _ReturnAddress();
        bool isReShade = false;
        
        // Cachear los rangos de memoria de los modulos conocidos de ReShade de forma lock-free
        static uintptr_t reshade_bases[4] = {0};
        static uint32_t reshade_sizes[4] = {0};
        static std::atomic<bool> reshade_cached{false};
        
        if (!reshade_cached.load(std::memory_order_relaxed)) {
            const wchar_t* modules[] = { L"dxgi.dll", L"d3d11.dll", L"d3d12.dll", L"dinput8.dll" };
            for (int i = 0; i < 4; ++i) {
                HMODULE hMod = GetModuleHandleW(modules[i]);
                if (hMod) {
                    MODULEINFO info = {0};
                    GetModuleInformation(GetCurrentProcess(), hMod, &info, sizeof(info));
                    reshade_bases[i] = (uintptr_t)info.lpBaseOfDll;
                    reshade_sizes[i] = info.SizeOfImage;
                }
            }
            reshade_cached.store(true, std::memory_order_relaxed);
        }
        
        uintptr_t caller_addr = (uintptr_t)caller;
        for (int i = 0; i < 4; ++i) {
            if (reshade_bases[i] != 0 && caller_addr >= reshade_bases[i] && caller_addr < (reshade_bases[i] + reshade_sizes[i])) {
                isReShade = true;
                break;
            }
        }

        if (isReShade) {
            return ERROR_SUCCESS; // Devolver el *pState real a ReShade!
        }

        // Neutralizar el estado entregado al juego (personaje inmóvil)
        *pState = EMPTY_STATE;
        return ERROR_SUCCESS;
    }

    // 4. PASSTHROUGH REAL: Si overlay está OFF y no hay cooldown, el juego recibe *pState real intacto
    return ERROR_SUCCESS;
}

static DWORD WINAPI hkXInputGetStateEx(DWORD dwUserIndex, XINPUT_STATE* pState) {
    return hkXInputGetState(dwUserIndex, pState);
}

namespace XInputHooks {

    void SetOverlayActive(bool active) {
        if (active != g_last_overlay_state) {
            g_last_overlay_state = active;
            g_cooldown_frames.store(10, std::memory_order_relaxed); // 10 frames de cooldown en la transición
        }
        g_overlay_active.store(active, std::memory_order_relaxed);
    }

    bool GetCapturedState(XINPUT_STATE& outState) {
        for (int attempt = 0; attempt < 5; ++attempt) {
            uint32_t s1 = g_captured_seq.load(std::memory_order_acquire);
            if (s1 & 1) continue;
            XINPUT_STATE copy = g_captured_state;
            uint32_t s2 = g_captured_seq.load(std::memory_order_acquire);
            if (s1 == s2) {
                outState = copy;
                return true;
            }
        }
        outState = g_captured_state;
        return true;
    }

    bool Initialize() {
        if (g_HooksInitialized) return true;

        Hooking::Initialize();

        for (const wchar_t* dllName : XINPUT_DLLS) {
            HMODULE hModule = GetModuleHandleW(dllName);
            if (!hModule) {
                hModule = LoadLibraryW(dllName);
            }
            if (!hModule) continue;

            FARPROC pXInputGetState = GetProcAddress(hModule, "XInputGetState");
            if (pXInputGetState && !oXInputGetState) {
                if (Hooking::CreateHookApi(dllName, "XInputGetState", &hkXInputGetState, &oXInputGetState)) {
                    Logger::Log("[XInput] Hookeado XInputGetState en %ls", dllName);
                }
            }

            if (wcscmp(dllName, L"xinput1_4.dll") == 0 || wcscmp(dllName, L"xinput1_3.dll") == 0) {
                FARPROC pXInputGetStateEx = (FARPROC)GetProcAddress(hModule, (LPCSTR)100); // 100 is GetStateEx ordinal
                if (!pXInputGetStateEx) pXInputGetStateEx = GetProcAddress(hModule, "XInputGetStateEx");
                
                if (pXInputGetStateEx && !oXInputGetStateEx) {
                    if (Hooking::CreateHookApi(dllName, "XInputGetStateEx", &hkXInputGetStateEx, &oXInputGetStateEx)) {
                        Logger::Log("[XInput] Hookeado XInputGetStateEx en %ls", dllName);
                    }
                }
            }
        }

        if (!oXInputGetState && !oXInputGetStateEx) {
            Logger::Log("[XInput] WARNING: No se pudo hookear ninguna variante de XInput.");
            return false;
        }

        g_HooksInitialized = true;
        Logger::Log("[XInput] Módulo inicializado correctamente.");
        return true;
    }

    void Shutdown() {
        if (!g_HooksInitialized) return;
        oXInputGetState   = nullptr;
        oXInputGetStateEx = nullptr;
        g_HooksInitialized = false;
    }

    void WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    }
}
