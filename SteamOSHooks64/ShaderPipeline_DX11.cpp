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
        ID3D11Texture2D* pBackBufferTex) {

        D3D11_TEXTURE2D_DESC desc;
        pBackBufferTex->GetDesc(&desc);

        if (g_pCopyTexture && desc.Width == g_CachedWidth && desc.Height == g_CachedHeight) return;

        if (g_pCopySRV)     { g_pCopySRV->Release();     g_pCopySRV = nullptr; }
        if (g_pCopyTexture) { g_pCopyTexture->Release();  g_pCopyTexture = nullptr; }

        desc.BindFlags      = D3D11_BIND_SHADER_RESOURCE;
        desc.Usage          = D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags      = 0;
        desc.SampleDesc.Count = 1;
        desc.SampleDesc.Quality = 0;

        HRESULT hr = pDevice->CreateTexture2D(&desc, nullptr, &g_pCopyTexture);
        if (SUCCEEDED(hr)) {
            pDevice->CreateShaderResourceView(g_pCopyTexture, nullptr, &g_pCopySRV);
        }

        g_CachedWidth  = desc.Width;
        g_CachedHeight = desc.Height;

        Logger::Log("[DX11 Shader] Textura de copia recreada: %ux%u (Format=%d)", desc.Width, desc.Height, desc.Format);
    }

    void Render(ID3D11DeviceContext* pContext, ID3D11Texture2D* pBackBufferTex,
        ID3D11RenderTargetView* pOutputRTV, UINT width, UINT height, const EffectParams& params) {

        // Desactivado el dibujado in-process para no corromper el estado del D3D11 DeviceContext del juego.
        // Toda la composición y escalado FSR se realiza a través de la capa externa de textura compartida / WGC.
        return;
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
