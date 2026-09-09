using System.Globalization;
using Spine;

namespace GpuSpine.Baking {
    /// <summary>Shared permutation key for editor layouts and evaluated runtime draw order.</summary>
    public static class GpuSpineDrawOrderKey {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        public static string Compute (int[] order) {
            ulong hash = Offset;
            foreach (int slot in order) hash = unchecked((hash ^ (uint)slot) * Prime);
            return hash.ToString("X16", CultureInfo.InvariantCulture);
        }

        public static string Compute (Skeleton skeleton) => ComputeHash(skeleton).ToString("X16", CultureInfo.InvariantCulture);

        public static ulong ComputeHash (Skeleton skeleton) {
            ulong hash = Offset;
            for (int i = 0; i < skeleton.DrawOrder.Count; i++)
                hash = unchecked((hash ^ (uint)skeleton.DrawOrder.Items[i].Data.Index) * Prime);
            return hash;
        }
    }
}
