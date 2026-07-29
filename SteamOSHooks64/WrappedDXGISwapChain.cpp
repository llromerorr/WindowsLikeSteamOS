#include "WrappedDXGISwapChain.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"
#include "ShaderPipeline.h"
#include "IPCReader.h"
#include "OverlayOSD.h"
#include <d3d11.h>
#include <dxgi1_6.h>

WrappedDXGISwapChain::WrappedDXGISwapChain(IDXGISwapChain* pReal) : m_pReal(pReal) {
    if (m_pReal) {
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal1));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal2));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal3));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal4));
    }
    Logger::Log("[WrappedDXGISwapChain] Created wrapper for real swapchain %p", m_pReal);
}

WrappedDXGISwapChain::~WrappedDXGISwapChain() {
    CleanupResources();

    // Si la swapchain se destruye, es probable que el Device tambin.
    // Apagamos los subsistemas globales para que se recreen limpios.
    ShaderPipelineDX11::Shutdown();
    OverlayOSD::DX11::Shutdown();

    if (m_pReal4) m_pReal4->Release();
    if (m_pReal3) m_pReal3->Release();
    if (m_pReal2) m_pReal2->Release();
    if (m_pReal1) m_pReal1->Release();
    if (m_pReal)  m_pReal->Release();
    Logger::Log("[WrappedDXGISwapChain] Destroyed wrapper for real swapchain %p", m_pReal);
}

void WrappedDXGISwapChain::CleanupResources() {
    if (m_pBackBufferRTV) { m_pBackBufferRTV->Release(); m_pBackBufferRTV = nullptr; }
    if (m_pBackBufferTex) { m_pBackBufferTex->Release(); m_pBackBufferTex = nullptr; }
    if (m_pD3D11Context) { m_pD3D11Context->Release(); m_pD3D11Context = nullptr; }
    if (m_pD3D11Device) { m_pD3D11Device->Release(); m_pD3D11Device = nullptr; }
    m_resourcesReady = false;
}

// --- IUnknown ---
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::QueryInterface(REFIID riid, void** ppvObject) {
    if (!ppvObject) return E_POINTER;

    if (riid == __uuidof(IUnknown) ||
        riid == __uuidof(IDXGIObject) ||
        riid == __uuidof(IDXGIDeviceSubObject) ||
        riid == __uuidof(IDXGISwapChain)) {
        *ppvObject = static_cast<IDXGISwapChain*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGISwapChain1) && m_pReal1) {
        *ppvObject = static_cast<IDXGISwapChain1*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGISwapChain2) && m_pReal2) {
        *ppvObject = static_cast<IDXGISwapChain2*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGISwapChain3) && m_pReal3) {
        *ppvObject = static_cast<IDXGISwapChain3*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGISwapChain4) && m_pReal4) {
        *ppvObject = static_cast<IDXGISwapChain4*>(this);
        AddRef();
        return S_OK;
    }

    return m_pReal->QueryInterface(riid, ppvObject);
}

ULONG STDMETHODCALLTYPE WrappedDXGISwapChain::AddRef() {
    return ++m_refCount;
}

ULONG STDMETHODCALLTYPE WrappedDXGISwapChain::Release() {
    ULONG res = --m_refCount;
    if (res == 0) {
        delete this;
    }
    return res;
}

// --- IDXGIObject ---
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetPrivateData(REFGUID Name, UINT DataSize, const void* pData) {
    return m_pReal->SetPrivateData(Name, DataSize, pData);
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetPrivateDataInterface(REFGUID Name, const IUnknown* pUnknown) {
    return m_pReal->SetPrivateDataInterface(Name, pUnknown);
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetPrivateData(REFGUID Name, UINT* pDataSize, void* pData) {
    return m_pReal->GetPrivateData(Name, pDataSize, pData);
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetParent(REFIID riid, void** ppParent) {
    return m_pReal->GetParent(riid, ppParent);
}

// --- IDXGIDeviceSubObject ---
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetDevice(REFIID riid, void** ppDevice) {
    return m_pReal->GetDevice(riid, ppDevice);
}

// --- IDXGISwapChain ---
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::Present(UINT SyncInterval, UINT Flags) {
    EffectParams params;
    IPCReader::ReadParams(params);

    if (!m_resourcesReady) {
        if (SUCCEEDED(m_pReal->GetDevice(IID_PPV_ARGS(&m_pD3D11Device)))) {
            m_pD3D11Device->GetImmediateContext(&m_pD3D11Context);
        }

        if (m_pD3D11Device && m_pD3D11Context) {
            if (SUCCEEDED(m_pReal->GetBuffer(0, IID_PPV_ARGS(&m_pBackBufferTex)))) {
                m_pD3D11Device->CreateRenderTargetView(m_pBackBufferTex, nullptr, &m_pBackBufferRTV);

                DXGI_SWAP_CHAIN_DESC desc;
                m_pReal->GetDesc(&desc);
                ResolutionSpoofer::InstallOn(desc.OutputWindow);

                if (desc.BufferDesc.Width > 0 && desc.BufferDesc.Height > 0) {
                    ResolutionSpoofer::g_State.fakeWidth = desc.BufferDesc.Width;
                    ResolutionSpoofer::g_State.fakeHeight = desc.BufferDesc.Height;
                }

                ShaderPipelineDX11::Initialize(m_pD3D11Device);
                OverlayOSD::InitializeCommon(desc.OutputWindow);
                OverlayOSD::DX11::Initialize(m_pD3D11Device, m_pD3D11Context);

                m_resourcesReady = true;
                Logger::Log("[WrappedDXGISwapChain] D3D11 direct rendering context initialized. HWND=%p", desc.OutputWindow);
            }
        }
    }

    if (m_resourcesReady && m_pBackBufferRTV) {
        DXGI_SWAP_CHAIN_DESC desc;
        m_pReal->GetDesc(&desc);

        // Render CRT Shaders if enabled
        if (params.enablePostProcess || params.enableCRT) {
            ShaderPipelineDX11::Render(m_pD3D11Context, m_pBackBufferTex, m_pBackBufferRTV,
                desc.BufferDesc.Width, desc.BufferDesc.Height, params);
        }

        // Render OSD / Overlay directly on game backbuffer RTV
        OverlayOSD::DX11::Render(m_pD3D11Context, m_pBackBufferRTV);
    }

    static uint32_t frameCounter = 0;
    IPCReader::WriteTelemetry(11, ++frameCounter, 0.0f);

    // Present the REAL SwapChain directly!
    return m_pReal->Present(SyncInterval, Flags);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetBuffer(UINT Buffer, REFIID riid, void** ppSurface) {
    return m_pReal->GetBuffer(Buffer, riid, ppSurface);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetFullscreenState(BOOL Fullscreen, IDXGIOutput* pTarget) {
    Logger::Log("[WrappedDXGISwapChain] Intercepted SetFullscreenState(%d) -> Enforcing Borderless Fullscreen", Fullscreen);
    DXGI_SWAP_CHAIN_DESC desc;
    if (SUCCEEDED(m_pReal->GetDesc(&desc))) {
        HWND hwnd = desc.OutputWindow;
        if (Fullscreen) {
            DEVMODEW devMode = {};
            devMode.dmSize = sizeof(DEVMODEW);
            EnumDisplaySettingsW(NULL, ENUM_CURRENT_SETTINGS, &devMode);
            ShowWindow(hwnd, SW_RESTORE);
            SetWindowLongW(hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, devMode.dmPelsWidth, devMode.dmPelsHeight, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
    }
    return m_pReal->SetFullscreenState(FALSE, nullptr);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetFullscreenState(BOOL* pFullscreen, IDXGIOutput** ppTarget) {
    HRESULT hr = m_pReal->GetFullscreenState(pFullscreen, ppTarget);
    if (pFullscreen) *pFullscreen = TRUE;
    return hr;
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetDesc(DXGI_SWAP_CHAIN_DESC* pDesc) {
    return m_pReal->GetDesc(pDesc);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::ResizeBuffers(UINT BufferCount, UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags) {
    Logger::Log("[WrappedDXGISwapChain] ResizeBuffers called (%dx%d)", Width, Height);
    CleanupResources();
    return m_pReal->ResizeBuffers(BufferCount, Width, Height, NewFormat, SwapChainFlags);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::ResizeTarget(const DXGI_MODE_DESC* pNewTargetParameters) {
    return m_pReal->ResizeTarget(pNewTargetParameters);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetContainingOutput(IDXGIOutput** ppOutput) {
    return m_pReal->GetContainingOutput(ppOutput);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetFrameStatistics(DXGI_FRAME_STATISTICS* pStats) {
    return m_pReal->GetFrameStatistics(pStats);
}

HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetLastPresentCount(UINT* pLastPresentCount) {
    return m_pReal->GetLastPresentCount(pLastPresentCount);
}

// --- IDXGISwapChain1 ---
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetDesc1(DXGI_SWAP_CHAIN_DESC1* pDesc) {
    return m_pReal1 ? m_pReal1->GetDesc1(pDesc) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetFullscreenDesc(DXGI_SWAP_CHAIN_FULLSCREEN_DESC* pDesc) {
    return m_pReal1 ? m_pReal1->GetFullscreenDesc(pDesc) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetHwnd(HWND* pHwnd) {
    return m_pReal1 ? m_pReal1->GetHwnd(pHwnd) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetCoreWindow(REFIID refiid, void** ppv) {
    return m_pReal1 ? m_pReal1->GetCoreWindow(refiid, ppv) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::Present1(UINT SyncInterval, UINT PresentFlags, const DXGI_PRESENT_PARAMETERS* pPresentParameters) {
    return Present(SyncInterval, PresentFlags);
}
BOOL STDMETHODCALLTYPE WrappedDXGISwapChain::IsTemporaryMonoSupported() {
    return m_pReal1 ? m_pReal1->IsTemporaryMonoSupported() : FALSE;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetRestrictToOutput(IDXGIOutput** ppRestrictToOutput) {
    return m_pReal1 ? m_pReal1->GetRestrictToOutput(ppRestrictToOutput) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetBackgroundColor(const DXGI_RGBA* pColor) {
    return m_pReal1 ? m_pReal1->SetBackgroundColor(pColor) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetBackgroundColor(DXGI_RGBA* pColor) {
    return m_pReal1 ? m_pReal1->GetBackgroundColor(pColor) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetRotation(DXGI_MODE_ROTATION Rotation) {
    return m_pReal1 ? m_pReal1->SetRotation(Rotation) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetRotation(DXGI_MODE_ROTATION* pRotation) {
    return m_pReal1 ? m_pReal1->GetRotation(pRotation) : E_FAIL;
}

// --- IDXGISwapChain2 ---
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetSourceSize(UINT Width, UINT Height) {
    return m_pReal2 ? m_pReal2->SetSourceSize(Width, Height) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetSourceSize(UINT* pWidth, UINT* pHeight) {
    return m_pReal2 ? m_pReal2->GetSourceSize(pWidth, pHeight) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetMaximumFrameLatency(UINT MaxLatency) {
    return m_pReal2 ? m_pReal2->SetMaximumFrameLatency(MaxLatency) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetMaximumFrameLatency(UINT* pMaxLatency) {
    return m_pReal2 ? m_pReal2->GetMaximumFrameLatency(pMaxLatency) : E_FAIL;
}
HANDLE STDMETHODCALLTYPE WrappedDXGISwapChain::GetFrameLatencyWaitableObject() {
    return m_pReal2 ? m_pReal2->GetFrameLatencyWaitableObject() : nullptr;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetMatrixTransform(const DXGI_MATRIX_3X2_F* pMatrix) {
    return m_pReal2 ? m_pReal2->SetMatrixTransform(pMatrix) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::GetMatrixTransform(DXGI_MATRIX_3X2_F* pMatrix) {
    return m_pReal2 ? m_pReal2->GetMatrixTransform(pMatrix) : E_FAIL;
}

// --- IDXGISwapChain3 ---
UINT STDMETHODCALLTYPE WrappedDXGISwapChain::GetCurrentBackBufferIndex() {
    return m_pReal3 ? m_pReal3->GetCurrentBackBufferIndex() : 0;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::CheckColorSpaceSupport(DXGI_COLOR_SPACE_TYPE ColorSpace, UINT* pColorSpaceSupport) {
    return m_pReal3 ? m_pReal3->CheckColorSpaceSupport(ColorSpace, pColorSpaceSupport) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetColorSpace1(DXGI_COLOR_SPACE_TYPE ColorSpace) {
    return m_pReal3 ? m_pReal3->SetColorSpace1(ColorSpace) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::ResizeBuffers1(UINT BufferCount, UINT Width, UINT Height, DXGI_FORMAT Format, UINT SwapChainFlags, const UINT* pCreationNodeMask, IUnknown* const* ppPresentQueue) {
    CleanupResources();
    return m_pReal3 ? m_pReal3->ResizeBuffers1(BufferCount, Width, Height, Format, SwapChainFlags, pCreationNodeMask, ppPresentQueue) : E_FAIL;
}

// --- IDXGISwapChain4 ---
HRESULT STDMETHODCALLTYPE WrappedDXGISwapChain::SetHDRMetaData(DXGI_HDR_METADATA_TYPE Type, UINT Size, void* pMetaData) {
    return m_pReal4 ? m_pReal4->SetHDRMetaData(Type, Size, pMetaData) : E_FAIL;
}
