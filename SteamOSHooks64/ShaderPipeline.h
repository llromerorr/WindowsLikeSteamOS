#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <windows.h>
#include "IPCLayout.h"

struct CRTConstantBuffer {
    float screenWidth;
    float screenHeight;
    float curvature;           // 0 = plano, 4-8 = curvatura CRT típica
    float scanlineIntensity;   // 0.0 (desactivado) - 1.0 (scanlines fuertes)
    float time;                // segundos, por si se animan efectos (ruido, flicker)
    float enableCRT;
    float padding[2];          // relleno obligatorio hasta 32 bytes
};

static_assert(sizeof(CRTConstantBuffer) % 16 == 0,
    "CRTConstantBuffer debe ser multiplo de 16 bytes para constant buffers");

namespace ShaderPipelineDX11 {
    bool Initialize(ID3D11Device* pDevice);
    void Render(ID3D11DeviceContext* pContext, ID3D11Texture2D* pBackBufferTex,
        ID3D11RenderTargetView* pOutputRTV, UINT width, UINT height, const EffectParams& params);
    void Shutdown();
}

namespace ShaderPipelineDX12 {
    bool Initialize(ID3D12Device* pDevice, DXGI_FORMAT rtvFormat);
    void Render(ID3D12Device* pDevice, ID3D12GraphicsCommandList* pCmdList,
        ID3D12Resource* pBackBuffer, D3D12_CPU_DESCRIPTOR_HANDLE outputRTV,
        UINT width, UINT height, const EffectParams& params);
    void Shutdown();
}
