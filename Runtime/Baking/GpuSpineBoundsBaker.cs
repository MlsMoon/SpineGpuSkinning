using System.Collections.Generic;
using UnityEngine;

namespace GpuSpine.Baking {
    /// <summary>Conservative per-bone local envelopes, avoiding runtime vertex skinning for bounds.</summary>
    public static class GpuSpineBoundsBaker {
        public static void Bake(GpuSpineBakedEntry entry) {
            Mesh mesh = entry.Mesh;
            entry.BoneBounds = new Bounds[entry.BoneCount];
            entry.UsedBones = new bool[entry.BoneCount];
            var vertices = new List<Vector3>();
            var influence12 = new List<Vector4>();
            var influence3 = new List<Vector2>();
            var bones = new List<Vector4>();
            var weights = new List<Vector4>();
            mesh.GetVertices(vertices); mesh.GetUVs(1, influence12); mesh.GetUVs(2, influence3);
            mesh.GetUVs(3, bones); mesh.GetUVs(4, weights);
            for (int vertex = 0; vertex < vertices.Count; vertex++) {
                for (int influence = 0; influence < 4; influence++) {
                    if (weights[vertex][influence] <= 0) continue;
                    int bone = (int)bones[vertex][influence];
                    Vector2 point = influence == 0 ? (Vector2)vertices[vertex] :
                        influence == 1 ? new Vector2(influence12[vertex].x, influence12[vertex].y) :
                        influence == 2 ? new Vector2(influence12[vertex].z, influence12[vertex].w) : influence3[vertex];
                    if (!entry.UsedBones[bone]) {
                        entry.UsedBones[bone] = true;
                        entry.BoneBounds[bone] = new Bounds(point, Vector3.zero);
                    } else {
                        Bounds bounds = entry.BoneBounds[bone];
                        bounds.Encapsulate(point); entry.BoneBounds[bone] = bounds;
                    }
                }
            }
        }
    }
}
