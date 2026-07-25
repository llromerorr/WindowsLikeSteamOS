// =============================================================================
// CRT.hlsl — Filtro CRT básico con scanlines, curvatura y viñeta.
// Compatible con D3D11 (vs_5_0/ps_5_0) y D3D12 (vs_5_1/ps_5_1) sin cambios.
// =============================================================================

Texture2D    g_InputTexture : register(t0);
SamplerState g_Sampler      : register(s0);

cbuffer CRTParams : register(b0) {
    float g_ScreenWidth;
    float g_ScreenHeight;
    float g_Curvature;
    float g_ScanlineIntensity;
    float g_Time;
    float3 g_Padding; // debe existir para que el layout coincida con el C++
};

struct VSOutput {
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOutput VSMain(uint id : SV_VertexID) {
    VSOutput output;
    output.uv  = float2((id << 1) & 2, id & 2);
    output.pos = float4(output.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

float2 CurveUV(float2 uv, float curvature) {
    uv = uv * 2.0 - 1.0;
    float2 offset = abs(uv.yx) / curvature;
    uv = uv + uv * offset * offset;
    uv = uv * 0.5 + 0.5;
    return uv;
}

float4 PSMain(VSOutput input) : SV_TARGET {
    float2 uv = input.uv;

    if (g_Curvature > 0.01) {
        uv = CurveUV(uv, g_Curvature);
    }

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        return float4(0, 0, 0, 1);
    }

    float4 color = g_InputTexture.Sample(g_Sampler, uv);

    float scanline = sin(uv.y * g_ScreenHeight * 3.14159265 * 2.0) * 0.5 + 0.5;
    color.rgb *= lerp(1.0, scanline, g_ScanlineIntensity);

    float2 vigUV = uv * (1.0 - uv.yx);
    float vig = vigUV.x * vigUV.y * 15.0;
    vig = pow(saturate(vig), 0.25);
    color.rgb *= vig;

    return color;
}
