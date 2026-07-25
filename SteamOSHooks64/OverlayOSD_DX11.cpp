#include "OverlayOSD.h"
#include "Logger.h"

#include <imgui.h>
#include <backends/imgui_impl_dx11.h>
#include <backends/imgui_impl_win32.h>

namespace OverlayOSD { void BuildUI(); }

namespace OverlayOSD::DX11 {

    bool g_Initialized = false;

    bool Initialize(ID3D11Device* pDevice, ID3D11DeviceContext* pContext) {
        if (g_Initialized) return true;

        if (!ImGui_ImplDX11_Init(pDevice, pContext)) {
            Logger::Log("[OverlayOSD DX11] Fallo ImGui_ImplDX11_Init");
            return false;
        }

        SetBackendName("DirectX 11");
        g_Initialized = true;
        Logger::Log("[OverlayOSD DX11] Backend inicializado.");
        return true;
    }

    void Render(ID3D11DeviceContext* pContext, ID3D11RenderTargetView* pOutputRTV) {
        if (!g_Initialized) return;

        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();

        BuildUI();

        ImGui::Render();

        pContext->OMSetRenderTargets(1, &pOutputRTV, nullptr);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    }

    void Shutdown() {
        if (!g_Initialized) return;
        ImGui_ImplDX11_Shutdown();
        g_Initialized = false;
    }
}
