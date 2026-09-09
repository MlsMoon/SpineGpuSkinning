#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "SpineGpuSkinning.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

struct Attributes
{
    float3 positionOS : POSITION;   // influence 0 local (x, y) + baked z
    float2 uv : TEXCOORD0;
    float4 color : COLOR;
    GPU_SPINE_VERTEX_CHANNELS
    uint instanceID : SV_InstanceID;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR;
    float2 positionSS : TEXCOORD1;
    float3 positionWS : TEXCOORD4;
    nointerpolation float slotIndex : TEXCOORD2;
    nointerpolation uint instanceID : TEXCOORD3;
};

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

// NOTE: Do not ifdef the properties here as SRP batcher can not handle different layouts.
CBUFFER_START(UnityPerMaterial)
    float4 _Color;
    float _Cutoff;
CBUFFER_END

Varyings GpuSpineVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    uint instanceID = GpuSpineResolveInstanceID(input.instanceID);

    // GPU skinning: bind-pose vertex attributes -> skeleton space -> world space.
    float3 positionSS = GpuSpineSkinPosition(input.positionOS, input.influence12,
        input.influence3, input.boneIndices, input.boneWeights, input.dynInfo,
        input.deformInfo, instanceID);
    float3 positionWS = mul(GpuSpineGetLocalToWorld(instanceID), float4(positionSS, 1)).xyz;
    output.positionWS = positionWS;
    output.positionSS = positionSS.xy;
    output.slotIndex = input.slotIndex;
    output.instanceID = instanceID;
    output.positionCS = TransformWorldToHClip(positionWS);
    output.uv = input.uv;

    // Vertex color composition, 1:1 with the CPU path (MeshGenerator.cs:953-972):
    // attachment COLOR x slot color x instance skeleton color x material tint, then
    // premultiply rgb by the combined alpha (PMA).
    float4 color = GpuSpineGetVertexColor(input.color, input.slotIndex, instanceID) * _Color;
    output.color = GpuSpinePremultiplyColor(color, input.deformInfo.z);
    return output;
}

half4 GpuSpineFragment(Varyings input) : SV_Target
{
    GpuSpineClip(input.positionSS, input.slotIndex, input.instanceID);
    half4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
    #ifdef _STRAIGHT_ALPHA_INPUT
    texel.rgb *= texel.a;
    #endif
    half4 color = texel * input.color;
    #ifdef _ALPHATEST_ON
    clip(color.a - _Cutoff);
    #endif
    return color;
}

float3 _LightDirection;
float3 _LightPosition;
Varyings GpuSpineShadowVertex(Attributes input)
{
    Varyings output = GpuSpineVertex(input);
    float3 normalWS = normalize(mul((float3x3)GpuSpineGetLocalToWorld(output.instanceID), float3(0, 0, -1)));
    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
        float3 lightDirectionWS = normalize(_LightPosition - output.positionWS);
    #else
        float3 lightDirectionWS = _LightDirection;
    #endif
    output.positionCS = TransformWorldToHClip(ApplyShadowBias(output.positionWS, normalWS, lightDirectionWS));
    #if UNITY_REVERSED_Z
        output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
    #else
        output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
    #endif
    return output;
}

half4 GpuSpineShadowFragment(Varyings input) : SV_Target
{
    GpuSpineClip(input.positionSS, input.slotIndex, input.instanceID);
    clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a * input.color.a - _Cutoff);
    return 0;
}
