#pragma once
#include <windows.h>
#include <atomic>
#include "OverlayOSD.h"
#include "XInputHooks.h"

namespace ResolutionSpoofer {

    // Estado global compartido — se setea desde tu app C# vía named pipe/shared memory
    // apenas la DLL se inyecta, ANTES de que el juego cree su swapchain.
    struct SpoofState {
        HWND     hGameWindow   = nullptr;
        LONG     fakeWidth     = 800;   // Resolución que el motor DEBE creer que tiene
        LONG     fakeHeight    = 600;
        LONG     realWidth     = 1920;  // Resolución física real del HWND (borderless)
        LONG     realHeight    = 1080;
        std::atomic<bool> spoofEnabled = false;
    };

    inline SpoofState g_State;

    // Puntero al WndProc original del juego (subclassing)
    inline WNDPROC g_OriginalWndProc = nullptr;

    // ---- Nuestro WndProc interceptor ----
    inline LRESULT CALLBACK HookedWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {

    OverlayOSD::WndProcHandler(hWnd, msg, wParam, lParam);
    XInputHooks::WndProcHandler(hWnd, msg, wParam, lParam);

    if (OverlayOSD::WantsInputCapture(msg)) {
        return TRUE;
    }

    if (g_State.spoofEnabled.load() && hWnd == g_State.hGameWindow) {

            switch (msg) {
            
            case WM_WINDOWPOSCHANGING: {
                WINDOWPOS* pos = reinterpret_cast<WINDOWPOS*>(lParam);
                
                // 1. Modificar el original para que Windows aplique pantalla completa real
                if (!(pos->flags & SWP_NOMOVE)) {
                    pos->x = 0;
                    pos->y = 0;
                }
                if (!(pos->flags & SWP_NOSIZE)) {
                    pos->cx = g_State.realWidth;
                    pos->cy = g_State.realHeight;
                }

                // 2. Crear una copia con la resolución fake para el motor del juego
                WINDOWPOS posCopy = *pos;
                if (!(posCopy.flags & SWP_NOMOVE)) {
                    posCopy.x = 0;
                    posCopy.y = 0;
                }
                if (!(posCopy.flags & SWP_NOSIZE)) {
                    posCopy.cx = g_State.fakeWidth;
                    posCopy.cy = g_State.fakeHeight;
                }
                
                // Retornar la llamada al WndProc original PERO con la copia fake
                return CallWindowProc(g_OriginalWndProc, hWnd, msg, wParam, reinterpret_cast<LPARAM>(&posCopy));
            }

            case WM_WINDOWPOSCHANGED: {
                WINDOWPOS posCopy = *reinterpret_cast<WINDOWPOS*>(lParam);
                if (!(posCopy.flags & SWP_NOMOVE)) {
                    posCopy.x = 0;
                    posCopy.y = 0;
                }
                if (!(posCopy.flags & SWP_NOSIZE)) {
                    posCopy.cx = g_State.fakeWidth;
                    posCopy.cy = g_State.fakeHeight;
                }
                return CallWindowProc(g_OriginalWndProc, hWnd, msg, wParam, reinterpret_cast<LPARAM>(&posCopy));
            }

            case WM_SIZE: {
                lParam = MAKELPARAM(g_State.fakeWidth, g_State.fakeHeight);
                break;
            }

            case WM_STYLECHANGING: {
                if (wParam == GWL_STYLE) {
                    STYLESTRUCT* ss = reinterpret_cast<STYLESTRUCT*>(lParam);
                    ss->styleNew = WS_POPUP | WS_VISIBLE;
                    return 0;
                }
                break;
            }

            // Evita que el juego intente re-centrar el cursor basado en
            // coordenadas de una resolución que ya no coincide con la física.
            case WM_ACTIVATE:
            case WM_SETFOCUS:
                break;

            default:
                break;
            }
        }

        return CallWindowProc(g_OriginalWndProc, hWnd, msg, wParam, lParam);
    }

    // Instala el subclassing sobre la ventana del juego.
    // Se debe llamar una única vez, apenas se detecta la HWND del juego
    // (normalmente en el primer CreateDeviceAndSwapChain exitoso).
    inline void InstallOn(HWND hwnd) {
        if (g_OriginalWndProc) return; // ya instalado

        DEVMODEW devMode = {};
        devMode.dmSize = sizeof(DEVMODEW);
        if (EnumDisplaySettingsW(NULL, ENUM_CURRENT_SETTINGS, &devMode)) {
            g_State.realWidth = devMode.dmPelsWidth;
            g_State.realHeight = devMode.dmPelsHeight;
        }

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
