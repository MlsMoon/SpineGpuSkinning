using System;
namespace GpuSpine.Core {
    /// <summary>本相机本帧的独立绘制范围；不同范围不可复用同一个在途 args。</summary>
    internal readonly struct GpuSpineSliceKey : IEquatable<GpuSpineSliceKey> {
        readonly int indexStart, indexCount, start, count;
        public GpuSpineSliceKey(int indexStart, int indexCount, int start, int count) {
            this.indexStart = indexStart; this.indexCount = indexCount; this.start = start; this.count = count;
        }
        public bool Equals(GpuSpineSliceKey other) => indexStart == other.indexStart && indexCount == other.indexCount && start == other.start && count == other.count;
        public override bool Equals(object obj) => obj is GpuSpineSliceKey other && Equals(other);
        public override int GetHashCode() => unchecked(((indexStart * 397 ^ indexCount) * 397 ^ start) * 397 ^ count);
    }
}
