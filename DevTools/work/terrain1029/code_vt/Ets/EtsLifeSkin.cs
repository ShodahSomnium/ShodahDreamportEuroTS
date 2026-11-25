using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The Unity half of a mover's skinning, apart from EtsLife so it can be tested on its own (living probe 12, a
    /// throwaway Unity project): the bone hierarchy from the pmg's bind pose, Unity bind poses from the file's bind
    /// matrices, bone weights from EtsModelSkin, lod skeletons mapped onto the model's own, and a legacy
    /// AnimationClip from an EtsMoverAnim.
    ///
    /// The game's frame is right-handed and Unity's left-handed; EtsModelBuilder mirrors z (x, y, -z), and for the
    /// skeleton the same mirror S = diag(1, 1, -1) is applied to everything: positions (x, y, -z), rotations
    /// (-x, -y, z, w), a bone's bind matrix S W S, scales unchanged. Composing each bone's local TRS up the chain
    /// reproduces the file's bind matrix W exactly on every mover skeleton measured (living probe 10), so the
    /// hierarchy at rest and the bind poses (S W S)^-1 agree and an unanimated model draws undeformed.
    /// </summary>
    public static class EtsLifeSkin
    {
        /// <summary>
        /// The skeleton under 'body' (bones parented as the file says, roots to body) in the bind pose, each bone's
        /// path from body (for the clip's curves) and its bind pose.
        /// </summary>
        public static Transform[] BuildSkeleton(EtsModelData model, Transform body, out string[] paths, out Matrix4x4[] bindposes)
        {
            int count = model.Bones.Length;
            var bones = new Transform[count];
            paths = new string[count];
            bindposes = new Matrix4x4[count];

            for (int i = 0; i < count; i++)
            {
                string name = model.Bones[i].Name;
                bones[i] = new GameObject(string.IsNullOrEmpty(name) ? "bone" + i : name).transform;
            }

            for (int i = 0; i < count; i++)
            {
                EtsModelBone bone = model.Bones[i];
                Transform parent = bone.Parent >= 0 && bone.Parent < count ? bones[bone.Parent] : body;
                bones[i].SetParent(parent, false);
                bones[i].localPosition = new Vector3(bone.Translation[0], bone.Translation[1], -bone.Translation[2]);
                bones[i].localRotation = new Quaternion(-bone.Rotation[0], -bone.Rotation[1], bone.Rotation[2], bone.Rotation[3]).normalized;
                bones[i].localScale = SaneScale(bone.Scale[0], bone.Scale[1], bone.Scale[2]);
                bindposes[i] = BindPose(bone.World);
            }

            for (int i = 0; i < count; i++)
            {
                paths[i] = BonePath(model, i, bones);
            }

            return bones;
        }

        /// <summary>A legacy clip (the only kind a runtime-made clip can be) that loops, with the animated bones' curves, keys thinned to at most ~150 per curve.</summary>
        public static AnimationClip BuildClip(EtsMoverAnim anim, string[] paths)
        {
            var clip = new AnimationClip { name = "life " + anim.Path.Substring(anim.Path.LastIndexOf('/') + 1), legacy = true, wrapMode = WrapMode.Loop };
            int step = Mathf.Max(1, Mathf.CeilToInt(anim.FrameCount / 150f));

            for (int k = 0; k < anim.BoneCount; k++)
            {
                int bone = anim.BoneIndices[k];
                if (bone < 0 || bone >= paths.Length) continue;
                AddCurves(clip, anim, k, paths[bone], step);
            }

            return clip;
        }

        /// <summary>The key step BuildClip uses for an animation.</summary>
        public static int KeyStep(EtsMoverAnim anim)
        {
            return Mathf.Max(1, Mathf.CeilToInt(anim.FrameCount / 150f));
        }

        /// <summary>Each bone of a lod skeleton to the model's bone with the same bind matrix (within a millimetre), or null when one has none.</summary>
        public static Transform[] MapBones(EtsModelData model, EtsModelData lod, Transform[] bones)
        {
            var result = new Transform[lod.Bones.Length];

            for (int j = 0; j < lod.Bones.Length; j++)
            {
                float[] w = lod.Bones[j].World;
                int best = -1;
                float bestDiff = float.MaxValue;

                for (int k = 0; k < model.Bones.Length; k++)
                {
                    float[] a = model.Bones[k].World;
                    float diff = 0f;
                    for (int e = 0; e < 12 && diff < bestDiff; e++) diff = Mathf.Max(diff, Mathf.Abs(a[e] - w[e]));

                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = k;
                    }
                }

                if (best < 0 || bestDiff > 1e-3f)
                {
                    return null;
                }

                result[j] = bones[best];
            }

            return result;
        }

        public static BoneWeight[] Weights(byte[] indices, byte[] weights, int count)
        {
            var result = new BoneWeight[count];
            var idx = new int[4];
            var w = new float[4];

            for (int v = 0; v < count; v++)
            {
                float sum = 0f;

                for (int k = 0; k < 4; k++)
                {
                    idx[k] = indices[v * 4 + k];
                    w[k] = weights[v * 4 + k];
                    sum += w[k];
                }

                // Largest first (Unity's own convention for four influences).
                for (int a = 1; a < 4; a++)
                {
                    for (int b = a; b > 0 && w[b] > w[b - 1]; b--)
                    {
                        float tw = w[b]; w[b] = w[b - 1]; w[b - 1] = tw;
                        int ti = idx[b]; idx[b] = idx[b - 1]; idx[b - 1] = ti;
                    }
                }

                if (sum <= 0f)
                {
                    w[0] = 1f;
                    sum = 1f;
                }

                result[v] = new BoneWeight
                {
                    boneIndex0 = idx[0], weight0 = w[0] / sum,
                    boneIndex1 = idx[1], weight1 = w[1] / sum,
                    boneIndex2 = idx[2], weight2 = w[2] / sum,
                    boneIndex3 = idx[3], weight3 = w[3] / sum
                };
            }

            return result;
        }

        /// <summary>The file's bind (bone to model, row-major, translation in the last column) in Unity's mirrored frame, inverted.</summary>
        public static Matrix4x4 BindPose(float[] w)
        {
            var m = new Matrix4x4();

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float sr = r == 2 ? -1f : 1f, sc = c == 2 ? -1f : 1f;
                    m[r, c] = sr * w[r * 4 + c] * sc;
                }
            }

            if (m.m33 == 0f && m.m30 == 0f && m.m31 == 0f && m.m32 == 0f)
            {
                m.m33 = 1f;
            }

            return m.inverse;
        }

        public static Vector3 SaneScale(float x, float y, float z)
        {
            return new Vector3(x > 1e-4f && !float.IsNaN(x) ? x : 1f, y > 1e-4f && !float.IsNaN(y) ? y : 1f, z > 1e-4f && !float.IsNaN(z) ? z : 1f);
        }

        public static string BonePath(EtsModelData model, int bone, Transform[] bones)
        {
            string path = bones[bone].name;
            int current = model.Bones[bone].Parent;
            int guard = 0;

            while (current >= 0 && current < bones.Length && guard++ < bones.Length)
            {
                path = bones[current].name + "/" + path;
                current = model.Bones[current].Parent;
            }

            return path;
        }

        /// <summary>Position, rotation (sign-continuous) and, where the file scales, scale curves of one animated bone, straight-line between keys as the game blends them.</summary>
        public static void AddCurves(AnimationClip clip, EtsMoverAnim anim, int k, string path, int step)
        {
            var frames = new List<int>();
            for (int f = 0; f < anim.FrameCount; f += step) frames.Add(f);
            if (frames[frames.Count - 1] != anim.FrameCount - 1) frames.Add(anim.FrameCount - 1);
            int n = frames.Count;
            var times = new float[n];
            var values = new float[10][];
            for (int c = 0; c < 10; c++) values[c] = new float[n];
            float px = 0f, py = 0f, pz = 0f, pw = 1f;

            for (int i = 0; i < n; i++)
            {
                int f = frames[i];
                int slot = f * anim.BoneCount + k;
                times[i] = anim.FrameTimes[f];
                float tx = anim.Translations[slot * 3], ty = anim.Translations[slot * 3 + 1], tz = anim.Translations[slot * 3 + 2];
                float qx = -anim.Rotations[slot * 4], qy = -anim.Rotations[slot * 4 + 1], qz = anim.Rotations[slot * 4 + 2], qw = anim.Rotations[slot * 4 + 3];

                if (i > 0 && px * qx + py * qy + pz * qz + pw * qw < 0f)
                {
                    qx = -qx; qy = -qy; qz = -qz; qw = -qw;
                }

                px = qx; py = qy; pz = qz; pw = qw;
                values[0][i] = tx; values[1][i] = ty; values[2][i] = -tz;
                values[3][i] = qx; values[4][i] = qy; values[5][i] = qz; values[6][i] = qw;
                values[7][i] = anim.Scales[slot * 3]; values[8][i] = anim.Scales[slot * 3 + 1]; values[9][i] = anim.Scales[slot * 3 + 2];
            }

            string[] properties = { "localPosition.x", "localPosition.y", "localPosition.z", "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w", "localScale.x", "localScale.y", "localScale.z" };
            int curves = anim.HasScale ? 10 : 7;

            for (int c = 0; c < curves; c++)
            {
                clip.SetCurve(path, typeof(Transform), properties[c], Linear(times, values[c]));
            }
        }

        public static AnimationCurve Linear(float[] times, float[] values)
        {
            int n = times.Length;
            var keys = new Keyframe[n];

            for (int i = 0; i < n; i++)
            {
                float left = i > 0 && times[i] > times[i - 1] ? (values[i] - values[i - 1]) / (times[i] - times[i - 1]) : 0f;
                float right = i + 1 < n && times[i + 1] > times[i] ? (values[i + 1] - values[i]) / (times[i + 1] - times[i]) : left;
                if (i == 0) left = right;
                keys[i] = new Keyframe(times[i], values[i], left, right);
            }

            return new AnimationCurve(keys);
        }
    }
}
