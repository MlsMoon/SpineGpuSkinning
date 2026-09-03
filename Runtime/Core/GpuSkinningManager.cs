using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GpuSpine.Baking;
using GpuSpine.Core;
using UnityEngine;

namespace GpuSpine {
	/// <summary>
	/// Central submission hub of the GPU skinning path. Lazily created at play mode start
	/// (HideAndDontSave, DontDestroyOnLoad). GpuSkeletonRenderer instances register here with their
	/// baked entry; the manager groups them into per-(entry, submesh) batches and, once per
	/// frame after all skeleton updates (execution order 32000, later than every UpdateComplete),
	/// writes the bone palettes and instance data, sorts back-to-front, uploads and submits
	/// Graphics.DrawMeshInstancedIndirect. Zero cost while no instance is registered.
	/// </summary>
	[DefaultExecutionOrder(32000)]
	public sealed class GpuSkinningManager : MonoBehaviour {
		/// <summary>Default shader of the plugin (standard universal URP unlit PMA).</summary>
		const string DefaultShaderName = "GpuSpine/URP/Skeleton";

		struct BatchKey : IEquatable<BatchKey> {
			readonly GpuSpineBakedEntry entry;
			readonly int submeshIndex;

			public BatchKey (GpuSpineBakedEntry entry, int submeshIndex) {
				this.entry = entry;
				this.submeshIndex = submeshIndex;
			}

			public bool Equals (BatchKey other) {
				return ReferenceEquals(entry, other.entry) && submeshIndex == other.submeshIndex;
			}

			public override bool Equals (object obj) {
				return obj is BatchKey && Equals((BatchKey)obj);
			}

			public override int GetHashCode () {
				return RuntimeHelpers.GetHashCode(entry) ^ (submeshIndex * 31);
			}
		}

		static GpuSkinningManager instance;
		static readonly GpuSpineBatchInfo[] noBatches = new GpuSpineBatchInfo[0];

		readonly Dictionary<BatchKey, GpuSpineBatch> batches = new Dictionary<BatchKey, GpuSpineBatch>();
		readonly Dictionary<GpuSkeletonRenderer, List<GpuSpineBatch>> batchesByInstance = new Dictionary<GpuSkeletonRenderer, List<GpuSpineBatch>>();
		readonly List<GpuSpineBatchInfo> batchInfoCache = new List<GpuSpineBatchInfo>();
		Shader defaultShader;
		bool defaultShaderSearched;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void ResetStatics () {
			instance = null; // Covers play mode entry with domain reload disabled.
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		static void Bootstrap () {
			EnsureInstance();
		}

		/// <summary>The play mode singleton (null in edit mode).</summary>
		public static GpuSkinningManager Instance { get { return instance; } }

		static GpuSkinningManager EnsureInstance () {
			if (instance == null) {
				GameObject go = new GameObject("GpuSkinningManager");
				go.hideFlags = HideFlags.HideAndDontSave;
				DontDestroyOnLoad(go);
				instance = go.AddComponent<GpuSkinningManager>();
			}
			return instance;
		}

		/// <summary>Registers an instance into one batch per submesh of its baked entry. Registering an
		/// already registered instance is a no-op.</summary>
		public static void Register (GpuSkeletonRenderer renderer, GpuSpineBakedEntry entry) {
			GpuSkinningManager manager = EnsureInstance();
			if (manager.batchesByInstance.ContainsKey(renderer)) return;
			List<GpuSpineBatch> joined = new List<GpuSpineBatch>(entry.Submeshes.Length);
			for (int i = 0; i < entry.Submeshes.Length; i++) {
				BatchKey key = new BatchKey(entry, i);
				GpuSpineBatch batch;
				if (!manager.batches.TryGetValue(key, out batch)) {
					Material material = manager.CreateBatchMaterial(entry.Submeshes[i].PageMaterial, renderer.MaterialOverride);
					if (material == null) continue; // No usable material source; error already logged.
					batch = new GpuSpineBatch(entry, i, material);
					manager.batches.Add(key, batch);
				}
				batch.Add(renderer);
				joined.Add(batch);
			}
			manager.batchesByInstance.Add(renderer, joined);
		}

		/// <summary>Removes an instance from all its batches; batches left empty are disposed (buffers
		/// released, cloned material destroyed).</summary>
		public static void Unregister (GpuSkeletonRenderer renderer) {
			GpuSkinningManager manager = instance;
			if (manager == null) return;
			List<GpuSpineBatch> joined;
			if (!manager.batchesByInstance.TryGetValue(renderer, out joined)) return;
			manager.batchesByInstance.Remove(renderer);
			for (int i = 0; i < joined.Count; i++) {
				GpuSpineBatch batch = joined[i];
				batch.Remove(renderer);
				if (batch.RegisteredCount == 0) {
					manager.batches.Remove(new BatchKey(batch.Entry, batch.SubmeshIndex));
					batch.Dispose();
				}
			}
		}

		/// <summary>Moves an instance to the batches of another baked entry (skin combination change).</summary>
		public static void ChangeEntry (GpuSkeletonRenderer renderer, GpuSpineBakedEntry newEntry) {
			Unregister(renderer);
			Register(renderer, newEntry);
		}

		/// <summary>
		/// Read-only views of the batches submitted this frame, for custom RenderPasses that resubmit
		/// the same indirect draws into their own pass. The list is reused; do not cache it across frames.
		/// </summary>
		public static IReadOnlyList<GpuSpineBatchInfo> GetBatches () {
			GpuSkinningManager manager = instance;
			if (manager == null) return noBatches;
			manager.batchInfoCache.Clear();
			foreach (KeyValuePair<BatchKey, GpuSpineBatch> pair in manager.batches) {
				GpuSpineBatch batch = pair.Value;
				if (batch.SubmittedCount == 0) continue;
				manager.batchInfoCache.Add(new GpuSpineBatchInfo {
					Mesh = batch.Entry.Mesh,
					SubmeshIndex = batch.SubmeshIndex,
					Material = batch.Material,
					ArgsBuffer = batch.ArgsBuffer,
					Bounds = batch.Bounds,
					InstanceCount = batch.SubmittedCount
				});
			}
			return manager.batchInfoCache;
		}

		void LateUpdate () {
			if (batches.Count == 0) return; // Zero cost while nothing is registered.
			Camera camera = ResolveCamera();
			foreach (KeyValuePair<BatchKey, GpuSpineBatch> pair in batches)
				pair.Value.SubmitFrame(camera);
		}

		void OnDestroy () {
			foreach (KeyValuePair<BatchKey, GpuSpineBatch> pair in batches) pair.Value.Dispose();
			batches.Clear();
			batchesByInstance.Clear();
			if (instance == this) instance = null;
		}

		/// <summary>
		/// Creates the cloned batch material for one atlas page. Always a clone, never the shared page
		/// material: the Spine runtime flips enableInstancing on shared materials
		/// (SkeletonRenderer.SetMaterialSettingsToFixDrawOrder). The material override contributes its
		/// shader; property values (including the atlas texture) stay with the page material clone.
		/// Without an override the plugin default shader applies.
		/// </summary>
		Material CreateBatchMaterial (Material pageMaterial, Material materialOverride) {
			Material source = pageMaterial != null ? pageMaterial : materialOverride;
			if (source == null) {
				Debug.LogError("GpuSkinningManager: cannot create a batch material, both the atlas page material and the material override are null.");
				return null;
			}
			Material clone = new Material(source);
			Shader shader = materialOverride != null ? materialOverride.shader : GetDefaultShader();
			if (shader != null) clone.shader = shader;
			clone.enableInstancing = true; // Required by DrawMeshInstancedIndirect.
			clone.name = source.name + "_GpuSpine";
			return clone;
		}

		Shader GetDefaultShader () {
			if (!defaultShaderSearched) {
				defaultShader = Shader.Find(DefaultShaderName);
				defaultShaderSearched = true;
				if (defaultShader == null)
					Debug.LogError("GpuSkinningManager: default shader '" + DefaultShaderName + "' not found; batch materials keep the page material shader.");
			}
			return defaultShader;
		}

		static Camera ResolveCamera () {
			Camera main = Camera.main;
			if (main != null) return main;
			Camera[] all = Camera.allCameras;
			return all.Length > 0 ? all[0] : null;
		}
	}
}
