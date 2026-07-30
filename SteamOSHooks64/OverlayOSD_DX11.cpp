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
        UINT numScissorRects = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        D3D11_RECT scissorRects[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];

        ID3D11RenderTargetView* rtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
        ID3D11DepthStencilView* dsv = nullptr;
        ID3D11InputLayout* inputLayout = nullptr;
        D3D11_PRIMITIVE_TOPOLOGY topology;

        ID3D11Buffer* vertexBuffer = nullptr;
        UINT vertexBufferStride = 0;
        UINT vertexBufferOffset = 0;
        ID3D11Buffer* indexBuffer = nullptr;
        DXGI_FORMAT indexFormat = DXGI_FORMAT_UNKNOWN;
        UINT indexOffset = 0;

        ID3D11VertexShader* vs = nullptr;
        ID3D11PixelShader* ps = nullptr;
        ID3D11Buffer* vsConstantBuffer = nullptr;
        ID3D11Buffer* psConstantBuffer = nullptr;
        ID3D11ShaderResourceView* psSRV = nullptr;
        ID3D11SamplerState* psSampler = nullptr;

        ID3D11RasterizerState* rs = nullptr;
        ID3D11BlendState* blendState = nullptr;
        FLOAT blendFactor[4];
        UINT sampleMask;
        ID3D11DepthStencilState* depthStencilState = nullptr;
        UINT stencilRef;

        D3D11StateGuard(ID3D11DeviceContext* pContext) : ctx(pContext) {
            ctx->RSGetViewports(&numViewports, viewports);
            ctx->RSGetScissorRects(&numScissorRects, scissorRects);
            ctx->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtv, &dsv);
            ctx->IAGetInputLayout(&inputLayout);
            ctx->IAGetPrimitiveTopology(&topology);
            ctx->IAGetVertexBuffers(0, 1, &vertexBuffer, &vertexBufferStride, &vertexBufferOffset);
            ctx->IAGetIndexBuffer(&indexBuffer, &indexFormat, &indexOffset);
            ctx->VSGetShader(&vs, nullptr, nullptr);
            ctx->PSGetShader(&ps, nullptr, nullptr);
            ctx->VSGetConstantBuffers(0, 1, &vsConstantBuffer);
            ctx->PSGetConstantBuffers(0, 1, &psConstantBuffer);
            ctx->PSGetShaderResources(0, 1, &psSRV);
            ctx->PSGetSamplers(0, 1, &psSampler);
            ctx->RSGetState(&rs);
            ctx->OMGetBlendState(&blendState, blendFactor, &sampleMask);
            ctx->OMGetDepthStencilState(&depthStencilState, &stencilRef);
        }

        ~D3D11StateGuard() {
            ctx->RSSetViewports(numViewports, viewports);
            ctx->RSSetScissorRects(numScissorRects, scissorRects);
            ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtv, dsv);
            ctx->IASetInputLayout(inputLayout);
            ctx->IASetPrimitiveTopology(topology);
            ctx->IASetVertexBuffers(0, 1, &vertexBuffer, &vertexBufferStride, &vertexBufferOffset);
            ctx->IASetIndexBuffer(indexBuffer, indexFormat, indexOffset);
            ctx->VSSetShader(vs, nullptr, 0);
            ctx->PSSetShader(ps, nullptr, 0);
            ctx->VSSetConstantBuffers(0, 1, &vsConstantBuffer);
            ctx->PSSetConstantBuffers(0, 1, &psConstantBuffer);
            ctx->PSSetShaderResources(0, 1, &psSRV);
            ctx->PSSetSamplers(0, 1, &psSampler);
            ctx->RSSetState(rs);
            ctx->OMSetBlendState(blendState, blendFactor, sampleMask);
            ctx->OMSetDepthStencilState(depthStencilState, stencilRef);

            for (UINT i = 0; i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i) {
                if (rtv[i]) rtv[i]->Release();
            }
            if (dsv) dsv->Release();
            if (inputLayout) inputLayout->Release();
            if (vertexBuffer) vertexBuffer->Release();
            if (indexBuffer) indexBuffer->Release();
            if (vs) vs->Release();
            if (ps) ps->Release();
            if (vsConstantBuffer) vsConstantBuffer->Release();
            if (psConstantBuffer) psConstantBuffer->Release();
            if (psSRV) psSRV->Release();
            if (psSampler) psSampler->Release();
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
