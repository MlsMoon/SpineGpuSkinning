using System.Collections.Generic;
using UnityEngine;

namespace GpuSpine {
    /// <summary>宿主注册的 Spine GPU 运行时切换集合；不依赖任何项目业务类型。</summary>
    public static class GpuSpineRuntimeSwitch {
        /// <summary>一次切换的状态快照，用于宿主命令撤销与重做。</summary>
        public sealed class StateSnapshot {
            internal readonly GpuSkeletonRenderer[] Renderers;
            internal readonly bool[] Enabled;
            internal StateSnapshot(GpuSkeletonRenderer[] renderers, bool[] enabled) { Renderers = renderers; Enabled = enabled; }
        }
        static readonly List<GpuSkeletonRenderer> Renderers = new List<GpuSkeletonRenderer>();
        /// <summary>注册一个由宿主负责归类的实例。</summary>
        public static void Register(GpuSkeletonRenderer renderer) { if (renderer != null && !Renderers.Contains(renderer)) Renderers.Add(renderer); }
        /// <summary>注销一个宿主实例。</summary>
        public static void Unregister(GpuSkeletonRenderer renderer) { if (renderer != null) Renderers.Remove(renderer); }
        /// <summary>捕获当前组件启用状态。</summary>
        public static StateSnapshot CaptureState() {
            var renderers = Renderers.ToArray(); var enabled = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) enabled[i] = renderers[i] != null && renderers[i].enabled;
            return new StateSnapshot(renderers, enabled);
        }
        /// <summary>把所有注册实例切换到目标蒙皮组件状态。</summary>
        public static int Apply(bool useGpu) {
            int count = 0;
            for (int i = Renderers.Count - 1; i >= 0; i--) {
                var renderer = Renderers[i];
                if (renderer == null) { Renderers.RemoveAt(i); continue; }
                renderer.enabled = useGpu; count++;
            }
            return count;
        }
        /// <summary>恢复之前捕获的逐实例状态。</summary>
        public static int Restore(StateSnapshot snapshot) {
            if (snapshot == null) return 0; int count = 0;
            for (int i = 0; i < snapshot.Renderers.Length; i++) {
                var renderer = snapshot.Renderers[i];
                if (renderer == null) continue;
                renderer.enabled = snapshot.Enabled[i]; count++;
            }
            return count;
        }
        /// <summary>返回当前注册实例数量。</summary>
        public static int RegisteredCount => Renderers.Count;
        /// <summary>返回当前已完成 GPU 激活的实例数量。</summary>
        public static int ActiveGpuCount {
            get { int count = 0; foreach (var renderer in Renderers) if (renderer != null && renderer.IsGpuActive) count++; return count; }
        }
    }
}
