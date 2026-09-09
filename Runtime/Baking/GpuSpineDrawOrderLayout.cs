using System;
using UnityEngine;

namespace GpuSpine.Baking {
    /// <summary>Editor-baked index layout for one resolved slot permutation.</summary>
    [Serializable]
    public sealed class GpuSpineDrawOrderLayout {
        public string Key;
        public Mesh Mesh;
        public int[] Indices;
        public GpuSpineSubmesh[] Submeshes;
    }
}
