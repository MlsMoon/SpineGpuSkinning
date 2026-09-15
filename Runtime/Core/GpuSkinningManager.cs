using System.Collections.Generic;
using GpuSpine.Baking;
using GpuSpine.Core;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSpine {
    /// <summary>Registration owner; each rendering camera owns an independent submission snapshot.</summary>
    public sealed class GpuSkinningManager : MonoBehaviour {
        static GpuSkinningManager instance;
        static readonly GpuSpineBatchInfo[] Empty = new GpuSpineBatchInfo[0];
        /// <summary>Profiler marker isolating this plugin's beginCameraRendering work (batch
        /// lifecycle, buffer uploads, indirect submission) from other subscribers of the event.</summary>
        static readonly ProfilerMarker CameraPrepareMarker = new ProfilerMarker("GpuSpine.CameraPrepare");
        readonly Dictionary<GpuSkeletonRenderer, GpuSpineBakedEntry> sources = new();
        readonly Dictionary<Renderer, GpuSkeletonRenderer> byRenderer = new();
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
            if (!IsValidEntry(renderer, entry)) return false;
            GpuSkinningManager manager = EnsureInstance();
            if (manager.sources.ContainsKey(renderer)) return true;
            manager.sources.Add(renderer, entry);
            BindRenderer(manager, renderer);
            entry.RetainRuntimeLayout();
            return true;
        }

        /// <summary>注册与布局切换共享资源有效性检查。</summary>
        static bool IsValidEntry(GpuSkeletonRenderer renderer, GpuSpineBakedEntry entry) {
            if (entry == null || entry.Mesh == null || entry.Submeshes == null || entry.Submeshes.Length == 0) return false;
            foreach (var part in entry.Submeshes) {
                Material page = part.PageMaterial;
                Shader shader = renderer.ResolveMaterialShader(page);
                if (page == null || shader == null || !shader.isSupported) return false;
            }
            return true;
        }

        public static void Unregister(GpuSkeletonRenderer renderer) {
            if (instance == null || !instance.sources.TryGetValue(renderer, out var entry)) return;
            instance.sources.Remove(renderer);
            UnbindRenderer(instance, renderer);
            foreach (var frame in instance.cameras.Values) frame.Remove(renderer);
            entry.ReleaseRuntimeLayout();
        }

        public static bool ChangeEntry(GpuSkeletonRenderer renderer, GpuSpineBakedEntry entry) {
            EntryChanges++;
            if (!IsValidEntry(renderer, entry)) return false;
            if (instance != null && instance.sources.TryGetValue(renderer, out var previous) &&
                ReferenceEquals(previous.ResourceOwner, entry.ResourceOwner) && ReferenceEquals(previous.Mesh, entry.Mesh)) {
                previous.ReleaseRuntimeLayout(); entry.RetainRuntimeLayout();
                instance.sources[renderer] = entry;
                foreach (var frame in instance.cameras.Values) frame.ChangeLayout(renderer, entry);
                return true;
            }
            Unregister(renderer);
            return Register(renderer, entry);
        }

        /// <summary>Monotonic lifecycle counters for churn diagnostics:
        /// [0] ChangeEntry calls, [1] batches created, [2] batches disposed,
        /// [3] batches parked idle, [4] idle batches reused, [5] draw slices created,
        /// [6] combined layout meshes built.</summary>
        public static int[] GetLifecycleCounters() {
            return new[] {
                EntryChanges,
                GpuSpineCameraFrame.BatchesCreated, GpuSpineCameraFrame.BatchesDisposed,
                GpuSpineCameraFrame.BatchesParked, GpuSpineCameraFrame.BatchesReused,
                GpuSpineBatch.SlicesCreated, GpuSpineBakedEntry.LayoutMeshesBuilt
            };
        }
        /// <summary>返回值类型快照，供连续采样使用；旧数组 API 保持七项兼容。</summary>
        public static GpuSpineLifecycleCounters GetLifecycleSnapshot() => new GpuSpineLifecycleCounters {
            EntryChanges = EntryChanges,
            BatchesCreated = GpuSpineCameraFrame.BatchesCreated,
            BatchesDisposed = GpuSpineCameraFrame.BatchesDisposed,
            BatchesParked = GpuSpineCameraFrame.BatchesParked,
            BatchesReused = GpuSpineCameraFrame.BatchesReused,
            SlicesCreated = GpuSpineBatch.SlicesCreated,
            LayoutMeshesBuilt = GpuSpineBakedEntry.LayoutMeshesBuilt,
            CapacityGrowths = GpuSpineBatch.CapacityGrowths
        };
        static int EntryChanges;

        void BeginCamera(ScriptableRenderContext context, Camera camera) {
            if (sources.Count == 0) return;
            using (CameraPrepareMarker.Auto()) {
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
        }

        public static IReadOnlyList<GpuSpineBatchInfo> GetBatches(Camera camera) {
            return instance != null && camera != null && instance.cameras.TryGetValue(camera, out var frame) ? frame.Draws : Empty;
        }

        public static IReadOnlyList<GpuSpineBatchInfo> GetBatches(Camera camera, GpuSkeletonRenderer source) {
            return instance != null && camera != null && source != null && instance.cameras.TryGetValue(camera, out var frame)
                ? frame.GetDraws(source) : Empty;
        }

        public static IReadOnlyList<GpuSpineBatchInfo> GetBatches() => instance?.lastCamera?.Draws ?? Empty;

        /// <summary>Looks up the GPU skeleton that currently owns this MeshRenderer.</summary>
        public static bool TryGetByRenderer(Renderer source, out GpuSkeletonRenderer gpu) {
            gpu = null;
            return instance != null && source != null && instance.byRenderer.TryGetValue(source, out gpu);
        }

        static void BindRenderer(GpuSkinningManager manager, GpuSkeletonRenderer renderer) {
            MeshRenderer source = renderer.SourceRenderer;
            if (source != null) manager.byRenderer[source] = renderer;
        }

        static void UnbindRenderer(GpuSkinningManager manager, GpuSkeletonRenderer renderer) {
            MeshRenderer source = renderer.SourceRenderer;
            if (source != null && manager.byRenderer.TryGetValue(source, out GpuSkeletonRenderer mapped) && mapped == renderer)
                manager.byRenderer.Remove(source);
        }

        void OnDestroy() {
            UnregisterEvents();
            foreach (var frame in cameras.Values) frame.Dispose();
            foreach (var entry in sources.Values) entry.ReleaseRuntimeLayout();
            cameras.Clear(); sources.Clear(); byRenderer.Clear();
            GpuSpineBakedRuntime.ClearRuntimeLayouts();
            if (instance == this) instance = null;
        }
    }
}
