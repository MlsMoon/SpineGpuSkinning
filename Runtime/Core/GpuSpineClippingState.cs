using System;
using Spine;
using UnityEngine;

namespace GpuSpine.Core {
    /// <summary>Exports only clip polygons; visible mesh vertices are never CPU-skinned here.</summary>
    internal sealed class GpuSpineClippingState {
        readonly Triangulator triangulator = new Triangulator();
        readonly ExposedList<float> polygon = new ExposedList<float>();
        public readonly Vector2[] TriangleVertices;
        public readonly Vector2[] SlotRanges;

        public GpuSpineClippingState (int vertexCapacity, int slotCount) {
            TriangleVertices = new Vector2[vertexCapacity];
            SlotRanges = new Vector2[slotCount];
        }

        public void Update (Skeleton skeleton) {
            Array.Clear(SlotRanges, 0, SlotRanges.Length);
            ClippingAttachment active = null;
            Vector2 range = Vector2.zero;
            int cursor = 0;
            foreach (Slot slot in skeleton.DrawOrder) {
                if (slot.Bone.Active) {
                    if (active == null && slot.Attachment is ClippingAttachment clip) {
                        active = clip;
                        polygon.Resize(clip.WorldVerticesLength);
                        clip.ComputeWorldVertices(slot, 0, clip.WorldVerticesLength, polygon.Items, 0, 2);
                        SkeletonClipping.MakeClockwise(polygon);
                        ExposedList<int> indices = triangulator.Triangulate(polygon);
                        if (cursor + indices.Count > TriangleVertices.Length)
                            throw new InvalidOperationException("GpuSpine clipping capacity is stale; rebake the skeleton.");
                        range = new Vector2(cursor, indices.Count / 3);
                        for (int i = 0; i < indices.Count; i++) {
                            int vertex = indices.Items[i] * 2;
                            TriangleVertices[cursor++] = new Vector2(polygon.Items[vertex], polygon.Items[vertex + 1]);
                        }
                    }
                    if (active != null) SlotRanges[slot.Data.Index] = range;
                }
                if (active != null && active.EndSlot == slot.Data) active = null;
            }
        }

    }
}
