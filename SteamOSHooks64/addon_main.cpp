// addon_main.cpp
#define NOMINMAX
#include <imgui.h>
#include <reshade.hpp>
#include <windows.h>
#include <string>
#include <atomic>
#include <algorithm>
#include <cmath>
#include <unordered_set>

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
    bool              g_qam_native_open = false;

    // --- Efectos ReShade Cache ---

    struct EffectsCache
    {
        bool valid = false;

        // --- techniques ---
        reshade::api::effect_technique tech_smaa_prepass{0}; 
        reshade::api::effect_technique tech_smaa_main{0};
        reshade::api::effect_technique tech_taa{0};
        reshade::api::effect_technique tech_cmaa2{0};
        reshade::api::effect_technique tech_cas{0};
        reshade::api::effect_technique tech_rcas{0};
        reshade::api::effect_technique tech_crt_newpixie{0};

        // --- uniforms ---
        reshade::api::effect_uniform_variable u_taa_jitter{0};   
        reshade::api::effect_uniform_variable u_taa_seeking{0};  
        reshade::api::effect_uniform_variable u_taa_debug{0};    
        reshade::api::effect_uniform_variable u_cmaa2_edge_threshold{0}; 

        reshade::api::effect_uniform_variable u_crt_acc{0};      
        reshade::api::effect_uniform_variable u_crt_blur_x{0};   
        reshade::api::effect_uniform_variable u_crt_blur_y{0};   
        reshade::api::effect_uniform_variable u_crt_curvature{0}; 
        reshade::api::effect_uniform_variable u_crt_wiggle{0};   
        reshade::api::effect_uniform_variable u_crt_scanroll{0}; 
        reshade::api::effect_uniform_variable u_crt_frame{0};    

        reshade::api::effect_uniform_variable u_cas_strength{0};
        reshade::api::effect_uniform_variable u_rcas_sharpness{0};

        uint64_t next_cache_attempt_ms = 0;
    };

    static EffectsCache g_fx;
    static std::unordered_set<std::string> g_missing_logged;

    static void invalidate_effect_cache()
    {
        g_fx = EffectsCache{};
        g_missing_logged.clear();
    }

    static void log_missing_once(const char *what)
    {
        if (g_missing_logged.emplace(what).second)
            reshade::log_message(reshade::log_level::warning, what);
    }

    static bool ensure_effect_cache(reshade::api::effect_runtime *runtime)
    {
        if (!runtime) return false;
        if (g_fx.valid) return true;

        const uint64_t now = GetTickCount64();
        if (now < g_fx.next_cache_attempt_ms)
            return false;

        g_fx.next_cache_attempt_ms = now + 1000; 

        g_fx.tech_taa = runtime->find_technique(nullptr, "TAA");
        if (g_fx.tech_taa.handle == 0) log_missing_once("[WLSOS FX] Missing technique: TAA");

        g_fx.tech_crt_newpixie = runtime->find_technique(nullptr, "CRTNewPixie");
        if (g_fx.tech_crt_newpixie.handle == 0) log_missing_once("[WLSOS FX] Missing technique: CRTNewPixie");

        g_fx.tech_smaa_main = runtime->find_technique(nullptr, "SMAA");
        g_fx.tech_smaa_prepass = runtime->find_technique(nullptr, "SMAA_Prepass");
        g_fx.tech_cmaa2 = runtime->find_technique(nullptr, "CMAA2");
        g_fx.tech_cas = runtime->find_technique(nullptr, "CAS");
        g_fx.tech_rcas = runtime->find_technique(nullptr, "RCAS");

        g_fx.u_taa_jitter  = runtime->find_uniform_variable(nullptr, "Jitter_Ammount");
        g_fx.u_taa_seeking = runtime->find_uniform_variable(nullptr, "Seeking");
        g_fx.u_taa_debug   = runtime->find_uniform_variable(nullptr, "DebugOutput");

        g_fx.u_crt_acc       = runtime->find_uniform_variable(nullptr, "acc_modulate");
        g_fx.u_crt_blur_x    = runtime->find_uniform_variable(nullptr, "blur_x");
        g_fx.u_crt_blur_y    = runtime->find_uniform_variable(nullptr, "blur_y");
        g_fx.u_crt_curvature = runtime->find_uniform_variable(nullptr, "curvature");
        g_fx.u_crt_wiggle    = runtime->find_uniform_variable(nullptr, "wiggle_toggle");
        g_fx.u_crt_scanroll  = runtime->find_uniform_variable(nullptr, "scanroll");
        g_fx.u_crt_frame     = runtime->find_uniform_variable(nullptr, "use_frame");

        g_fx.u_cas_strength   = runtime->find_uniform_variable(nullptr, "ContrastAdaptation");
        g_fx.u_rcas_sharpness = runtime->find_uniform_variable(nullptr, "Sharpness");
        g_fx.u_cmaa2_edge_threshold = runtime->find_uniform_variable(nullptr, "EdgeThreshold");

        const bool ok = (g_fx.tech_smaa_main.handle != 0) || (g_fx.tech_taa.handle != 0) || (g_fx.tech_crt_newpixie.handle != 0) || (g_fx.tech_cas.handle != 0) || (g_fx.tech_rcas.handle != 0);

        if (!ok) return false;

        g_fx.valid = true;
        reshade::log_message(reshade::log_level::info, "[WLSOS FX] Effect handles cached.");
        return true;
    }

    static void apply_effect_state(reshade::api::effect_runtime *runtime,
                                   uint8_t aa_mode,
                                   uint8_t sharpen_mode,
                                   bool crt_enabled,
                                   float sharpen_strength,
                                   float crt_intensity,
                                   float taa_jitter,
                                   float taa_seeking,
                                   float cmaa2_edge_threshold)
    {
        if (!ensure_effect_cache(runtime))
            return;

        const bool use_smaa  = (aa_mode == wls::AA_SMAA);
        const bool use_taa   = (aa_mode == wls::AA_TAA);
        const bool use_cmaa2 = (aa_mode == wls::AA_CMAA2);

        if (g_fx.tech_smaa_prepass.handle != 0) runtime->set_technique_state(g_fx.tech_smaa_prepass, use_smaa);
        if (g_fx.tech_smaa_main.handle != 0) runtime->set_technique_state(g_fx.tech_smaa_main, use_smaa);
        if (g_fx.tech_taa.handle != 0) runtime->set_technique_state(g_fx.tech_taa, use_taa);
        if (g_fx.tech_cmaa2.handle != 0) runtime->set_technique_state(g_fx.tech_cmaa2, use_cmaa2);

        if (use_taa) {
            const float j = std::clamp(taa_jitter, 0.0f, 1.0f);
            const float s = std::clamp(taa_seeking, 0.025f, 0.25f);
            const int dbg = 0;
            if (g_fx.u_taa_jitter.handle)  runtime->set_uniform_value_float(g_fx.u_taa_jitter, j);
            if (g_fx.u_taa_seeking.handle) runtime->set_uniform_value_float(g_fx.u_taa_seeking, s);
            if (g_fx.u_taa_debug.handle)   runtime->set_uniform_value_int(g_fx.u_taa_debug, dbg);
        }
        
        if (use_cmaa2) {
            const float e = std::clamp(cmaa2_edge_threshold, 0.02f, 0.15f);
            if (g_fx.u_cmaa2_edge_threshold.handle) runtime->set_uniform_value_float(g_fx.u_cmaa2_edge_threshold, e);
        }

        const bool use_cas  = (sharpen_mode == wls::SHARPEN_CAS);
        const bool use_rcas = (sharpen_mode == wls::SHARPEN_RCAS);
        if (g_fx.tech_cas.handle)  runtime->set_technique_state(g_fx.tech_cas, use_cas);
        if (g_fx.tech_rcas.handle) runtime->set_technique_state(g_fx.tech_rcas, use_rcas);

        const float strength = std::clamp(sharpen_strength, 0.0f, 1.0f);
        if (use_cas && g_fx.u_cas_strength.handle)
            runtime->set_uniform_value_float(g_fx.u_cas_strength, strength);
        if (use_rcas && g_fx.u_rcas_sharpness.handle)
            runtime->set_uniform_value_float(g_fx.u_rcas_sharpness, strength);

        if (g_fx.tech_crt_newpixie.handle)
            runtime->set_technique_state(g_fx.tech_crt_newpixie, crt_enabled);

        if (crt_enabled) {
            const float t = std::clamp(crt_intensity, 0.0f, 1.0f);
            const float acc = (0.35f + (0.85f - 0.35f) * t);
            const float blur = (0.0f + (2.0f - 0.0f) * t);
            const float curv = (1.2f + (2.3f - 1.2f) * t);
            const int scanroll = (t > 0.2f) ? 1 : 0;
            const int wiggle   = (t > 0.6f) ? 1 : 0;
            const int frame    = 0;

            if (g_fx.u_crt_acc.handle)       runtime->set_uniform_value_float(g_fx.u_crt_acc, acc);
            if (g_fx.u_crt_blur_x.handle)    runtime->set_uniform_value_float(g_fx.u_crt_blur_x, blur);
            if (g_fx.u_crt_blur_y.handle)    runtime->set_uniform_value_float(g_fx.u_crt_blur_y, blur);
            if (g_fx.u_crt_curvature.handle) runtime->set_uniform_value_float(g_fx.u_crt_curvature, curv);
            if (g_fx.u_crt_scanroll.handle)  runtime->set_uniform_value_int(g_fx.u_crt_scanroll, scanroll);
            if (g_fx.u_crt_wiggle.handle)    runtime->set_uniform_value_int(g_fx.u_crt_wiggle, wiggle);
            if (g_fx.u_crt_frame.handle)     runtime->set_uniform_value_int(g_fx.u_crt_frame, frame);
        }
    }

    struct LastAppliedState {
        uint8_t aa_mode = 255;
        uint8_t sharpen_mode = 255;
        uint8_t crt_enabled = 255;
        float sharpen_strength = -1.0f;
        float crt_intensity = -1.0f;
        float taa_jitter = -1.0f;
        float taa_seeking = -1.0f;
        float cmaa2_edge_threshold = -1.0f;

        bool has_changed(const HostToAddonState& s) const {
            return aa_mode != s.aa_mode || 
                   sharpen_mode != s.sharpen_mode || 
                   crt_enabled != s.crt_enabled ||
                   sharpen_strength != s.sharpen_strength ||
                   crt_intensity != s.crt_intensity ||
                   taa_jitter != s.taa_jitter ||
                   taa_seeking != s.taa_seeking ||
                   cmaa2_edge_threshold != s.cmaa2_edge_threshold;
        }

        void update(const HostToAddonState& s) {
            aa_mode = s.aa_mode;
            sharpen_mode = s.sharpen_mode;
            crt_enabled = s.crt_enabled;
            sharpen_strength = s.sharpen_strength;
            crt_intensity = s.crt_intensity;
            taa_jitter = s.taa_jitter;
            taa_seeking = s.taa_seeking;
            cmaa2_edge_threshold = s.cmaa2_edge_threshold;
        }
    };

    static LastAppliedState g_last_applied;

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

    void notify_ipc_action(uint32_t mask)
    {
        if (!g_a2h) return;
        uint32_t seq = g_a2h->seq.load(std::memory_order_relaxed);
        g_a2h->seq.store(seq + 1, std::memory_order_release);
        g_a2h->request_mask = mask;
        g_a2h->request_epoch++;
        g_a2h->seq.store(seq + 2, std::memory_order_release);
    }



    void draw_native_qam_overlay(reshade::api::effect_runtime *)
    {
        if (g_reshade_menu_open.load(std::memory_order_relaxed))
            return;

        bool is_alive = host_alive();
        bool should_show = g_qam_native_open || (g_snapshot.overlay_visible != 0 && is_alive) || g_debug_f10_overlay;

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
                // Pestaña: Efectos (AA / Sharpen / CRT)
                ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "EFECTOS VISUALES");
                ImGui::Spacing();

                ImGui::Text("Anti-Aliasing");
                static int s_aa_mode = 0; // 0=OFF, 1=SMAA, 2=TAA, 3=CMAA2
                if (g_a2h) s_aa_mode = g_snapshot.aa_mode;
                if (ImGui::RadioButton("OFF##AA", &s_aa_mode, 0)) { if (g_a2h) { g_a2h->desired_aa_mode = 0; notify_ipc_action(wls::REQ_AA); } }
                ImGui::SameLine();
                if (ImGui::RadioButton("SMAA", &s_aa_mode, 1)) { if (g_a2h) { g_a2h->desired_aa_mode = 1; notify_ipc_action(wls::REQ_AA); } }
                ImGui::SameLine();
                if (ImGui::RadioButton("TAA", &s_aa_mode, 2)) { if (g_a2h) { g_a2h->desired_aa_mode = 2; notify_ipc_action(wls::REQ_AA); } }
                ImGui::SameLine();
                if (ImGui::RadioButton("CMAA2", &s_aa_mode, 3)) { if (g_a2h) { g_a2h->desired_aa_mode = 3; notify_ipc_action(wls::REQ_AA); } }

                ImGui::Spacing();
                ImGui::Separator();
                ImGui::Spacing();

                ImGui::Text("Filtro de Nitidez (Sharpen)");
                static int s_sharp_mode = 0; // 0=OFF, 1=CAS, 2=RCAS
                if (g_a2h) s_sharp_mode = g_snapshot.sharpen_mode;
                if (ImGui::RadioButton("OFF##SHARP", &s_sharp_mode, 0)) { if (g_a2h) { g_a2h->desired_sharpen_mode = 0; notify_ipc_action(wls::REQ_SHARPEN); } }
                ImGui::SameLine();
                if (ImGui::RadioButton("FidelityFX CAS", &s_sharp_mode, 1)) { if (g_a2h) { g_a2h->desired_sharpen_mode = 1; notify_ipc_action(wls::REQ_SHARPEN); } }
                ImGui::SameLine();
                if (ImGui::RadioButton("AMD RCAS", &s_sharp_mode, 2)) { if (g_a2h) { g_a2h->desired_sharpen_mode = 2; notify_ipc_action(wls::REQ_SHARPEN); } }

                ImGui::Spacing();
                ImGui::Separator();
                ImGui::Spacing();

                ImGui::Text("Modo Retro (CRT)");
                static int s_crt_mode = 0; // 0=OFF, 1=NewPixie
                if (g_a2h) s_crt_mode = g_snapshot.crt_enabled;
                if (ImGui::RadioButton("OFF##CRT", &s_crt_mode, 0)) { if (g_a2h) { g_a2h->desired_crt_enabled = 0; notify_ipc_action(wls::REQ_CRT); } }
                ImGui::SameLine();
                if (ImGui::RadioButton("NewPixie CRT", &s_crt_mode, 1)) { if (g_a2h) { g_a2h->desired_crt_enabled = 1; notify_ipc_action(wls::REQ_CRT); } }

                ImGui::Spacing();
                ImGui::Separator();
                ImGui::Spacing();

                ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "SISTEMA");
                ImGui::Spacing();
                
                static int s_vol = 80;
                if (g_a2h) s_vol = (int)(g_snapshot.master_volume * 100.0f);
                if (ImGui::SliderInt("Volumen", &s_vol, 0, 100, "%d%%"))
                {
                    if (g_a2h) {
                        g_a2h->desired_master_volume = s_vol / 100.0f;
                        notify_ipc_action(wls::REQ_VOLUME);
                    }
                }
            }
            else if (s_active_tab == 1)
            {
                // Pestaña: Rendimiento
                ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "LIMITE DE RENDIMIENTO");
                ImGui::Spacing();

                ImGui::Text("Limite de FPS");
                static int s_fps_cap = 0; // 0=OFF, 30, 45, 60
                if (g_a2h) s_fps_cap = g_snapshot.fps_limit;
                if (ImGui::RadioButton("Sin Limite", &s_fps_cap, 0)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 0; notify_ipc_action(wls::REQ_FPS_LIMIT); }
                }
                if (ImGui::RadioButton("30 FPS", &s_fps_cap, 30)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 30; notify_ipc_action(wls::REQ_FPS_LIMIT); }
                }
                ImGui::SameLine();
                if (ImGui::RadioButton("45 FPS", &s_fps_cap, 45)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 45; notify_ipc_action(wls::REQ_FPS_LIMIT); }
                }
                ImGui::SameLine();
                if (ImGui::RadioButton("60 FPS", &s_fps_cap, 60)) {
                    if (g_a2h) { g_a2h->desired_fps_limit = 60; notify_ipc_action(wls::REQ_FPS_LIMIT); }
                }
            }
            else if (s_active_tab == 2)
            {
                // Pestaña: Energía / Opciones
                ImGui::TextColored(ImVec4(0.10f, 0.62f, 1.00f, 1.00f), "OPCIONES DEL SISTEMA");
                ImGui::Spacing();

                ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.20f, 0.25f, 0.35f, 1.00f));
                
                if (ImGui::Button("Suspender Consola", ImVec2(-1, 40))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_SUSPEND; notify_ipc_action(wls::REQ_POWER); }
                }
                if (ImGui::Button("Reiniciar Consola", ImVec2(-1, 40))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_RESTART; notify_ipc_action(wls::REQ_POWER); }
                }
                if (ImGui::Button("Apagar Consola", ImVec2(-1, 40))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_SHUTDOWN; notify_ipc_action(wls::REQ_POWER); }
                }
                
                ImGui::Spacing();
                ImGui::Separator();
                ImGui::Spacing();

                ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.70f, 0.15f, 0.15f, 1.00f)); // Rojo para salir
                if (ImGui::Button("Modo Escritorio (Salir)", ImVec2(-1, 44))) {
                    if (g_a2h) { g_a2h->requested_power_action = wls::POWER_ACTION_DESKTOP; notify_ipc_action(wls::REQ_POWER); }
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

        // CONTROL DE MÁSCARA DE INPUT Y TOGGLE NATIVO XINPUT (1.5s Select/Back)
        static ULONGLONG s_back_down_ms = 0;
        static bool s_back_handled = false;

        XINPUT_STATE xstate;
        if (XInputHooks::GetCapturedState(xstate))
        {
            bool back_down = (xstate.Gamepad.wButtons & XINPUT_GAMEPAD_BACK) != 0;
            if (back_down)
            {
                const ULONGLONG now = GetTickCount64();
                if (s_back_down_ms == 0)
                {
                    s_back_down_ms = now;
                }
                else if ((now - s_back_down_ms >= 1500) && !s_back_handled)
                {
                    s_back_handled = true;
                    g_qam_native_open = !g_qam_native_open;
                    reshade::log_message(reshade::log_level::info, ("[WLSOS] Native Select 1.5s hold detected! QAM state: " + std::to_string(g_qam_native_open)).c_str());
                }
            }
            else
            {
                s_back_down_ms = 0;
                s_back_handled = false;
            }
        }

        bool is_alive = host_alive();
        bool reshade_open = g_reshade_menu_open.load(std::memory_order_relaxed);
        bool overlay_active = g_qam_native_open || (is_alive && g_snapshot.overlay_visible != 0) || g_debug_f10_overlay || reshade_open;

        static bool s_last_overlay_active = false;
        if (overlay_active != s_last_overlay_active) {
            s_last_overlay_active = overlay_active;
            std::string state_msg = std::string("[WLSOS] Overlay & Input Mask state changed: active=") + (overlay_active ? "1" : "0") + " (host_alive=" + (is_alive ? "1" : "0") + ", reshade_open=" + (reshade_open ? "1" : "0") + ")";
            reshade::log_message(reshade::log_level::info, state_msg.c_str());
        }

        if (is_alive && g_last_applied.has_changed(g_snapshot))
        {
            apply_effect_state(runtime,
                               g_snapshot.aa_mode,
                               g_snapshot.sharpen_mode,
                               g_snapshot.crt_enabled != 0,
                               g_snapshot.sharpen_strength,
                               g_snapshot.crt_intensity,
                               g_snapshot.taa_jitter,
                               g_snapshot.taa_seeking,
                               g_snapshot.cmaa2_edge_threshold);
            g_last_applied.update(g_snapshot);
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
        reshade::register_overlay("OSD", draw_native_qam_overlay);
        break;
    case DLL_PROCESS_DETACH:
        // Desinstalación limpia final de hooks XInput solo al descargar el addon
        XInputHooks::Shutdown();
        detach_ipc();
        reshade::unregister_event<reshade::addon_event::init_device>(on_init_device);
        reshade::unregister_event<reshade::addon_event::destroy_device>(on_destroy_device);
        reshade::unregister_event<reshade::addon_event::reshade_present>(on_present);
        reshade::unregister_overlay("OSD", draw_native_qam_overlay);
        reshade::unregister_addon(hModule);
        break;
    }
    return TRUE;
}

