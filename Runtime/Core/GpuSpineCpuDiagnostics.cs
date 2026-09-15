using System;
using Unity.Profiling;
using UnityEngine;
namespace GpuSpine.Core {
    /// <summary>固定 marker 与无分配计数；不输出日志，不改变更新节奏。</summary>
    public static class GpuSpineCpuDiagnostics {
        internal static readonly ProfilerMarker Skin = new("GpuSpine.Source.SkinKey");
        internal static readonly ProfilerMarker Order = new("GpuSpine.Source.DrawOrder");
        internal static readonly ProfilerMarker Bones = new("GpuSpine.Source.Bones");
        internal static readonly ProfilerMarker Dynamic = new("GpuSpine.Source.DynamicSlots");
        internal static readonly ProfilerMarker Deform = new("GpuSpine.Source.Deform");
        internal static readonly ProfilerMarker Colors = new("GpuSpine.Source.SlotColors");
        internal static readonly ProfilerMarker Clip = new("GpuSpine.Source.Clipping");
        internal static readonly ProfilerMarker Bounds = new("GpuSpine.Source.Bounds");
        internal static readonly ProfilerMarker Membership = new("GpuSpine.Prepare.Membership");
        internal static readonly ProfilerMarker BatchSort = new("GpuSpine.Prepare.BatchSort");
        internal static readonly ProfilerMarker CameraSort = new("GpuSpine.Prepare.CameraSort");
        internal static readonly ProfilerMarker Staging = new("GpuSpine.Prepare.Staging");
        internal static readonly ProfilerMarker Upload = new("GpuSpine.Prepare.SetData");
        internal static readonly ProfilerMarker Slice = new("GpuSpine.Prepare.SliceLookup");
        internal static readonly ProfilerMarker Draw = new("GpuSpine.Prepare.DrawIndirect");
        internal static readonly ProfilerMarker InstanceData = new("GpuSpine.Source.InstanceData");
        static readonly long[] copied = new long[6], uploaded = new long[6], calls = new long[6];
        public static long SkinChecks { get; internal set; }
        public static long SkinHashRecomputations { get; internal set; }
        public static GpuSpineTransferSnapshot GetTransfers(GpuSpineDataChannel channel) {
            int i=(int)channel;
            return new GpuSpineTransferSnapshot { CopiedBytes=copied[i], UploadedBytes=uploaded[i], UploadCalls=calls[i] };
        }
        internal static void Copy(GpuSpineDataChannel channel, Array source, int sourceStart, Array target, int targetStart, int count, int stride) {
            Array.Copy(source,sourceStart,target,targetStart,count); copied[(int)channel]+=(long)count*stride;
        }
        internal static void RecordCopy(GpuSpineDataChannel channel, int count, int stride) => copied[(int)channel]+=(long)count*stride;
        internal static void SetData(GpuSpineDataChannel channel, ComputeBuffer buffer, Array data, int count, int stride) {
            using var scope=Upload.Auto();
            buffer.SetData(data,0,0,count); uploaded[(int)channel]+=(long)count*stride;calls[(int)channel]++;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() {
            Array.Clear(copied,0,copied.Length);Array.Clear(uploaded,0,uploaded.Length);Array.Clear(calls,0,calls.Length);
            SkinChecks=0;SkinHashRecomputations=0;
        }
    }
}
