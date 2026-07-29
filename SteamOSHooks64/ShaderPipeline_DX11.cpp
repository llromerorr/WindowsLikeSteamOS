#include "ShaderPipeline.h"
#include "ShaderSource_CRT.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"
#include <d3dcompiler.h>

#pragma comment(lib, "d3dcompiler.lib")

namespace ShaderPipelineDX11 {

    ID3D11VertexShader*       g_pVertexShader   = nullptr;
    ID3D11PixelShader*        g_pPixelShader    = nullptr;
    ID3D11InputLayout*        g_pInputLayout    = nullptr;
    ID3D11Buffer*             g_pConstantBuffer = nullptr;
    ID3D11SamplerState*       g_pSamplerState   = nullptr;

    ID3D11Texture2D*          g_pCopyTexture = nullptr;
    ID3D11ShaderResourceView* g_pCopySRV     = nullptr;

    bool g_Initialized  = false;
    UINT g_CachedWidth  = 0;
    UINT g_CachedHeight = 0;

    static void LogBlobError(ID3DBlob* pErrorBlob) {
        if (pErrorBlob) {
            Logger::Log("[DX11 Shader] Error de compilacion: %s",
                (char*)pErrorBlob->GetBufferPointer());
            pErrorBlob->Release();
        }
    }

    static bool CompileShaders(ID3D11Device* pDevice) {
        ID3DBlob* pVSBlob = nullptr;
        ID3DBlob* pPSBlob = nullptr;
        ID3DBlob* pErrorBlob = nullptr;

        HRESULT hr = D3DCompile(g_CRT_HLSL_Source, strlen(g_CRT_HLSL_Source),
            nullptr, nullptr, nullptr, "VSMain", "vs_5_0", 0, 0, &pVSBlob, &pErrorBlob);
        if (FAILED(hr)) { LogBlobError(pErrorBlob); return false; }

        hr = D3DCompile(g_CRT_HLSL_Source, strlen(g_CRT_HLSL_Source),
            nullptr, nullptr, nullptr, "PSMain", "ps_5_0", 0, 0, &pPSBlob, &pErrorBlob);
        if (FAILED(hr)) { LogBlobError(pErrorBlob); pVSBlob->Release(); return false; }

        pDevice->CreateVertexShader(pVSBlob->GetBufferPointer(), pVSBlob->GetBufferSize(),
            nullptr, &g_pVertexShader);
        pDevice->CreatePixelShader(pPSBlob->GetBufferPointer(), pPSBlob->GetBufferSize(),
            nullptr, &g_pPixelShader);

        pDevice->CreateInputLayout(nullptr, 0,
            pVSBlob->GetBufferPointer(), pVSBlob->GetBufferSize(), &g_pInputLayout);

        pVSBlob->Release();
        pPSBlob->Release();
        return true;
    }

    bool Initialize(ID3D11Device* pDevice) {
        if (g_Initialized) return true;

        if (!CompileShaders(pDevice)) {
            Logger::Log("[DX11 Shader] FATAL: fallo al compilar CRT.hlsl");
            return false;
        }

        D3D11_BUFFER_DESC cbDesc = {};
        cbDesc.Usage          = D3D11_USAGE_DYNAMIC;
        cbDesc.ByteWidth      = sizeof(CRTConstantBuffer);
        cbDesc.BindFlags      = D3D11_BIND_CONSTANT_BUFFER;
        cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        pDevice->CreateBuffer(&cbDesc, nullptr, &g_pConstantBuffer);

        D3D11_SAMPLER_DESC sampDesc = {};
        sampDesc.Filter         = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampDesc.AddressU       = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampDesc.AddressV       = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampDesc.AddressW       = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampDesc.ComparisonFunc = D3D11_COMPARISON_NEVER;
        pDevice->CreateSamplerState(&sampDesc, &g_pSamplerState);

        g_Initialized = true;
        Logger::Log("[DX11 Shader] Pipeline CRT inicializado correctamente.");
        return true;
    }

    static void EnsureCopyResources(ID3D11Device* pDevice,
        ID3D11Texture2D* pBackBufferTex, UINT width, UINT height) {

        if (g_pCopyTexture && width == g_CachedWidth && height == g_CachedHeight) return;

        if (g_pCopySRV)     { g_pCopySRV->Release();     g_pCopySRV = nullptr; }
        if (g_pCopyTexture) { g_pCopyTexture->Release();  g_pCopyTexture = nullptr; }

        D3D11_TEXTURE2D_DESC desc;
        pBackBufferTex->GetDesc(&desc);
        desc.BindFlags      = D3D11_BIND_SHADER_RESOURCE;
        desc.Usage          = D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags      = 0;

        pDevice->CreateTexture2D(&desc, nullptr, &g_pCopyTexture);
        pDevice->CreateShaderResourceView(g_pCopyTexture, nullptr, &g_pCopySRV);

        g_CachedWidth  = width;
        g_CachedHeight = height;

        Logger::Log("[DX11 Shader] Textura de copia recreada: %ux%u", width, height);
    }

    void Render(ID3D11DeviceContext* pContext, ID3D11Texture2D* pBackBufferTex,
        ID3D11RenderTargetView* pOutputRTV, UINT width, UINT height, const EffectParams& params) {

        if (!g_Initialized) return;
        
        bool needsScaling = ResolutionSpoofer::g_State.spoofEnabled.load();
        bool needsCRT = (params.enablePostProcess && params.enableCRT);

        if (!needsScaling && !needsCRT) return;

        ID3D11Device* pDevice = nullptr;
        pContext->GetDevice(&pDevice);
        EnsureCopyResources(pDevice, pBackBufferTex, width, height);
        pDevice->Release();

        pContext->CopyResource(g_pCopyTexture, pBackBufferTex);

        D3D11_MAPPED_SUBRESOURCE mapped;
        if (SUCCEEDED(pContext->Map(g_pConstantBuffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
            CRTConstantBuffer* pData = reinterpret_cast<CRTConstantBuffer*>(mapped.pData);
            pData->screenWidth       = (float)width;
            pData->screenHeight      = (float)height;
            pData->curvature         = needsCRT ? params.curvature : 0.0f;
            pData->scanlineIntensity = needsCRT ? params.scanlineIntensity : 0.0f;
            pData->time              = (float)GetTickCount64() / 1000.0f;
            pData->enableCRT         = needsCRT ? 1.0f : 0.0f;
            pContext->Unmap(g_pConstantBuffer, 0);
        }

        pContext->OMSetRenderTargets(1, &pOutputRTV, nullptr);
        
        // Limpiar el fondo a negro para el letterbox
        const float clearColor[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
        pContext->ClearRenderTargetView(pOutputRTV, clearColor);

        float aspectM = (float)ResolutionSpoofer::g_State.realWidth / (float)ResolutionSpoofer::g_State.realHeight;
        float aspectG = (float)width / (float)height;

        float vw = (float)width;
        float vh = (float)height;

        if (aspectG < aspectM) { // Pillarbox
            vh = (float)height;
            vw = ((float)width * width * ResolutionSpoofer::g_State.realHeight) / ((float)height * ResolutionSpoofer::g_State.realWidth);
        } else if (aspectG > aspectM) { // Letterbox
            vw = (float)width;
            vh = ((float)height * height * ResolutionSpoofer::g_State.realWidth) / ((float)width * ResolutionSpoofer::g_State.realHeight);
        }
        float vx = ((float)width - vw) / 2.0f;
        float vy = ((float)height - vh) / 2.0f;

        D3D11_VIEWPORT vp = { vx, vy, vw, vh, 0.0f, 1.0f };
        pContext->RSSetViewports(1, &vp);

        pContext->IASetInputLayout(g_pInputLayout);
        pContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        pContext->VSSetShader(g_pVertexShader, nullptr, 0);
        pContext->PSSetShader(g_pPixelShader, nullptr, 0);
        pContext->PSSetShaderResources(0, 1, &g_pCopySRV);
        pContext->PSSetSamplers(0, 1, &g_pSamplerState);
        pContext->PSSetConstantBuffers(0, 1, &g_pConstantBuffer);

        pContext->Draw(3, 0);

        // MUY IMPORTANTE: Desenlazar el SRV para que el juego pueda escribir
        // en el backbuffer en el siguiente frame sin que D3D11 se queje de un hazard.
        ID3D11ShaderResourceView* nullSRV[1] = { nullptr };
        pContext->PSSetShaderResources(0, 1, nullSRV);
    }

    void Shutdown() {
        if (g_pCopySRV)         g_pCopySRV->Release();
        if (g_pCopyTexture)     g_pCopyTexture->Release();
        if (g_pSamplerState)    g_pSamplerState->Release();
        if (g_pConstantBuffer)  g_pConstantBuffer->Release();
        if (g_pInputLayout)     g_pInputLayout->Release();
        if (g_pPixelShader)     g_pPixelShader->Release();
        if (g_pVertexShader)    g_pVertexShader->Release();

        g_pCopySRV = nullptr; g_pCopyTexture = nullptr; g_pSamplerState = nullptr;
        g_pConstantBuffer = nullptr; g_pInputLayout = nullptr;
        g_pPixelShader = nullptr; g_pVertexShader = nullptr;
        g_Initialized = false;
    }
}
