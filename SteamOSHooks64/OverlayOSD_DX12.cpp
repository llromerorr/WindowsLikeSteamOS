#include "OverlayOSD.h"
#include "Logger.h"

#include <imgui.h>
#include <backends/imgui_impl_dx12.h>
#include <backends/imgui_impl_win32.h>

namespace OverlayOSD { void BuildUI(); }

namespace OverlayOSD::DX12 {

    bool g_Initialized = false;
    ID3D12DescriptorHeap* g_pImGuiSRVHeap = nullptr;

    bool Initialize(ID3D12Device* pDevice, UINT numFramesInFlight, DXGI_FORMAT rtvFormat) {
        if (g_Initialized) return true;

        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.Type           = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDesc.NumDescriptors = 1;
        heapDesc.Flags          = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        if (FAILED(pDevice->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&g_pImGuiSRVHeap)))) {
            Logger::Log("[OverlayOSD DX12] Fallo creando heap SRV dedicado");
            return false;
        }

        bool ok = ImGui_ImplDX12_Init(
            pDevice,
            (int)numFramesInFlight,
            rtvFormat,
            g_pImGuiSRVHeap,
            g_pImGuiSRVHeap->GetCPUDescriptorHandleForHeapStart(),
            g_pImGuiSRVHeap->GetGPUDescriptorHandleForHeapStart()
        );

        if (!ok) {
            Logger::Log("[OverlayOSD DX12] Fallo ImGui_ImplDX12_Init");
            g_pImGuiSRVHeap->Release();
            g_pImGuiSRVHeap = nullptr;
            return false;
        }

        SetBackendName("DirectX 12");
        g_Initialized = true;
        Logger::Log("[OverlayOSD DX12] Backend inicializado (%u frames in flight)", numFramesInFlight);
        return true;
    }

    void Render(ID3D12GraphicsCommandList* pCmdList, D3D12_CPU_DESCRIPTOR_HANDLE outputRTV) {
        if (!g_Initialized) return;

        ImGui_ImplDX12_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();

        BuildUI();

        ImGui::Render();

        ID3D12DescriptorHeap* heaps[] = { g_pImGuiSRVHeap };
        pCmdList->SetDescriptorHeaps(1, heaps);
        pCmdList->OMSetRenderTargets(1, &outputRTV, FALSE, nullptr);

        ImGui_ImplDX12_RenderDrawData(ImGui::GetDrawData(), pCmdList);
    }

    void Shutdown() {
        if (!g_Initialized) return;
        ImGui_ImplDX12_Shutdown();
        if (g_pImGuiSRVHeap) { g_pImGuiSRVHeap->Release(); g_pImGuiSRVHeap = nullptr; }
        g_Initialized = false;
    }
}
