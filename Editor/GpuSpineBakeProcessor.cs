using System;
using Spine.Unity;
using UnityEditor;
using UnityEngine;

namespace GpuSpine.Editor {
	/// <summary>
	/// Automatic bake trigger: whenever a SkeletonDataAsset is imported or updated, its baked data
	/// container sub-asset is (re)built via <see cref="GpuSpineBakerEditorUtility.Rebake"/>.
	/// <para/>
	/// Import-loop safety: Rebake saves the asset file, which re-enters this postprocessor. The static
	/// <see cref="rebaking"/> flag covers synchronous reentrancy; a deferred second pass runs Rebake
	/// again but hits the no-change check (source fingerprint + entry keys) and returns without
	/// saving, terminating the loop.
	/// </summary>
	public sealed class GpuSpineBakeProcessor : AssetPostprocessor {
		static bool rebaking;

		static void OnPostprocessAllAssets (string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload) {
			if (rebaking) return;
			for (int i = 0; i < importedAssets.Length; i++) {
				SkeletonDataAsset asset = AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(importedAssets[i]);
				if (asset == null) continue;
				rebaking = true;
				try {
					GpuSpineBakerEditorUtility.Rebake(asset);
				} catch (Exception exception) {
					// Keep the import pipeline alive; the container keeps its previous state.
					Debug.LogException(exception, asset);
				} finally {
					rebaking = false;
				}
			}
		}
	}
}
