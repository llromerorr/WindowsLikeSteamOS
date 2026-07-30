#include <windows.h>
#include "Hooking.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"

using ClipCursor_t = BOOL(WINAPI*)(const RECT*);
ClipCursor_t oClipCursor = nullptr;

BOOL WINAPI hkClipCursor(const RECT* lpRect) {
    if (ResolutionSpoofer::g_State.spoofEnabled.load()) {
        // Ignoramos el clip para que el ratón físico pueda moverse por el monitor completo
        // y llegar a la ventana WPF superpuesta
        return TRUE;
    }
    return oClipCursor(lpRect);
}

using SetCursorPos_t = BOOL(WINAPI*)(int, int);
SetCursorPos_t oSetCursorPos = nullptr;

BOOL WINAPI hkSetCursorPos(int X, int Y) {
    if (ResolutionSpoofer::g_State.spoofEnabled.load()) {
        // Evitamos que el juego intente recentrar el cursor físico
        return TRUE;
    }
    return oSetCursorPos(X, Y);
}

bool InitWin32Hooks() {
    bool ok = true;
    ok &= Hooking::CreateHookApi(L"user32.dll", "ClipCursor",
        (void*)&hkClipCursor, (void**)&oClipCursor);
    ok &= Hooking::CreateHookApi(L"user32.dll", "SetCursorPos",
        (void*)&hkSetCursorPos, (void**)&oSetCursorPos);
    return ok;
}
