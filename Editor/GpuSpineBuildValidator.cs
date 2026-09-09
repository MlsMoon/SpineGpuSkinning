using GpuSpine.Baking;
using Spine.Unity;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GpuSpine.Editor {
    public sealed class GpuSpineBuildValidator : IPreprocessBuildWithReport, IProcessSceneWithReport {
        public int callbackOrder => -1900;

        public void OnPreprocessBuild(BuildReport report) {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })) {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("/Editor/")) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) ValidateRoot(prefab);
            }
        }

        public void OnProcessScene(Scene scene, BuildReport report) {
            if (report == null) return;
            foreach (GameObject root in scene.GetRootGameObjects()) ValidateRoot(root);
        }

        static void ValidateRoot(GameObject root) {
            foreach (GpuSkeletonRenderer renderer in root.GetComponentsInChildren<GpuSkeletonRenderer>(true)) {
                GpuSpineBakedData data = renderer.BakedData;
                if (data == null) continue; // Runtime registry users retain the normal CPU fallback contract.
                SkeletonAnimation skeleton = renderer.GetComponentInChildren<SkeletonAnimation>(true);
                if (skeleton == null || skeleton.skeletonDataAsset != data.SourceAsset ||
                    !GpuSpineBakerEditorUtility.IsCurrent(data))
                    throw new BuildFailedException("GpuSpine data is stale or mismatched: " + AssetDatabase.GetAssetPath(root));
                if (data.DefaultShader == null)
                    throw new BuildFailedException("GpuSpine default shader reference is missing: " + AssetDatabase.GetAssetPath(data));
            }
        }
    }
}
