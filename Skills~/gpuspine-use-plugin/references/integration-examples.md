# GpuSpine: Integration Examples

Complete, copy-paste-ready integration code. Paths are relative to the plugin root
(`SpineGpuSkinning/`); adjust the shader include path to where the folder lives in your
project.

## 1. Custom shader integration

Contract (from `Runtime/Shaders/SpineGpuSkinning.hlsl`):

- Include `Runtime/Shaders/SpineGpuSkinning.hlsl`.
- Declare the vertex struct **exactly** matching the baked layout (see the table below).
- Call `GpuSpineSkinToWorld(...)` as the first line of the vertex function. Current
  signature (8 arguments — note the `dynInfo` and `deformInfo` parameters):

  ```hlsl
  float3 GpuSpineSkinToWorld(float3 positionOS, float4 influence12, float2 influence3,
      float4 boneIndices, float4 boneWeights, float2 dynInfo, float3 deformInfo, uint instanceID);
  ```

- The skinning buffers (`_GpuSpineBones`, `_GpuSpineInstances`, `_GpuSpineDynSlots`,
  `_GpuSpineDeform`, `_GpuSpineSlotColors` and the count uniforms) are bound at **material
  level** by the runtime (`Runtime/Core/GpuSpineBatch.cs`). Never declare or bind them
  yourself; just include the hlsl file.
- Compose the vertex color through `GpuSpineGetVertexColor(input.color, input.slotIndex,
  input.instanceID)` (attachment COLOR x slot color x instance skeleton color), then your
  own tints, then premultiply rgb by the combined alpha; for additive slots
  (`deformInfo.z > 0.5`) apply `LinearToSRGB` to the alpha first and output alpha 0 (the
  CPU PMA additive trick).
- `#pragma target 3.5` or higher (StructuredBuffer + SV_InstanceID).
- Assign a material with your shader to `GpuSkeletonRenderer.MaterialOverride`. Only the
  **shader** is taken from the override; the per-page atlas texture and other property
  values stay with the page material clone. `enableInstancing` is set on the clone.

### Baked vertex layout (your `Attributes` struct must match this)

| Semantic | Type | Content |
|---|---|---|
| `POSITION` | float3 | Local (x, y) of influence 0; z = baked z layering |
| `TEXCOORD0` | float2 | Atlas uv |
| `COLOR` | float4 | Attachment color with its raw alpha (additive flag is in TEXCOORD6.z) |
| `TEXCOORD1` | float4 | (vx1, vy1, vx2, vy2) — local coords of influences 1 and 2 |
| `TEXCOORD2` | float2 | (vx3, vy3) — local coords of influence 3 |
| `TEXCOORD3` | float4 | 4 bone indices (integers stored as floats) |
| `TEXCOORD4` | float4 | 4 normalized weights |
| `TEXCOORD5` | float2 | (dynSlotId, variantId); (-1, -1) for static vertices |
| `TEXCOORD6` | float3 | (deformOffset, deformMode, additiveFlag); (-1, -1, flag) for non-deform slots |
| `TEXCOORD7` | float2 | (slotIndex, 0) into the `_GpuSpineSlotColors` segment |
| `SV_InstanceID` | uint | Instance index into the skinning buffers |

### Complete example: per-instance tinted unlit PMA shader

```hlsl
Shader "MyProject/SpineGpu Tinted"
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

        // PMA straight out: additive slots carry a baked vertex alpha of 0, which turns
        // this blend into One One (additive), matching the CPU path.
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off // Mirrored skeletons (negative ScaleX) flip triangle winding.

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Adjust this path to the plugin location in your project.
            #include "Assets/Plugins/SpineGpuSkinning/Runtime/Shaders/SpineGpuSkinning.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float4 influence12 : TEXCOORD1;
                float2 influence3 : TEXCOORD2;
                float4 boneIndices : TEXCOORD3;
                float4 boneWeights : TEXCOORD4;
                float2 dynInfo : TEXCOORD5;
                float3 deformInfo : TEXCOORD6;
                float slotIndex : TEXCOORD7;
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

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Cutoff;
            CBUFFER_END

            Varyings Vert (Attributes input)
            {
                Varyings output = (Varyings)0;

                // GPU skinning first: bind-pose vertex attributes -> skeleton space -> world.
                float3 positionWS = GpuSpineSkinToWorld(input.positionOS, input.influence12,
                    input.influence3, input.boneIndices, input.boneWeights, input.dynInfo,
                    input.deformInfo, input.instanceID);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv;

                // Per-instance extension slot written by the C# driver below.
                float4 instanceTint = GpuSpineGetInstanceCustom0(input.instanceID);
                float4 color = GpuSpineGetVertexColor(input.color, input.slotIndex, input.instanceID) * _Color * instanceTint;
                // PMA: premultiply rgb by the combined alpha; additive slots (deformInfo.z)
                // apply the CPU gamma compensation and output alpha 0 (see Blend above).
                float combinedAlpha = color.a;
                if (input.deformInfo.z > 0.5)
                    combinedAlpha = LinearToSRGB(combinedAlpha);
                color.rgb *= combinedAlpha;
                if (input.deformInfo.z > 0.5)
                    color.a = 0.0;
                output.color = color;
                return output;
            }

            half4 Frag (Varyings input) : SV_Target
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
```

### C# side: fill the Custom0 slot via WriteInstanceData

`WriteInstanceData` fires once per submitted instance per frame, after `LocalToWorld` and
`Color` have been filled (`Runtime/GpuSkeletonRenderer.cs`).

```csharp
using GpuSpine;
using GpuSpine.Core;
using UnityEngine;

/// <summary>Writes a per-instance tint into the Custom0 slot of the draw data.</summary>
[RequireComponent(typeof(GpuSkeletonRenderer))]
public sealed class SpineGpuTintDriver : MonoBehaviour {
    public Color Tint = Color.white;

    GpuSkeletonRenderer gpuRenderer;

    void Awake () {
        gpuRenderer = GetComponent<GpuSkeletonRenderer>();
    }

    void OnEnable () {
        gpuRenderer.WriteInstanceData += OnWriteInstanceData;
    }

    void OnDisable () {
        gpuRenderer.WriteInstanceData -= OnWriteInstanceData;
    }

    void OnWriteInstanceData (GpuSkeletonRenderer sender, ref GpuSpineInstanceData data) {
        data.Custom0 = new Vector4(Tint.r, Tint.g, Tint.b, Tint.a);
    }
}
```

✅ Put skinning first, then read instance data, then shade.
❌ Do not declare `_GpuSpineBones`/`_GpuSpineInstances` yourself or try to drive them from
a `MaterialPropertyBlock` — the runtime owns those bindings and rebinds them when a batch
grows.

## 2. Custom RenderPass integration

Contract (from `Runtime/Core/GpuSkinningManager.cs` and `Runtime/Core/GpuSpineBatch.cs`):

- `GpuSkinningManager.GetBatches()` returns read-only views of the batches submitted this
  frame (batches with zero submitted instances are skipped). **The list is reused — do not
  cache it across frames.**
- `GpuSpineBatchInfo` fields:
  - `Mesh` — the baked entry's mesh;
  - `SubmeshIndex` — submesh this batch draws (one batch per atlas page boundary);
  - `Material` — the cloned batch material **with the bound skinning buffers**;
  - `ArgsBuffer` — `uint[5]` indirect args buffer (indexCount, instanceCount, indexStart,
    baseVertex, 0);
  - `Bounds` — world-space union bounds of all submitted instances;
  - `InstanceCount` — number of submitted instances this frame.
- The batch material already carries every skinning buffer binding
  (`material.SetBuffer(...)` in the batch), so
  `CommandBuffer.DrawMeshInstancedIndirect(mesh, submeshIndex, material, pass, argsBuffer, 0)`
  works as-is.
- Buffers are uploaded in `GpuSkinningManager.LateUpdate` (execution order 32000), so any
  render pass of the same frame sees the current pose.
- `CommandBuffer.DrawMeshInstancedIndirect` culls against the mesh's own (bind-pose,
  skeleton-space) bounds. Use `GpuSpineBatchInfo.Bounds` (world-space union of live
  instances) for your own whole-batch gating when culling matters.

### Complete example: re-submit all batches into a private mask render texture

```csharp
using System.Collections.Generic;
using GpuSpine;
using GpuSpine.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>Draws every live GpuSpine batch a second time into a private mask RT.</summary>
public sealed class GpuSpineMaskFeature : ScriptableRendererFeature {
    [SerializeField] RenderTexture maskTarget;

    GpuSpineMaskPass pass;

    public override void Create () {
        pass = new GpuSpineMaskPass();
        pass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
    }

    public override void AddRenderPasses (ScriptableRenderer renderer, ref RenderingData renderingData) {
        if (maskTarget == null || GpuSkinningManager.Instance == null) return;
        pass.Setup(maskTarget);
        renderer.EnqueuePass(pass);
    }

    sealed class GpuSpineMaskPass : ScriptableRenderPass {
        RenderTexture target;

        public void Setup (RenderTexture target) {
            this.target = target;
        }

        public override void Execute (ScriptableRenderContext context, ref RenderingData renderingData) {
            IReadOnlyList<GpuSpineBatchInfo> batches = GpuSkinningManager.GetBatches();
            if (batches.Count == 0) return;

            CommandBuffer cmd = CommandBufferPool.Get("GpuSpine Mask");
            cmd.SetRenderTarget(target);
            cmd.ClearRenderTarget(true, true, Color.clear);
            for (int i = 0; i < batches.Count; i++) {
                GpuSpineBatchInfo batch = batches[i];
                // Pass index 0 = the shader's single forward pass. The batch material
                // already carries the bound skinning buffers, so this draw works as-is.
                cmd.DrawMeshInstancedIndirect(batch.Mesh, batch.SubmeshIndex, batch.Material,
                    0, batch.ArgsBuffer, 0);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
```

✅ Read `GetBatches()` fresh inside `Execute`, use the batch material as provided.
❌ Do not cache the list, the materials or the buffers across frames: batches are disposed
when their last instance unregisters (cloned material destroyed, buffers released), and
buffer bindings are refreshed whenever a batch grows.
