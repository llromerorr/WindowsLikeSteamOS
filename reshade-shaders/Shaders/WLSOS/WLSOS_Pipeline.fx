// WLSOS_Pipeline.fx
// Unified ReShade FX pipeline for WindowsLikeSteamOS (RCAS Sharpness + CRT Post-processing)

#include "ReShade.fxh"

// =============================================================================
// UNIFORMS (Controlled via IPC by Add-on C++)
// =============================================================================

uniform int   WLSOS_FSR_Enabled   < ui_type = "checkbox"; ui_label = "FSR / RCAS Enabled"; > = 0;
uniform float WLSOS_FSR_Sharpness < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_label = "FSR Sharpness"; > = 0.2;

uniform int   WLSOS_CRT_Enabled   < ui_type = "checkbox"; ui_label = "CRT Filter Enabled"; > = 0;
uniform float WLSOS_CRT_Intensity < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_label = "CRT Intensity"; > = 0.5;

// =============================================================================
// INTERMEDIATE RENDER TARGETS
// =============================================================================

texture2D WLSOS_RT0
{
    Width  = BUFFER_WIDTH;
    Height = BUFFER_HEIGHT;
    Format = RGBA8;
};
sampler2D WLSOS_RT0_Samp { Texture = WLSOS_RT0; };

// =============================================================================
// RCAS SHARPENING IMPLEMENTATION
// =============================================================================

float3 ApplyRCAS(float2 uv, float sharpness)
{
    float2 rcpFrame = float2(BUFFER_RCP_WIDTH, BUFFER_RCP_HEIGHT);

    // 3x3 Cross Sample
    float3 p = tex2D(ReShade::BackBuffer, uv).rgb;
    float3 e = tex2D(ReShade::BackBuffer, uv + float2(0.0, -rcpFrame.y)).rgb;
    float3 b = tex2D(ReShade::BackBuffer, uv + float2(-rcpFrame.x, 0.0)).rgb;
    float3 d = tex2D(ReShade::BackBuffer, uv + float2(rcpFrame.x, 0.0)).rgb;
    float3 h = tex2D(ReShade::BackBuffer, uv + float2(0.0, rcpFrame.y)).rgb;

    // Min and Max RGB bounds
    float3 minRGB = min(p, min(b, min(d, min(e, h))));
    float3 maxRGB = max(p, max(b, max(d, max(e, h))));

    // Calculate attenuation weight
    // Map sharpness 0.0..1.0 to RCAS limit [-0.18 .. 0.0]
    float weightLimit = lerp(0.0, -0.18, saturate(sharpness));
    float3 weight = clamp(min(minRGB, 2.0 - maxRGB) / maxRGB, 0.0, 1.0) * weightLimit;

    // Filtered output
    return saturate((weight * (e + b + d + h) + p) / (4.0 * weight + 1.0));
}

float4 PS_FSR(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float4 color = tex2D(ReShade::BackBuffer, uv);

    if (WLSOS_FSR_Enabled != 0)
    {
        color.rgb = ApplyRCAS(uv, WLSOS_FSR_Sharpness);
    }

    return color;
}

// =============================================================================
// CRT SCANLINE & VIGNETTE IMPLEMENTATION
// =============================================================================

float3 ApplyCRT(float3 color, float2 uv, float intensity)
{
    // Scanlines
    float scanline = sin(uv.y * BUFFER_HEIGHT * 3.14159265 * 2.0) * 0.5 + 0.5;
    color *= lerp(1.0, scanline, saturate(intensity));

    // Vignette
    float2 vigUV = uv * (1.0 - uv.yx);
    float vig = vigUV.x * vigUV.y * 15.0;
    vig = pow(saturate(vig), 0.25);
    color *= lerp(1.0, vig, saturate(intensity * 0.7));

    return color;
}

float4 PS_CRT(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float4 color = tex2D(WLSOS_RT0_Samp, uv);

    if (WLSOS_CRT_Enabled != 0)
    {
        color.rgb = ApplyCRT(color.rgb, uv, WLSOS_CRT_Intensity);
    }

    return color;
}

// =============================================================================
// PIPELINE TECHNIQUE DEFINITION
// =============================================================================

technique WLSOS_PIPELINE < ui_label = "WindowsLikeSteamOS Pipeline (FSR/RCAS + CRT)"; >
{
    pass P0
    {
        VertexShader = PostProcessVS;
        PixelShader  = PS_FSR;
        RenderTarget = WLSOS_RT0;
    }
    pass P1
    {
        VertexShader = PostProcessVS;
        PixelShader  = PS_CRT;
    }
}
