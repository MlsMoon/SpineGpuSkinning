using System.Collections.Generic;
using Spine;
using UnityEngine;

namespace GpuSpine.Baking {
    /// <summary>Builds index-only draw-order variants from the baked attachment streams.</summary>
    public static class GpuSpineOrderBaker {
        sealed class Run {
            public Material Material;
            public bool Additive;
            public readonly List<int> Indices = new List<int>();
        }

        public static void Bake (SkeletonData data, GpuSpineBakedEntry entry) {
            if (entry.Mesh == null || entry.VertexCount == 0) return;
            GpuSpineBoundsBaker.Bake(entry);
            var channels = new List<Vector2>();
            entry.Mesh.GetUVs(7, channels);
            var slots = new List<Run>[data.Slots.Count];
            for (int s = 0; s < slots.Length; s++) slots[s] = new List<Run>();
            for (int submesh = 0; submesh < entry.Submeshes.Length; submesh++) {
                GpuSpineSubmesh source = entry.Submeshes[submesh];
                int[] indices = entry.Mesh.GetTriangles(submesh);
                for (int i = 0; i < indices.Length; i += 3) {
                    int slot = (int)channels[indices[i]].x;
                    List<Run> runs = slots[slot];
                    Run run = runs.Count == 0 ? null : runs[runs.Count - 1];
                    if (run == null || run.Material != source.PageMaterial) {
                        run = new Run { Material = source.PageMaterial, Additive = source.HasPmaAdditiveSlot };
                        runs.Add(run);
                    }
                    run.Indices.Add(indices[i]); run.Indices.Add(indices[i + 1]); run.Indices.Add(indices[i + 2]);
                }
            }
            List<int[]> orders = CollectOrders(data);
            var layouts = new List<GpuSpineDrawOrderLayout>();
            for (int o = 0; o < orders.Count; o++) {
                var runs = new List<Run>();
                foreach (int slot in orders[o]) {
                    foreach (Run source in slots[slot]) {
                        Run run = runs.Count == 0 ? null : runs[runs.Count - 1];
                        if (run == null || run.Material != source.Material) {
                            run = new Run { Material = source.Material };
                            runs.Add(run);
                        }
                        run.Additive |= source.Additive;
                        run.Indices.AddRange(source.Indices);
                    }
                }
                string key = GpuSpineDrawOrderKey.Compute(orders[o]);
                var indices = new List<int>();
                var submeshes = new GpuSpineSubmesh[runs.Count];
                for (int r = 0; r < runs.Count; r++) {
                    submeshes[r] = new GpuSpineSubmesh {
                        PageMaterial = runs[r].Material, IndexCount = runs[r].Indices.Count,
                        IndexStart = indices.Count, HasPmaAdditiveSlot = runs[r].Additive
                    };
                    indices.AddRange(runs[r].Indices);
                }
                layouts.Add(new GpuSpineDrawOrderLayout { Key = key, Indices = indices.ToArray(), Submeshes = submeshes });
                if (o == 0) {
                    entry.Mesh.subMeshCount = submeshes.Length;
                    for (int r = 0; r < submeshes.Length; r++)
                        entry.Mesh.SetTriangles(indices, submeshes[r].IndexStart, submeshes[r].IndexCount, r, false);
                    entry.Submeshes = submeshes;
                }
            }
            entry.DrawOrderLayouts = layouts.ToArray();
            entry.ClipVertexCapacity = ComputeClipCapacity(data, entry.SkinNames);
        }

        static List<int[]> CollectOrders (SkeletonData data) {
            int[] setup = new int[data.Slots.Count];
            for (int i = 0; i < setup.Length; i++) setup[i] = i;
            var orders = new List<int[]> { setup };
            var keys = new HashSet<string> { GpuSpineDrawOrderKey.Compute(setup) };
            foreach (Spine.Animation animation in data.Animations) {
                foreach (Timeline timeline in animation.Timelines) {
                    if (!(timeline is DrawOrderTimeline orderTimeline)) continue;
                    foreach (int[] order in orderTimeline.DrawOrders) {
                        if (order != null && keys.Add(GpuSpineDrawOrderKey.Compute(order))) orders.Add(order);
                    }
                }
            }
            return orders;
        }

        static int ComputeClipCapacity (SkeletonData data, string[] skins) {
            Skin effective = GpuSpineBaker.BuildEffectiveSkin(data, skins, out _);
            int[] maxima = new int[data.Slots.Count];
            foreach (Skin skin in new[] { data.DefaultSkin, effective }) {
                if (skin == null) continue;
                foreach (Skin.SkinEntry item in skin.Attachments) {
                    if (item.Attachment is ClippingAttachment clip)
                        maxima[item.SlotIndex] = Mathf.Max(maxima[item.SlotIndex], (clip.WorldVerticesLength / 2 - 2) * 3);
                }
            }
            int total = 0;
            foreach (int count in maxima) total += count;
            return total;
        }
    }
}
