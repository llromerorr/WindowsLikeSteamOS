#pragma once
#include <MinHook.h>
#include "Logger.h"

// Wrapper que centraliza la creación/activación de hooks para evitar
// fugas de estado y facilitar el cleanup en DLL_PROCESS_DETACH.
namespace Hooking {

    inline bool Initialize() {
        MH_STATUS status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
            Logger::Log("MH_Initialize failed: %d", status);
            return false;
        }
        return true;
    }

    inline void Shutdown() {
        // Intencionalmente NO llamamos a MH_DisableHook ni MH_Uninitialize aquí.
        // Dado que somos un proxy dxgi.dll que vive todo el ciclo de vida del juego,
        // intentar desinstalar hooks expone a:
        // 1. Romper la cadena de trampolines de RTSS (si se inyectó después de nosotros).
        // 2. Use-After-Free (UAF) si el hilo de render está dentro de oPresent() al uninitialize.
        Logger::Log("Hooking::Shutdown() -> Omitiendo desinstalación de hooks por seguridad (evita UAF y chain breaks).");
    }

    // Hook genérico por dirección (usado para vtable de DXGI)
    template <typename T>
    inline bool CreateHook(void* target, void* detour, T** original) {
        MH_STATUS s1 = MH_CreateHook(target, detour, reinterpret_cast<void**>(original));
        if (s1 != MH_OK) {
            Logger::Log("MH_CreateHook failed at %p: %d", target, s1);
            return false;
        }
        MH_STATUS s2 = MH_EnableHook(target);
        if (s2 != MH_OK) {
            Logger::Log("MH_EnableHook failed at %p: %d", target, s2);
            return false;
        }
        return true;
    }

    // Hook por nombre de módulo/función (IAT-style, usado para user32.dll)
    template <typename T>
    inline bool CreateHookApi(const wchar_t* module, const char* proc, void* detour, T** original) {
        MH_STATUS s1 = MH_CreateHookApi(module, proc, detour, reinterpret_cast<void**>(original));
        if (s1 != MH_OK) {
            Logger::Log("MH_CreateHookApi failed (%s): %d", proc, s1);
            return false;
        }
        MH_STATUS s2 = MH_EnableHook(MH_ALL_HOOKS);
        return s2 == MH_OK || s2 == MH_ERROR_ENABLED;
    }
}
