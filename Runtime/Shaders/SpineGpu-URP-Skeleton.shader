// Standard universal URP unlit PMA shader for the Spine GPU skinning path (the package-side
// counterpart of Spine's official Skeleton shader). Draws baked prototype meshes submitted via
// DrawMeshInstancedIndirect by GpuSkinningManager. Project-specific shader adaptations (e.g. a
// lit/custom Spine shader) belong on the project side: include SpineGpuSkinning.hlsl there and
// call GpuSpineSkinToWorld() as the first line of the vertex function.
Shader "GpuSpine/URP/Skeleton"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.1
        [Toggle(_ALPHATEST_ON)] _AlphaTest ("Alpha Test", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        // PMA straight out: additive slots carry a baked vertex alpha of 0, which turns this blend
        // into One One (additive), matching the CPU MeshGenerator PMA vertex color trick.
        Blend One OneMinusSrcAlpha
        ZWrite Off
        // Mirrored skeletons (negative ScaleX) flip triangle winding; culling must stay off.
        Cull Off

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            // StructuredBuffer and SV_InstanceID are available from target 3.5.
            #pragma target 3.5
            #pragma vertex GpuSpineVertex
            #pragma fragment GpuSpineFragment
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpineGpuSkinning.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;   // influence 0 local (x, y) + baked z
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float4 influence12 : TEXCOORD1; // (vx1, vy1, vx2, vy2)
                float2 influence3 : TEXCOORD2;  // (vx3, vy3)
                float4 boneIndices : TEXCOORD3; // 4 bone indices, integers stored as floats
                float4 boneWeights : TEXCOORD4; // 4 normalized weights
                float2 dynInfo : TEXCOORD5;     // x = dynamic slot id (-1 = static), y = variant id
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
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

                // GPU skinning: bind-pose vertex attributes -> skeleton space -> world space.
                float3 positionWS = GpuSpineSkinToWorld(input.positionOS, input.influence12,
                    input.influence3, input.boneIndices, input.boneWeights, input.dynInfo, input.instanceID);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;

                float4 color = input.color * GpuSpineGetInstanceColor(input.instanceID) * _Color;
                // PMA: premultiply rgb by the combined alpha. Slots baked for the additive trick
                // carry a vertex alpha of 0 and keep straight rgb (see Blend above).
                if (input.color.a > 0.0)
                    color.rgb *= color.a;
                output.color = color;
                return output;
            }

            half4 GpuSpineFragment(Varyings input) : SV_Target
            {
                half4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half4 color = texel * input.color;
                #ifdef _ALPHATEST_ON
                clip(color.a - _Cutoff);
                #endif
                return color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
