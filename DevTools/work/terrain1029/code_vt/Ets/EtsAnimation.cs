using System;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A bone animation from the game's pma files (version 5), as used for the interior's
    /// adjustable steering column, the stalks, wipers, gauges and warning lamps.
    ///
    /// Layout, measured on the demo's files: an 84-byte header - u32 version (5), u16 frame
    /// count and u16 flags, u32 bone count, u64 skeleton hash, float3 movement centre, float
    /// movement radius, float3 first and last translation, float length in seconds, then offsets
    /// to the frame lengths (one float per frame, the time each frame lasts), the bone indices
    /// (one byte per animated bone, indexing the skeleton of the pmg), the keyframes and two
    /// optional movement tracks. A keyframe is 56 bytes: a stretch quaternion, a rotation
    /// quaternion, a translation and a scale, quaternions stored w first. Keyframes are laid out
    /// frame by frame, one per animated bone.
    ///
    /// The frame count is 16 bits (instruments probe 2, 1.61.1.0): the Volvo FH16 2012's 36
    /// interior animations carry flags 6 in the upper half - they have the two movement tracks
    /// after the keyframes (50 frames: 600 bytes of float3, then 800 of quaternions) - and read
    /// as one u32 they came out as 393,218 frames and every one of them was refused, its
    /// steering column included. Every other demo animation has flags 0, so for them nothing
    /// changes. The movement tracks move a whole model (a crane, a walking animal) and are not
    /// read here.
    ///
    /// The keyframe's stretch quaternion orients a non-uniform scale, and the scale is not kept
    /// either: across the 17 demo cabs every driven key's scale is uniform and equal to its bone's
    /// bind scale - 1 almost everywhere, 0.915 on the Iveco Hi-Way's blinker stalk, 1.162 and 0.861
    /// on the MAN TGX's wipers (instruments probe 10) - so it cancels between the bind pose and the
    /// pose, and a skinned vertex lands where the game puts it without it (probe 8: 0.0001 mm).
    /// </summary>
    public sealed class EtsAnimation
    {
        public string Path;
        public int FrameCount;
        public int BoneCount;
        public float Length;

        /// <summary>The header's flags (the upper 16 bits of the old frame-count word); 6 = movement tracks present.</summary>
        public int Flags;

        /// <summary>Hash of the skeleton the bone indices refer to; the pmg carries the same value.</summary>
        public ulong SkeletonHash;

        /// <summary>Time of each frame from the start, seconds.</summary>
        public float[] FrameTimes;

        /// <summary>Skeleton bone index of each animated bone.</summary>
        public int[] BoneIndices;

        /// <summary>Rotations, x y z w, at (frame * BoneCount + bone) * 4.</summary>
        public float[] Rotations;

        /// <summary>Translations at (frame * BoneCount + bone) * 3.</summary>
        public float[] Translations;

        public static EtsAnimation Parse(byte[] b, string path)
        {
            if (b == null || b.Length < 84)
            {
                throw new ArgumentException(path + ": pma too short.");
            }

            uint version = U32(b, 0);

            if (version != 5)
            {
                throw new ArgumentException(path + ": pma version " + version + " is not understood (expected 5).");
            }

            var anim = new EtsAnimation
            {
                Path = path,
                FrameCount = b[4] | (b[5] << 8),
                Flags = b[6] | (b[7] << 8),
                BoneCount = (int)U32(b, 8),
                SkeletonHash = U32(b, 12) | ((ulong)U32(b, 16) << 32),
                Length = F32(b, 60)
            };

            int lengthsOffset = (int)U32(b, 64);
            int bonesOffset = (int)U32(b, 68);
            int framesOffset = (int)U32(b, 72);

            if (anim.FrameCount <= 0 || anim.BoneCount <= 0 || anim.BoneCount > 4096)
            {
                throw new ArgumentException(path + ": pma with " + anim.FrameCount + " frames and " + anim.BoneCount + " bones.");
            }

            if (lengthsOffset < 0 || lengthsOffset + anim.FrameCount * 4 > b.Length ||
                bonesOffset < 0 || bonesOffset + anim.BoneCount > b.Length ||
                framesOffset < 0 || (long)framesOffset + (long)anim.FrameCount * anim.BoneCount * 56 > b.Length)
            {
                throw new ArgumentException(path + ": pma tables run past the end of the file.");
            }

            anim.FrameTimes = new float[anim.FrameCount];
            float time = 0f;

            for (int f = 0; f < anim.FrameCount; f++)
            {
                anim.FrameTimes[f] = time;
                time += F32(b, lengthsOffset + f * 4);
            }

            if (!(anim.Length > 0f) || float.IsInfinity(anim.Length))
            {
                anim.Length = anim.FrameTimes[anim.FrameCount - 1];
            }

            anim.BoneIndices = new int[anim.BoneCount];

            for (int k = 0; k < anim.BoneCount; k++)
            {
                anim.BoneIndices[k] = b[bonesOffset + k];
            }

            anim.Rotations = new float[anim.FrameCount * anim.BoneCount * 4];
            anim.Translations = new float[anim.FrameCount * anim.BoneCount * 3];

            for (int f = 0; f < anim.FrameCount; f++)
            {
                for (int k = 0; k < anim.BoneCount; k++)
                {
                    int o = framesOffset + (f * anim.BoneCount + k) * 56;
                    int slot = f * anim.BoneCount + k;
                    anim.Rotations[slot * 4 + 3] = F32(b, o + 16);
                    anim.Rotations[slot * 4] = F32(b, o + 20);
                    anim.Rotations[slot * 4 + 1] = F32(b, o + 24);
                    anim.Rotations[slot * 4 + 2] = F32(b, o + 28);
                    anim.Translations[slot * 3] = F32(b, o + 32);
                    anim.Translations[slot * 3 + 1] = F32(b, o + 36);
                    anim.Translations[slot * 3 + 2] = F32(b, o + 40);
                }
            }

            return anim;
        }

        /// <summary>
        /// The pose of one animated bone at a point of the animation, 0 the first frame and 1 the
        /// last, blended between the two frames either side: the rotation by spherical
        /// interpolation (a gauge animation can have frames 130 degrees apart - the DAF XF's
        /// speedometer sweeps 260 degrees in three frames - where a normalised straight blend would
        /// run up to 10 degrees off the arc mid-way), the translation straight. No allocation.
        /// </summary>
        public void Sample(int animatedBone, float t01, float[] rotationXyzw, float[] translation)
        {
            float t = t01 > 0f ? (t01 < 1f ? t01 : 1f) : 0f;

            if (float.IsNaN(t01))
            {
                t = 0f;
            }

            float time = t * Length;
            int f0 = 0;

            while (f0 < FrameCount - 2 && FrameTimes[f0 + 1] <= time)
            {
                f0++;
            }

            int f1 = Math.Min(FrameCount - 1, f0 + 1);
            float span = FrameTimes[f1] - FrameTimes[f0];
            float u = span > 1e-6f ? Math.Max(0f, Math.Min(1f, (time - FrameTimes[f0]) / span)) : (time >= FrameTimes[f1] ? 1f : 0f);
            int a = (f0 * BoneCount + animatedBone) * 4;
            int c = (f1 * BoneCount + animatedBone) * 4;

            // The shorter way round between the two rotations.
            float dot = Rotations[a] * Rotations[c] + Rotations[a + 1] * Rotations[c + 1] + Rotations[a + 2] * Rotations[c + 2] + Rotations[a + 3] * Rotations[c + 3];
            float sign = dot < 0f ? -1f : 1f;
            dot *= sign;
            float wa = 1f - u, wc = u;

            if (dot < 0.9995f)
            {
                double theta = Math.Acos(Math.Min(1.0, (double)dot));
                double sin = Math.Sin(theta);

                if (sin > 1e-6)
                {
                    wa = (float)(Math.Sin((1.0 - u) * theta) / sin);
                    wc = (float)(Math.Sin(u * theta) / sin);
                }
            }

            float length = 0f;

            for (int i = 0; i < 4; i++)
            {
                rotationXyzw[i] = Rotations[a + i] * wa + Rotations[c + i] * sign * wc;
                length += rotationXyzw[i] * rotationXyzw[i];
            }

            length = (float)Math.Sqrt(length);

            if (length > 1e-6f)
            {
                for (int i = 0; i < 4; i++) rotationXyzw[i] /= length;
            }
            else
            {
                rotationXyzw[0] = rotationXyzw[1] = rotationXyzw[2] = 0f;
                rotationXyzw[3] = 1f;
            }

            int ta = (f0 * BoneCount + animatedBone) * 3;
            int tc = (f1 * BoneCount + animatedBone) * 3;

            for (int i = 0; i < 3; i++)
            {
                translation[i] = Translations[ta + i] * (1f - u) + Translations[tc + i] * u;
            }
        }

        private static uint U32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static float F32(byte[] b, int o)
        {
            return BitConverter.ToSingle(b, o);
        }
    }
}
