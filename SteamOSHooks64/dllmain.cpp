#include <windows.h>
#include "Hooking.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"
#include "D3D12Hooks.h"
#include "ShaderPipeline.h"
#include "IPCReader.h"
#include "OverlayOSD.h"
#include "XInputHooks.h"

// Forward declarations de los módulos
bool InitWin32Hooks();

HMODULE g_hModule = nullptr;
HANDLE  g_hMainThread = nullptr;

DWORD WINAPI InitializeThread(LPVOID lpParam) {

    Logger::Log("=== SteamOSHooks64.dll inyectada. Iniciando... ===");

    if (!Hooking::Initialize()) {
        Logger::Log("FATAL: No se pudo inicializar MinHook.");
        return 1;
    }

    bool dx12Ok = D3D12Hooks::Initialize();
    bool win32Ok = InitWin32Hooks();

    IPCReader::Initialize();
    bool xinputOk = XInputHooks::Initialize();

    Logger::Log("XInput: %s | D3D12: %s | Win32: %s",
        xinputOk ? "OK" : "N/A",
        dx12Ok ? "OK" : "N/A",
        win32Ok ? "OK" : "FALLO");

    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reasonForCall, LPVOID lpReserved) {
    switch (reasonForCall) {
    case DLL_PROCESS_ATTACH: {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
        g_hMainThread = CreateThread(nullptr, 0, InitializeThread, nullptr, 0, nullptr);
        break;
    }

    case DLL_PROCESS_DETACH: {
        if (lpReserved == nullptr) {
            Logger::Log("=== Descargando SteamOSHooks64.dll (FreeLibrary) ===");
            XInputHooks::Shutdown();
            OverlayOSD::DX11::Shutdown();
            OverlayOSD::DX12::Shutdown();
            OverlayOSD::ShutdownCommon();
            IPCReader::Shutdown();
            ShaderPipelineDX11::Shutdown();
            ShaderPipelineDX12::Shutdown();
            ResolutionSpoofer::Uninstall();
            Hooking::Shutdown();
        } else {
            Logger::Log("=== Proceso en terminación (lpReserved != null). Omitiendo desinicialización defensiva. ===");
        }

        if (g_hMainThread) {
            CloseHandle(g_hMainThread);
            g_hMainThread = nullptr;
        }
        break;
    }
    }
    return TRUE;
}
