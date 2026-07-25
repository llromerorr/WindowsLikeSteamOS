#define NOMINMAX
#include "XInputHooks.h"
#include "Hooking.h"
#include "IPCReader.h"
#include "OverlayOSD.h"
#include "Logger.h"

#include <MinHook.h>
#include <imgui.h>
#include <backends/imgui_impl_win32.h>
#include <atomic>
#include <chrono>
#include <algorithm>

using XInputGetState_t = DWORD(WINAPI*)(DWORD dwUserIndex, XINPUT_STATE* pState);
using XInputGetStateEx_t = DWORD(WINAPI*)(DWORD dwUserIndex, XINPUT_STATE* pState);

constexpr const wchar_t* XINPUT_DLLS[] = {
    L"xinput1_4.dll",
    L"xinput1_3.dll",
    L"xinput9_1_0.dll"
};

struct ShortcutState {
    bool guidePressed  = false;
    bool aPressed      = false;
    std::chrono::steady_clock::time_point guidePressTime;
    bool shortcutActivated = false;
};

static ShortcutState g_ShortcutState;
static std::atomic<bool> g_HooksInitialized{ false };

static XInputGetState_t   oXInputGetState   = nullptr;
static XInputGetStateEx_t oXInputGetStateEx = nullptr;

// We need to define some ImGui Nav inputs because depending on the ImGui version, 
// they might be handled differently, but we'll use the classic io.NavInputs mapping.
static void UpdateImGuiGamepadState(const XINPUT_STATE& state) {
    ImGuiIO& io = ImGui::GetIO();
    
    // In ImGui 1.87+ io.NavInputs is deprecated in favor of AddKeyEvent, but we stick to the provided code for compatibility if older.
    // If it fails to compile, we might need to adjust, but assuming it matches the current imgui version:
    
    // Safety check: if NavInputs doesn't exist, this will fail compilation. We'll add a quick `#if` if needed, 
    // but the prompt gave us this exact code, we will trust it.
    
#if defined(IMGUI_VERSION_NUM) && IMGUI_VERSION_NUM >= 18700
    // ImGui 1.87+ AddKeyEvent API
    io.AddKeyEvent(ImGuiKey_GamepadFaceDown, (state.Gamepad.wButtons & XINPUT_GAMEPAD_A) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadFaceRight, (state.Gamepad.wButtons & XINPUT_GAMEPAD_B) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadFaceLeft, (state.Gamepad.wButtons & XINPUT_GAMEPAD_X) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadFaceUp, (state.Gamepad.wButtons & XINPUT_GAMEPAD_Y) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadDpadLeft, (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_LEFT) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadDpadRight, (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_RIGHT) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadDpadUp, (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_UP) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadDpadDown, (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_DOWN) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadL1, (state.Gamepad.wButtons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadR1, (state.Gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadL3, (state.Gamepad.wButtons & XINPUT_GAMEPAD_LEFT_THUMB) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadR3, (state.Gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadStart, (state.Gamepad.wButtons & XINPUT_GAMEPAD_START) != 0);
    io.AddKeyEvent(ImGuiKey_GamepadBack, (state.Gamepad.wButtons & XINPUT_GAMEPAD_BACK) != 0);

    float lStickX = state.Gamepad.sThumbLX / 32767.0f;
    float lStickY = state.Gamepad.sThumbLY / 32767.0f;
    io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickLeft, lStickX < -0.3f, std::max(0.0f, -lStickX));
    io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickRight, lStickX > 0.3f, std::max(0.0f, lStickX));
    io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickUp, lStickY > 0.3f, std::max(0.0f, lStickY));
    io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickDown, lStickY < -0.3f, std::max(0.0f, -lStickY));
    
    io.AddKeyAnalogEvent(ImGuiKey_GamepadL2, state.Gamepad.bLeftTrigger > 30, state.Gamepad.bLeftTrigger / 255.0f);
    io.AddKeyAnalogEvent(ImGuiKey_GamepadR2, state.Gamepad.bRightTrigger > 30, state.Gamepad.bRightTrigger / 255.0f);
#else
    io.NavInputs[ImGuiNavInput_Activate]    = (state.Gamepad.wButtons & XINPUT_GAMEPAD_A) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_Cancel]      = (state.Gamepad.wButtons & XINPUT_GAMEPAD_B) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_Menu]        = (state.Gamepad.wButtons & XINPUT_GAMEPAD_START) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_Input]       = (state.Gamepad.wButtons & XINPUT_GAMEPAD_BACK) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_DpadLeft]    = (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_LEFT) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_DpadRight]   = (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_RIGHT) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_DpadUp]      = (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_UP) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_DpadDown]    = (state.Gamepad.wButtons & XINPUT_GAMEPAD_DPAD_DOWN) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_FocusPrev]   = (state.Gamepad.wButtons & XINPUT_GAMEPAD_LEFT_SHOULDER) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_FocusNext]   = (state.Gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_SHOULDER) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_TweakSlow]   = (state.Gamepad.wButtons & XINPUT_GAMEPAD_LEFT_THUMB) ? 1.0f : 0.0f;
    io.NavInputs[ImGuiNavInput_TweakFast]   = (state.Gamepad.wButtons & XINPUT_GAMEPAD_RIGHT_THUMB) ? 1.0f : 0.0f;

    io.NavInputs[ImGuiNavInput_LStickLeft]  = -std::max(0.0f, state.Gamepad.sThumbLX / 32767.0f);
    io.NavInputs[ImGuiNavInput_LStickRight] =  std::max(0.0f, state.Gamepad.sThumbLX / 32767.0f);
    io.NavInputs[ImGuiNavInput_LStickUp]    =  std::max(0.0f, state.Gamepad.sThumbLY / 32767.0f);
    io.NavInputs[ImGuiNavInput_LStickDown]  = -std::max(0.0f, state.Gamepad.sThumbLY / 32767.0f);

    io.NavInputs[ImGuiNavInput_L2] = state.Gamepad.bLeftTrigger / 255.0f;
    io.NavInputs[ImGuiNavInput_R2] = state.Gamepad.bRightTrigger / 255.0f;
#endif
    
    // Forzar flag de backend
    io.BackendFlags |= ImGuiBackendFlags_HasGamepad;
}

static const XINPUT_STATE EMPTY_STATE = { 0 };

static DWORD WINAPI hkXInputGetState(DWORD dwUserIndex, XINPUT_STATE* pState) {
    if (!pState) return ERROR_BAD_ARGUMENTS;

    DWORD result = oXInputGetState(dwUserIndex, pState);
    if (result != ERROR_SUCCESS) return result;

    bool guidePressed = (pState->Gamepad.wButtons & 0x0400) != 0; // XINPUT_GAMEPAD_GUIDE (undocumented)
    bool aPressed     = (pState->Gamepad.wButtons & XINPUT_GAMEPAD_A) != 0;

    if (guidePressed && aPressed) {
        if (!g_ShortcutState.guidePressed || !g_ShortcutState.aPressed) {
            g_ShortcutState.guidePressed = true;
            g_ShortcutState.aPressed     = true;
            g_ShortcutState.guidePressTime = std::chrono::steady_clock::now();
            g_ShortcutState.shortcutActivated = false;
        } else {
            auto now = std::chrono::steady_clock::now();
            auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                now - g_ShortcutState.guidePressTime).count();

            if (elapsed >= 1000 && !g_ShortcutState.shortcutActivated) {
                EffectParams params;
                IPCReader::ReadParams(params);
                params.showOverlay = params.showOverlay == 0 ? 1 : 0;
                IPCReader::WriteParams(params);
                g_ShortcutState.shortcutActivated = true;
                Logger::Log("[XInput] Atajo Guide+A detectado -> Toggle Overlay");
            }
        }
    } else {
        g_ShortcutState.guidePressed = false;
        g_ShortcutState.aPressed     = false;
    }

    if (OverlayOSD::IsVisible()) {
        UpdateImGuiGamepadState(*pState);
        *pState = EMPTY_STATE;
        return ERROR_SUCCESS;
    }

    return ERROR_SUCCESS;
}

static DWORD WINAPI hkXInputGetStateEx(DWORD dwUserIndex, XINPUT_STATE* pState) {
    return hkXInputGetState(dwUserIndex, pState);
}

namespace XInputHooks {

    bool Initialize() {
        if (g_HooksInitialized) return true;

        for (const wchar_t* dllName : XINPUT_DLLS) {
            HMODULE hModule = GetModuleHandleW(dllName);
            if (!hModule) continue;

            FARPROC pXInputGetState = GetProcAddress(hModule, "XInputGetState");
            if (!pXInputGetState) continue;

            if (Hooking::CreateHookApi(dllName, "XInputGetState", &hkXInputGetState,
                &oXInputGetState)) {
                Logger::Log("[XInput] Hookeado XInputGetState en %ls", dllName);
            }

            if (wcscmp(dllName, L"xinput1_4.dll") == 0) {
                FARPROC pXInputGetStateEx = (FARPROC)GetProcAddress(hModule, (LPCSTR)100); // 100 is GetStateEx
                if (!pXInputGetStateEx) pXInputGetStateEx = GetProcAddress(hModule, "XInputGetStateEx");
                
                if (pXInputGetStateEx) {
                    if (Hooking::CreateHookApi(dllName, "XInputGetStateEx", &hkXInputGetStateEx,
                        &oXInputGetStateEx)) {
                        Logger::Log("[XInput] Hookeado XInputGetStateEx en xinput1_4.dll");
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
        // ImGui_ImplWin32_WndProcHandler handles WM_DEVICECHANGE naturally.
    }
}
