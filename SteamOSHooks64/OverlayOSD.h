#pragma once
#include <d3d11.h>
#include <d3d12.h>
#include <windows.h>

namespace OverlayOSD {

    bool InitializeCommon(HWND hwnd);
    void ShutdownCommon();
    void WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
    bool IsVisible();
    bool WantsInputCapture(UINT msg);
    void SetBackendName(const char* name);

    namespace DX11 {
        bool Initialize(ID3D11Device* pDevice, ID3D11DeviceContext* pContext);
        void Render(ID3D11DeviceContext* pContext, ID3D11RenderTargetView* pOutputRTV);
        void Shutdown();
    }

    namespace DX12 {
        bool Initialize(ID3D12Device* pDevice, UINT numFramesInFlight, DXGI_FORMAT rtvFormat);
        void Render(ID3D12GraphicsCommandList* pCmdList, D3D12_CPU_DESCRIPTOR_HANDLE outputRTV);
        void Shutdown();
    }
}
