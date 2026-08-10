// addon_main.cpp
#define NOMINMAX
#include <imgui.h>
#include <reshade.hpp>
#include <windows.h>
#include <string>
#include <atomic>
#include <algorithm>
#include <cmath>

#include "ipc_protocol.h"
#include "overlay_texture_protocol.h"
#include "XInputHooks.h"

using namespace wls;

namespace
{
    HANDLE            g_mapping_h2a = nullptr;
    HostToAddonState *g_h2a = nullptr;

    HANDLE            g_mapping_a2h = nullptr;
    AddonToHostState *g_a2h = nullptr;

    // ── Novedades Fase 3: Textura en Shared Memory ──
    HANDLE                g_mapping_tex = nullptr;
    OverlayTextureHeader *g_tex_header = nullptr;
    reshade::api::resource g_overlay_tex = { 0 };
    reshade::api::resource_view g_overlay_srv = { 0 };

    bool              g_attach_attempted = false;
    ULONGLONG         g_last_attach_attempt_ms = 0;

    HostToAddonState  g_snapshot{};
    HostToAddonState  g_applied{};

    uint64_t          g_last_seen_heartbeat = 0;
    ULONGLONG         g_last_seen_heartbeat_time = 0;

    uint32_t          g_request_epoch = 1;
    uint64_t          g_addon_heartbeat = 0;

    bool              g_debug_f10_overlay = false;
    bool              g_f10_was_down = false;

    // Variables de peticion pendientes han sido eliminadas

    void setup_steamos_theme()
    {
        static bool theme_applied = false;
        if (theme_applied) return;
        theme_applied = true;

        ImGuiStyle& style = ImGui::GetStyle();
        style.WindowRounding = 12.0f;
        style.FrameRounding = 6.0f;
        style.PopupRounding = 8.0f;
        style.GrabRounding = 6.0f;
        style.WindowPadding = ImVec2(16, 16);
        style.ItemSpacing = ImVec2(12, 10);
        style.WindowBorderSize = 1.0f;

        ImVec4* colors = style.Colors;
        colors[ImGuiCol_WindowBg]           = ImVec4(0.08f, 0.09f, 0.12f, 0.94f); // SteamOS Dark
        colors[ImGuiCol_Border]             = ImVec4(0.20f, 0.22f, 0.28f, 0.50f);
        colors[ImGuiCol_FrameBg]            = ImVec4(0.14f, 0.16f, 0.22f, 1.00f);
        colors[ImGuiCol_FrameBgHovered]     = ImVec4(0.20f, 0.24f, 0.32f, 1.00f);
        colors[ImGuiCol_FrameBgActive]      = ImVec4(0.26f, 0.30f, 0.40f, 1.00f);
        colors[ImGuiCol_TitleBg]            = ImVec4(0.08f, 0.09f, 0.12f, 1.00f);
        colors[ImGuiCol_TitleBgActive]      = ImVec4(0.12f, 0.15f, 0.22f, 1.00f);
        colors[ImGuiCol_CheckMark]          = ImVec4(0.12f, 0.58f, 0.95f, 1.00f); // Steam Accent Blue
        colors[ImGuiCol_SliderGrab]         = ImVec4(0.12f, 0.58f, 0.95f, 1.00f);
        colors[ImGuiCol_SliderGrabActive]   = ImVec4(0.24f, 0.68f, 1.00f, 1.00f);
        colors[ImGuiCol_Button]             = ImVec4(0.16f, 0.20f, 0.28f, 1.00f);
        colors[ImGuiCol_ButtonHovered]      = ImVec4(0.22f, 0.28f, 0.38f, 1.00f);
        colors[ImGuiCol_ButtonActive]       = ImVec4(0.12f, 0.58f, 0.95f, 1.00f);
        colors[ImGuiCol_Header]             = ImVec4(0.16f, 0.22f, 0.32f, 1.00f);
        colors[ImGuiCol_HeaderHovered]      = ImVec4(0.22f, 0.30f, 0.42f, 1.00f);
        colors[ImGuiCol_HeaderActive]       = ImVec4(0.12f, 0.58f, 0.95f, 1.00f);
        colors[ImGuiCol_Text]               = ImVec4(0.92f, 0.94f, 0.96f, 1.00f);
        colors[ImGuiCol_TextDisabled]       = ImVec4(0.50f, 0.54f, 0.60f, 1.00f);

        ImGuiIO& io = ImGui::GetIO();
        io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad | ImGuiConfigFlags_NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags_HasGamepad;
    }

    // update_imgui_gamepad_state eliminado por incompatibilidad con el ABI ImGui de ReShade

    bool try_attach_ipc()
    {
        const DWORD pid = GetCurrentProcessId();
        
        const std::wstring name_h2a = IPC_MAPPING_PREFIX_HOST_TO_ADDON + std::to_wstring(pid);
        const std::wstring name_a2h = IPC_MAPPING_PREFIX_ADDON_TO_HOST + std::to_wstring(pid);
        const std::wstring name_tex = OVERLAY_TEX_MMF_PREFIX + std::to_wstring(pid);

        g_mapping_h2a = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name_h2a.c_str());
        g_mapping_a2h = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name_a2h.c_str());
        g_mapping_tex = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name_tex.c_str());

        if (!g_mapping_h2a || !g_mapping_a2h)
        {
            if (g_mapping_h2a) { CloseHandle(g_mapping_h2a); g_mapping_h2a = nullptr; }
            if (g_mapping_a2h) { CloseHandle(g_mapping_a2h); g_mapping_a2h = nullptr; }
            if (g_mapping_tex) { CloseHandle(g_mapping_tex); g_mapping_tex = nullptr; }
            return false;
        }

        void *view_h2a = MapViewOfFile(g_mapping_h2a, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(HostToAddonState));
        void *view_a2h = MapViewOfFile(g_mapping_a2h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(AddonToHostState));
        void *view_tex = g_mapping_tex ? MapViewOfFile(g_mapping_tex, FILE_MAP_ALL_ACCESS, 0, 0, OVERLAY_TEX_MMF_SIZE) : nullptr;

        if (!view_h2a || !view_a2h)
        {
            if (view_h2a) UnmapViewOfFile(view_h2a);
            if (view_a2h) UnmapViewOfFile(view_a2h);
            if (view_tex) UnmapViewOfFile(view_tex);
            CloseHandle(g_mapping_h2a);
            CloseHandle(g_mapping_a2h);
            if (g_mapping_tex) { CloseHandle(g_mapping_tex); g_mapping_tex = nullptr; }
            g_mapping_h2a = nullptr;
            g_mapping_a2h = nullptr;
            return false;
        }

        auto *candidate_h2a = reinterpret_cast<HostToAddonState *>(view_h2a);
        if (candidate_h2a->protocol_version != IPC_PROTOCOL_VERSION)
        {
            reshade::log_message(reshade::log_level::warning, "WLSOS addon: version de protocolo IPC no coincide");
            UnmapViewOfFile(view_h2a);
            UnmapViewOfFile(view_a2h);
            if (view_tex) UnmapViewOfFile(view_tex);
            CloseHandle(g_mapping_h2a);
            CloseHandle(g_mapping_a2h);
            if (g_mapping_tex) { CloseHandle(g_mapping_tex); g_mapping_tex = nullptr; }
            g_mapping_h2a = nullptr;
            g_mapping_a2h = nullptr;
            return false;
        }

        g_h2a = candidate_h2a;
        g_a2h = reinterpret_cast<AddonToHostState *>(view_a2h);
        g_tex_header = reinterpret_cast<OverlayTextureHeader *>(view_tex);
        g_last_seen_heartbeat = 0;
        g_last_seen_heartbeat_time = GetTickCount64();
        
        return true;
    }

    bool read_ipc_snapshot(HostToAddonState& out)
    {
        if (!g_h2a) return false;

        for (int attempt = 0; attempt < IPC_SEQLOCK_MAX_RETRIES; ++attempt)
        {
            const uint32_t seq1 = g_h2a->seq.load(std::memory_order_acquire);
            if (seq1 & 1)
                continue; 

            HostToAddonState copy;
            memcpy(&copy, g_h2a, sizeof(HostToAddonState));

            const uint32_t seq2 = g_h2a->seq.load(std::memory_order_acquire);
            if (seq1 == seq2)
            {
                memcpy(&out, &copy, sizeof(HostToAddonState));
                return true;
            }
        }
        return false;
    }

    bool host_alive()
    {
        if (!g_h2a) return false;

        const ULONGLONG now = GetTickCount64();
        if (g_snapshot.host_heartbeat != g_last_seen_heartbeat)
        {
            g_last_seen_heartbeat = g_snapshot.host_heartbeat;
            g_last_seen_heartbeat_time = now;
            return true;
        }
        return (g_last_seen_heartbeat_time != 0) && ((now - g_last_seen_heartbeat_time) < 2000); 
    }

    void send_heartbeat()
    {
        if (!g_a2h) return;
        g_addon_heartbeat++;
        
        uint32_t seq = g_a2h->seq.load(std::memory_order_relaxed);
        g_a2h->seq.store(seq + 1, std::memory_order_release);
        g_a2h->addon_heartbeat = g_addon_heartbeat;
        g_a2h->seq.store(seq + 2, std::memory_order_release);
    }

    static std::atomic_bool g_reshade_menu_open{ false };

    void draw_addon_settings_inline(reshade::api::effect_runtime *)
    {
        // Cero widgets interactivos, cero llamadas a estado complejo. 100% crash-proof.
        ImGui::TextColored(ImVec4(0.12f, 0.68f, 1.0f, 1.0f), "WindowsLikeSteamOS Add-on Activo");
        ImGui::Text("El IPC esta funcionando.");
        ImGui::Text("Usa el boton Select del mando o la tecla F10 para abrir el Menu de Acceso Rapido (QAM).");
    }

    void ApplySteamOSStyle()
    {
        ImGuiStyle& style = ImGui::GetStyle();
        style.WindowRounding = 12.0f;
        style.ChildRounding = 8.0f;
        style.FrameRounding = 6.0f;
        style.PopupRounding = 8.0f;
        style.ScrollbarRounding = 6.0f;
        style.GrabRounding = 6.0f;
        style.WindowPadding = ImVec2(16.0f, 16.0f);
        style.ItemSpacing = ImVec2(10.0f, 10.0f);

        ImVec4* colors = style.Colors;
        colors[ImGuiCol_WindowBg]          = ImVec4(0.06f, 0.08f, 0.12f, 0.95f);
        colors[ImGuiCol_ChildBg]           = ImVec4(0.09f, 0.12f, 0.18f, 0.85f);
        colors[ImGuiCol_Border]            = ImVec4(0.18f, 0.24f, 0.35f, 0.50f);
        colors[ImGuiCol_FrameBg]           = ImVec4(0.12f, 0.16f, 0.24f, 1.00f);
        colors[ImGuiCol_FrameBgHovered]    = ImVec4(0.16f, 0.22f, 0.32f, 1.00f);
        colors[ImGuiCol_FrameBgActive]     = ImVec4(0.10f, 0.62f, 1.00f, 0.40f);
        colors[ImGuiCol_TitleBg]           = ImVec4(0.06f, 0.08f, 0.12f, 1.00f);
        colors[ImGuiCol_TitleBgActive]     = ImVec4(0.10f, 0.62f, 1.00f, 1.00f);
        colors[ImGuiCol_Button]            = ImVec4(0.14f, 0.19f, 0.28f, 1.00f);
        colors[ImGuiCol_ButtonHovered]     = ImVec4(0.10f, 0.62f, 1.00f, 0.80f);
        colors[ImGuiCol_ButtonActive]      = ImVec4(0.10f, 0.62f, 1.00f, 1.00f);
        colors[ImGuiCol_Header]            = ImVec4(0.10f, 0.62f, 1.00f, 0.45f);
        colors[ImGuiCol_HeaderHovered]     = ImVec4(0.10f, 0.62f, 1.00f, 0.80f);
        colors[ImGuiCol_HeaderActive]      = ImVec4(0.10f, 0.62f, 1.00f, 1.00f);
        colors[ImGuiCol_SliderGrab]        = ImVec4(0.10f, 0.62f, 1.00f, 1.00f);
        colors[ImGuiCol_SliderGrabActive]  = ImVec4(0.30f, 0.72f, 1.00f, 1.00f);
        colors[ImGuiCol_CheckMark]         = ImVec4(0.10f, 0.62f, 1.00f, 1.00f);
        colors[ImGuiCol_Text]              = ImVec4(0.95f, 0.96f, 0.98f, 1.00f);
        colors[ImGuiCol_TextDisabled]      = ImVec4(0.50f, 0.55f, 0.64f, 1.00f);
    }

    void notify_ipc_action()
    {
        if (!g_a2h) return;
        uint32_t seq = g_a2h->seq.load(std::memory_order_relaxed);
        g_a2h->seq.store(seq + 1, std::memory_order_release);
        g_a2h->request_epoch++;
        g_a2h->seq.store(seq + 2, std::memory_order_release);
    }

    void draw_native_qam_overlay(reshade::api::effect_runtime *)
    {
        if (g_reshade_menu_open.load(std::memory_order_relaxed))
            return;

        bool is_alive = host_alive();
        bool should_show = (g_snapshot.overlay_visible != 0 && is_alive) || g_debug_f10_overlay;

        if (!should_show)
            return;

        ApplySteamOSStyle();

        ImGuiIO& io = ImGui::GetIO();
        io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad; // Habilitar mando nativo

        float screen_w = io.DisplaySize.x;
        float screen_h = io.DisplaySize.y;
        
        float panel_w = 340.0f;
        float panel_h = screen_h - 40.0f;
        float pos_x = screen_w - panel_w - 20.0f;
        float pos_y = 20.0f;

        ImGui::SetNextWindowPos(ImVec2(pos_x, pos_y), ImGuiCond_Always);
        ImGui::SetNextWindowSize(ImVec2(panel_w, panel_h), ImGuiCond_Always);

        ImGuiWindowFlags window_flags = ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | 
                                        ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse;

        if (ImGui::Begin("##SteamOS_QAM_Native", nullptr, window_flags))
        {
            // --- ENCABEZADO STEAMOS ---
            ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "STEAM DECK QAM");
            ImGui::SameLine();
            
            // Reloj en tiempo real
            time_t now = time(nullptr);
            tm* ltime = localtime(&now);
            char time_buf[16];
            if (ltime) strftime(time_buf, sizeof(time_buf), "%H:%M", ltime);
            else strcpy(time_buf, "--:--");
            
            float time_width = ImGui::CalcTextSize(time_buf).x;
            ImGui::SetCursorPosX(panel_w - time_width - 20.0f);
            ImGui::TextColored(ImVec4(0.70f, 0.75f, 0.82f, 1.00f), "%s", time_buf);

            ImGui::Separator();
            ImGui::Spacing();

            // --- SISTEMA DE PESTAÑAS ---
            static int s_active_tab = 0;
            
            ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 8.0f);
            if (ImGui::Button("Ajustes", ImVec2(90, 32))) s_active_tab = 0;
            ImGui::SameLine();
            if (ImGui::Button("Rendimiento", ImVec2(100, 32))) s_active_tab = 1;
            ImGui::SameLine();
            if (ImGui::Button("Energia", ImVec2(90, 32))) s_active_tab = 2;
            ImGui::PopStyleVar();

            ImGui::Spacing();
            ImGui::Separator();
            ImGui::Spacing();

            // --- CONTENIDO DE PESTAÑAS ---
            if (s_active_tab == 0)
            {
                // Pestaña: Ajustes de Sistema
                ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "AJUSTES DE AUDIO Y ESCALADO");
                ImGui::Spacing();

                static int s_vol = 80;
                if (ImGui::SliderInt("Volumen", &s_vol, 0, 100, "%d%%"))
                {
                    if (g_a2h) {
                        g_a2h->requested_volume = (uint8_t)s_vol;
                        notify_ipc_action();
                    }
                }

                ImGui::Spacing();
                ImGui::Separator();
                ImGui::Spacing();

                ImGui::Text("Escalado FSR (FidelityFX)");
                static int s_fsr_mode = 0; // 0=OFF, 1=720p, 2=900p
                if (ImGui::RadioButton("OFF", &s_fsr_mode, 0)) {
                    if (g_a2h) { g_a2h->desired_fsr_mode = 0; notify_ipc_action(); }
                }
                ImGui::SameLine();
                if (ImGui::RadioButton("720p (FSR)", &s_fsr_mode, 1)) {
                    if (g_a2h) { g_a2h->desired_fsr_mode = 1; notify_ipc_action(); }
                }
                ImGui::SameLine();
                if (ImGui::RadioButton("900p (FSR)", &s_fsr_mode, 2)) {
                    if (g_a2h) { g_a2h->desired_fsr_mode = 2; notify_ipc_action(); }
                }
            }
            else if (s_active_tab == 1)
            {
                // Pestaña: Rendimiento
                ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "LIMITE DE RENDIMIENTO");
                ImGui::Spacing();

                ImGui::Text("Limite de FPS");
                static int s_fps_cap = 0; // 0=OFF, 30, 45, 60
                if (ImGui::RadioButton("Sin Limite", &s_fps_cap, 0)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 0; notify_ipc_action(); }
                }
                if (ImGui::RadioButton("30 FPS", &s_fps_cap, 30)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 30; notify_ipc_action(); }
                }
                ImGui::SameLine();
                if (ImGui::RadioButton("45 FPS", &s_fps_cap, 45)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 45; notify_ipc_action(); }
                }
                ImGui::SameLine();
                if (ImGui::RadioButton("60 FPS", &s_fps_cap, 60)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 60; notify_ipc_action(); }
                }
            }
            else if (s_active_tab == 2)
            {
                // Pestaña: Energía / Opciones
                ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "OPCIONES DEL SISTEMA");
                ImGui::Spacing();

                ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.20f, 0.25f, 0.35f, 1.00f));
                
                if (ImGui::Button("Suspender Consola", ImVec2(-1, 40))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_SUSPEND; notify_ipc_action(); }
                }
                if (ImGui::Button("Reiniciar Consola", ImVec2(-1, 40))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_RESTART; notify_ipc_action(); }
                }
                if (ImGui::Button("Apagar Consola", ImVec2(-1, 40))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_SHUTDOWN; notify_ipc_action(); }
                }
                
                ImGui::Spacing();
                ImGui::Separator();
                ImGui::Spacing();

                ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.70f, 0.15f, 0.15f, 1.00f)); // Rojo para salir
                if (ImGui::Button("Modo Escritorio (Salir)", ImVec2(-1, 44))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_DESKTOP; notify_ipc_action(); }
                }
                ImGui::PopStyleColor();
                ImGui::PopStyleColor();
            }

            ImGui::End();
        }
    }

    void detach_ipc()
    {
        if (g_h2a) { UnmapViewOfFile(g_h2a); g_h2a = nullptr; }
        if (g_a2h) { UnmapViewOfFile(g_a2h); g_a2h = nullptr; }
        if (g_tex_header) { UnmapViewOfFile(g_tex_header); g_tex_header = nullptr; }
        if (g_mapping_h2a) { CloseHandle(g_mapping_h2a); g_mapping_h2a = nullptr; }
        if (g_mapping_a2h) { CloseHandle(g_mapping_a2h); g_mapping_a2h = nullptr; }
        if (g_mapping_tex) { CloseHandle(g_mapping_tex); g_mapping_tex = nullptr; }
    }

    void on_present(reshade::api::effect_runtime *runtime)
    {
        g_reshade_menu_open.store(false, std::memory_order_relaxed);

        bool was_connected = (g_h2a != nullptr && g_a2h != nullptr);

        if (!was_connected)
        {
            const ULONGLONG now = GetTickCount64();
            if (!g_attach_attempted || now - g_last_attach_attempt_ms > 1000)
            {
                g_attach_attempted = true;
                g_last_attach_attempt_ms = now;
                if (try_attach_ipc()) {
                    reshade::log_message(reshade::log_level::info, "[WLSOS] IPC connected during on_present retry");
                }
            }
        }
        else
        {
            send_heartbeat();
            read_ipc_snapshot(g_snapshot);

            bool is_alive = host_alive();
            if (!is_alive)
            {
                reshade::log_message(reshade::log_level::warning, "[WLSOS Watchdog] Host heartbeat stale (>2000ms). Disconnecting IPC, closing overlay, disabling input mask.");
                detach_ipc();
            }
            else if (g_tex_header && g_overlay_tex.handle != 0)
            {
                // Leer textura si hubo un cambio (usando seqlock)
                uint32_t seq1 = g_tex_header->seq.load(std::memory_order_acquire);
                if ((seq1 & 1) == 0) // Si no está siendo escrita
                {
                    static uint32_t s_last_tex_seq = 0;
                    if (seq1 != s_last_tex_seq)
                    {
                        uint32_t width = g_tex_header->width;
                        uint32_t height = g_tex_header->height;
                        if (width > 0 && width <= OVERLAY_TEX_MAX_WIDTH && height > 0 && height <= OVERLAY_TEX_MAX_HEIGHT)
                        {
                            const uint8_t *pixels = reinterpret_cast<const uint8_t*>(g_tex_header) + OVERLAY_TEX_HEADER_SIZE;
                            reshade::api::subresource_data data;
                            data.data = const_cast<uint8_t*>(pixels);
                            data.row_pitch = g_tex_header->stride;
                            data.slice_pitch = g_tex_header->stride * height;

                            reshade::api::subresource_box box;
                            box.left = 0;
                            box.top = 0;
                            box.front = 0;
                            box.right = width;
                            box.bottom = height;
                            box.back = 1;

                            runtime->get_device()->update_texture_region(data, g_overlay_tex, 0, &box);

                            uint32_t seq2 = g_tex_header->seq.load(std::memory_order_acquire);
                            if (seq1 == seq2) {
                                s_last_tex_seq = seq1;
                            }
                        }
                    }
                }
            }
        }

        // CONTROL DE MÁSCARA DE INPUT: Activo si Host vive + overlay_visible==1, F10 debug, o menú ReShade abierto
        bool is_alive = host_alive();
        bool reshade_open = g_reshade_menu_open.load(std::memory_order_relaxed);
        bool overlay_active = (is_alive && g_snapshot.overlay_visible != 0) || g_debug_f10_overlay || reshade_open;

        static bool s_last_overlay_active = false;
        if (overlay_active != s_last_overlay_active) {
            s_last_overlay_active = overlay_active;
            std::string state_msg = std::string("[WLSOS] Overlay & Input Mask state changed: active=") + (overlay_active ? "1" : "0") + " (host_alive=" + (is_alive ? "1" : "0") + ", reshade_open=" + (reshade_open ? "1" : "0") + ")";
            reshade::log_message(reshade::log_level::info, state_msg.c_str());
        }

        XInputHooks::SetOverlayActive(overlay_active);
    }

    void on_init_device(reshade::api::device *device)
    {
        DWORD pid = GetCurrentProcessId();
        std::string init_msg = "[WLSOS] on_init_device: pid=" + std::to_string(pid);
        reshade::log_message(reshade::log_level::info, init_msg.c_str());

        // Crear textura de overlay
        reshade::api::resource_desc tex_desc(
            OVERLAY_TEX_MAX_WIDTH, OVERLAY_TEX_MAX_HEIGHT, 1, 1,
            reshade::api::format::r8g8b8a8_unorm, 1,
            reshade::api::memory_heap::gpu_only,
            reshade::api::resource_usage::shader_resource | reshade::api::resource_usage::copy_dest
        );

        if (device->create_resource(tex_desc, nullptr, reshade::api::resource_usage::shader_resource, &g_overlay_tex))
        {
            reshade::api::resource_view_desc srv_desc(
                reshade::api::resource_view_type::texture_2d,
                reshade::api::format::r8g8b8a8_unorm, 0, 1
            );
            device->create_resource_view(g_overlay_tex, reshade::api::resource_usage::shader_resource, srv_desc, &g_overlay_srv);
            reshade::log_message(reshade::log_level::info, "[WLSOS] Overlay texture created successfully");
        }
        else
        {
            reshade::log_message(reshade::log_level::error, "[WLSOS] Failed to create overlay texture!");
        }

        // Inicializar hooks de XInput (Idempotente: solo se aplica una vez)
        XInputHooks::Initialize();

        if (try_attach_ipc()) {
            reshade::log_message(reshade::log_level::info, "[WLSOS] IPC attach OK: H2A and A2H mappings opened.");
            if (read_ipc_snapshot(g_snapshot)) {
                std::string snap_msg = "[WLSOS] First H2A snapshot: ver=" + std::to_string(g_snapshot.protocol_version) + 
                                       " host_pid=" + std::to_string(g_snapshot.host_pid) + 
                                       " overlay=" + std::to_string(g_snapshot.overlay_visible);
                reshade::log_message(reshade::log_level::info, snap_msg.c_str());
            }
        } else {
            reshade::log_message(reshade::log_level::warning, "[WLSOS] IPC attach failed initially. Will retry in on_present.");
        }
    }
}

namespace
{
    void on_destroy_device(reshade::api::device *device) 
    { 
        reshade::log_message(reshade::log_level::info, "[WLSOS] on_destroy_device: releasing IPC views and textures");
        if (g_overlay_srv.handle != 0) {
            device->destroy_resource_view(g_overlay_srv);
            g_overlay_srv = { 0 };
        }
        if (g_overlay_tex.handle != 0) {
            device->destroy_resource(g_overlay_tex);
            g_overlay_tex = { 0 };
        }
        detach_ipc();
    }
}

extern "C" __declspec(dllexport) const char *NAME = "WindowsLikeSteamOS";
extern "C" __declspec(dllexport) const char *NAME_OVERLAY = "WindowsLikeSteamOS QAM";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "SteamOS like features for Windows (FSR, Overlay, IPC)";

BOOL APIENTRY DllMain(HMODULE hModule, DWORD fdwReason, LPVOID)
{
    switch (fdwReason)
    {
    case DLL_PROCESS_ATTACH:
        if (!reshade::register_addon(hModule))
            return FALSE;

        reshade::register_event<reshade::addon_event::init_device>(on_init_device);
        reshade::register_event<reshade::addon_event::destroy_device>(on_destroy_device);
        reshade::register_event<reshade::addon_event::reshade_present>(on_present);
        reshade::register_overlay(nullptr, draw_addon_settings_inline);
        reshade::register_overlay("OSD", draw_native_qam_overlay);
        break;
    case DLL_PROCESS_DETACH:
        // Desinstalación limpia final de hooks XInput solo al descargar el addon
        XInputHooks::Shutdown();
        detach_ipc();
        reshade::unregister_event<reshade::addon_event::init_device>(on_init_device);
        reshade::unregister_event<reshade::addon_event::destroy_device>(on_destroy_device);
        reshade::unregister_event<reshade::addon_event::reshade_present>(on_present);
        reshade::unregister_overlay(nullptr, draw_addon_settings_inline);
        reshade::unregister_overlay("OSD", draw_native_qam_overlay);
        reshade::unregister_addon(hModule);
        break;
    }
    return TRUE;
}

