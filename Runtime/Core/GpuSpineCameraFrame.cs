using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GpuSpine.Baking;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSpine.Core {
    /// <summary>Owns camera-specific membership, ordering, buffers and immutable draw slices.</summary>
    internal sealed class GpuSpineCameraFrame : IDisposable {
		struct BatchKey : IEquatable<BatchKey> {
			readonly GpuSpineBakedEntry entry;
			readonly int submeshIndex;
			readonly Material materialOverride;
			readonly int layer;
			readonly uint renderingLayer;
			readonly UnityEngine.Rendering.ShadowCastingMode shadows;
			readonly GpuSpineSortMode sortMode;

			public BatchKey (GpuSpineBakedEntry entry, int submeshIndex, GpuSkeletonRenderer source) {
				this.entry = entry;
				this.submeshIndex = submeshIndex;
				materialOverride = source.MaterialOverride;
				layer = source.SourceRenderer.gameObject.layer;
				renderingLayer = source.SourceRenderer.renderingLayerMask;
				shadows = source.SourceRenderer.shadowCastingMode;
				sortMode = source.SortMode;
			}

			public bool Equals (BatchKey other) {
				return ReferenceEquals(entry, other.entry) && submeshIndex == other.submeshIndex
					&& ReferenceEquals(materialOverride, other.materialOverride) && layer == other.layer && renderingLayer == other.renderingLayer && shadows == other.shadows && sortMode == other.sortMode;
			}

			public override bool Equals (object obj) {
				return obj is BatchKey && Equals((BatchKey)obj);
			}

			public override int GetHashCode () {
				return RuntimeHelpers.GetHashCode(entry) ^ (submeshIndex * 31) ^ (layer * 397)
					^ (int)sortMode ^ (ReferenceEquals(materialOverride, null) ? 0 : RuntimeHelpers.GetHashCode(materialOverride));
			}
		}

        readonly Dictionary<BatchKey, GpuSpineBatch> batches = new();
        readonly Dictionary<GpuSpineBatch, BatchKey> keysByBatch = new();
        readonly Dictionary<GpuSkeletonRenderer, List<GpuSpineBatch>> batchesByInstance = new();
        readonly List<GpuSpineBatchInfo> batchInfoCache = new();
        readonly List<GpuSpineBatchInfo> selectedDraws = new();
        readonly List<GpuSkeletonRenderer> orderedSources = new();
        readonly List<GpuSkeletonRenderer> removed = new();
        readonly Camera camera;
        int preparedFrame = -1;
        public GpuSpineCameraFrame(Camera camera) { this.camera = camera; }
        public IReadOnlyList<GpuSpineBatchInfo> Draws => batchInfoCache;

        public void Prepare(IReadOnlyDictionary<GpuSkeletonRenderer, GpuSpineBakedEntry> sources) {
            if (preparedFrame == Time.frameCount) return;
            preparedFrame = Time.frameCount;
            batchInfoCache.Clear();
            removed.Clear();
            foreach (var pair in batchesByInstance) {
                if (pair.Key == null || !sources.TryGetValue(pair.Key, out var entry) ||
                    !pair.Key.ShouldSubmitTo(camera) || !ReferenceEquals(pair.Value[0].Entry, entry) ||
                    !keysByBatch[pair.Value[0]].Equals(new BatchKey(entry, 0, pair.Key))) removed.Add(pair.Key);
            }
            foreach (var source in removed) Remove(source);
            foreach (var pair in sources) {
                if (!pair.Key.ShouldSubmitTo(camera) || batchesByInstance.ContainsKey(pair.Key)) continue;
                var joined = new List<GpuSpineBatch>();
                for (int i = 0; i < pair.Value.Submeshes.Length; i++) {
                    var key = new BatchKey(pair.Value, i, pair.Key);
                    if (!batches.TryGetValue(key, out var batch)) {
                        Material material = CreateBatchMaterial(pair.Value.Submeshes[i].PageMaterial, pair.Key);
                        if (material == null) continue;
                        material.SetInt("_GpuSpineRenderingLayerMask", unchecked((int)pair.Key.SourceRenderer.renderingLayerMask));
                        batch = new GpuSpineBatch(pair.Value, i, material);
                        batches.Add(key, batch); keysByBatch.Add(batch, key);
                    }
                    batch.Add(pair.Key); joined.Add(batch);
                }
                if (joined.Count > 0) {
                    pair.Value.RetainRuntimeLayout();
                    batchesByInstance.Add(pair.Key, joined);
                }
            }
            foreach (GpuSpineBatch batch in batches.Values) batch.PrepareFrame(camera);
            orderedSources.Clear();
            foreach (var source in batchesByInstance.Keys) orderedSources.Add(source);
			for (int i = 1; i < orderedSources.Count; i++) {
				GpuSkeletonRenderer source = orderedSources[i];
				int j = i - 1;
				while (j >= 0 && Depth(orderedSources[j], camera) < Depth(source, camera)) {
					orderedSources[j + 1] = orderedSources[j];
					j--;
				}
				orderedSources[j + 1] = source;
			}
			for (int i = 0; i < orderedSources.Count;) {
				GpuSkeletonRenderer source = orderedSources[i];
				List<GpuSpineBatch> joined = batchesByInstance[source];
				int count = 1;
				if (joined.Count == 1) {
					GpuSpineBatch batch = joined[0];
					int start = batch.FindSourceIndex(source);
					while (i + count < orderedSources.Count) {
						List<GpuSpineBatch> next = batchesByInstance[orderedSources[i + count]];
						if (next.Count != 1 || next[0] != batch ||
							batch.FindSourceIndex(orderedSources[i + count]) != start + count) break;
						count++;
					}
				}
				foreach (GpuSpineBatch batch in joined) {
					GpuSpineBatchInfo draw = batch.GetSlice(batch.FindSourceIndex(source), count);
					batchInfoCache.Add(draw);
					MeshRenderer renderer = source.SourceRenderer;
					var parameters = new RenderParams(draw.Material) {
						camera = camera, layer = renderer.gameObject.layer,
						renderingLayerMask = renderer.renderingLayerMask, worldBounds = draw.Bounds,
						shadowCastingMode = renderer.shadowCastingMode, receiveShadows = renderer.receiveShadows,
						reflectionProbeUsage = renderer.reflectionProbeUsage, rendererPriority = renderer.rendererPriority
					};
					Graphics.RenderMeshIndirect(parameters, draw.Mesh, draw.ArgsBuffer);
				}
				i += count;
			}
            preparedFrame = Time.frameCount;
		}


		static float Depth (GpuSkeletonRenderer source, Camera camera) {
			if (source.SortMode == GpuSpineSortMode.None) return 0f;
			if (source.SortMode == GpuSpineSortMode.WorldZBackToFront) return source.transform.position.z;
			return camera != null ? Vector3.Dot(source.transform.position - camera.transform.position, camera.transform.forward) : 0f;
		}


        public IReadOnlyList<GpuSpineBatchInfo> GetDraws(GpuSkeletonRenderer source) {
            selectedDraws.Clear();
            if (!source.ShouldSubmitTo(camera) || !batchesByInstance.TryGetValue(source, out var joined))
                return selectedDraws;
            foreach (var batch in joined) {
                int index = batch.FindSourceIndex(source);
                if (index >= 0) selectedDraws.Add(batch.GetSlice(index, 1));
            }
            return selectedDraws;
        }

        public void Remove(GpuSkeletonRenderer source) {
            if (!batchesByInstance.TryGetValue(source, out var joined)) return;
            batchInfoCache.Clear();
            selectedDraws.Clear();
            preparedFrame = -1;
            joined[0].Entry.ReleaseRuntimeLayout();
            batchesByInstance.Remove(source);
            foreach (var batch in joined) {
                batch.Remove(source);
                if (batch.RegisteredCount != 0) continue;
                batches.Remove(keysByBatch[batch]); keysByBatch.Remove(batch); batch.Dispose();
            }
        }

        public void Dispose() {
            foreach (var joined in batchesByInstance.Values) joined[0].Entry.ReleaseRuntimeLayout();
            foreach (var batch in batches.Values) batch.Dispose();
            batchesByInstance.Clear(); batches.Clear(); keysByBatch.Clear(); batchInfoCache.Clear();
        }
		Material CreateBatchMaterial (Material pageMaterial, GpuSkeletonRenderer renderer) {
			Material materialOverride = renderer.MaterialOverride;
			Material source = pageMaterial != null ? pageMaterial : materialOverride;
			if (source == null) {
				Debug.LogError("GpuSkinningManager: cannot create a batch material, both the atlas page material and the material override are null.");
				return null;
			}
			bool useOverride = materialOverride != null && (pageMaterial == null || pageMaterial.shader == materialOverride.shader);
			Shader shader = renderer.ResolveMaterialShader(pageMaterial);
			if (shader == null || !shader.isSupported) return null;
			Material clone = new Material(source);
			clone.shader = shader;
			// A material override also contributes its shader keywords, so project-side shaders can
			// ship a GPU-skinning variant keyword (e.g. SPINE_GPU_SKINNING) through the clone.
			if (useOverride) {
				foreach (string keyword in materialOverride.shaderKeywords) clone.EnableKeyword(keyword);
			}
			clone.enableInstancing = true; // Required by DrawMeshInstancedIndirect.
			clone.name = source.name + "_GpuSpine";
			return clone;
		}

    }
}
