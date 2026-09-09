using System;
using UnityEngine;

namespace GpuSpine.Core {
    internal sealed class GpuSpineDrawSlice : IDisposable {
        public readonly Material Material;
        public readonly GraphicsBuffer Args;
        readonly uint[] arguments;
        public readonly int Start;
        public int Count { get; private set; }

        public GpuSpineDrawSlice(Material material, uint[] template, int start) {
            Material = new Material(material);
            Material.SetInt("_GpuSpineInstanceOffset", start);
            Start = start;
            arguments = (uint[])template.Clone();
            Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }

        public void SetCount(int count) {
            if (Count == count) return;
            Count = count;
            arguments[1] = (uint)count;
            Args.SetData(arguments);
        }

        public void Dispose() {
            Args.Release();
            UnityEngine.Object.Destroy(Material);
        }
    }
}
