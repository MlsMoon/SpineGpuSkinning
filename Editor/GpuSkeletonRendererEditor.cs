using GpuSpine.Baking;
using Spine.Unity;
using UnityEditor;
using UnityEngine;

namespace GpuSpine.Editor {
	/// <summary>
	/// Adds a bake button on <see cref="GpuSkeletonRenderer"/> only when the referenced
	/// SkeletonDataAsset has no GpuSpineBakedData container.
	/// </summary>
	[CustomEditor(typeof(GpuSkeletonRenderer))]
	public sealed class GpuSkeletonRendererEditor : UnityEditor.Editor {
		public override void OnInspectorGUI() {
			DrawDefaultInspector();

			var renderer = (GpuSkeletonRenderer)target;
			SkeletonAnimation skeletonAnimation = renderer.GetComponent<SkeletonAnimation>();
			if (skeletonAnimation == null)
				skeletonAnimation = renderer.GetComponentInChildren<SkeletonAnimation>();
			if (skeletonAnimation == null) return;

			SkeletonDataAsset asset = skeletonAnimation.skeletonDataAsset;
			if (asset == null) return;

			string path = AssetDatabase.GetAssetPath(asset);
			if (string.IsNullOrEmpty(path)) return;
			if (GpuSpineBakerEditorUtility.FindContainer(path) != null) return;

			EditorGUILayout.Space();
			EditorGUILayout.HelpBox(
				"This SkeletonDataAsset has no GpuSpineBakedData. Bake it to enable the GPU path.",
				MessageType.Info);
			using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode)) {
				if (!GUILayout.Button("Bake Skeleton Data")) return;
				GpuSpineBakedData container = GpuSpineBakerEditorUtility.Rebake(asset);
				if (container == null) return;
				Undo.RecordObject(renderer, "Bake Skeleton Data");
				renderer.BakedData = container;
				EditorUtility.SetDirty(renderer);
			}
		}
	}
}
