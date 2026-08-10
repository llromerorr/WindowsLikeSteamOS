// addon_main.cpp
//
// Add-on de ReShade para WindowsLikeSteamOS.
// Reemplaza la capa de hooking manual (MinHook sobre Present/SetFullscreenState)
// por la infraestructura de hooking ya madura de ReShade, y agrega:
//   - Un panel de Quick Access Menu dibujado con ImGui DENTRO del framebuffer
//     del juego (mismo mecanismo que usa el overlay nativo de ReShade/Steam).
//   - Control de efectos (FSR, CRT, etc.) vía las técnicas de ReShade.
//   - IPC lock-free hacia el host en C#.
//
// DECISIONES DE ROBUSTEZ (documentadas inline donde aplican):
//   - DllMain NO hace I/O bloqueante. Windows toma el "loader lock" durante
//     DLL_PROCESS_ATTACH; llamar OpenFileMapping/MapViewOfFile ahí puede
//     colgar el proceso del juego en escenarios raros de carga concurrente
//     de DLLs. Todo el intento de conexión IPC se difiere al primer Present.
//   - Reintento con backoff (1s) en vez de reintentar cada frame: evita gastar
//     ciclos si el juego arrancó antes que el host, sin busy-looping.
//   - Filosofía "bypass primero": si no hay IPC conectado, on_present()
//     retorna casi inmediatamente. Cero overhead cuando no hay host.
//   - Heartbeat: si el host deja de latir por 2s, se asume muerto/colgado y
//     el addon fuerza un estado seguro (overlay oculto, efectos apagados)
//     en vez de confiar en memoria compartida potencialmente stale.
//   - Nunca hacer panic/throw que cruce el límite de la DLL hacia el juego:
//     un except no capturado acá puede tumbar el juego entero.

#include <reshade.hpp>
#include <imgui.h>
#include <windows.h>
#include <string>

#include "ipc_protocol.h"

using namespace wls;

namespace
{
    HANDLE    g_mapping = nullptr;
    IpcState *g_ipc = nullptr;

    bool     g_attach_attempted = false;
    ULONGLONG g_last_attach_attempt_ms = 0;

    // Copia local que SÍ es segura de tocar desde ImGui (el usuario puede
    // arrastrar un slider a mitad de un frame). Es la "fuente de verdad" del
    // lado del addon; separada del struct compartido a propósito.
    IpcState g_snapshot{};

    // Último estado efectivamente aplicado a las técnicas de ReShade, para
    // no llamar a la API de efectos todos los frames si nada cambió.
    IpcState g_applied{};

    uint64_t  g_last_seen_heartbeat = 0;
    ULONGLONG g_last_seen_heartbeat_time = 0;

    bool try_attach_ipc()
    {
        const DWORD pid = GetCurrentProcessId();
        const std::wstring name = IPC_MAPPING_PREFIX + std::to_wstring(pid);

        g_mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name.c_str());
        if (!g_mapping)
            return false; // el host todavía no creó el mapping, o este proceso no está bajo su control

        void *view = MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(IpcState));
        if (!view)
        {
            CloseHandle(g_mapping);
            g_mapping = nullptr;
            return false;
        }

        auto *candidate = reinterpret_cast<IpcState *>(view);
        if (candidate->protocol_version != IPC_PROTOCOL_VERSION)
        {
            // Versión de protocolo desconocida: mejor no tocar nada que
            // interpretar mal un layout distinto.
            reshade::log_message(reshade::log_level::warning,
                "WLSOS addon: version de protocolo IPC no coincide, quedando en bypass");
            UnmapViewOfFile(view);
            CloseHandle(g_mapping);
            g_mapping = nullptr;
            return false;
        }

        g_ipc = candidate;
        reshade::log_message(reshade::log_level::info, "WLSOS addon: IPC conectado al host");
        return true;
    }

    // Lectura lock-free vía seqlock. Reintenta un número acotado de veces;
    // si sigue "torn" simplemente conserva el último snapshot bueno en vez
    // de bloquear el render thread o leer basura.
    bool read_ipc_snapshot(IpcState &out)
    {
        if (!g_ipc)
            return false;

        for (int attempt = 0; attempt < 4; ++attempt)
        {
            const uint32_t seq1 = g_ipc->seq;
            if (seq1 & 1)
                continue; // escritor a mitad de update, reintentar

            IpcState copy = *g_ipc; // sin punteros internos: copia plana segura

            const uint32_t seq2 = g_ipc->seq;
            if (seq1 == seq2)
            {
                out = copy;
                return true;
            }
        }
        return false;
    }

    bool host_alive()
    {
        const ULONGLONG now = GetTickCount64();
        if (g_snapshot.host_heartbeat != g_last_seen_heartbeat)
        {
            g_last_seen_heartbeat = g_snapshot.host_heartbeat;
            g_last_seen_heartbeat_time = now;
            return true;
        }
        return (now - g_last_seen_heartbeat_time) < 2000; // 2s de gracia
    }

    void apply_effect_state(reshade::api::effect_runtime *runtime)
    {
        // Solo tocar la API de técnicas si algo realmente cambió respecto a
        // lo último aplicado. Llamar set_technique_state todos los frames es
        // trabajo innecesario y, en algunos backends, costoso.
        if (g_snapshot.fsr_enabled != g_applied.fsr_enabled)
        {
            // TODO: runtime->set_technique_state(fsr_technique_handle, g_snapshot.fsr_enabled);
        }
        if (g_snapshot.crt_enabled != g_applied.crt_enabled)
        {
            // TODO: runtime->set_technique_state(crt_technique_handle, g_snapshot.crt_enabled);
        }
        // Los sliders (sharpness/intensity) sí pueden escribirse siempre que
        // cambien; son floats de uniform, no operaciones de pipeline.
        g_applied = g_snapshot;
    }

    void draw_overlay(reshade::api::effect_runtime *)
    {
        if (!g_snapshot.overlay_visible || !host_alive())
            return;

        ImGui::SetNextWindowSize(ImVec2(420, 260), ImGuiCond_FirstUseEver);
        ImGui::Begin("Quick Access Menu", nullptr, ImGuiWindowFlags_NoCollapse);

        ImGui::Checkbox("FSR", &g_snapshot.fsr_enabled);
        ImGui::SliderFloat("Nitidez", &g_snapshot.fsr_sharpness, 0.0f, 1.0f);
        ImGui::Separator();
        ImGui::Checkbox("CRT", &g_snapshot.crt_enabled);
        ImGui::SliderFloat("Intensidad CRT", &g_snapshot.crt_intensity, 0.0f, 1.0f);

        ImGui::End();

        // NOTA DE DISEÑO: estos widgets modifican g_snapshot LOCAL. Por ahora
        // el addon es de solo-lectura respecto al host (el host es el único
        // dueño de la verdad). Si más adelante quieren que el panel in-game
        // pueda escribir de vuelta hacia C#, agreguen un SEGUNDO struct
        // ("addon -> host") con su propio seqlock, en vez de que el addon
        // escriba sobre el struct que ya es propiedad del host. Un solo
        // struct con dos escritores es la forma más segura de introducir
        // una race condition que solo aparece 1 de cada 500 toggles.
    }

    void on_present(reshade::api::effect_runtime *runtime)
    {
        if (!g_ipc)
        {
            const ULONGLONG now = GetTickCount64();
            if (!g_attach_attempted || now - g_last_attach_attempt_ms > 1000)
            {
                g_attach_attempted = true;
                g_last_attach_attempt_ms = now;
                try_attach_ipc();
            }
            return; // bypass total: cero trabajo extra si no hay host conectado
        }

        if (!read_ipc_snapshot(g_snapshot))
        {
            // Lectura torn 4 veces seguidas es señal de un escritor muy
            // agresivo o de un problema real; se sigue con el último
            // snapshot bueno conocido en vez de arriesgar datos corruptos.
        }

        if (!host_alive())
        {
            // Fail-safe, no fail-open: si el host se cayó, apagamos todo en
            // vez de dejar el último estado (que podría ser "overlay abierto
            // para siempre" si el host murió con el panel visible).
            g_snapshot.overlay_visible = false;
            g_snapshot.fsr_enabled = false;
            g_snapshot.crt_enabled = false;
        }

        apply_effect_state(runtime);
    }

    void on_init_device(reshade::api::device *)
    {
        reshade::log_message(reshade::log_level::info, "WLSOS addon: device inicializado");
    }

    void on_destroy_device(reshade::api::device *)
    {
        // Si en el futuro el addon llega a poseer recursos de GPU propios
        // (texturas, buffers), liberarlos ACÁ, no en un destructor global:
        // para cuando el destructor global corra el device ya puede ser
        // inválido (alt-tab, cambio de resolución, D3D device lost).
    }

    void detach_ipc()
    {
        if (g_ipc)
        {
            UnmapViewOfFile(g_ipc);
            g_ipc = nullptr;
        }
        if (g_mapping)
        {
            CloseHandle(g_mapping);
            g_mapping = nullptr;
        }
    }
}

extern "C" __declspec(dllexport) const char *NAME = "WindowsLikeSteamOS QAM";
extern "C" __declspec(dllexport) const char *DESCRIPTION =
    "Panel de acceso rapido y puente de efectos para WindowsLikeSteamOS.";

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        // Importante: NO llamar OpenFileMapping/MapViewOfFile acá. DllMain
        // corre bajo el loader lock del proceso; cualquier I/O que dispare
        // carga de otras DLLs o bloqueo puede colgar el juego. El intento de
        // conexión IPC se hace perezosamente en el primer on_present().
        if (!reshade::register_addon(hModule))
            return FALSE;

        reshade::register_event<reshade::addon_event::init_device>(on_init_device);
        reshade::register_event<reshade::addon_event::destroy_device>(on_destroy_device);
        reshade::register_event<reshade::addon_event::present>(on_present);
        reshade::register_overlay(nullptr, draw_overlay);
        break;

    case DLL_PROCESS_DETACH:
        detach_ipc();
        reshade::unregister_addon(hModule);
        break;
    }
    return TRUE;
}
