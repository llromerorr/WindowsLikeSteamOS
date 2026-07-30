#include "D3D12Hooks.h"
#include "Hooking.h"
#include "Logger.h"
#include "ResolutionSpoofer.h"
#include "ShaderPipeline.h"
#include "IPCReader.h"
#include "OverlayOSD.h"

#include <dxgi1_4.h>
#include <d3d12.h>
#include <unordered_map>
#include <mutex>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

inline D3D12_RESOURCE_BARRIER TransitionBarrier(ID3D12Resource* pResource, D3D12_RESOURCE_STATES stateBefore, D3D12_RESOURCE_STATES stateAfter) {
    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
    barrier.Transition.pResource = pResource;
    barrier.Transition.StateBefore = stateBefore;
    barrier.Transition.StateAfter = stateAfter;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    return barrier;
}

namespace D3D12Hooks {

    using ExecuteCommandLists_t = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
    using CreateSwapChainForHwnd_t = HRESULT(STDMETHODCALLTYPE*)(
        IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
    using Present1_t = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

    ExecuteCommandLists_t     oExecuteCommandLists     = nullptr;
    CreateSwapChainForHwnd_t  oCreateSwapChainForHwnd  = nullptr;
    Present1_t                oPresent1                = nullptr;

    std::unordered_map<IDXGISwapChain*, ID3D12CommandQueue*> g_SwapChainToQueue;
    std::mutex g_QueueMapMutex;

    struct D3D12FrameContext {
        ID3D12Device*              pDevice            = nullptr;
        ID3D12DescriptorHeap*      pRTVHeap           = nullptr;
        UINT                       rtvDescriptorSize  = 0;
        ID3D12CommandAllocator*    pCommandAllocators[4] = {nullptr};
        ID3D12GraphicsCommandList* pCommandList       = nullptr;
        ID3D12Fence*               pFence             = nullptr;
        UINT64                     fenceValues[4]     = {0};
        HANDLE                     hFenceEvent        = nullptr;
        UINT                       bufferCount        = 0;
        bool                       ready              = false;
    };

    std::unordered_map<IDXGISwapChain*, D3D12FrameContext> g_Contexts;
    std::mutex g_ContextMapMutex;

    void STDMETHODCALLTYPE hkExecuteCommandLists(
        ID3D12CommandQueue* pQueue, UINT NumCommandLists, ID3D12CommandList* const* ppCommandLists) {
        oExecuteCommandLists(pQueue, NumCommandLists, ppCommandLists);
    }

    HRESULT STDMETHODCALLTYPE hkCreateSwapChainForHwnd(
        IDXGIFactory2* pFactory, IUnknown* pDevice, HWND hWnd,
        const DXGI_SWAP_CHAIN_DESC1* pDesc,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* pFullscreenDesc,
        IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain) {

        ID3D12CommandQueue* pRealQueue = nullptr;
        bool isDx12 = SUCCEEDED(pDevice->QueryInterface(IID_PPV_ARGS(&pRealQueue)));

        HRESULT hr = oCreateSwapChainForHwnd(
            pFactory, pDevice, hWnd, pDesc, pFullscreenDesc, pRestrictToOutput, ppSwapChain);

        if (SUCCEEDED(hr) && isDx12 && ppSwapChain && *ppSwapChain) {
            std::lock_guard<std::mutex> lock(g_QueueMapMutex);
            g_SwapChainToQueue[*ppSwapChain] = pRealQueue;

            Logger::Log("[D3D12] SwapChain %p vinculado a CommandQueue real %p (HWND=%p)",
                *ppSwapChain, pRealQueue, hWnd);
        }
        else if (pRealQueue) {
            pRealQueue->Release();
        }

        return hr;
    }

    HRESULT STDMETHODCALLTYPE hkPresent1(
        IDXGISwapChain1* pSwapChain, UINT SyncInterval, UINT Flags,
        const DXGI_PRESENT_PARAMETERS* pPresentParameters) {

        EffectParams dummy;
        // In hkPresent1 we might not have the params ready unless we read them here,
        // but normally DXGIHooks hkPresent handles it. For now pass a dummy or read it.
        // But actually hkPresent1 is hooked here, so we must read.
        IPCReader::ReadParams(dummy);

        OnPreDx12Present(pSwapChain, dummy);
        return oPresent1(pSwapChain, SyncInterval, Flags, pPresentParameters);
    }

    bool EnsureContextReady(IDXGISwapChain* pSwapChain, D3D12FrameContext& ctx) {
        if (ctx.ready) return true;

        if (FAILED(pSwapChain->GetDevice(IID_PPV_ARGS(&ctx.pDevice)))) {
            return false;
        }

        DXGI_SWAP_CHAIN_DESC scDesc;
        pSwapChain->GetDesc(&scDesc);
        ctx.bufferCount = scDesc.BufferCount;

        D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc = {};
        rtvHeapDesc.NumDescriptors = ctx.bufferCount;
        rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rtvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

        if (FAILED(ctx.pDevice->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&ctx.pRTVHeap)))) {
            Logger::Log("[D3D12] Fallo creando RTV Heap");
            return false;
        }
        ctx.rtvDescriptorSize = ctx.pDevice->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

        D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = ctx.pRTVHeap->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < ctx.bufferCount; ++i) {
            ID3D12Resource* pBackBuffer = nullptr;
            pSwapChain->GetBuffer(i, IID_PPV_ARGS(&pBackBuffer));
            ctx.pDevice->CreateRenderTargetView(pBackBuffer, nullptr, rtvHandle);
            pBackBuffer->Release();
            rtvHandle.ptr += ctx.rtvDescriptorSize;
        }

        for (UINT i = 0; i < ctx.bufferCount; ++i) {
            if (FAILED(ctx.pDevice->CreateCommandAllocator(
                D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&ctx.pCommandAllocators[i])))) {
                Logger::Log("[D3D12] Fallo creando CommandAllocator");
                return false;
            }
        }

        if (FAILED(ctx.pDevice->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_DIRECT, ctx.pCommandAllocators[0],
            nullptr, IID_PPV_ARGS(&ctx.pCommandList)))) {
            Logger::Log("[D3D12] Fallo creando CommandList");
            return false;
        }
        ctx.pCommandList->Close();

        ctx.pDevice->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&ctx.pFence));
        for (UINT i = 0; i < 4; ++i) ctx.fenceValues[i] = 0;
        ctx.hFenceEvent = CreateEvent(nullptr, FALSE, FALSE, nullptr);

        ResolutionSpoofer::InstallOn(scDesc.OutputWindow);
        ShaderPipelineDX12::Initialize(ctx.pDevice, scDesc.BufferDesc.Format);

        ctx.ready = true;
        Logger::Log("[D3D12] Contexto de render inicializado para SwapChain %p (%u buffers)",
            pSwapChain, ctx.bufferCount);
        return true;
    }

    bool OnPreDx12Present(IDXGISwapChain* pSwapChain, const EffectParams& params) {
        ID3D12CommandQueue* pQueue = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_QueueMapMutex);
            auto it = g_SwapChainToQueue.find(pSwapChain);
            if (it == g_SwapChainToQueue.end()) return false;
            pQueue = it->second;
        }

        D3D12FrameContext* ctx = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_ContextMapMutex);
            ctx = &g_Contexts[pSwapChain];
        }

        if (!EnsureContextReady(pSwapChain, *ctx)) return false;

        DXGI_SWAP_CHAIN_DESC scDesc;
        pSwapChain->GetDesc(&scDesc);
        OverlayOSD::InitializeCommon(scDesc.OutputWindow);
        OverlayOSD::DX12::Initialize(ctx->pDevice, ctx->bufferCount, scDesc.BufferDesc.Format);

        IDXGISwapChain3* pSwapChain3 = nullptr;
        if (FAILED(pSwapChain->QueryInterface(IID_PPV_ARGS(&pSwapChain3)))) {
            return false;
        }
        UINT backBufferIndex = pSwapChain3->GetCurrentBackBufferIndex();
        pSwapChain3->Release();

        ID3D12Resource* pBackBuffer = nullptr;
        pSwapChain->GetBuffer(backBufferIndex, IID_PPV_ARGS(&pBackBuffer));

        D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = ctx->pRTVHeap->GetCPUDescriptorHandleForHeapStart();
        rtvHandle.ptr += backBufferIndex * ctx->rtvDescriptorSize;

        const UINT64 fenceToWait = ctx->fenceValues[backBufferIndex];
        if (fenceToWait != 0 && ctx->pFence->GetCompletedValue() < fenceToWait) {
            ctx->pFence->SetEventOnCompletion(fenceToWait, ctx->hFenceEvent);
            WaitForSingleObject(ctx->hFenceEvent, INFINITE);
        }

        ID3D12CommandAllocator* pCurrentAllocator = ctx->pCommandAllocators[backBufferIndex];
        pCurrentAllocator->Reset();
        ctx->pCommandList->Reset(pCurrentAllocator, nullptr);

        bool needsPostProcess = params.enablePostProcess && params.enableCRT;

        if (needsPostProcess) {
            ShaderPipelineDX12::Render(ctx->pDevice, ctx->pCommandList, pBackBuffer,
                rtvHandle, scDesc.BufferDesc.Width, scDesc.BufferDesc.Height, params);
        } else {
            D3D12_RESOURCE_BARRIER toRT = TransitionBarrier(pBackBuffer,
                D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
            ctx->pCommandList->ResourceBarrier(1, &toRT);
            ctx->pCommandList->OMSetRenderTargets(1, &rtvHandle, FALSE, nullptr);
        }

        OverlayOSD::DX12::Render(ctx->pCommandList, rtvHandle);

        D3D12_RESOURCE_BARRIER toPresent = TransitionBarrier(pBackBuffer,
            D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
        ctx->pCommandList->ResourceBarrier(1, &toPresent);

        ctx->pCommandList->Close();

        ID3D12CommandList* lists[] = { ctx->pCommandList };
        pQueue->ExecuteCommandLists(1, lists);

        static UINT64 globalFenceCounter = 1;
        const UINT64 newFenceValue = globalFenceCounter++;
        pQueue->Signal(ctx->pFence, newFenceValue);
        ctx->fenceValues[backBufferIndex] = newFenceValue;

        pBackBuffer->Release();
        return true;
    }

    void OnResizeBuffers(IDXGISwapChain* pSwapChain) {
        std::lock_guard<std::mutex> lock(g_ContextMapMutex);
        auto it = g_Contexts.find(pSwapChain);
        if (it != g_Contexts.end()) {
            D3D12FrameContext& ctx = it->second;
            if (ctx.ready) {
                // Wait for GPU to finish
                if (ctx.pFence && ctx.hFenceEvent) {
                    ID3D12CommandQueue* pQueue = nullptr;
                    {
                        std::lock_guard<std::mutex> qlock(g_QueueMapMutex);
                        auto qit = g_SwapChainToQueue.find(pSwapChain);
                        if (qit != g_SwapChainToQueue.end()) {
                            pQueue = qit->second;
                            UINT64 maxFence = 0;
                            for (UINT i = 0; i < 4; ++i) {
                                if (ctx.fenceValues[i] > maxFence) maxFence = ctx.fenceValues[i];
                            }
                            if (maxFence > 0) {
                                pQueue->Signal(ctx.pFence, maxFence + 1);
                                if (ctx.pFence->GetCompletedValue() <= maxFence) {
                                    ctx.pFence->SetEventOnCompletion(maxFence + 1, ctx.hFenceEvent);
                                    WaitForSingleObject(ctx.hFenceEvent, INFINITE);
                                }
                            }
                        }
                    }
                }
                
                OverlayOSD::DX12::Shutdown();
                ShaderPipelineDX12::Shutdown();

                if (ctx.pCommandList) { ctx.pCommandList->Release(); ctx.pCommandList = nullptr; }
                for (UINT i = 0; i < 4; ++i) {
                    if (ctx.pCommandAllocators[i]) {
                        ctx.pCommandAllocators[i]->Release();
                        ctx.pCommandAllocators[i] = nullptr;
                    }
                }
                if (ctx.pRTVHeap) { ctx.pRTVHeap->Release(); ctx.pRTVHeap = nullptr; }
                if (ctx.pFence) { ctx.pFence->Release(); ctx.pFence = nullptr; }
                if (ctx.hFenceEvent) { CloseHandle(ctx.hFenceEvent); ctx.hFenceEvent = nullptr; }
                if (ctx.pDevice) { ctx.pDevice->Release(); ctx.pDevice = nullptr; }

                ctx.ready = false;
                Logger::Log("[D3D12] Contexto de render liberado por ResizeBuffers");
            }
        }
    }

    bool Initialize() {
        WNDCLASSEXW wc = { sizeof(WNDCLASSEXW), CS_CLASSDC, DefWindowProcW,
            0L, 0L, GetModuleHandle(nullptr), nullptr, nullptr, nullptr, nullptr,
            L"SteamOSHooksDx12Dummy", nullptr };
        RegisterClassExW(&wc);
        HWND hDummyWnd = CreateWindowW(wc.lpszClassName, L"Dummy12", WS_OVERLAPPEDWINDOW,
            0, 0, 100, 100, nullptr, nullptr, wc.hInstance, nullptr);

        ID3D12Device* pDummyDevice = nullptr;
        if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&pDummyDevice)))) {
            Logger::Log("[D3D12] No se pudo crear dummy device (¿el juego no usa DX12?). Abortando módulo D3D12.");
            DestroyWindow(hDummyWnd);
            return false;
        }

        D3D12_COMMAND_QUEUE_DESC queueDesc = {};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        ID3D12CommandQueue* pDummyQueue = nullptr;
        pDummyDevice->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&pDummyQueue));

        IDXGIFactory2* pDummyFactory = nullptr;
        CreateDXGIFactory1(IID_PPV_ARGS(&pDummyFactory));

        DXGI_SWAP_CHAIN_DESC1 scDesc = {};
        scDesc.BufferCount = 2;
        scDesc.Width = 100;
        scDesc.Height = 100;
        scDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scDesc.SampleDesc.Count = 1;

        IDXGISwapChain1* pDummySwapChain = nullptr;
        HRESULT hr = pDummyFactory->CreateSwapChainForHwnd(
            pDummyQueue, hDummyWnd, &scDesc, nullptr, nullptr, &pDummySwapChain);

        if (FAILED(hr)) {
            Logger::Log("[D3D12] Fallo creando dummy swapchain: 0x%08X", hr);
            pDummyQueue->Release();
            pDummyDevice->Release();
            pDummyFactory->Release();
            DestroyWindow(hDummyWnd);
            return false;
        }

        void** queueVtable   = *reinterpret_cast<void***>(pDummyQueue);
        void** factoryVtable = *reinterpret_cast<void***>(pDummyFactory);
        void** swapVtable    = *reinterpret_cast<void***>(pDummySwapChain);

        bool ok = true;
        ok &= Hooking::CreateHook(queueVtable[10], // VT_EXECUTECOMMANDLISTS
            &hkExecuteCommandLists, &oExecuteCommandLists);
        ok &= Hooking::CreateHook(factoryVtable[15], // VT_CREATESWAPCHAINFORHWND
            &hkCreateSwapChainForHwnd, &oCreateSwapChainForHwnd);
        ok &= Hooking::CreateHook(swapVtable[22], // VT_PRESENT1
            &hkPresent1, &oPresent1);

        pDummySwapChain->Release();
        pDummyQueue->Release();
        pDummyDevice->Release();
        pDummyFactory->Release();
        DestroyWindow(hDummyWnd);
        UnregisterClassW(wc.lpszClassName, wc.hInstance);

        Logger::Log("[D3D12] Módulo inicializado correctamente: %s", ok ? "OK" : "CON ERRORES");
        return ok;
    }
}
