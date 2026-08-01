#define A_GPU 1
#define A_HLSL 1

#include "ffx_a.h"

cbuffer cbFSR : register(b0) {
    uint4 Const0;
    uint4 Const1;
    uint4 Const2;
    uint4 Const3;
};

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearSampler : register(s0);

#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p) { return InputTexture.SampleLevel(LinearSampler, p, 0); }
AF4 FsrEasuGF(AF2 p) { return InputTexture.SampleLevel(LinearSampler, p, 0); }
AF4 FsrEasuBF(AF2 p) { return InputTexture.SampleLevel(LinearSampler, p, 0); }

#include "ffx_fsr1.h"

struct VS_OUTPUT {
    float4 position : SV_POSITION;
    float2 texcoord : TEXCOORD;
};

float4 main(VS_OUTPUT input) : SV_TARGET {
    AU2 gxy = AU2(input.position.xy);
    AF3 c;
    FsrEasuF(c, gxy, Const0, Const1, Const2, Const3);
    float alpha = InputTexture.SampleLevel(LinearSampler, input.texcoord, 0).a;
    return float4(c, alpha);
}
