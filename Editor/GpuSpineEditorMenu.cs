using System.Text;
using GpuSpine.Baking;
using Spine.Unity;
using UnityEditor;
using UnityEngine;

namespace GpuSpine.Editor {
	/// <summary>
	/// Shared editor menus for GpuSpine. Generic items live under Tools/GPUSpineSkin;
	/// project-local smoke tools should hang off <see cref="TempRoot"/> instead of a
	/// separate top-level menu. Asset context items stay under Assets/GpuSpine.
	/// </summary>
	public static class GpuSpineEditorMenu {
		public const string Root = "Tools/GPUSpineSkin";
		public const string TempRoot = Root + "/Temp";
		public const string AssetsRoot = "Assets/GpuSpine";

		[MenuItem(Root + "/Rebake Selected", true)]
		[MenuItem(Root + "/Force Rebake Selected", true)]
		[MenuItem(Root + "/Log Audit Report", true)]
		[MenuItem(Root + "/Dump Baked Data", true)]
		[MenuItem(AssetsRoot + "/Rebake Skeleton Data", true)]
		[MenuItem(AssetsRoot + "/Log Audit Report", true)]
		static bool ValidateSelected () {
			return SelectedAssets().Length > 0;
		}

		static SkeletonDataAsset[] SelectedAssets () {
			return Selection.GetFiltered<SkeletonDataAsset>(SelectionMode.Assets);
		}

		[MenuItem(Root + "/Bake All Skeleton Data", false, -10)]
		static void BakeAllSkeletonData () {
			string[] guids = AssetDatabase.FindAssets("t:SkeletonDataAsset");
			int baked = 0;
			int skipped = 0;
			int failed = 0;
			try {
				for (int i = 0; i < guids.Length; i++) {
					string path = AssetDatabase.GUIDToAssetPath(guids[i]);
					SkeletonDataAsset asset = AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(path);
					string label = asset != null ? asset.name : path;
					EditorUtility.DisplayProgressBar("GpuSpine", "Baking " + label, guids.Length == 0 ? 0f : (float)i / guids.Length);
					if (asset == null) {
						failed++;
						continue;
					}
					try {
						GpuSpineBakedData container = GpuSpineBakerEditorUtility.FindContainer(path);
						if (container != null && GpuSpineBakerEditorUtility.IsCurrent(container)) {
							skipped++;
							continue;
						}
						GpuSpineBakedData result = GpuSpineBakerEditorUtility.Rebake(asset);
						if (result == null) failed++;
						else baked++;
					} catch (System.Exception exception) {
						failed++;
						Debug.LogException(exception, asset);
					}
				}
			} finally {
				EditorUtility.ClearProgressBar();
			}
			Debug.Log("GpuSpine Bake All Skeleton Data: baked " + baked
				+ ", skipped (up to date) " + skipped
				+ ", failed " + failed + ".");
		}

		[MenuItem(Root + "/Rebake Selected", false, 0)]
		[MenuItem(AssetsRoot + "/Rebake Skeleton Data")]
		static void RebakeSelected () {
			SkeletonDataAsset[] assets = SelectedAssets();
			for (int i = 0; i < assets.Length; i++) GpuSpineBakerEditorUtility.Rebake(assets[i]);
		}

		[MenuItem(Root + "/Force Rebake Selected", false, 1)]
		static void ForceRebakeSelected () {
			SkeletonDataAsset[] assets = SelectedAssets();
			for (int i = 0; i < assets.Length; i++) GpuSpineBakerEditorUtility.ForceRebake(assets[i]);
		}

		[MenuItem(Root + "/Log Audit Report", false, 2)]
		[MenuItem(AssetsRoot + "/Log Audit Report")]
		static void LogAuditSelected () {
			SkeletonDataAsset[] assets = SelectedAssets();
			for (int i = 0; i < assets.Length; i++) {
				SkeletonDataAsset asset = assets[i];
				GpuSpineAuditReport audit = GpuSpineAuditor.Audit(asset.GetSkeletonData(true));
				Debug.Log(FormatAuditReport(asset, audit), asset);
			}
		}

		[MenuItem(Root + "/Dump Baked Data", false, 20)]
		static void DumpSelected () {
			SkeletonDataAsset[] assets = SelectedAssets();
			for (int i = 0; i < assets.Length; i++) {
				string path = AssetDatabase.GetAssetPath(assets[i]);
				Debug.Log(FormatContainer(path, GpuSpineBakerEditorUtility.FindContainer(path)), assets[i]);
			}
		}

		[MenuItem(Root + "/Inspect Default Shader", false, 21)]
		static void InspectDefaultShader () {
			Shader shader = Shader.Find("GpuSpine/URP/Skeleton");
			if (shader == null) {
				Debug.LogWarning("GpuSpine default shader 'GpuSpine/URP/Skeleton' was not found.");
				return;
			}
			Material material = new Material(shader) { enableInstancing = true };
			StringBuilder builder = new StringBuilder();
			builder.Append("GpuSpine default shader: supported=").Append(shader.isSupported)
				.Append(", error=").Append(ShaderUtil.ShaderHasError(shader))
				.Append(", passes=").Append(material.passCount);
			ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
			for (int i = 0; i < messages.Length; i++)
				builder.Append("\n  ").Append(messages[i].severity).Append(": ").Append(messages[i].message);
			for (int i = 0; i < material.passCount; i++)
				builder.Append("\n  pass '").Append(material.GetPassName(i)).Append("' SetPass=")
					.Append(material.SetPass(i));
			UnityEngine.Object.DestroyImmediate(material);
			Debug.Log(builder.ToString());
		}

		public static string FormatAuditReport (SkeletonDataAsset asset, GpuSpineAuditReport audit) {
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

		public static string FormatContainer (string path, GpuSpineBakedData container) {
			StringBuilder builder = new StringBuilder();
			builder.AppendLine("=== " + path + " ===");
			if (container == null) {
				builder.AppendLine("  container: NONE");
				return builder.ToString();
			}
			builder.AppendLine("  container: " + container.name + ", zSpacing=" + container.BakedZSpacing);
			GpuSpineAuditReport audit = container.Audit;
			if (audit == null) {
				builder.AppendLine("  audit: NONE");
			} else {
				builder.AppendLine("  audit: passed=" + audit.Passed
					+ ", drawOrderTimeline=" + audit.HasDrawOrderTimeline
					+ ", clipping=" + audit.HasClipping
					+ ", dynamicSlots=" + audit.DynamicSlots.Count
					+ ", truncated=" + audit.TruncatedVertexCount
					+ ", failures=" + audit.Failures.Count
					+ ", warnings=" + audit.Warnings.Count);
				for (int i = 0; i < audit.Failures.Count && i < 10; i++)
					builder.AppendLine("    FAILURE: " + audit.Failures[i]);
				for (int i = 0; i < audit.Warnings.Count && i < 10; i++)
					builder.AppendLine("    WARNING: " + audit.Warnings[i]);
				for (int i = 0; i < audit.DynamicSlots.Count; i++) {
					GpuSpineDynamicSlotInfo info = audit.DynamicSlots[i];
					builder.AppendLine("    dynSlot #" + info.SlotIndex + " '" + info.SlotName + "' variants="
						+ info.AttachmentNames.Length + ": " + string.Join(", ", info.AttachmentNames));
				}
			}
			builder.AppendLine("  entries: " + (container.Entries != null ? container.Entries.Count : 0));
			if (container.Entries != null) {
				for (int i = 0; i < container.Entries.Count; i++) {
					GpuSpineBakedEntry entry = container.Entries[i];
					if (entry == null) { builder.AppendLine("    [" + i + "] null"); continue; }
					builder.AppendLine("    [" + i + "] '" + entry.DisplayName + "' key=" + entry.Key
						+ " mesh=" + (entry.Mesh != null ? entry.Mesh.vertexCount + "v" : "NULL")
						+ " submeshes=" + (entry.Submeshes != null ? entry.Submeshes.Length : 0)
						+ " bones=" + entry.BoneCount
						+ " dynVariants=" + (entry.DynamicVariants != null ? entry.DynamicVariants.Length : 0)
						+ " dynSlots=" + entry.DynamicSlotCount
						+ " skins=[" + (entry.SkinNames != null ? string.Join("+", entry.SkinNames) : "") + "]");
					if (entry.Submeshes == null) continue;
					for (int s = 0; s < entry.Submeshes.Length; s++) {
						GpuSpineSubmesh submesh = entry.Submeshes[s];
						builder.AppendLine("        submesh[" + s + "] mat="
							+ (submesh.PageMaterial != null ? submesh.PageMaterial.name : "NULL")
							+ " idxStart=" + submesh.IndexStart + " idxCount=" + submesh.IndexCount
							+ " additive=" + submesh.HasPmaAdditiveSlot);
					}
				}
			}
			builder.AppendLine("  declaredCombos: "
				+ (container.DeclaredCombos != null ? container.DeclaredCombos.Count : 0));
			return builder.ToString();
		}
	}
}
