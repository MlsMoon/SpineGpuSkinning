using System;
using System.Text;
using GpuSpine.Baking;
using Spine;
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

		[MenuItem("Assets/GpuSpine/Rebake Skeleton Data", true)]
		static bool ValidateRebakeSelected () {
			return Selection.GetFiltered<SkeletonDataAsset>(SelectionMode.Assets).Length > 0;
		}

		/// <summary>Manually rebakes every selected SkeletonDataAsset (also repairs containers whose
		/// entry meshes were deleted by hand).</summary>
		[MenuItem("Assets/GpuSpine/Rebake Skeleton Data")]
		static void RebakeSelected () {
			SkeletonDataAsset[] assets = Selection.GetFiltered<SkeletonDataAsset>(SelectionMode.Assets);
			for (int i = 0; i < assets.Length; i++) GpuSpineBakerEditorUtility.Rebake(assets[i]);
		}

		[MenuItem("Assets/GpuSpine/Log Audit Report", true)]
		static bool ValidateLogAuditSelected () {
			return Selection.GetFiltered<SkeletonDataAsset>(SelectionMode.Assets).Length > 0;
		}

		/// <summary>Audits every selected SkeletonDataAsset and logs the graded report details
		/// (failures, warnings, dynamic slots and their variants) without baking.</summary>
		[MenuItem("Assets/GpuSpine/Log Audit Report")]
		static void LogAuditSelected () {
			SkeletonDataAsset[] assets = Selection.GetFiltered<SkeletonDataAsset>(SelectionMode.Assets);
			for (int i = 0; i < assets.Length; i++) {
				SkeletonDataAsset asset = assets[i];
				GpuSpineAuditReport audit = GpuSpineAuditor.Audit(asset.GetSkeletonData(true));
				Debug.Log(FormatAuditReport(asset, audit), asset);
			}
		}

		static string FormatAuditReport (SkeletonDataAsset asset, GpuSpineAuditReport audit) {
			StringBuilder builder = new StringBuilder();
			builder.Append("GpuSpine audit of '").Append(asset.name).Append("': ")
				.Append(audit.Passed ? "PASSED" : "FAILED")
				.Append(", drawOrderTimeline=").Append(audit.HasDrawOrderTimeline)
				.Append(", clipping=").Append(audit.HasClipping)
				.Append(", dynamicSlots=").Append(audit.DynamicSlots.Count);
			for (int i = 0; i < audit.Failures.Count; i++)
				builder.Append("\n  FAILURE: ").Append(audit.Failures[i]);
			for (int i = 0; i < audit.Warnings.Count; i++)
				builder.Append("\n  WARNING: ").Append(audit.Warnings[i]);
			for (int i = 0; i < audit.DynamicSlots.Count; i++) {
				GpuSpineDynamicSlotInfo info = audit.DynamicSlots[i];
				builder.Append("\n  dynamic slot '").Append(info.SlotName).Append("' (#").Append(info.SlotIndex)
					.Append("), ").Append(info.AttachmentNames.Length).Append(" variant(s): ")
					.Append(string.Join(", ", info.AttachmentNames));
			}
			return builder.ToString();
		}
	}
}
