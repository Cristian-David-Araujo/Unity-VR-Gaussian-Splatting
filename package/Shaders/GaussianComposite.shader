// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            // Separate blend for color and ALPHA.  In passthrough/MR the eye-buffer alpha is the
            // composition mask the XR compositor uses to mix in the real camera, so it must be
            // accumulated as proper coverage, not overwritten.
            //   RGB:   src.rgb*src.a + dst.rgb*(1-src.a)   (normal over)
            //   Alpha: src.a       + dst.a*(1-src.a)       (premultiplied coverage accumulate)
            // This keeps splats opaque (alpha rises where splats are) while leaving the passthrough
            // mask intact where there are no splats, so neither eye flickers.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
// Enable stereo instancing so this fullscreen pass composites into both eye slices
// when Unity uses Single-Pass Instanced or GPU Multiview rendering (e.g. Quest VR).
#pragma multi_compile_instancing
#include "UnityCG.cginc"

// UNITY_VERTEX_INPUT_INSTANCE_ID must live inside a struct, not a raw function parameter.
struct AppData
{
    uint vtxID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2f
{
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

v2f vert (AppData v)
{
    v2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    float2 quadPos = float2(v.vtxID&1, (v.vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

// _GaussianSplatRT is always a plain Texture2D (we force Tex2D in the RT descriptor).
// Both eyes receive the same center-eye splat image, composited via stereo instancing.
// It is rendered at REDUCED resolution (see GaussianSplatURPFeature k_SplatRTScale) and bilinearly
// upsampled here, so we sample with normalized UVs instead of a 1:1 integer Load.
Texture2D _GaussianSplatRT;
SamplerState sampler_GaussianSplatRT;
// 1 in VR: the off-screen splat RT is rendered Y-flipped vs the eye color buffer.
float _GaussianSplatFlipY;

half4 frag (v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    // Normalized UV of this eye-buffer pixel; bilinearly samples the (possibly lower-res) splat RT.
    float2 uv = i.vertex.xy / _ScreenParams.xy;
    if (_GaussianSplatFlipY > 0.5)
        uv.y = 1.0 - uv.y;
    half4 col = _GaussianSplatRT.Sample(sampler_GaussianSplatRT, uv);
    col.rgb = GammaToLinearSpace(col.rgb);
    col.a = saturate(col.a * 1.5);
    return col;
}
ENDCG
        }
    }
}
