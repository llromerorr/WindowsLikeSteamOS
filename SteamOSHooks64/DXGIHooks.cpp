#include <d3d11_1.h>
#include <dxgi1_4.h>
#include <atomic>
#include "Hooking.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"
#include "D3D12Hooks.h"
#include "ShaderPipeline.h"
#include "IPCReader.h"
#include "OverlayOSD.h"
#include <wrl.h>

using Microsoft::WRL::ComPtr;

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace DXGIHooks {

    enum VTableIndex {
        VT_PRESENT          = 8,
        VT_GETBUFFER        = 9,
        VT_SETFULLSCREEN    = 10,
        VT_RESIZEBUFFERS    = 13,
        VT_RESIZETARGET     = 14
    };

    using Present_t          = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
    using GetBuffer_t        = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, REFIID, void**);
    using GetFullscreenState_t = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, BOOL*, IDXGIOutput**);
    using SetFullscreen_t    = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, BOOL, IDXGIOutput*);
    using ResizeTarget_t     = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, const DXGI_MODE_DESC*);
    using ResizeBuffers_t    = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
    
    using CreateSwapChain_t = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
    using CreateSwapChainForHwnd_t = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);

    Present_t       oPresent       = nullptr;
    GetBuffer_t     oGetBuffer     = nullptr;
    GetFullscreenState_t oGetFullscreenState = nullptr;
    SetFullscreen_t oSetFullscreen = nullptr;
    ResizeTarget_t  oResizeTarget  = nullptr;
    ResizeBuffers_t oResizeBuffers = nullptr;
    
    CreateSwapChain_t oCreateSwapChain = nullptr;
    CreateSwapChainForHwnd_t oCreateSwapChainForHwnd = nullptr;

    std::atomic<bool> g_FactoryHooked{false};
    std::atomic<bool> g_SwapChainHooked{false};
    uint8_t g_expectedPresentBytes[16] = {0};

    bool g_FakeFullscreen = false;

    ID3D11Device*           g_pDevice        = nullptr;
    ID3D11DeviceContext*    g_pContext       = nullptr;
    ID3D11Texture2D*        g_pBackBufferTex = nullptr;
    ID3D11RenderTargetView* g_pBackBufferRTV = nullptr;
    ID3D11Texture2D*        g_pFakeBackBufferTex = nullptr;
    bool                    g_ResourcesReady = false;

    static ID3D11Texture2D* g_pSharedTexture     = nullptr;
    static IDXGIKeyedMutex* g_pSharedKeyedMutex = nullptr;
    static HANDLE           g_hSharedHandle      = nullptr;
    static UINT             g_SharedWidth        = 0;
    static UINT             g_SharedHeight       = 0;
    static LUID             g_AdapterLuid        = {};
    static bool             g_bHandleWritten     = false;
    static bool             g_bIsNtHandle        = false;

    DXGI_FORMAT StripSRGB(DXGI_FORMAT format) {
        switch (format) {
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
            default: return format;
        }
    }

    static void EnsureSharedTexture(ID3D11Device* pDevice, UINT width, UINT height, DXGI_FORMAT format) {
        format = StripSRGB(format);
        if (g_pSharedTexture && g_SharedWidth == width && g_SharedHeight == height) return;

        if (g_pSharedKeyedMutex) { g_pSharedKeyedMutex->Release(); g_pSharedKeyedMutex = nullptr; }
        if (g_pSharedTexture)     { g_pSharedTexture->Release();     g_pSharedTexture = nullptr; }
        // Si el handle viejo era NT, deberíamos cerrarlo, pero por simplicidad el OS lo limpiará, 
        // o podemos llamar a CloseHandle(g_hSharedHandle) si g_bIsNtHandle.
        if (g_hSharedHandle && g_bIsNtHandle) { CloseHandle(g_hSharedHandle); }
        g_hSharedHandle = nullptr;

        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width              = width;
        desc.Height             = height;
        desc.MipLevels          = 1;
        desc.ArraySize          = 1;
        desc.Format             = format;
        desc.SampleDesc.Count   = 1;
        desc.SampleDesc.Quality = 0;
        desc.Usage              = D3D11_USAGE_DEFAULT;
        desc.BindFlags          = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags          = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

        ComPtr<ID3D11Device1> pDevice1;
        bool canUseNtHandle = false; // SUCCEEDED(pDevice->QueryInterface(IID_PPV_ARGS(&pDevice1)));
        
        if (canUseNtHandle) {
            desc.MiscFlags |= D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
        }

        HRESULT hr = pDevice->CreateTexture2D(&desc, nullptr, &g_pSharedTexture);
        if (FAILED(hr)) {
            Logger::Log("[SharedTexture] ERROR 0x%08X al crear textura compartida KEYEDMUTEX", hr);
            return;
        }

        g_pSharedTexture->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&g_pSharedKeyedMutex);

        if (canUseNtHandle) {
            ComPtr<IDXGIResource1> pResource1;
            if (SUCCEEDED(g_pSharedTexture->QueryInterface(IID_PPV_ARGS(&pResource1)))) {
                pResource1->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &g_hSharedHandle);
            }
        } else {
            ComPtr<IDXGIResource> pRes;
            if (SUCCEEDED(g_pSharedTexture->QueryInterface(IID_PPV_ARGS(&pRes)))) {
                pRes->GetSharedHandle(&g_hSharedHandle);
            }
        }

        g_SharedWidth  = width;
        g_SharedHeight = height;
        
        LUID luid = {};
        ComPtr<IDXGIDevice> pDXGIDevice;
        if (SUCCEEDED(pDevice->QueryInterface(IID_PPV_ARGS(&pDXGIDevice)))) {
            ComPtr<IDXGIAdapter> pAdapter;
            if (SUCCEEDED(pDXGIDevice->GetAdapter(&pAdapter))) {
                DXGI_ADAPTER_DESC adesc;
                if (SUCCEEDED(pAdapter->GetDesc(&adesc))) {
                    luid = adesc.AdapterLuid;
                }
            }
        }

        g_AdapterLuid = luid;
        g_bHandleWritten = false;
        g_bIsNtHandle = canUseNtHandle;

        Logger::Log("[SharedTexture] Creada textura compartida %ux%u (Handle=0x%llX, NT=%d)", width, height, (uint64_t)g_hSharedHandle, canUseNtHandle);
    }

    HRESULT STDMETHODCALLTYPE hkGetBuffer(IDXGISwapChain* pSwapChain, UINT Buffer, REFIID riid, void** ppSurface) {
        if (!g_pDevice) {
            pSwapChain->GetDevice(__uuidof(ID3D11Device), (void**)&g_pDevice);
        }
        if (g_pDevice && !g_pContext) {
            g_pDevice->GetImmediateContext(&g_pContext);
        }

        EffectParams params;
        IPCReader::ReadParams(params);
        ResolutionSpoofer::g_State.spoofEnabled.store(params.enableResolutionSpoof != 0);

        return oGetBuffer(pSwapChain, Buffer, riid, ppSurface);
    }

    HRESULT STDMETHODCALLTYPE hkGetFullscreenState(IDXGISwapChain* pSwapChain, BOOL* pFullscreen, IDXGIOutput** ppTarget) {
        return oGetFullscreenState(pSwapChain, pFullscreen, ppTarget);
    }

    HRESULT STDMETHODCALLTYPE hkSetFullscreenState(IDXGISwapChain* pSwapChain, BOOL Fullscreen, IDXGIOutput* pTarget) {
        if (ResolutionSpoofer::g_State.spoofEnabled.load()) {
            Logger::Log("[Hook] SetFullscreenState bloqueado (DXGI_ERROR_NOT_CURRENTLY_AVAILABLE).");
            return DXGI_ERROR_NOT_CURRENTLY_AVAILABLE;
        }
        return oSetFullscreen(pSwapChain, Fullscreen, pTarget);
    }

    HRESULT STDMETHODCALLTYPE hkResizeTarget(IDXGISwapChain* pSwapChain, const DXGI_MODE_DESC* pNewTargetParameters) {
        Logger::Log("[Hook] ResizeTarget interceptado (%ux%u) -> Pasando a la original.",
            pNewTargetParameters->Width, pNewTargetParameters->Height);
        return oResizeTarget(pSwapChain, pNewTargetParameters);
    }

    HRESULT STDMETHODCALLTYPE hkResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
        UINT Width, UINT Height, DXGI_FORMAT Format, UINT Flags) {
        Logger::Log("[Hook] ResizeBuffers solicitado: %ux%u", Width, Height);
        if (g_pContext) {
            ID3D11RenderTargetView* nullViews[] = { nullptr };
            g_pContext->OMSetRenderTargets(1, nullViews, nullptr);
            g_pContext->ClearState();
        }

        if (g_pBackBufferTex) {
            g_pBackBufferTex->Release();
            g_pBackBufferTex = nullptr;
        }
        if (g_pBackBufferRTV) {
            g_pBackBufferRTV->Release();
            g_pBackBufferRTV = nullptr;
            g_ResourcesReady = false;
        }
        
        D3D12Hooks::OnResizeBuffers(pSwapChain);
        
        HRESULT hr = oResizeBuffers(pSwapChain, BufferCount, Width, Height, Format, Flags);
        if (FAILED(hr)) {
            Logger::Log("[Hook] ERROR oResizeBuffers fallo con hr=0x%08X", hr);
        }
        return hr;
    }

    HRESULT STDMETHODCALLTYPE hkPresent(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags) {
        EffectParams params;
        IPCReader::ReadParams(params);

        if (D3D12Hooks::OnPreDx12Present(pSwapChain, params)) {
            return oPresent(pSwapChain, SyncInterval, Flags);
        }

        if (!g_ResourcesReady) {
            if (!g_pDevice) {
                pSwapChain->GetDevice(__uuidof(ID3D11Device), (void**)&g_pDevice);
            }
            if (g_pDevice && !g_pContext) {
                g_pDevice->GetImmediateContext(&g_pContext);
            }

            if (g_pDevice && g_pContext) {
                DXGI_SWAP_CHAIN_DESC desc;
                pSwapChain->GetDesc(&desc);
                ResolutionSpoofer::InstallOn(desc.OutputWindow);

        // Ya no actualizamos fakeWidth/fakeHeight aquí

                ShaderPipelineDX11::Initialize(g_pDevice);
                OverlayOSD::InitializeCommon(desc.OutputWindow);
                OverlayOSD::DX11::Initialize(g_pDevice, g_pContext);

                g_ResourcesReady = true;
                Logger::Log("[Present] Recursos D3D11 inicializados. HWND=%p", desc.OutputWindow);
            }
        }

        if (g_ResourcesReady && g_pDevice && g_pContext) {
            ID3D11Texture2D* pBackBuffer = nullptr;
            HRESULT hrBuf = oGetBuffer(pSwapChain, 0, __uuidof(ID3D11Texture2D), (void**)&pBackBuffer);
            if (SUCCEEDED(hrBuf) && pBackBuffer) {
                D3D11_TEXTURE2D_DESC texDesc;
                pBackBuffer->GetDesc(&texDesc);

                EnsureSharedTexture(g_pDevice, texDesc.Width, texDesc.Height, texDesc.Format);

                if (g_hSharedHandle && !g_bHandleWritten && IPCReader::IsConnected()) {
                    if (IPCReader::WriteSharedHandle(g_hSharedHandle, g_SharedWidth, g_SharedHeight, g_AdapterLuid, g_bIsNtHandle)) {
                        g_bHandleWritten = true;
                    }
                }

                // Dibujar el OSD in-process (opcional)
                ID3D11RenderTargetView* pRTV = nullptr;
                if (SUCCEEDED(g_pDevice->CreateRenderTargetView(pBackBuffer, nullptr, &pRTV)) && pRTV) {
                    OverlayOSD::DX11::Render(g_pContext, pRTV);
                    pRTV->Release();
                }

                // Copiar a la textura compartida para FSR externo
                if (g_pSharedTexture && g_pSharedKeyedMutex) {
                    if (SUCCEEDED(g_pSharedKeyedMutex->AcquireSync(0, 16))) {
                        if (texDesc.SampleDesc.Count > 1) {
                            g_pContext->ResolveSubresource(g_pSharedTexture, 0, pBackBuffer, 0, StripSRGB(texDesc.Format));
                        } else {
                            g_pContext->CopySubresourceRegion(g_pSharedTexture, 0, 0, 0, 0, pBackBuffer, 0, nullptr);
                        }
                        g_pSharedKeyedMutex->ReleaseSync(1);
                    }
                }

                pBackBuffer->Release();
            } else {
                static int errCount = 0;
                if (errCount++ % 60 == 0) {
                    Logger::Log("[Present] ERROR: oGetBuffer fallo con hr=0x%08X", hrBuf);
                }
            }
        }

        static uint32_t frameCounter = 0;
        frameCounter++;

        if (frameCounter % 60 == 0) {
            void** vtable = *reinterpret_cast<void***>(pSwapChain);
            if (memcmp(vtable[VT_PRESENT], g_expectedPresentBytes, 16) != 0) {
                Logger::Log("[Hook] ADVERTENCIA: Present() re-hookeado externamente (posible superposición como RTSS).");
            }
        }

        IPCReader::WriteTelemetry(11, frameCounter, 0.0f);

        return oPresent(pSwapChain, SyncInterval, Flags);
    }

    HRESULT STDMETHODCALLTYPE hkCreateSwapChain(IDXGIFactory* pFactory, IUnknown* pDevice, DXGI_SWAP_CHAIN_DESC* pDesc, IDXGISwapChain** ppSwapChain) {
        HRESULT hr = oCreateSwapChain(pFactory, pDevice, pDesc, ppSwapChain);
        if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain) {
            bool expected = false;
            if (g_SwapChainHooked.compare_exchange_strong(expected, true)) {
                void** vtable = *reinterpret_cast<void***>(*ppSwapChain);
                bool ok = true;
                ok &= Hooking::CreateHook(vtable[VT_PRESENT],       &hkPresent,          &oPresent);
                if (ok) {
                    memcpy(g_expectedPresentBytes, vtable[VT_PRESENT], sizeof(g_expectedPresentBytes));
                }
                ok &= Hooking::CreateHook(vtable[VT_GETBUFFER],     &hkGetBuffer,        &oGetBuffer);
                ok &= Hooking::CreateHook(vtable[11],               &hkGetFullscreenState, &oGetFullscreenState);
                ok &= Hooking::CreateHook(vtable[VT_SETFULLSCREEN], &hkSetFullscreenState, &oSetFullscreen);
                ok &= Hooking::CreateHook(vtable[VT_RESIZETARGET],  &hkResizeTarget,     &oResizeTarget);
                ok &= Hooking::CreateHook(vtable[VT_RESIZEBUFFERS], &hkResizeBuffers,    &oResizeBuffers);
                Logger::Log("[Hook] IDXGISwapChain hooks installed successfully from CreateSwapChain");
            }
        }
        return hr;
    }

    HRESULT STDMETHODCALLTYPE hkCreateSwapChainForHwnd(IDXGIFactory2* pFactory, IUnknown* pDevice, HWND hWnd, const DXGI_SWAP_CHAIN_DESC1* pDesc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* pFullscreenDesc, IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain) {
        HRESULT hr = oCreateSwapChainForHwnd(pFactory, pDevice, hWnd, pDesc, pFullscreenDesc, pRestrictToOutput, ppSwapChain);
        if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain) {
            bool expected = false;
            if (g_SwapChainHooked.compare_exchange_strong(expected, true)) {
                void** vtable = *reinterpret_cast<void***>(*ppSwapChain);
                bool ok = true;
                ok &= Hooking::CreateHook(vtable[VT_PRESENT],       &hkPresent,          &oPresent);
                if (ok) {
                    memcpy(g_expectedPresentBytes, vtable[VT_PRESENT], sizeof(g_expectedPresentBytes));
                }
                ok &= Hooking::CreateHook(vtable[VT_GETBUFFER],     &hkGetBuffer,        &oGetBuffer);
                ok &= Hooking::CreateHook(vtable[11],               &hkGetFullscreenState, &oGetFullscreenState);
                ok &= Hooking::CreateHook(vtable[VT_SETFULLSCREEN], &hkSetFullscreenState, &oSetFullscreen);
                ok &= Hooking::CreateHook(vtable[VT_RESIZETARGET],  &hkResizeTarget,     &oResizeTarget);
                ok &= Hooking::CreateHook(vtable[VT_RESIZEBUFFERS], &hkResizeBuffers,    &oResizeBuffers);
                Logger::Log("[Hook] IDXGISwapChain hooks installed successfully from CreateSwapChainForHwnd");
            }
        }
        return hr;
    }

    void InstallFactoryHooks(IUnknown* pFactoryUnk) {
        if (!pFactoryUnk) return;

        bool expected = false;
        if (!g_FactoryHooked.compare_exchange_strong(expected, true)) {
            return;
        }

        IDXGIFactory* pFactory = nullptr;
        if (SUCCEEDED(pFactoryUnk->QueryInterface(__uuidof(IDXGIFactory), (void**)&pFactory))) {
            void** vtable = *reinterpret_cast<void***>(pFactory);
            Hooking::CreateHook(vtable[10], &hkCreateSwapChain, &oCreateSwapChain);
            pFactory->Release();
        }

        IDXGIFactory2* pFactory2 = nullptr;
        if (SUCCEEDED(pFactoryUnk->QueryInterface(__uuidof(IDXGIFactory2), (void**)&pFactory2))) {
            void** vtable = *reinterpret_cast<void***>(pFactory2);
            Hooking::CreateHook(vtable[15], &hkCreateSwapChainForHwnd, &oCreateSwapChainForHwnd);
            pFactory2->Release();
        }
    }
}
