using System;
using UnityEngine;
using GpuSpine.Baking;

namespace GpuSpine.Core {
    /// <summary>独立 args 缓冲，可跨帧重用；材质由批次按实例偏移拥有。</summary>
    internal sealed class GpuSpineDrawSlice : IDisposable {
        public Material Material { get; private set; }
        public readonly GraphicsBuffer Args;
        readonly uint[] arguments = new uint[5];
        bool initialized;
        public GpuSpineDrawSlice() {
            Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }
        /// <summary>同帧每个切片只绑定一种几何与实例范围，后续重绘直接复用。</summary>
        public void Configure(Material material, GpuSpineSubmesh part, int count) {
            Material = material;
            uint indices = (uint)part.IndexCount, offset = (uint)part.IndexStart, instances = (uint)count;
            if (initialized && arguments[0] == indices && arguments[1] == instances && arguments[2] == offset) return;
            arguments[0] = indices; arguments[1] = instances; arguments[2] = offset;
            // 烘焙与合并布局网格都使用零 base vertex；实例偏移由材质提供。
            arguments[3] = 0; arguments[4] = 0;
            Args.SetData(arguments); initialized = true;
        }
        public void Dispose() { Args.Release(); Material = null; }
    }
}
