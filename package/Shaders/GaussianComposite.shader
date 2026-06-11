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
            Blend SrcAlpha OneMinusSrcAlpha

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
Texture2D _GaussianSplatRT;

half4 frag (v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    col.rgb = GammaToLinearSpace(col.rgb);
    col.a = saturate(col.a * 1.5);
    return col;
}
ENDCG
        }
    }
}
