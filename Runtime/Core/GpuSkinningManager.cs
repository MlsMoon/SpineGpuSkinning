using System.Collections.Generic;
using GpuSpine.Baking;
using GpuSpine.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSpine {
    /// <summary>Registration owner; each rendering camera owns an independent submission snapshot.</summary>
    public sealed class GpuSkinningManager : MonoBehaviour {
        static GpuSkinningManager instance;
        static readonly GpuSpineBatchInfo[] Empty = new GpuSpineBatchInfo[0];
        readonly Dictionary<GpuSkeletonRenderer, GpuSpineBakedEntry> sources = new();
        readonly Dictionary<Camera, GpuSpineCameraFrame> cameras = new();
        readonly List<Camera> removedCameras = new();
        GpuSpineCameraFrame lastCamera;

        public static GpuSkinningManager Instance => instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { instance = null; }

        static GpuSkinningManager EnsureInstance() {
            if (instance != null) return instance;
            var go = new GameObject("GpuSkinningManager") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<GpuSkinningManager>();
            return instance;
        }

        void OnEnable() { RegisterEvents(); }
        void OnDisable() { UnregisterEvents(); }
        void RegisterEvents() { RenderPipelineManager.beginCameraRendering += BeginCamera; }
        void UnregisterEvents() { RenderPipelineManager.beginCameraRendering -= BeginCamera; }

        public static bool Register(GpuSkeletonRenderer renderer, GpuSpineBakedEntry entry) {
            if (entry == null || entry.Mesh == null || entry.Submeshes == null || entry.Submeshes.Length == 0) return false;
            foreach (var part in entry.Submeshes) {
                Material page = part.PageMaterial;
                Shader shader = renderer.ResolveMaterialShader(page);
                if (page == null || shader == null || !shader.isSupported) return false;
            }
            GpuSkinningManager manager = EnsureInstance();
            if (manager.sources.ContainsKey(renderer)) return true;
            manager.sources.Add(renderer, entry);
            entry.RetainRuntimeLayout();
            return true;
        }

        public static void Unregister(GpuSkeletonRenderer renderer) {
            if (instance == null || !instance.sources.TryGetValue(renderer, out var entry)) return;
            instance.sources.Remove(renderer);
            foreach (var frame in instance.cameras.Values) frame.Remove(renderer);
            entry.ReleaseRuntimeLayout();
        }

        public static bool ChangeEntry(GpuSkeletonRenderer renderer, GpuSpineBakedEntry entry) {
            Unregister(renderer);
            return Register(renderer, entry);
        }

        void BeginCamera(ScriptableRenderContext context, Camera camera) {
            if (sources.Count == 0) return;
            removedCameras.Clear();
            foreach (var pair in cameras) if (pair.Key == null) removedCameras.Add(pair.Key);
            foreach (var removed in removedCameras) { cameras[removed].Dispose(); cameras.Remove(removed); }
            if (!cameras.TryGetValue(camera, out var frame)) {
                frame = new GpuSpineCameraFrame(camera);
                cameras.Add(camera, frame);
            }
            frame.Prepare(sources);
            lastCamera = frame;
        }

        public static IReadOnlyList<GpuSpineBatchInfo> GetBatches(Camera camera) {
            return instance != null && camera != null && instance.cameras.TryGetValue(camera, out var frame) ? frame.Draws : Empty;
        }

        public static IReadOnlyList<GpuSpineBatchInfo> GetBatches(Camera camera, GpuSkeletonRenderer source) {
            return instance != null && camera != null && source != null && instance.cameras.TryGetValue(camera, out var frame)
                ? frame.GetDraws(source) : Empty;
        }

        public static IReadOnlyList<GpuSpineBatchInfo> GetBatches() => instance?.lastCamera?.Draws ?? Empty;

        void OnDestroy() {
            UnregisterEvents();
            foreach (var frame in cameras.Values) frame.Dispose();
            foreach (var entry in sources.Values) entry.ReleaseRuntimeLayout();
            cameras.Clear(); sources.Clear();
            GpuSpineBakedRuntime.ClearRuntimeLayouts();
            if (instance == this) instance = null;
        }
    }
}
