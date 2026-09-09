using GpuSpine;
using Spine.Unity;
using UnityEngine;

namespace GpuSpine.Example {
    /// <summary>示例角色：保存移动状态，切换蒙皮时保持动画与位置连续。</summary>
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

        /// <summary>固定种子生成角色差异，避免 CPU/GPU 对比时改变负载。</summary>
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

        /// <summary>真实 Spine Skin 切换；恢复当前动画姿态，不重置 TrackTime。</summary>
        public void ApplySkin(int selection) {
            SkinIndex = selection < 0 ? Ordinal % 3 : Mathf.Clamp(selection, 0, 2);
            Skeleton.Skeleton.SetSkin(SkinNames[Kind][SkinIndex]);
            Skeleton.Skeleton.SetSlotsToSetupPose();
            Skeleton.AnimationState.Apply(Skeleton.Skeleton);
            Vector3 position = transform.localPosition;
            position.z = Depth;
            transform.localPosition = position;
        }

        // 不同行之间没有几何重叠；以皮肤分组的深度让混合展示仍能合批。
        float Depth => Kind * -0.1f + SkinIndex * -0.02f;

        /// <summary>仅在数量或窗口比例改变时安排位置，模式切换不会调用此方法。</summary>
        public void Arrange(int columns, int rowOffset, float left) {
            int row = Ordinal / columns + rowOffset;
            int column = Ordinal % columns;
            transform.localPosition = new Vector3(left + (column + 0.2f + m_phase * 0.6f) * 1.65f,
                -row * 2.05f, Depth);
        }

        /// <summary>水平行走与边界转向；所有角色由控制器统一推进。</summary>
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
