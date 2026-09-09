#ifndef GPU_SPINE_SKINNING_INCLUDED
#define GPU_SPINE_SKINNING_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

#define GPU_SPINE_VERTEX_CHANNELS \
    float4 influence12 : TEXCOORD1; \
    float2 influence3 : TEXCOORD2; \
    float4 boneIndices : TEXCOORD3; \
    float4 boneWeights : TEXCOORD4; \
    float2 dynInfo : TEXCOORD5; \
    float3 deformInfo : TEXCOORD6; \
    float slotIndex : TEXCOORD7;

// GPU skinning data channel for Spine indirect draw batches (DrawMeshInstancedIndirect).
// Third-party shaders include this file and call GpuSpineSkinToWorld() as the first line of their
// vertex function; the buffers are bound at material level by the runtime (GpuSpine.Core.GpuSpineBatch).
// All structs mirror their C# counterparts in GpuSpine.Core field-by-field and byte-by-byte.

// Mirror of GpuSpine.Core.GpuBoneMatrix (C#: 3 x Vector2, 24 bytes, LayoutKind.Sequential).
// Row0 = (a, b), Row1 = (c, d), Row2 = (worldX, worldY): the bone's 2D world affine matrix in
// skeleton space, exported from Bone.A/B/C/D/WorldX/WorldY after SkeletonAnimation.UpdateComplete.
struct GpuBoneMatrix
{
    float2 row0;
    float2 row1;
    float2 row2;
};

// Explicit affine rows avoid matrix packing differences. Six float4 fields, 96 bytes.
struct GpuSpineInstanceData
{
    float4 localToWorldRow0;
    float4 localToWorldRow1;
    float4 localToWorldRow2;
    float4 color;
    float4 custom0;
    float4 custom1;
};

// Bone matrix palette, layout [instanceID * boneCount + boneIndex].
StructuredBuffer<GpuBoneMatrix> _GpuSpineBones;
// Per-instance draw data, layout [instanceID].
StructuredBuffer<GpuSpineInstanceData> _GpuSpineInstances;

float4x4 GpuSpineGetLocalToWorld(uint instanceID)
{
    GpuSpineInstanceData data = _GpuSpineInstances[instanceID];
    return float4x4(data.localToWorldRow0, data.localToWorldRow1,
        data.localToWorldRow2, float4(0, 0, 0, 1));
}
// Bone count per instance (palette stride).
uint _GpuSpineBoneCount;
// Number of dynamic slots per instance (0 when the baked entry has none: _GpuSpineDynSlots is
// then left unbound and every read below is short-circuited by this count; v1 targets
// Windows/D3D11, where reading an unbound StructuredBuffer would additionally return 0).
uint _GpuSpineDynSlotCount;
// Per-instance dynamic slot variant selection (AttachmentTimeline support), layout
// [instanceID * _GpuSpineDynSlotCount + dynSlotId]. The selected variant id, or 0xFFFFFFFF to
// fold every variant of the slot (slot without attachment / attachment not baked as a variant).
StructuredBuffer<uint> _GpuSpineDynSlots;
// Per-instance deform segment length in float2 units (0 when the baked entry has no deform slots:
// _GpuSpineDeform is then left unbound and every read below is short-circuited by the baked
// deformOffset of -1; v1 targets Windows/D3D11, where reading an unbound StructuredBuffer would
// additionally return 0).
uint _GpuSpineDeformStride;
// Per-instance deform data (DeformTimeline support), layout
// [instanceID * _GpuSpineDeformStride + deformOffset]. Unweighted attachments receive absolute
// local positions, weighted attachments per-influence offsets.
StructuredBuffer<float2> _GpuSpineDeform;
// Per-instance slot color segment length (0 when the baked entry has no slots, which never
// happens in practice: _GpuSpineSlotColors is then left unbound and the slot color multiply
// below is short-circuited, keeping the attachment color untouched).
uint _GpuSpineSlotCount;
// Per-instance slot colors (slot.R/G/B/A of every slot, setup-static colors included), layout
// [instanceID * _GpuSpineSlotCount + slotIndex]. The CPU AnimationState writes the same fields
// for slot color timelines, so timeline-driven colors arrive here for free.
StructuredBuffer<float4> _GpuSpineSlotColors;

// Instance visibility filter for per-instance resubmission (e.g. interactive masks): when not
// 0xFFFFFFFF, every instance whose instanceID differs folds to the origin. Left unbound by the
// runtime (reads 0 on D3D11), so resubmitting passes must always set it explicitly.
uint _GpuSpineInstanceFilter;
uint _GpuSpineInstanceOffset;

uint GpuSpineResolveInstanceID(uint drawInstanceID)
{
    return drawInstanceID + _GpuSpineInstanceOffset;
}

uint _GpuSpineClipStride;
StructuredBuffer<float2> _GpuSpineClipVertices;
StructuredBuffer<float2> _GpuSpineClipRanges;

float GpuSpineCross2(float2 a, float2 b)
{
    return a.x * b.y - a.y * b.x;
}

void GpuSpineClip(float2 positionSS, float slotIndex, uint instanceID)
{
    if (_GpuSpineClipStride == 0) return;
    float2 range = _GpuSpineClipRanges[instanceID * _GpuSpineSlotCount + (uint)slotIndex];
    if (range.y == 0) return;
    uint start = instanceID * _GpuSpineClipStride + (uint)range.x;
    for (uint triangleIndex = 0; triangleIndex < (uint)range.y; triangleIndex++)
    {
        uint index = start + triangleIndex * 3;
        float2 a = _GpuSpineClipVertices[index];
        float2 b = _GpuSpineClipVertices[index + 1];
        float2 c = _GpuSpineClipVertices[index + 2];
        float3 sides = float3(GpuSpineCross2(b - a, positionSS - a),
            GpuSpineCross2(c - b, positionSS - b), GpuSpineCross2(a - c, positionSS - c));
        if (abs(GpuSpineCross2(b - a, c - a)) > 1e-12 &&
            (all(sides >= 0) || all(sides <= 0))) return;
    }
    clip(-1);
}

// Applies one bone's 2D affine matrix: wx = vx * a + vy * b + worldX ; wy = vx * c + vy * d + worldY.
float2 GpuSpineApplyBoneMatrix(GpuBoneMatrix bone, float2 localPos)
{
    return float2(localPos.x * bone.row0.x + localPos.y * bone.row0.y + bone.row2.x,
                  localPos.x * bone.row1.x + localPos.y * bone.row1.y + bone.row2.y);
}

// Skins one vertex of the baked prototype mesh into skeleton space (x, y, z), 1:1 with the CPU
// path (VertexAttachment.ComputeWorldVertices). Vertex attribute contract of the baked mesh:
// positionOS = influence 0 local (x, y) with z = zSpacing * setup draw order index,
// influence12 = TEXCOORD1 (vx1, vy1, vx2, vy2), influence3 = TEXCOORD2 (vx3, vy3),
// boneIndices = TEXCOORD3 (4 bone indices stored as floats), boneWeights = TEXCOORD4 (4 weights),
// dynInfo = TEXCOORD5 (x = dynamic slot id, -1 for static vertices; y = variant id),
// deformInfo = TEXCOORD6 (x = float2 index into the instance deform segment, -1 for vertices of
// non-deform slots; y = mode: 0 = absolute replacement (unweighted), 1 = offset add (weighted)).
// z = additive-blend flag, consumed by the color composition in the shader, not here).
float3 GpuSpineSkinPosition(float3 positionOS, float4 influence12, float2 influence3, float4 boneIndices, float4 boneWeights, float2 dynInfo, float3 deformInfo, uint instanceID)
{
    // Per-instance resubmission filter: instances outside the filter fold to the origin, so a
    // shared batch can be redrawn for one instance only (the whole triangle degenerates).
    if (_GpuSpineInstanceFilter != 0xFFFFFFFF && _GpuSpineInstanceFilter != instanceID)
        return float3(0.0, 0.0, 0.0);
    // Dynamic slot folding: a vertex of an unselected variant collapses to the origin. All three
    // vertices of its triangle fold to the same point, the triangle degenerates to zero area and
    // produces no fragments. Static vertices (dynInfo.x = -1) skip the lookup.
    if (_GpuSpineDynSlotCount > 0 && dynInfo.x > -0.5)
    {
        uint selected = _GpuSpineDynSlots[instanceID * _GpuSpineDynSlotCount + (uint)dynInfo.x];
        if (selected != (uint)dynInfo.y)
            return float3(0.0, 0.0, 0.0);
    }
    uint baseIndex = instanceID * _GpuSpineBoneCount;
    // Deform application, before the bone weighting and 1:1 with
    // VertexAttachment.ComputeWorldVertices: mode 0 replaces the local coordinate with the deform
    // value, mode 1 adds it (VertexAttachment.cs:106-108 / :139-147). Influence i reads
    // deformOffset + i (consecutive per-influence elements); reads happen only at used influences,
    // so an unweighted vertex never reads past its own float2 entry.
    bool hasDeform = _GpuSpineDeformStride > 0 && deformInfo.x > -0.5;
    uint deformBase = instanceID * _GpuSpineDeformStride + (uint)deformInfo.x;
    float2 local0 = positionOS.xy;
    if (hasDeform) local0 = deformInfo.y < 0.5 ? _GpuSpineDeform[deformBase] : local0 + _GpuSpineDeform[deformBase];
    // Influence 0 always exists in the baked stream; the remaining influences skip weight 0.
    float2 skinned = GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.x], local0) * boneWeights.x;
    if (boneWeights.y > 0.0)
    {
        float2 local1 = influence12.xy;
        if (hasDeform) local1 = deformInfo.y < 0.5 ? _GpuSpineDeform[deformBase + 1] : local1 + _GpuSpineDeform[deformBase + 1];
        skinned += GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.y], local1) * boneWeights.y;
    }
    if (boneWeights.z > 0.0)
    {
        float2 local2 = influence12.zw;
        if (hasDeform) local2 = deformInfo.y < 0.5 ? _GpuSpineDeform[deformBase + 2] : local2 + _GpuSpineDeform[deformBase + 2];
        skinned += GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.z], local2) * boneWeights.z;
    }
    if (boneWeights.w > 0.0)
    {
        float2 local3 = influence3;
        if (hasDeform) local3 = deformInfo.y < 0.5 ? _GpuSpineDeform[deformBase + 3] : local3 + _GpuSpineDeform[deformBase + 3];
        skinned += GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.w], local3) * boneWeights.w;
    }
    return float3(skinned, positionOS.z); // z stays at the baked value (draw order layering).
}

// Skins one vertex and transforms the skeleton-space result into world space via the instance's
// localToWorld matrix (the GameObject transform, which the CPU path gets from Unity for free).
// Folded dynamic variant vertices stay collapsed after the transform (one shared world point).
float3 GpuSpineSkinToWorld(float3 positionOS, float4 influence12, float2 influence3, float4 boneIndices, float4 boneWeights, float2 dynInfo, float3 deformInfo, uint instanceID)
{
    float3 skeletonPos = GpuSpineSkinPosition(positionOS, influence12, influence3, boneIndices, boneWeights, dynInfo, deformInfo, instanceID);
    return mul(GpuSpineGetLocalToWorld(instanceID), float4(skeletonPos, 1.0)).xyz;
}

// Per-instance skeleton color (skeleton.R/G/B/A).
float4 GpuSpineGetInstanceColor(uint instanceID)
{
    return _GpuSpineInstances[instanceID].color;
}

// Combined vertex color before the material tint: attachment COLOR x slot color x instance
// skeleton color, mirroring the CPU composition (skeleton.RGBA x slot.RGBA x attachment.RGBA,
// MeshGenerator.cs:953-972). A zero slot count short-circuits the slot multiply (slot colors
// effectively all 1); slotIndex comes from TEXCOORD7.
float4 GpuSpineGetVertexColor(float4 attachmentColor, float slotIndex, uint instanceID)
{
    float4 c = attachmentColor;
    if (_GpuSpineSlotCount > 0 && slotIndex > -0.5)
        c *= _GpuSpineSlotColors[instanceID * _GpuSpineSlotCount + (uint)slotIndex];
    return c * _GpuSpineInstances[instanceID].color;
}

// Shared premultiplied vertex-color composition, including additive slot alpha.
float4 GpuSpinePremultiplyColor(float4 color, float additiveFlag)
{
    float alpha = additiveFlag > 0.5 ? LinearToSRGB(color.a) : color.a;
    color.rgb *= alpha;
    if (additiveFlag > 0.5) color.a = 0;
    return color;
}

// Per-instance extension slot 0, filled via the GpuSpineInstanceDataWriter callback.
float4 GpuSpineGetInstanceCustom0(uint instanceID)
{
    return _GpuSpineInstances[instanceID].custom0;
}

// Per-instance extension slot 1, filled via the GpuSpineInstanceDataWriter callback.
float4 GpuSpineGetInstanceCustom1(uint instanceID)
{
    return _GpuSpineInstances[instanceID].custom1;
}

#endif // GPU_SPINE_SKINNING_INCLUDED
