#pragma once
#include <windows.h>
#include <xinput.h>

namespace XInputHooks {
    bool Initialize();
    void Shutdown();
    void SetOverlayActive(bool active);
    bool GetCapturedState(XINPUT_STATE& outState);
    void WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
}
