using GpuSpine;
using Spine.Unity;
using UnityEngine;

namespace GpuSpine.Example {
    /// <summary>Example character: keeps motion state so a skinning toggle does not reset animation or position.</summary>
    public sealed class GpuSpineExampleWalker : MonoBehaviour {
        public SkeletonAnimation Skeleton;
        public GpuSkeletonRenderer Gpu;
        public int Kind;
        public int Ordinal;
        public int SkinIndex { get; private set; }
        static readonly string[][] SkinNames = {
            new[] { "default", "forest", "midnight" }, new[] { "default", "copper", "rose" }
        };
        float m_direction;
        float m_speed;
        float m_phase;

        /// <summary>Seeded per-character variation so a CPU/GPU compare does not change the workload.</summary>
        public void Configure(int kind, int ordinal) {
            Kind = kind;
            Ordinal = ordinal;
            var random = new System.Random(1937 + kind * 100003 + ordinal * 17);
            m_direction = random.NextDouble() < 0.5 ? -1f : 1f;
            m_phase = (float)random.NextDouble();
            m_speed = Mathf.Lerp(0.42f, 0.62f, (float)random.NextDouble());
            Skeleton.Initialize(false);
            var track = Skeleton.AnimationState.SetAnimation(0, "walk", true);
            track.TrackTime = m_phase * track.Animation.Duration;
            Skeleton.timeScale = m_speed / 0.52f;
            Skeleton.Skeleton.ScaleX = m_direction;
        }

        /// <summary>Real Spine skin switch. Restores the current pose and does not reset TrackTime.</summary>
        public void ApplySkin(int selection) {
            SkinIndex = selection < 0 ? Ordinal % 3 : Mathf.Clamp(selection, 0, 2);
            Skeleton.Skeleton.SetSkin(SkinNames[Kind][SkinIndex]);
            Skeleton.Skeleton.SetSlotsToSetupPose();
            Skeleton.AnimationState.Apply(Skeleton.Skeleton);
            Vector3 position = transform.localPosition;
            position.z = Depth;
            transform.localPosition = position;
        }

        // Lanes do not overlap in geometry. Skin-grouped depth keeps mixed display batchable.
        float Depth => Kind * -0.1f + SkinIndex * -0.02f;

        /// <summary>Layout only when count or aspect changes. A mode switch does not call this.</summary>
        public void Arrange(int columns, int rowOffset, float left) {
            int row = Ordinal / columns + rowOffset;
            int column = Ordinal % columns;
            transform.localPosition = new Vector3(left + (column + 0.2f + m_phase * 0.6f) * 1.65f,
                -row * 2.05f, Depth);
        }

        /// <summary>Horizontal walk and edge turn. The controller advances every character.</summary>
        public void Tick(float deltaTime, float left, float right) {
            Vector3 position = transform.localPosition;
            position.x += m_direction * m_speed * deltaTime;
            if (position.x < left || position.x > right) {
                position.x = Mathf.Clamp(position.x, left, right);
                m_direction = -m_direction;
                Skeleton.Skeleton.ScaleX = m_direction;
            }
            transform.localPosition = position;
        }
    }
}
