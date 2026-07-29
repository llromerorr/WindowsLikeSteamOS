#include "ProxyLoader.h"
#include "Logger.h"
#include <dxgi1_6.h>
#include <string>

namespace ProxyLoader {

    static HMODULE g_hRealModule = nullptr;

    bool Initialize() {
        if (g_hRealModule) return true;

        // Check first if dxgi_chain.dll exists in current process directory (for coexisting with ReShade/DXVK)
        wchar_t exePath[MAX_PATH];
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        wchar_t* lastSlash = wcsrchr(exePath, L'\\');
        if (lastSlash) *lastSlash = L'\0';

        std::wstring chainPath = std::wstring(exePath) + L"\\dxgi_chain.dll";
        if (GetFileAttributesW(chainPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
            g_hRealModule = LoadLibraryW(chainPath.c_str());
            if (g_hRealModule) {
                Logger::Log("[ProxyLoader] Loaded chained dxgi.dll from: %ls", chainPath.c_str());
            }
        }

        // Fallback to system dxgi.dll if chain loading was not used or failed
        if (!g_hRealModule) {
            wchar_t systemDir[MAX_PATH];
            GetSystemDirectoryW(systemDir, MAX_PATH);
            std::wstring systemDxgi = std::wstring(systemDir) + L"\\dxgi.dll";
            g_hRealModule = LoadLibraryW(systemDxgi.c_str());
            if (g_hRealModule) {
                Logger::Log("[ProxyLoader] Loaded system dxgi.dll from: %ls", systemDxgi.c_str());
            } else {
                Logger::Log("[ProxyLoader] FATAL: Failed to load system dxgi.dll from %ls", systemDxgi.c_str());
                return false;
            }
        }

        return true;
    }

    void Shutdown() {
        if (g_hRealModule) {
            FreeLibrary(g_hRealModule);
            g_hRealModule = nullptr;
        }
    }

    HMODULE GetRealModule() {
        return g_hRealModule;
    }

    FARPROC GetRealProcAddress(const char* procName) {
        if (!g_hRealModule) Initialize();
        return g_hRealModule ? GetProcAddress(g_hRealModule, procName) : nullptr;
    }
}

// Exported proxy functions
extern "C" {

HRESULT WINAPI dxgi_proxy_CreateDXGIFactory(REFIID riid, void** ppFactory) {
    if (!ProxyLoader::Initialize()) return E_FAIL;
    auto proc = (HRESULT(WINAPI*)(REFIID, void**))ProxyLoader::GetRealProcAddress("CreateDXGIFactory");
    if (!proc) return E_FAIL;

    IDXGIFactory* pRealFactory = nullptr;
    HRESULT hr = proc(riid, (void**)&pRealFactory);
    if (SUCCEEDED(hr) && pRealFactory) {
        *ppFactory = pRealFactory;
        Logger::Log("[dxgi_proxy] CreateDXGIFactory intercepted successfully (using MinHook fallback).");
    }
    return hr;
}

HRESULT WINAPI dxgi_proxy_CreateDXGIFactory1(REFIID riid, void** ppFactory) {
    if (!ProxyLoader::Initialize()) return E_FAIL;
    auto proc = (HRESULT(WINAPI*)(REFIID, void**))ProxyLoader::GetRealProcAddress("CreateDXGIFactory1");
    if (!proc) return E_FAIL;

    IDXGIFactory1* pRealFactory1 = nullptr;
    HRESULT hr = proc(riid, (void**)&pRealFactory1);
    if (SUCCEEDED(hr) && pRealFactory1) {
        *ppFactory = pRealFactory1;
        Logger::Log("[dxgi_proxy] CreateDXGIFactory1 intercepted successfully (using MinHook fallback).");
    }
    return hr;
}

HRESULT WINAPI dxgi_proxy_CreateDXGIFactory2(UINT Flags, REFIID riid, void** ppFactory) {
    if (!ProxyLoader::Initialize()) return E_FAIL;
    auto proc = (HRESULT(WINAPI*)(UINT, REFIID, void**))ProxyLoader::GetRealProcAddress("CreateDXGIFactory2");
    if (!proc) return E_FAIL;

    IDXGIFactory2* pRealFactory2 = nullptr;
    HRESULT hr = proc(Flags, riid, (void**)&pRealFactory2);
    if (SUCCEEDED(hr) && pRealFactory2) {
        *ppFactory = pRealFactory2;
        Logger::Log("[dxgi_proxy] CreateDXGIFactory2 intercepted successfully (using MinHook fallback).");
    }
    return hr;
}

void WINAPI dxgi_proxy_ApplyCompatResolutionQuirking() {
    auto proc = ProxyLoader::GetRealProcAddress("ApplyCompatResolutionQuirking");
    if (proc) ((void(WINAPI*)())proc)();
}

void WINAPI dxgi_proxy_CompatString() {
    auto proc = ProxyLoader::GetRealProcAddress("CompatString");
    if (proc) ((void(WINAPI*)())proc)();
}

void WINAPI dxgi_proxy_CompatValue() {
    auto proc = ProxyLoader::GetRealProcAddress("CompatValue");
    if (proc) ((void(WINAPI*)())proc)();
}

void WINAPI dxgi_proxy_DXGID3D10CreateDevice() {
    auto proc = ProxyLoader::GetRealProcAddress("DXGID3D10CreateDevice");
    if (proc) ((void(WINAPI*)())proc)();
}

void WINAPI dxgi_proxy_DXGID3D10CreateLayeredDevice() {
    auto proc = ProxyLoader::GetRealProcAddress("DXGID3D10CreateLayeredDevice");
    if (proc) ((void(WINAPI*)())proc)();
}

void WINAPI dxgi_proxy_DXGID3D10GetLayeredDeviceSize() {
    auto proc = ProxyLoader::GetRealProcAddress("DXGID3D10GetLayeredDeviceSize");
    if (proc) ((void(WINAPI*)())proc)();
}

void WINAPI dxgi_proxy_DXGIDeclareAdapterRemovalSupport() {
    auto proc = ProxyLoader::GetRealProcAddress("DXGIDeclareAdapterRemovalSupport");
    if (proc) ((void(WINAPI*)())proc)();
}

void WINAPI dxgi_proxy_DXGIGetDebugInterface1() {
    auto proc = ProxyLoader::GetRealProcAddress("DXGIGetDebugInterface1");
    if (proc) ((void(WINAPI*)())proc)();
}

}
