#define A_GPU 1
#define A_HLSL 1

#include "ffx_a.h"

cbuffer cbRCAS : register(b0) {
    uint4 Const0;
};

Texture2D<float4> InputTexture : register(t0);

#define FSR_RCAS_F 1
AF4 FsrRcasLoadF(ASU2 p) { return InputTexture.Load(int3(p.x, p.y, 0)); }
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}

#include "ffx_fsr1.h"

struct VS_OUTPUT {
    float4 position : SV_POSITION;
    float2 texcoord : TEXCOORD;
};

float4 main(VS_OUTPUT input) : SV_TARGET {
    AU2 gxy = AU2(input.position.xy);
    AF3 c;
    FsrRcasF(c.r, c.g, c.b, gxy, Const0);
    float alpha = InputTexture.Load(int3(gxy.x, gxy.y, 0)).a;
    return float4(c, alpha);
}
