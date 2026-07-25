#include "OverlayOSD.h"
#include "IPCReader.h"
#include "Logger.h"

#include <imgui.h>
#include <backends/imgui_impl_win32.h>
#include <atomic>
#include <cstring>

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(
    HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace OverlayOSD {

    bool g_CommonInitialized = false;
    std::atomic<bool> g_Visible{ false };
    bool g_LocalToggle = false;
    char g_BackendName[32] = "Detectando...";

    bool InitializeCommon(HWND hwnd) {
        if (g_CommonInitialized) return true;

        IMGUI_CHECKVERSION();
        ImGui::CreateContext();

        ImGuiIO& io = ImGui::GetIO();
        io.IniFilename = nullptr;

        ImGui::StyleColorsDark();
        ImGuiStyle& style = ImGui::GetStyle();
        style.WindowRounding = 8.0f;
        style.FrameRounding  = 4.0f;

        if (!ImGui_ImplWin32_Init(hwnd)) {
            Logger::Log("[OverlayOSD] Fallo ImGui_ImplWin32_Init");
            ImGui::DestroyContext();
            return false;
        }

        g_CommonInitialized = true;
        Logger::Log("[OverlayOSD] Contexto comun inicializado (HWND=%p)", hwnd);
        return true;
    }

    void ShutdownCommon() {
        if (!g_CommonInitialized) return;
        ImGui_ImplWin32_Shutdown();
        ImGui::DestroyContext();
        g_CommonInitialized = false;
    }

    void SetBackendName(const char* name) {
        strncpy_s(g_BackendName, name, _TRUNCATE);
    }

    bool IsVisible() {
        return g_Visible.load(std::memory_order_relaxed);
    }

    void WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
        if (!g_CommonInitialized) return;

        ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam);

        if (msg == WM_KEYDOWN && wParam == VK_INSERT) {
            bool isAutoRepeat = (lParam & (1 << 30)) != 0;
            if (!isAutoRepeat) {
                g_LocalToggle = !g_LocalToggle;
                Logger::Log("[OverlayOSD] Toggle local (INSERT) -> %d", g_LocalToggle);
            }
        }
    }

    bool WantsInputCapture(UINT msg) {
        if (!IsVisible()) return false;

        ImGuiIO& io = ImGui::GetIO();
        bool isMouseMsg = (msg >= WM_MOUSEFIRST && msg <= WM_MOUSELAST);
        bool isKeyMsg    = (msg >= WM_KEYFIRST && msg <= WM_KEYLAST) || msg == WM_CHAR;

        if (isMouseMsg) return io.WantCaptureMouse;
        if (isKeyMsg)   return io.WantCaptureKeyboard;
        return false;
    }

    void BuildUI() {
        static uint64_t lastTick    = GetTickCount64();
        static int      frameCount  = 0;
        static float    currentFPS  = 0.0f;

        frameCount++;
        uint64_t now = GetTickCount64();
        if (now - lastTick >= 250) {
            currentFPS = frameCount * 1000.0f / (float)(now - lastTick);
            frameCount = 0;
            lastTick = now;
        }

        EffectParams params;
        IPCReader::ReadParams(params);

        bool visible = (params.showOverlay != 0) || g_LocalToggle;
        g_Visible.store(visible, std::memory_order_relaxed);
        if (!visible) return;

        ImGui::SetNextWindowSize(ImVec2(360, 280), ImGuiCond_FirstUseEver);
        ImGui::SetNextWindowPos(ImVec2(40, 40), ImGuiCond_FirstUseEver);
        ImGui::SetNextWindowBgAlpha(0.88f);

        ImGui::Begin("SteamOS - Quick Access Menu", nullptr,
            ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoSavedSettings);

        ImGui::TextColored(ImVec4(0.4f, 0.9f, 0.4f, 1.0f), "FPS: %.1f", currentFPS);
        ImGui::Text("Backend detectado: %s", g_BackendName);
        ImGui::Separator();

        bool changed = false;

        bool postFXEnabled = params.enablePostProcess != 0;
        if (ImGui::Checkbox("Post-procesado activo", &postFXEnabled)) {
            params.enablePostProcess = postFXEnabled ? 1u : 0u;
            changed = true;
        }

        bool crtEnabled = params.enableCRT != 0;
        if (ImGui::Checkbox("Filtro CRT", &crtEnabled)) {
            params.enableCRT = crtEnabled ? 1u : 0u;
            changed = true;
        }

        ImGui::BeginDisabled(!crtEnabled || !postFXEnabled);
        if (ImGui::SliderFloat("Curvatura", &params.curvature, 0.0f, 10.0f, "%.1f")) {
            changed = true;
        }
        if (ImGui::SliderFloat("Scanlines", &params.scanlineIntensity, 0.0f, 1.0f, "%.2f")) {
            changed = true;
        }
        ImGui::EndDisabled();

        ImGui::Separator();
        ImGui::TextDisabled("INSERT = toggle local (debug sin panel externo)");

        ImGui::End();

        if (changed) {
            IPCReader::WriteParams(params);
        }
    }
}
