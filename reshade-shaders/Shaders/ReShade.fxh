// ReShade.fxh - Standard ReShade header
// Provides BackBuffer, DepthBuffer, and PostProcessVS for ReShade FX shaders

#pragma once

namespace ReShade
{
	texture BackBufferTex : COLOR;
	sampler BackBuffer { Texture = BackBufferTex; };

	texture DepthBufferTex : DEPTH;
	sampler DepthBuffer { Texture = DepthBufferTex; };
}

void PostProcessVS(in uint id : SV_VertexID, out float4 position : SV_Position, out float2 texcoord : TEXCOORD)
{
	texcoord.x = (id == 2) ? 2.0 : 0.0;
	texcoord.y = (id == 1) ? 2.0 : 0.0;
	position = float4(texcoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}
