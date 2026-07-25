#include "ShaderPipeline.h"
#include "ShaderSource_CRT.h"
#include "Logger.h"
#include <d3dcompiler.h>

#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "d3d12.lib")

namespace ShaderPipelineDX12 {

    ID3D12RootSignature* g_pRootSignature = nullptr;
    ID3D12PipelineState* g_pPSO           = nullptr;
    ID3D12DescriptorHeap* g_pSRVHeap = nullptr;
    ID3D12Resource*       g_pCopyTexture         = nullptr;
    D3D12_RESOURCE_STATES g_CopyTextureState     = D3D12_RESOURCE_STATE_COPY_DEST;

    bool        g_Initialized  = false;
    UINT        g_CachedWidth  = 0;
    UINT        g_CachedHeight = 0;
    DXGI_FORMAT g_CachedFormat = DXGI_FORMAT_UNKNOWN;

    static void LogBlobError(ID3DBlob* pErrorBlob) {
        if (pErrorBlob) {
            Logger::Log("[DX12 Shader] Error: %s", (char*)pErrorBlob->GetBufferPointer());
            pErrorBlob->Release();
        }
    }

    static D3D12_RESOURCE_BARRIER TransitionBarrier(
        ID3D12Resource* pResource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource   = pResource;
        barrier.Transition.StateBefore = before;
        barrier.Transition.StateAfter  = after;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        return barrier;
    }

    static bool CreateRootSignature(ID3D12Device* pDevice) {
        D3D12_DESCRIPTOR_RANGE srvRange = {};
        srvRange.RangeType                         = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors                    = 1;
        srvRange.BaseShaderRegister                = 0; // t0
        srvRange.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;

        D3D12_ROOT_PARAMETER rootParams[2] = {};
        rootParams[0].ParameterType                       = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        rootParams[0].DescriptorTable.NumDescriptorRanges = 1;
        rootParams[0].DescriptorTable.pDescriptorRanges   = &srvRange;
        rootParams[0].ShaderVisibility                    = D3D12_SHADER_VISIBILITY_PIXEL;

        rootParams[1].ParameterType             = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        rootParams[1].Constants.ShaderRegister  = 0; // b0
        rootParams[1].Constants.Num32BitValues  = sizeof(CRTConstantBuffer) / 4;
        rootParams[1].ShaderVisibility          = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.Filter           = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU         = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressV         = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressW         = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.ComparisonFunc   = D3D12_COMPARISON_FUNC_NEVER;
        sampler.ShaderRegister   = 0; // s0
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

        D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
        rsDesc.NumParameters     = 2;
        rsDesc.pParameters       = rootParams;
        rsDesc.NumStaticSamplers = 1;
        rsDesc.pStaticSamplers   = &sampler;
        rsDesc.Flags             = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

        ID3DBlob* pSigBlob = nullptr;
        ID3DBlob* pErrorBlob = nullptr;
        HRESULT hr = D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1,
            &pSigBlob, &pErrorBlob);
        if (FAILED(hr)) { LogBlobError(pErrorBlob); return false; }

        hr = pDevice->CreateRootSignature(0, pSigBlob->GetBufferPointer(),
            pSigBlob->GetBufferSize(), IID_PPV_ARGS(&g_pRootSignature));

        pSigBlob->Release();
        return SUCCEEDED(hr);
    }

    static bool CreatePSO(ID3D12Device* pDevice, DXGI_FORMAT rtvFormat) {
        ID3DBlob* pVSBlob = nullptr;
        ID3DBlob* pPSBlob = nullptr;
        ID3DBlob* pErrorBlob = nullptr;

        HRESULT hr = D3DCompile(g_CRT_HLSL_Source, strlen(g_CRT_HLSL_Source),
            nullptr, nullptr, nullptr, "VSMain", "vs_5_1", 0, 0, &pVSBlob, &pErrorBlob);
        if (FAILED(hr)) { LogBlobError(pErrorBlob); return false; }

        hr = D3DCompile(g_CRT_HLSL_Source, strlen(g_CRT_HLSL_Source),
            nullptr, nullptr, nullptr, "PSMain", "ps_5_1", 0, 0, &pPSBlob, &pErrorBlob);
        if (FAILED(hr)) { LogBlobError(pErrorBlob); pVSBlob->Release(); return false; }

        D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature = g_pRootSignature;
        psoDesc.VS = { pVSBlob->GetBufferPointer(), pVSBlob->GetBufferSize() };
        psoDesc.PS = { pPSBlob->GetBufferPointer(), pPSBlob->GetBufferSize() };

        D3D12_BLEND_DESC blendDesc = {};
        blendDesc.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        psoDesc.BlendState = blendDesc;

        D3D12_RASTERIZER_DESC rastDesc = {};
        rastDesc.FillMode = D3D12_FILL_MODE_SOLID;
        rastDesc.CullMode = D3D12_CULL_MODE_NONE;
        rastDesc.DepthClipEnable = TRUE;
        psoDesc.RasterizerState = rastDesc;

        D3D12_DEPTH_STENCIL_DESC dsDesc = {};
        dsDesc.DepthEnable   = FALSE;
        dsDesc.StencilEnable = FALSE;
        psoDesc.DepthStencilState = dsDesc;

        psoDesc.InputLayout           = { nullptr, 0 };
        psoDesc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        psoDesc.NumRenderTargets      = 1;
        psoDesc.RTVFormats[0]         = rtvFormat;
        psoDesc.SampleMask            = UINT_MAX;
        psoDesc.SampleDesc.Count      = 1;

        hr = pDevice->CreateGraphicsPipelineState(&psoDesc, IID_PPV_ARGS(&g_pPSO));

        pVSBlob->Release();
        pPSBlob->Release();
        return SUCCEEDED(hr);
    }

    bool Initialize(ID3D12Device* pDevice, DXGI_FORMAT rtvFormat) {
        if (g_Initialized) return true;

        if (!CreateRootSignature(pDevice)) {
            Logger::Log("[DX12 Shader] FATAL: fallo creando Root Signature");
            return false;
        }
        if (!CreatePSO(pDevice, rtvFormat)) {
            Logger::Log("[DX12 Shader] FATAL: fallo creando PSO");
            return false;
        }

        D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc = {};
        srvHeapDesc.Type           = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        srvHeapDesc.NumDescriptors = 1;
        srvHeapDesc.Flags          = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        pDevice->CreateDescriptorHeap(&srvHeapDesc, IID_PPV_ARGS(&g_pSRVHeap));

        g_Initialized = true;
        Logger::Log("[DX12 Shader] Pipeline CRT inicializado correctamente.");
        return true;
    }

    static void EnsureCopyResources(ID3D12Device* pDevice,
        DXGI_FORMAT format, UINT width, UINT height) {

        if (g_pCopyTexture && width == g_CachedWidth &&
            height == g_CachedHeight && format == g_CachedFormat) return;

        if (g_pCopyTexture) { g_pCopyTexture->Release(); g_pCopyTexture = nullptr; }

        D3D12_HEAP_PROPERTIES heapProps = { D3D12_HEAP_TYPE_DEFAULT };

        D3D12_RESOURCE_DESC texDesc = {};
        texDesc.Dimension        = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        texDesc.Width            = width;
        texDesc.Height           = height;
        texDesc.DepthOrArraySize = 1;
        texDesc.MipLevels        = 1;
        texDesc.Format           = format;
        texDesc.SampleDesc.Count = 1;
        texDesc.Layout           = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        texDesc.Flags            = D3D12_RESOURCE_FLAG_NONE;

        pDevice->CreateCommittedResource(&heapProps, D3D12_HEAP_FLAG_NONE, &texDesc,
            D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g_pCopyTexture));
        g_CopyTextureState = D3D12_RESOURCE_STATE_COPY_DEST;

        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
        srvDesc.Format                  = format;
        srvDesc.ViewDimension            = D3D12_SRV_DIMENSION_TEXTURE2D;
        srvDesc.Shader4ComponentMapping  = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srvDesc.Texture2D.MipLevels      = 1;

        pDevice->CreateShaderResourceView(g_pCopyTexture, &srvDesc,
            g_pSRVHeap->GetCPUDescriptorHandleForHeapStart());

        g_CachedWidth  = width;
        g_CachedHeight = height;
        g_CachedFormat = format;

        Logger::Log("[DX12 Shader] Textura de copia recreada: %ux%u", width, height);
    }

    void Render(ID3D12Device* pDevice, ID3D12GraphicsCommandList* pCmdList,
        ID3D12Resource* pBackBuffer, D3D12_CPU_DESCRIPTOR_HANDLE outputRTV,
        UINT width, UINT height, const EffectParams& params) {

        if (!g_Initialized) return;
        if (!params.enablePostProcess || !params.enableCRT) return;

        D3D12_RESOURCE_DESC bbDesc = pBackBuffer->GetDesc();
        EnsureCopyResources(pDevice, bbDesc.Format, width, height);

        D3D12_RESOURCE_BARRIER barriers[2];
        barriers[0] = TransitionBarrier(pBackBuffer,
            D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_SOURCE);
        barriers[1] = TransitionBarrier(g_pCopyTexture,
            g_CopyTextureState, D3D12_RESOURCE_STATE_COPY_DEST);
        pCmdList->ResourceBarrier(2, barriers);
        g_CopyTextureState = D3D12_RESOURCE_STATE_COPY_DEST;

        pCmdList->CopyResource(g_pCopyTexture, pBackBuffer);

        barriers[0] = TransitionBarrier(pBackBuffer,
            D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_RENDER_TARGET);
        barriers[1] = TransitionBarrier(g_pCopyTexture,
            D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        pCmdList->ResourceBarrier(2, barriers);
        g_CopyTextureState = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;

        pCmdList->OMSetRenderTargets(1, &outputRTV, FALSE, nullptr);

        D3D12_VIEWPORT vp = { 0.0f, 0.0f, (float)width, (float)height, 0.0f, 1.0f };
        D3D12_RECT scissor = { 0, 0, (LONG)width, (LONG)height };
        pCmdList->RSSetViewports(1, &vp);
        pCmdList->RSSetScissorRects(1, &scissor);

        ID3D12DescriptorHeap* heaps[] = { g_pSRVHeap };
        pCmdList->SetDescriptorHeaps(1, heaps);

        pCmdList->SetGraphicsRootSignature(g_pRootSignature);
        pCmdList->SetGraphicsRootDescriptorTable(0,
            g_pSRVHeap->GetGPUDescriptorHandleForHeapStart());

        CRTConstantBuffer cb = {};
        cb.screenWidth       = (float)width;
        cb.screenHeight      = (float)height;
        cb.curvature         = params.curvature;
        cb.scanlineIntensity = params.scanlineIntensity;
        cb.time              = (float)GetTickCount64() / 1000.0f;
        pCmdList->SetGraphicsRoot32BitConstants(1, sizeof(CRTConstantBuffer) / 4, &cb, 0);

        pCmdList->SetPipelineState(g_pPSO);
        pCmdList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        pCmdList->DrawInstanced(3, 1, 0, 0);
    }

    void Shutdown() {
        if (g_pCopyTexture)    g_pCopyTexture->Release();
        if (g_pSRVHeap)        g_pSRVHeap->Release();
        if (g_pPSO)            g_pPSO->Release();
        if (g_pRootSignature)  g_pRootSignature->Release();

        g_pCopyTexture = nullptr; g_pSRVHeap = nullptr;
        g_pPSO = nullptr; g_pRootSignature = nullptr;
        g_Initialized = false;
    }
}
