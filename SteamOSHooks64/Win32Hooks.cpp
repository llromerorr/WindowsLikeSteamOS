#include <windows.h>
#include "Hooking.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"

using ChangeDisplaySettingsExW_t = LONG(WINAPI*)(LPCWSTR, DEVMODEW*, HWND, DWORD, LPVOID);
ChangeDisplaySettingsExW_t oChangeDisplaySettingsExW = nullptr;

LONG WINAPI hkChangeDisplaySettingsExW(LPCWSTR lpszDeviceName, DEVMODEW* lpDevMode,
    HWND hwnd, DWORD dwflags, LPVOID lParam) {

    if (ResolutionSpoofer::g_State.spoofEnabled.load()) {
        Logger::Log("[Hook] ChangeDisplaySettingsExW interceptado -> Bloqueado.");
        return DISP_CHANGE_SUCCESSFUL;
    }
    
    return oChangeDisplaySettingsExW(lpszDeviceName, lpDevMode, hwnd, dwflags, lParam);
}

using GetClientRect_t = BOOL(WINAPI*)(HWND, LPRECT);
GetClientRect_t oGetClientRect = nullptr;

BOOL WINAPI hkGetClientRect(HWND hWnd, LPRECT lpRect) {
    BOOL result = oGetClientRect(hWnd, lpRect);

    using namespace ResolutionSpoofer;
    if (result && g_State.spoofEnabled.load() && hWnd == g_State.hGameWindow) {
        lpRect->left   = 0;
        lpRect->top    = 0;
        lpRect->right  = g_State.fakeWidth;
        lpRect->bottom = g_State.fakeHeight;
    }
    return result;
}

using GetWindowRect_t = BOOL(WINAPI*)(HWND, LPRECT);
GetWindowRect_t oGetWindowRect = nullptr;

BOOL WINAPI hkGetWindowRect(HWND hWnd, LPRECT lpRect) {
    BOOL result = oGetWindowRect(hWnd, lpRect);

    using namespace ResolutionSpoofer;
    if (result && g_State.spoofEnabled.load() && hWnd == g_State.hGameWindow) {
        lpRect->left   = 0;
        lpRect->top    = 0;
        lpRect->right  = g_State.fakeWidth;
        lpRect->bottom = g_State.fakeHeight;
    }
    return result;
}

bool InitWin32Hooks() {
    bool ok = true;
    ok &= Hooking::CreateHookApi(L"user32.dll", "ChangeDisplaySettingsExW",
        &hkChangeDisplaySettingsExW, &oChangeDisplaySettingsExW);
    // Nota: El de GetClientRect se inicializa en dllmain.cpp según la arquitectura de la IA
    return ok;
}
