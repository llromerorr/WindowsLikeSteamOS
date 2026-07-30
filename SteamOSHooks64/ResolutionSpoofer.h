#pragma once
#include <windows.h>
#include <atomic>
#include "OverlayOSD.h"
#include "XInputHooks.h"

namespace ResolutionSpoofer {

    // Estado global simplificado
    struct SpoofState {
        HWND     hGameWindow   = nullptr;
        std::atomic<bool> spoofEnabled = false;
    };

    inline SpoofState g_State;
    inline WNDPROC g_OriginalWndProc = nullptr;

    inline LRESULT CALLBACK HookedWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
        OverlayOSD::WndProcHandler(hWnd, msg, wParam, lParam);
        XInputHooks::WndProcHandler(hWnd, msg, wParam, lParam);

        if (OverlayOSD::WantsInputCapture(msg)) {
            return TRUE;
        }

        return CallWindowProc(g_OriginalWndProc, hWnd, msg, wParam, lParam);
    }

    inline void InstallOn(HWND hwnd) {
        if (g_OriginalWndProc) return;

        g_State.hGameWindow = hwnd;
        g_OriginalWndProc = reinterpret_cast<WNDPROC>(
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(HookedWndProc))
        );
    }

    inline void Uninstall() {
        if (g_OriginalWndProc && g_State.hGameWindow) {
            SetWindowLongPtrW(g_State.hGameWindow, GWLP_WNDPROC,
                reinterpret_cast<LONG_PTR>(g_OriginalWndProc));
            g_OriginalWndProc = nullptr;
        }
    }
}
