using UnityEditor;
using UnityEngine;

namespace GpuSpine.Example.Editor {
    /// <summary>通过原生 Play Mode 运行示例，退出后恢复用户原有启动场景设置。</summary>
    [InitializeOnLoad]
    public static class GpuSpineExamplePlayMode {
        const string PendingKey = "GpuSpine.Example.PlayPending";
        const string PreviousKey = "GpuSpine.Example.PreviousPlayScene";

        static GpuSpineExamplePlayMode() {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("Tools/GPUSpineSkin/Play Comparison Example", false, 41)]
        public static void Play() {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(GpuSpineExampleBuilder.ScenePath);
            if (scene == null) {
                Debug.LogError("Build the comparison example first.");
                return;
            }
            var previous = UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene;
            SessionState.SetString(PreviousKey, previous != null ? AssetDatabase.GetAssetPath(previous) : "");
            SessionState.SetBool(PendingKey, true);
            UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene = scene;
            EditorApplication.EnterPlaymode();
        }

        static void OnPlayModeChanged(PlayModeStateChange state) {
            if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(PendingKey, false)) return;
            UnityEditor.SceneManagement.EditorSceneManager.playModeStartScene =
                AssetDatabase.LoadAssetAtPath<SceneAsset>(SessionState.GetString(PreviousKey, ""));
            SessionState.EraseBool(PendingKey);
            SessionState.EraseString(PreviousKey);
        }
    }
}
