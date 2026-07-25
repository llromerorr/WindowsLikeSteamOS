#pragma once
#include <windows.h>
#include <xinput.h>

namespace XInputHooks {
    bool Initialize();
    void Shutdown();
    void WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
}
