using System.Collections.Generic;
using UnityEngine;

namespace GpuSpine {
    /// <summary>Host-registered set of GPU spine renderers for a CPU/GPU toggle. No game-specific types.</summary>
    public static class GpuSpineRuntimeSwitch {
        /// <summary>Snapshot of enabled state for host undo/redo.</summary>
        public sealed class StateSnapshot {
            internal readonly GpuSkeletonRenderer[] Renderers;
            internal readonly bool[] Enabled;
            internal StateSnapshot(GpuSkeletonRenderer[] renderers, bool[] enabled) { Renderers = renderers; Enabled = enabled; }
        }
        static readonly List<GpuSkeletonRenderer> Renderers = new List<GpuSkeletonRenderer>();
        /// <summary>Register one instance the host owns.</summary>
        public static void Register(GpuSkeletonRenderer renderer) { if (renderer != null && !Renderers.Contains(renderer)) Renderers.Add(renderer); }
        /// <summary>Unregister one host instance.</summary>
        public static void Unregister(GpuSkeletonRenderer renderer) { if (renderer != null) Renderers.Remove(renderer); }
        /// <summary>Capture the current enabled state.</summary>
        public static StateSnapshot CaptureState() {
            var renderers = Renderers.ToArray(); var enabled = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) enabled[i] = renderers[i] != null && renderers[i].enabled;
            return new StateSnapshot(renderers, enabled);
        }
        /// <summary>Set every registered instance to the target GPU-component state.</summary>
        public static int Apply(bool useGpu) {
            int count = 0;
            for (int i = Renderers.Count - 1; i >= 0; i--) {
                var renderer = Renderers[i];
                if (renderer == null) { Renderers.RemoveAt(i); continue; }
                renderer.enabled = useGpu; count++;
            }
            return count;
        }
        /// <summary>Restore a previously captured per-instance state.</summary>
        public static int Restore(StateSnapshot snapshot) {
            if (snapshot == null) return 0; int count = 0;
            for (int i = 0; i < snapshot.Renderers.Length; i++) {
                var renderer = snapshot.Renderers[i];
                if (renderer == null) continue;
                renderer.enabled = snapshot.Enabled[i]; count++;
            }
            return count;
        }
        /// <summary>Number of currently registered instances.</summary>
        public static int RegisteredCount => Renderers.Count;
        /// <summary>Number of registered instances that are GPU-active.</summary>
        public static int ActiveGpuCount {
            get { int count = 0; foreach (var renderer in Renderers) if (renderer != null && renderer.IsGpuActive) count++; return count; }
        }
    }
}
