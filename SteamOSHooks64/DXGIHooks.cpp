#include <d3d11.h>
#include <dxgi1_4.h>
#include "Hooking.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"
#include "D3D12Hooks.h"
#include "ShaderPipeline.h"
#include "IPCReader.h"
#include "OverlayOSD.h"

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

    Present_t       oPresent       = nullptr;
    GetBuffer_t     oGetBuffer     = nullptr;
    GetFullscreenState_t oGetFullscreenState = nullptr;
    SetFullscreen_t oSetFullscreen = nullptr;
    ResizeTarget_t  oResizeTarget  = nullptr;
    ResizeBuffers_t oResizeBuffers = nullptr;

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

    static void EnsureSharedTexture(ID3D11Device* pDevice, UINT width, UINT height, DXGI_FORMAT format) {
        if (g_pSharedTexture && g_SharedWidth == width && g_SharedHeight == height) return;

        if (g_pSharedKeyedMutex) { g_pSharedKeyedMutex->Release(); g_pSharedKeyedMutex = nullptr; }
        if (g_pSharedTexture)     { g_pSharedTexture->Release();     g_pSharedTexture = nullptr; }
        if (g_hSharedHandle)      { CloseHandle(g_hSharedHandle);    g_hSharedHandle = nullptr; }

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
        desc.MiscFlags          = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

        HRESULT hr = pDevice->CreateTexture2D(&desc, nullptr, &g_pSharedTexture);
        if (SUCCEEDED(hr) && g_pSharedTexture) {
            g_pSharedTexture->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&g_pSharedKeyedMutex);

            IDXGIResource1* pRes1 = nullptr;
            if (SUCCEEDED(g_pSharedTexture->QueryInterface(__uuidof(IDXGIResource1), (void**)&pRes1))) {
                pRes1->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                    L"SteamOS_SharedBackbuffer", &g_hSharedHandle);
                pRes1->Release();
            }
            g_SharedWidth  = width;
            g_SharedHeight = height;
            Logger::Log("[SharedTexture] Creada textura compartida nombrada 'SteamOS_SharedBackbuffer' %ux%u", width, height);
        } else {
            Logger::Log("[SharedTexture] ERROR 0x%08X al crear textura compartida NTHANDLE", hr);
        }
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
        Logger::Log("[Hook] SetFullscreenState solicitado: %d", Fullscreen);
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

        if (Width > 0 && Height > 0) {
            ResolutionSpoofer::g_State.fakeWidth = Width;
            ResolutionSpoofer::g_State.fakeHeight = Height;
        }
        
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
        
        ResolutionSpoofer::g_State.spoofEnabled.store(params.enableResolutionSpoof != 0);

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
                oGetBuffer(pSwapChain, 0, __uuidof(ID3D11Texture2D), (void**)&g_pBackBufferTex);
                if (g_pBackBufferTex) {
                    g_pDevice->CreateRenderTargetView(g_pBackBufferTex, nullptr, &g_pBackBufferRTV);
                    g_pBackBufferTex->Release();
                    g_pBackBufferTex = nullptr;

                    DXGI_SWAP_CHAIN_DESC desc;
                    pSwapChain->GetDesc(&desc);
                    ResolutionSpoofer::InstallOn(desc.OutputWindow);

                    if (desc.BufferDesc.Width > 0 && desc.BufferDesc.Height > 0) {
                        ResolutionSpoofer::g_State.fakeWidth = desc.BufferDesc.Width;
                        ResolutionSpoofer::g_State.fakeHeight = desc.BufferDesc.Height;
                    }

                    ShaderPipelineDX11::Initialize(g_pDevice);
                    OverlayOSD::InitializeCommon(desc.OutputWindow);
                    OverlayOSD::DX11::Initialize(g_pDevice, g_pContext);

                    g_ResourcesReady = true;
                    Logger::Log("[Present] Recursos D3D11 inicializados. HWND=%p", desc.OutputWindow);
                }
            }
        }

        if (g_ResourcesReady) {
            OverlayOSD::DX11::Render(g_pContext, g_pBackBufferRTV);
        }

        static uint32_t frameCounter = 0;
        frameCounter++;

        IPCReader::WriteTelemetry(11, frameCounter, 0.0f);

        return oPresent(pSwapChain, SyncInterval, Flags);
    }

    bool Initialize() {
        WNDCLASSEXW wc = { sizeof(WNDCLASSEXW), CS_CLASSDC, DefWindowProcW,
            0L, 0L, GetModuleHandle(nullptr), nullptr, nullptr, nullptr, nullptr,
            L"SteamOSHooksDummy", nullptr };
        RegisterClassExW(&wc);
        HWND hDummyWnd = CreateWindowW(wc.lpszClassName, L"Dummy", WS_OVERLAPPEDWINDOW,
            0, 0, 100, 100, nullptr, nullptr, wc.hInstance, nullptr);

        DXGI_SWAP_CHAIN_DESC sd = {};
        sd.BufferCount = 1;
        sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.OutputWindow = hDummyWnd;
        sd.SampleDesc.Count = 1;
        sd.Windowed = TRUE;

        IDXGISwapChain* pTempSwapChain = nullptr;
        ID3D11Device* pTempDevice = nullptr;
        ID3D11DeviceContext* pTempContext = nullptr;
        D3D_FEATURE_LEVEL fl;

        HRESULT hr = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
            nullptr, 0, D3D11_SDK_VERSION, &sd,
            &pTempSwapChain, &pTempDevice, &fl, &pTempContext);

        if (FAILED(hr)) {
            Logger::Log("Fallo al crear dummy device/swapchain: 0x%08X", hr);
            DestroyWindow(hDummyWnd);
            return false;
        }

        void** vtable = *reinterpret_cast<void***>(pTempSwapChain);

        bool ok = true;
        ok &= Hooking::CreateHook(vtable[VT_PRESENT],       &hkPresent,          &oPresent);
        ok &= Hooking::CreateHook(vtable[VT_GETBUFFER],     &hkGetBuffer,        &oGetBuffer);
        ok &= Hooking::CreateHook(vtable[11],               &hkGetFullscreenState, &oGetFullscreenState);
        ok &= Hooking::CreateHook(vtable[VT_SETFULLSCREEN], &hkSetFullscreenState, &oSetFullscreen);
        ok &= Hooking::CreateHook(vtable[VT_RESIZETARGET],  &hkResizeTarget,     &oResizeTarget);
        ok &= Hooking::CreateHook(vtable[VT_RESIZEBUFFERS], &hkResizeBuffers,    &oResizeBuffers);

        pTempSwapChain->Release();
        pTempContext->Release();
        pTempDevice->Release();
        DestroyWindow(hDummyWnd);
        UnregisterClassW(wc.lpszClassName, wc.hInstance);

        return ok;
    }
}
