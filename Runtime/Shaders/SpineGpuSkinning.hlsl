#ifndef GPU_SPINE_SKINNING_INCLUDED
#define GPU_SPINE_SKINNING_INCLUDED

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

// Mirror of GpuSpine.Core.GpuSpineInstanceData (C#: Matrix4x4 + 3 x Vector4, 112 bytes,
// LayoutKind.Sequential). row_major matches the byte order of UnityEngine.Matrix4x4, so
// mul(localToWorld, pos) applies the C# matrix as-is.
struct GpuSpineInstanceData
{
    row_major float4x4 localToWorld;
    float4 color;
    float4 custom0;
    float4 custom1;
};

// Bone matrix palette, layout [instanceID * boneCount + boneIndex].
StructuredBuffer<GpuBoneMatrix> _GpuSpineBones;
// Per-instance draw data, layout [instanceID].
StructuredBuffer<GpuSpineInstanceData> _GpuSpineInstances;
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
// dynInfo = TEXCOORD5 (x = dynamic slot id, -1 for static vertices; y = variant id).
float3 GpuSpineSkinPosition(float3 positionOS, float4 influence12, float2 influence3, float4 boneIndices, float4 boneWeights, float2 dynInfo, uint instanceID)
{
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
    // Influence 0 always exists in the baked stream; the remaining influences skip weight 0.
    float2 skinned = GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.x], positionOS.xy) * boneWeights.x;
    if (boneWeights.y > 0.0)
        skinned += GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.y], influence12.xy) * boneWeights.y;
    if (boneWeights.z > 0.0)
        skinned += GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.z], influence12.zw) * boneWeights.z;
    if (boneWeights.w > 0.0)
        skinned += GpuSpineApplyBoneMatrix(_GpuSpineBones[baseIndex + (uint)boneIndices.w], influence3) * boneWeights.w;
    return float3(skinned, positionOS.z); // z stays at the baked value (draw order layering).
}

// Skins one vertex and transforms the skeleton-space result into world space via the instance's
// localToWorld matrix (the GameObject transform, which the CPU path gets from Unity for free).
// Folded dynamic variant vertices stay collapsed after the transform (one shared world point).
float3 GpuSpineSkinToWorld(float3 positionOS, float4 influence12, float2 influence3, float4 boneIndices, float4 boneWeights, float2 dynInfo, uint instanceID)
{
    float3 skeletonPos = GpuSpineSkinPosition(positionOS, influence12, influence3, boneIndices, boneWeights, dynInfo, instanceID);
    return mul(_GpuSpineInstances[instanceID].localToWorld, float4(skeletonPos, 1.0)).xyz;
}

// Per-instance skeleton color (skeleton.R/G/B/A).
float4 GpuSpineGetInstanceColor(uint instanceID)
{
    return _GpuSpineInstances[instanceID].color;
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
