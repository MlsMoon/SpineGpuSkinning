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
        [Toggle(_STRAIGHT_ALPHA_INPUT)] _StraightAlphaInput ("Straight Alpha Texture", Float) = 0
        [HideInInspector] _StencilRef ("Stencil Reference", Float) = 1
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
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
        Stencil { Ref [_StencilRef] Comp [_StencilComp] Pass Keep }

        HLSLINCLUDE
        #include "SpineGpuURPCommon.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            // StructuredBuffer and SV_InstanceID are available from target 3.5.
            #pragma target 3.5
            #pragma vertex GpuSpineVertex
            #pragma multi_compile_instancing
            #pragma fragment GpuSpineFragment
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _STRAIGHT_ALPHA_INPUT

            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Offset 1, 1
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex GpuSpineShadowVertex
            #pragma multi_compile_instancing
            #pragma fragment GpuSpineShadowFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
    }

    Fallback Off
}
