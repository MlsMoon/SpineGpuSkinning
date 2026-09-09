using System;
using System.Collections.Generic;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace GpuSpine.Baking {
	/// <summary>
	/// Runtime registry of baked data containers, keyed by the SkeletonDataAsset reference. Baking is
	/// an editor-time step; at runtime nothing is baked here — the game side (or a GpuSkeletonRenderer
	/// with its BakedData field assigned) registers the container sub-asset, and components look up
	/// entries by content key. A lookup miss means "no baked data for this skeleton": the caller logs a
	/// warning and stays on the CPU path.
	/// <para/>
	/// The registry is cleared on every play mode start (SubsystemRegistration covers entering play
	/// mode with domain reload disabled), so registrations must be renewed per session — typically from
	/// the components' OnEnable.
	/// </summary>
	public static class GpuSpineBakedRuntime {
		static readonly Dictionary<SkeletonDataAsset, GpuSpineBakedData> registry = new Dictionary<SkeletonDataAsset, GpuSpineBakedData>();

		static readonly Dictionary<GpuSpineBakedData, int> leases = new Dictionary<GpuSpineBakedData, int>();

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void ResetStatics () {
			ClearRuntimeLayouts();
			registry.Clear(); leases.Clear();
		}

		public static void ClearRuntimeLayouts () {
			foreach (GpuSpineBakedData container in leases.Keys) ClearLayouts(container);
			foreach (GpuSpineBakedData container in registry.Values) ClearLayouts(container);
		}

		static void ClearLayouts(GpuSpineBakedData container) {
				if (container == null) return;
				foreach (GpuSpineBakedEntry entry in container.Entries) entry.ClearRuntimeLayouts();
		}

		internal static void Retain(GpuSpineBakedData data) {
			leases.TryGetValue(data, out int count); leases[data] = count + 1;
		}

		internal static void Release(GpuSpineBakedData data) {
			if (data == null || !leases.TryGetValue(data, out int count)) return;
			if (count > 1) { leases[data] = count - 1; return; }
			leases.Remove(data); ClearLayouts(data);
		}

		/// <summary>Registers (or replaces) the baked data container of a skeleton data asset.</summary>
		public static void Register (SkeletonDataAsset asset, GpuSpineBakedData data) {
			if (asset == null) throw new ArgumentNullException("asset");
			if (data == null) throw new ArgumentNullException("data");
			registry[asset] = data;
		}

		/// <summary>Returns the registered baked data container of a skeleton data asset, if any.</summary>
		public static bool TryGet (SkeletonDataAsset asset, out GpuSpineBakedData data) {
			if (asset == null) {
				data = null;
				return false;
			}
			if (registry.TryGetValue(asset, out data)) return true;
			foreach (var leased in leases.Keys) {
				if (leased != null && leased.SourceAsset == asset) { data = leased; return true; }
			}
			return false;
		}

		/// <summary>Removes the registration of a skeleton data asset (no-op when not registered).</summary>
		public static void Unregister (SkeletonDataAsset asset) {
			if (asset == null) return;
			registry.Remove(asset);
		}

		/// <summary>
		/// Computes the content key of a live skeleton's current skin combination, with the exact same
		/// semantics the editor used to key the baked entries (setup attachments resolved through
		/// skeleton.Skin with default skin fallback — see <see cref="GpuSpineBakeKey"/>). A skeleton
		/// whose Skin is a composite built via Skin.AddSkin in the same order as the declared
		/// combination resolves to the combination's entry.
		/// </summary>
		public static ulong ComputeRuntimeHash(Skeleton skeleton) => GpuSpineBakeKey.ComputeHash(skeleton.Data, skeleton.Skin);

		public static string ComputeRuntimeKey (Skeleton skeleton) {
			if (skeleton == null) throw new ArgumentNullException("skeleton");
			return GpuSpineBakeKey.Compute(skeleton.Data, skeleton.Skin);
		}
	}
}
