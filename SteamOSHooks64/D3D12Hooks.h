#pragma once
#include <d3d12.h>
#include <dxgi1_4.h>
#include <windows.h>
#include "IPCLayout.h"

// Módulo dedicado exclusivamente al pipeline moderno DX12.
// Se comunica con DXGIHooks.cpp mediante OnPreDx12Present, que actúa
// como "puente" para reutilizar el hook de Present ya existente sin
// duplicar la lógica de detección D3D11 vs D3D12.
namespace D3D12Hooks {

    // Inicializa el bootstrap: crea device/queue/swapchain dummy,
    // roba las vtables reales y coloca los hooks de MinHook.
    // Debe llamarse UNA VEZ desde InitializeThread() en dllmain.cpp.
    bool Initialize();

    // Punto de entrada llamado desde DXGIHooks::hkPresent (ver parche más abajo).
    // Retorna true si el swapchain pertenece a un dispositivo D3D12 y el
    // frame fue procesado por nuestro pipeline de post-proceso.
    // Retorna false si el swapchain es D3D11/D3D9 (no es responsabilidad
    // de este módulo) y el flujo debe continuar por el camino normal.
    bool OnPreDx12Present(IDXGISwapChain* pSwapChain, const EffectParams& params);

    // Limpia recursos DX12 si se detecta un ResizeBuffers
    void OnResizeBuffers(IDXGISwapChain* pSwapChain);
}
