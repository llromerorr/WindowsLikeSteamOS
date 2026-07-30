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

struct D3D11StateGuard {
        ID3D11DeviceContext* ctx;
        UINT numViewports = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        D3D11_VIEWPORT viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
        ID3D11RenderTargetView* rtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
        ID3D11DepthStencilView* dsv = nullptr;
        ID3D11InputLayout* inputLayout = nullptr;
        D3D11_PRIMITIVE_TOPOLOGY topology;
        ID3D11VertexShader* vs = nullptr;
        ID3D11PixelShader* ps = nullptr;
        ID3D11RasterizerState* rs = nullptr;
        ID3D11BlendState* blendState = nullptr;
        FLOAT blendFactor[4];
        UINT sampleMask;
        ID3D11DepthStencilState* depthStencilState = nullptr;
        UINT stencilRef;

        D3D11StateGuard(ID3D11DeviceContext* pContext) : ctx(pContext) {
            ctx->RSGetViewports(&numViewports, viewports);
            ctx->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtv, &dsv);
            ctx->IAGetInputLayout(&inputLayout);
            ctx->IAGetPrimitiveTopology(&topology);
            ctx->VSGetShader(&vs, nullptr, nullptr);
            ctx->PSGetShader(&ps, nullptr, nullptr);
            ctx->RSGetState(&rs);
            ctx->OMGetBlendState(&blendState, blendFactor, &sampleMask);
            ctx->OMGetDepthStencilState(&depthStencilState, &stencilRef);
        }

        ~D3D11StateGuard() {
            ctx->RSSetViewports(numViewports, viewports);
            ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtv, dsv);
            ctx->IASetInputLayout(inputLayout);
            ctx->IASetPrimitiveTopology(topology);
            ctx->VSSetShader(vs, nullptr, 0);
            ctx->PSSetShader(ps, nullptr, 0);
            ctx->RSSetState(rs);
            ctx->OMSetBlendState(blendState, blendFactor, sampleMask);
            ctx->OMSetDepthStencilState(depthStencilState, stencilRef);

            for (UINT i = 0; i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i) {
                if (rtv[i]) rtv[i]->Release();
            }
            if (dsv) dsv->Release();
            if (inputLayout) inputLayout->Release();
            if (vs) vs->Release();
            if (ps) ps->Release();
            if (rs) rs->Release();
            if (blendState) blendState->Release();
            if (depthStencilState) depthStencilState->Release();
        }
    };

    void Render(ID3D11DeviceContext* pContext, ID3D11RenderTargetView* pOutputRTV) {
        if (!g_Initialized || !pContext || !pOutputRTV) return;

        D3D11StateGuard stateGuard(pContext);

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
