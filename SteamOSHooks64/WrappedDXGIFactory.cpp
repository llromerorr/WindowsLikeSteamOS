#include "WrappedDXGIFactory.h"
#include "WrappedDXGISwapChain.h"
#include "Logger.h"

WrappedDXGIFactory::WrappedDXGIFactory(IDXGIFactory* pReal) : m_pReal(pReal) {
    if (m_pReal) {
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal1));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal2));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal3));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal4));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal5));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal6));
        m_pReal->QueryInterface(IID_PPV_ARGS(&m_pReal7));
    }
    Logger::Log("[WrappedDXGIFactory] Created wrapper for real factory %p", m_pReal);
}

WrappedDXGIFactory::~WrappedDXGIFactory() {
    if (m_pReal7) m_pReal7->Release();
    if (m_pReal6) m_pReal6->Release();
    if (m_pReal5) m_pReal5->Release();
    if (m_pReal4) m_pReal4->Release();
    if (m_pReal3) m_pReal3->Release();
    if (m_pReal2) m_pReal2->Release();
    if (m_pReal1) m_pReal1->Release();
    if (m_pReal)  m_pReal->Release();
    Logger::Log("[WrappedDXGIFactory] Destroyed wrapper for real factory %p", m_pReal);
}

// --- IUnknown ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::QueryInterface(REFIID riid, void** ppvObject) {
    if (!ppvObject) return E_POINTER;

    if (riid == __uuidof(IUnknown) ||
        riid == __uuidof(IDXGIObject) ||
        riid == __uuidof(IDXGIFactory)) {
        *ppvObject = static_cast<IDXGIFactory*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGIFactory1) && m_pReal1) {
        *ppvObject = static_cast<IDXGIFactory1*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGIFactory2) && m_pReal2) {
        *ppvObject = static_cast<IDXGIFactory2*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGIFactory3) && m_pReal3) {
        *ppvObject = static_cast<IDXGIFactory3*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGIFactory4) && m_pReal4) {
        *ppvObject = static_cast<IDXGIFactory4*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGIFactory5) && m_pReal5) {
        *ppvObject = static_cast<IDXGIFactory5*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGIFactory6) && m_pReal6) {
        *ppvObject = static_cast<IDXGIFactory6*>(this);
        AddRef();
        return S_OK;
    }
    if (riid == __uuidof(IDXGIFactory7) && m_pReal7) {
        *ppvObject = static_cast<IDXGIFactory7*>(this);
        AddRef();
        return S_OK;
    }

    return m_pReal->QueryInterface(riid, ppvObject);
}

ULONG STDMETHODCALLTYPE WrappedDXGIFactory::AddRef() {
    return ++m_refCount;
}

ULONG STDMETHODCALLTYPE WrappedDXGIFactory::Release() {
    ULONG res = --m_refCount;
    if (res == 0) {
        delete this;
    }
    return res;
}

// --- IDXGIObject ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::SetPrivateData(REFGUID Name, UINT DataSize, const void* pData) {
    return m_pReal->SetPrivateData(Name, DataSize, pData);
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::SetPrivateDataInterface(REFGUID Name, const IUnknown* pUnknown) {
    return m_pReal->SetPrivateDataInterface(Name, pUnknown);
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::GetPrivateData(REFGUID Name, UINT* pDataSize, void* pData) {
    return m_pReal->GetPrivateData(Name, pDataSize, pData);
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::GetParent(REFIID riid, void** ppParent) {
    return m_pReal->GetParent(riid, ppParent);
}

// --- IDXGIFactory ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::EnumAdapters(UINT Adapter, IDXGIAdapter** ppAdapter) {
    return m_pReal->EnumAdapters(Adapter, ppAdapter);
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::MakeWindowAssociation(HWND WindowHandle, UINT Flags) {
    return m_pReal->MakeWindowAssociation(WindowHandle, Flags);
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::GetWindowAssociation(HWND* pWindowHandle) {
    return m_pReal->GetWindowAssociation(pWindowHandle);
}

HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::CreateSwapChain(IUnknown* pDevice, DXGI_SWAP_CHAIN_DESC* pDesc, IDXGISwapChain** ppSwapChain) {
    if (!ppSwapChain) return E_POINTER;

    IDXGISwapChain* pRealSwapChain = nullptr;
    HRESULT hr = m_pReal->CreateSwapChain(pDevice, pDesc, &pRealSwapChain);
    if (SUCCEEDED(hr) && pRealSwapChain) {
        WrappedDXGISwapChain* pWrapped = new WrappedDXGISwapChain(pRealSwapChain);
        *ppSwapChain = pWrapped;
        Logger::Log("[WrappedDXGIFactory] Intercepted CreateSwapChain. Wrapped swapchain returned to game.");
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::CreateSoftwareAdapter(HMODULE Module, IDXGIAdapter** ppAdapter) {
    return m_pReal->CreateSoftwareAdapter(Module, ppAdapter);
}

// --- IDXGIFactory1 ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::EnumAdapters1(UINT Adapter, IDXGIAdapter1** ppAdapter) {
    return m_pReal1 ? m_pReal1->EnumAdapters1(Adapter, ppAdapter) : E_FAIL;
}
BOOL STDMETHODCALLTYPE WrappedDXGIFactory::IsCurrent() {
    return m_pReal1 ? m_pReal1->IsCurrent() : TRUE;
}

// --- IDXGIFactory2 ---
BOOL STDMETHODCALLTYPE WrappedDXGIFactory::IsWindowedStereoEnabled() {
    return m_pReal2 ? m_pReal2->IsWindowedStereoEnabled() : FALSE;
}

HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::CreateSwapChainForHwnd(IUnknown* pDevice, HWND hWnd, const DXGI_SWAP_CHAIN_DESC1* pDesc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* pFullscreenDesc, IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain) {
    if (!ppSwapChain) return E_POINTER;
    if (!m_pReal2) return E_FAIL;

    IDXGISwapChain1* pRealSwapChain1 = nullptr;
    HRESULT hr = m_pReal2->CreateSwapChainForHwnd(pDevice, hWnd, pDesc, pFullscreenDesc, pRestrictToOutput, &pRealSwapChain1);
    if (SUCCEEDED(hr) && pRealSwapChain1) {
        WrappedDXGISwapChain* pWrapped = new WrappedDXGISwapChain(pRealSwapChain1);
        *ppSwapChain = pWrapped;
        Logger::Log("[WrappedDXGIFactory] Intercepted CreateSwapChainForHwnd. Wrapped swapchain returned to game.");
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::CreateSwapChainForCoreWindow(IUnknown* pDevice, IUnknown* pWindow, const DXGI_SWAP_CHAIN_DESC1* pDesc, IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain) {
    if (!ppSwapChain) return E_POINTER;
    if (!m_pReal2) return E_FAIL;

    IDXGISwapChain1* pRealSwapChain1 = nullptr;
    HRESULT hr = m_pReal2->CreateSwapChainForCoreWindow(pDevice, pWindow, pDesc, pRestrictToOutput, &pRealSwapChain1);
    if (SUCCEEDED(hr) && pRealSwapChain1) {
        WrappedDXGISwapChain* pWrapped = new WrappedDXGISwapChain(pRealSwapChain1);
        *ppSwapChain = pWrapped;
        Logger::Log("[WrappedDXGIFactory] Intercepted CreateSwapChainForCoreWindow.");
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::GetSharedResourceAdapterLuid(HANDLE hResource, LUID* pLuid) {
    return m_pReal2 ? m_pReal2->GetSharedResourceAdapterLuid(hResource, pLuid) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::RegisterStereoStatusWindow(HWND WindowHandle, UINT wMsg, DWORD* pdwCookie) {
    return m_pReal2 ? m_pReal2->RegisterStereoStatusWindow(WindowHandle, wMsg, pdwCookie) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::RegisterStereoStatusEvent(HANDLE hEvent, DWORD* pdwCookie) {
    return m_pReal2 ? m_pReal2->RegisterStereoStatusEvent(hEvent, pdwCookie) : E_FAIL;
}
void STDMETHODCALLTYPE WrappedDXGIFactory::UnregisterStereoStatus(DWORD dwCookie) {
    if (m_pReal2) m_pReal2->UnregisterStereoStatus(dwCookie);
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::RegisterOcclusionStatusWindow(HWND WindowHandle, UINT wMsg, DWORD* pdwCookie) {
    return m_pReal2 ? m_pReal2->RegisterOcclusionStatusWindow(WindowHandle, wMsg, pdwCookie) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::RegisterOcclusionStatusEvent(HANDLE hEvent, DWORD* pdwCookie) {
    return m_pReal2 ? m_pReal2->RegisterOcclusionStatusEvent(hEvent, pdwCookie) : E_FAIL;
}
void STDMETHODCALLTYPE WrappedDXGIFactory::UnregisterOcclusionStatus(DWORD dwCookie) {
    if (m_pReal2) m_pReal2->UnregisterOcclusionStatus(dwCookie);
}

HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::CreateSwapChainForComposition(IUnknown* pDevice, const DXGI_SWAP_CHAIN_DESC1* pDesc, IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain) {
    if (!ppSwapChain) return E_POINTER;
    if (!m_pReal2) return E_FAIL;

    IDXGISwapChain1* pRealSwapChain1 = nullptr;
    HRESULT hr = m_pReal2->CreateSwapChainForComposition(pDevice, pDesc, pRestrictToOutput, &pRealSwapChain1);
    if (SUCCEEDED(hr) && pRealSwapChain1) {
        WrappedDXGISwapChain* pWrapped = new WrappedDXGISwapChain(pRealSwapChain1);
        *ppSwapChain = pWrapped;
        Logger::Log("[WrappedDXGIFactory] Intercepted CreateSwapChainForComposition.");
    }
    return hr;
}

// --- IDXGIFactory3 ---
UINT STDMETHODCALLTYPE WrappedDXGIFactory::GetCreationFlags() {
    return m_pReal3 ? m_pReal3->GetCreationFlags() : 0;
}

// --- IDXGIFactory4 ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::EnumAdapterByLuid(LUID AdapterLuid, REFIID riid, void** ppvAdapter) {
    return m_pReal4 ? m_pReal4->EnumAdapterByLuid(AdapterLuid, riid, ppvAdapter) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::EnumWarpAdapter(REFIID riid, void** ppvAdapter) {
    return m_pReal4 ? m_pReal4->EnumWarpAdapter(riid, ppvAdapter) : E_FAIL;
}

// --- IDXGIFactory5 ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::CheckFeatureSupport(DXGI_FEATURE Feature, void* pFeatureSupportData, UINT FeatureSupportDataSize) {
    return m_pReal5 ? m_pReal5->CheckFeatureSupport(Feature, pFeatureSupportData, FeatureSupportDataSize) : E_FAIL;
}

// --- IDXGIFactory6 ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::EnumAdapterByGpuPreference(UINT Adapter, DXGI_GPU_PREFERENCE GpuPreference, REFIID riid, void** ppvAdapter) {
    return m_pReal6 ? m_pReal6->EnumAdapterByGpuPreference(Adapter, GpuPreference, riid, ppvAdapter) : E_FAIL;
}

// --- IDXGIFactory7 ---
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::RegisterAdaptersChangedEvent(HANDLE hEvent, DWORD* pdwCookie) {
    return m_pReal7 ? m_pReal7->RegisterAdaptersChangedEvent(hEvent, pdwCookie) : E_FAIL;
}
HRESULT STDMETHODCALLTYPE WrappedDXGIFactory::UnregisterAdaptersChangedEvent(DWORD dwCookie) {
    return m_pReal7 ? m_pReal7->UnregisterAdaptersChangedEvent(dwCookie) : E_FAIL;
}
