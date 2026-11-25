using System;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The pose of a model's skeleton: every bone's transform relative to its parent, starting
    /// from the bind pose the pmg stores and overwritten by whichever animations are applied.
    /// From there the world (model space) transform of any bone follows by walking up to the root.
    /// All in the game's own coordinates and quaternion convention; conversion to Unity is the
    /// caller's job.
    ///
    /// Apply and TryWorld allocate nothing (scratch arrays kept per pose), so a pose can be
    /// sampled every frame; one pose must not be used from two threads at once.
    /// </summary>
    public sealed class EtsSkeletonPose
    {
        private readonly EtsModelData _model;
        private readonly float[][] _rotation;
        private readonly float[][] _translation;
        private readonly int[] _chain;
        private readonly float[] _rot = new float[4];
        private readonly float[] _trans = new float[3];
        private readonly float[] _offset = new float[3];

        public EtsSkeletonPose(EtsModelData model)
        {
            _model = model;
            int count = model.Bones.Length;
            _rotation = new float[count][];
            _translation = new float[count][];
            _chain = new int[count + 1];

            for (int i = 0; i < count; i++)
            {
                _rotation[i] = (float[])model.Bones[i].Rotation.Clone();
                _translation[i] = (float[])model.Bones[i].Translation.Clone();
            }
        }

        public int BoneCount => _model.Bones.Length;

        /// <summary>Back to the bind pose the pmg stores.</summary>
        public void Reset()
        {
            for (int i = 0; i < _rotation.Length; i++)
            {
                Array.Copy(_model.Bones[i].Rotation, _rotation[i], 4);
                Array.Copy(_model.Bones[i].Translation, _translation[i], 3);
            }
        }

        /// <summary>Applies an animation at a point of it, 0 to 1. Returns how many bones it moved.</summary>
        public int Apply(EtsAnimation animation, float t01)
        {
            if (animation == null)
            {
                return 0;
            }

            int moved = 0;

            for (int k = 0; k < animation.BoneCount; k++)
            {
                int bone = animation.BoneIndices[k];

                if (bone < 0 || bone >= _rotation.Length)
                {
                    continue;
                }

                animation.Sample(k, t01, _rot, _trans);
                Array.Copy(_rot, _rotation[bone], 4);
                Array.Copy(_trans, _translation[bone], 3);
                moved++;
            }

            return moved;
        }

        /// <summary>A bone's current transform relative to its parent (x, y, z, w and x, y, z), copied out. False out of range.</summary>
        public bool TryLocal(int bone, float[] position, float[] rotationXyzw)
        {
            if (bone < 0 || bone >= _rotation.Length)
            {
                return false;
            }

            Array.Copy(_translation[bone], position, 3);
            Array.Copy(_rotation[bone], rotationXyzw, 4);
            return true;
        }

        /// <summary>
        /// The transform of a bone in model space under the current pose: position and rotation
        /// (x, y, z, w). False when the bone index is out of range or its parents loop.
        /// </summary>
        public bool TryWorld(int bone, float[] position, float[] rotationXyzw)
        {
            if (bone < 0 || bone >= _rotation.Length)
            {
                return false;
            }

            int length = 0;
            int current = bone;

            while (current >= 0 && current < _rotation.Length)
            {
                if (length >= _chain.Length - 1)
                {
                    return false;
                }

                _chain[length++] = current;
                current = _model.Bones[current].Parent;
            }

            position[0] = position[1] = position[2] = 0f;
            rotationXyzw[0] = rotationXyzw[1] = rotationXyzw[2] = 0f;
            rotationXyzw[3] = 1f;

            for (int i = length - 1; i >= 0; i--)
            {
                int b = _chain[i];
                Rotate(rotationXyzw, _translation[b], _offset);
                position[0] += _offset[0];
                position[1] += _offset[1];
                position[2] += _offset[2];
                Multiply(rotationXyzw, _rotation[b], rotationXyzw);
            }

            return true;
        }

        /// <summary>Rotates a vector by a quaternion (x, y, z, w). The result may be the vector itself.</summary>
        public static void Rotate(float[] q, float[] v, float[] result)
        {
            float x = q[0], y = q[1], z = q[2], w = q[3];
            float vx = v[0], vy = v[1], vz = v[2];
            float tx = 2f * (y * vz - z * vy);
            float ty = 2f * (z * vx - x * vz);
            float tz = 2f * (x * vy - y * vx);
            result[0] = vx + w * tx + (y * tz - z * ty);
            result[1] = vy + w * ty + (z * tx - x * tz);
            result[2] = vz + w * tz + (x * ty - y * tx);
        }

        /// <summary>Quaternion product a * b, x y z w, so that rotating by the result is b then a. The result may be a or b.</summary>
        public static void Multiply(float[] a, float[] b, float[] result)
        {
            float ax = a[0], ay = a[1], az = a[2], aw = a[3];
            float bx = b[0], by = b[1], bz = b[2], bw = b[3];
            result[0] = aw * bx + ax * bw + ay * bz - az * by;
            result[1] = aw * by - ax * bz + ay * bw + az * bx;
            result[2] = aw * bz + ax * by - ay * bx + az * bw;
            result[3] = aw * bw - ax * bx - ay * by - az * bz;
        }
    }
}
