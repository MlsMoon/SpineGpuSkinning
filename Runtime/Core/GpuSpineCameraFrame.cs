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
			readonly Mesh mesh;
			readonly Material pageMaterial;
			readonly Material materialOverride;
			readonly int layer;
			readonly uint renderingLayer;
			readonly UnityEngine.Rendering.ShadowCastingMode shadows;
			readonly GpuSpineSortMode sortMode;

			public BatchKey (GpuSpineBakedEntry entry, int submeshIndex, GpuSkeletonRenderer source) {
				this.entry = entry.ResourceOwner;
				mesh = entry.Mesh;
				pageMaterial = entry.Submeshes[submeshIndex].PageMaterial;
				materialOverride = source.MaterialOverride;
				layer = source.SourceRenderer.gameObject.layer;
				renderingLayer = source.SourceRenderer.renderingLayerMask;
				shadows = source.SourceRenderer.shadowCastingMode;
				sortMode = source.SortMode;
			}

			public bool Equals (BatchKey other) {
				return ReferenceEquals(entry, other.entry) && ReferenceEquals(mesh, other.mesh) && ReferenceEquals(pageMaterial, other.pageMaterial)
					&& ReferenceEquals(materialOverride, other.materialOverride) && layer == other.layer && renderingLayer == other.renderingLayer && shadows == other.shadows && sortMode == other.sortMode;
			}

			public override bool Equals (object obj) {
				return obj is BatchKey && Equals((BatchKey)obj);
			}

			public override int GetHashCode () {
				return RuntimeHelpers.GetHashCode(entry) ^ RuntimeHelpers.GetHashCode(mesh) ^ (ReferenceEquals(pageMaterial, null) ? 0 : RuntimeHelpers.GetHashCode(pageMaterial)) ^ (layer * 397)
					^ (int)sortMode ^ (ReferenceEquals(materialOverride, null) ? 0 : RuntimeHelpers.GetHashCode(materialOverride));
			}
		}

		/// <summary>Upper bound of empty batches kept alive for reuse by this camera. Draw-order and
		/// skin changes move instances between entries, which would otherwise dispose and recreate the
		/// same (entry, submesh) batches — cloned materials, GPU buffers and CPU staging arrays — every
		/// time. The pool bounds that churn; overflow falls back to the old dispose behaviour.</summary>
		const int MaxIdleBatches = 64;

		/// <summary>Process-wide batch lifecycle counters (monotonic), for allocation-churn
		/// diagnostics. Read via <see cref="GpuSkinningManager.GetLifecycleCounters"/>.</summary>
		internal static int BatchesCreated, BatchesDisposed, BatchesParked, BatchesReused;

        readonly Dictionary<BatchKey, GpuSpineBatch> batches = new();
        readonly Dictionary<GpuSpineBatch, BatchKey> keysByBatch = new();
		readonly Dictionary<BatchKey, GpuSpineBatch> idleBatches = new();
		readonly List<BatchKey> idleOrder = new(); // Oldest first; eviction order once the pool is full.
        readonly Dictionary<GpuSkeletonRenderer, List<GpuSpineBatch>> batchesByInstance = new();
        readonly Dictionary<GpuSkeletonRenderer, GpuSpineBakedEntry> entriesByInstance = new();
        readonly Dictionary<GpuSkeletonRenderer, BatchKey> sourceKeys = new();
        readonly List<GpuSpineBatchInfo> batchInfoCache = new();
        readonly List<GpuSpineBatchInfo> selectedDraws = new();
        readonly List<GpuSkeletonRenderer> orderedSources = new();
        readonly List<GpuSkeletonRenderer> removed = new();
        readonly Camera camera;
        int preparedFrame = -1;
        public GpuSpineCameraFrame(Camera camera) { this.camera = camera; }
        public IReadOnlyList<GpuSpineBatchInfo> Draws => batchInfoCache;

        public void Prepare(Dictionary<GpuSkeletonRenderer, GpuSpineBakedEntry> sources) {
            if (preparedFrame == Time.frameCount) return;
            preparedFrame = Time.frameCount;
            using (GpuSpineCpuDiagnostics.Membership.Auto()) {
            batchInfoCache.Clear();
            removed.Clear();
            foreach (var pair in batchesByInstance) {
                if (pair.Key == null || !sources.TryGetValue(pair.Key, out var entry) ||
                    !pair.Key.ShouldSubmitTo(camera) || !ReferenceEquals(entriesByInstance[pair.Key], entry) ||
                    !sourceKeys[pair.Key].Equals(new BatchKey(entry, 0, pair.Key))) removed.Add(pair.Key);
            }
            foreach (var source in removed) Remove(source);
            foreach (var pair in sources) {
                if (!pair.Key.ShouldSubmitTo(camera) || batchesByInstance.ContainsKey(pair.Key)) continue;
                var joined = new List<GpuSpineBatch>();
                for (int i = 0; i < pair.Value.Submeshes.Length; i++) {
                    var key = new BatchKey(pair.Value, i, pair.Key);
                    if (!batches.TryGetValue(key, out var batch)) {
                        batch = TakeIdle(key);
                        if (batch != null) {
                            BatchesReused++;
                        } else {
                            Material material = CreateBatchMaterial(pair.Value.Submeshes[i].PageMaterial, pair.Key);
                            if (material == null) continue;
                            material.SetInt("_GpuSpineRenderingLayerMask", unchecked((int)pair.Key.SourceRenderer.renderingLayerMask));
                            batch = new GpuSpineBatch(pair.Value, material);
                            BatchesCreated++;
                        }
                        batches.Add(key, batch); keysByBatch.Add(batch, key);
                    }
                    if (!joined.Contains(batch)) { batch.Add(pair.Key); joined.Add(batch); }
                }
                if (joined.Count > 0) {
                    pair.Value.RetainRuntimeLayout();
                    batchesByInstance.Add(pair.Key, joined);
                    entriesByInstance.Add(pair.Key, pair.Value);
                    sourceKeys.Add(pair.Key, new BatchKey(pair.Value, 0, pair.Key));
                }
            }
            }
            foreach (GpuSpineBatch batch in batches.Values) batch.PrepareFrame(camera);
            using (GpuSpineCpuDiagnostics.CameraSort.Auto()) {
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
            }
            for (int i = 0; i < orderedSources.Count;) {
                GpuSkeletonRenderer source = orderedSources[i];
                GpuSpineBakedEntry entry = entriesByInstance[source];
                int count = 1;
                // Merge instances only for a single segment with the same geometry range. Multi-segment characters keep per-character painter order.
                if (entry.Submeshes.Length == 1) {
                    GpuSpineBatch batch = batchesByInstance[source][0];
                    int start = batch.FindSourceIndex(source);
                    while (i + count < orderedSources.Count) {
                        GpuSkeletonRenderer next = orderedSources[i + count];
                        GpuSpineBakedEntry nextEntry = entriesByInstance[next];
                        if (nextEntry.Submeshes.Length != 1 || batchesByInstance[next][0] != batch ||
                            entry.Submeshes[0].IndexStart != nextEntry.Submeshes[0].IndexStart ||
                            entry.Submeshes[0].IndexCount != nextEntry.Submeshes[0].IndexCount ||
                            batch.FindSourceIndex(next) != start + count) break;
                        count++;
                    }
                }
                for (int part = 0; part < entry.Submeshes.Length; part++) {
                    if (!batches.TryGetValue(new BatchKey(entry, part, source), out GpuSpineBatch batch)) continue;
                    GpuSpineBatchInfo draw = batch.GetSlice(entry.Submeshes[part], batch.FindSourceIndex(source), count);
                    batchInfoCache.Add(draw);
                    MeshRenderer renderer = source.SourceRenderer;
                    var parameters = new RenderParams(draw.Material) {
                        camera = camera, layer = renderer.gameObject.layer,
                        renderingLayerMask = renderer.renderingLayerMask, worldBounds = draw.Bounds,
                        shadowCastingMode = renderer.shadowCastingMode, receiveShadows = renderer.receiveShadows,
                        reflectionProbeUsage = renderer.reflectionProbeUsage, rendererPriority = renderer.rendererPriority
                    };
                    using (GpuSpineCpuDiagnostics.Draw.Auto()) Graphics.RenderMeshIndirect(parameters, draw.Mesh, draw.ArgsBuffer);
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
            GpuSpineBakedEntry entry = entriesByInstance[source];
            for (int part = 0; part < entry.Submeshes.Length; part++) {
                if (!batches.TryGetValue(new BatchKey(entry, part, source), out GpuSpineBatch batch)) continue;
                int index = batch.FindSourceIndex(source);
                if (index >= 0) selectedDraws.Add(batch.GetSlice(entry.Submeshes[part], index, 1));
            }
            return selectedDraws;
        }

        /// <summary>Keep members and buffers when only the draw range changes, so an animation reorder does not rebuild the joined list.</summary>
        public void ChangeLayout(GpuSkeletonRenderer source, GpuSpineBakedEntry entry) {
            if (!batchesByInstance.TryGetValue(source, out var joined)) return;
            // The group required by the new layout must already exist. A page-set change still unregisters and registers fully.
            for (int part = 0; part < entry.Submeshes.Length; part++) {
                if (!batches.TryGetValue(new BatchKey(entry, part, source), out var batch) || !joined.Contains(batch)) {
                    Remove(source); return;
                }
            }
            foreach (var batch in joined) {
                bool needed = false;
                for (int part = 0; part < entry.Submeshes.Length; part++) {
                    if (keysByBatch[batch].Equals(new BatchKey(entry, part, source))) { needed = true; break; }
                }
                if (!needed) { Remove(source); return; }
            }
            entriesByInstance[source].ReleaseRuntimeLayout(); entry.RetainRuntimeLayout();
            entriesByInstance[source] = entry; sourceKeys[source] = new BatchKey(entry, 0, source);
            preparedFrame = -1; batchInfoCache.Clear(); selectedDraws.Clear();
        }

        public void Remove(GpuSkeletonRenderer source) {
            if (!batchesByInstance.TryGetValue(source, out var joined)) return;
            batchInfoCache.Clear();
            selectedDraws.Clear();
            preparedFrame = -1;
            entriesByInstance[source].ReleaseRuntimeLayout();
            entriesByInstance.Remove(source); sourceKeys.Remove(source);
            batchesByInstance.Remove(source);
            foreach (var batch in joined) {
                batch.Remove(source);
                if (batch.RegisteredCount != 0) continue;
                BatchKey key = keysByBatch[batch];
                batches.Remove(key); keysByBatch.Remove(batch);
                ParkIdle(key, batch);
            }
        }

		/// <summary>Takes a pooled empty batch for the key, or null when none is parked.</summary>
		GpuSpineBatch TakeIdle (BatchKey key) {
			if (!idleBatches.TryGetValue(key, out GpuSpineBatch batch)) return null;
			idleBatches.Remove(key);
			idleOrder.Remove(key);
			return batch;
		}

		/// <summary>Parks an emptied batch for reuse instead of disposing it. The membership version
		/// bump on the next Add re-triggers every dirty upload, and EnsureCapacity rebinds all slice
		/// materials after buffer regrowth, so a reused batch needs no extra reset. Once the pool is
		/// full the oldest parked batch is disposed (the pre-pool behaviour for every empty batch).</summary>
		void ParkIdle (BatchKey key, GpuSpineBatch batch) {
			if (idleBatches.Count >= MaxIdleBatches) {
				BatchKey oldest = idleOrder[0];
				idleOrder.RemoveAt(0);
				GpuSpineBatch evicted = idleBatches[oldest];
				idleBatches.Remove(oldest);
				evicted.Dispose();
				BatchesDisposed++;
			}
			idleBatches.Add(key, batch);
			idleOrder.Add(key);
			BatchesParked++;
		}

        public void Dispose() {
            foreach (var entry in entriesByInstance.Values) entry.ReleaseRuntimeLayout();
            entriesByInstance.Clear(); sourceKeys.Clear();
            foreach (var batch in batches.Values) batch.Dispose();
			foreach (var batch in idleBatches.Values) batch.Dispose();
			BatchesDisposed += batches.Count + idleBatches.Count;
            batchesByInstance.Clear(); batches.Clear(); keysByBatch.Clear(); batchInfoCache.Clear();
			idleBatches.Clear(); idleOrder.Clear();
        }
		Material CreateBatchMaterial (Material pageMaterial, GpuSkeletonRenderer renderer) {
			Material materialOverride = renderer.MaterialOverride;
			Material source = pageMaterial != null ? pageMaterial : materialOverride;
			if (source == null) {
				if (GpuSpineDiagnostics.EnableLogging) Debug.LogError("GpuSkinningManager: cannot create a batch material, both the atlas page material and the material override are null.");
				return null;
			}
			bool useOverride = materialOverride != null && (pageMaterial == null || pageMaterial.shader == materialOverride.shader);
			Shader shader = renderer.ResolveMaterialShader(pageMaterial);
			if (shader == null || !shader.isSupported) return null;
			Material clone = new Material(source);
			if (clone.shader != shader) clone.shader = shader;
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
