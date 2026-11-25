using System;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The falling rain round the local player (2026-10-14, time of day and weather).
    ///
    /// The game draws its rain as billboards of /material/environment/rain.mat (eut2.dif.flat.billb.over, rain.tobj 64 x 64)
    /// round the camera, as many as the profile's rain_intensity asks; how many and how large is the engine's (not in the
    /// files). Here: ONE mesh of <see cref="Drops"/> quads drawn with the scene's decal shader in its rain mode
    /// (Shaders/EtsDecal.shader _LampMode 2) - the drops fall through a 24 x 16 x 24 m box that follows the head and wraps, so
    /// each stays put in the world; a drop shows when its random rank is under rain_intensity; each faces the head's centre
    /// (the same quad for both eyes: stereo-correct), is 1.8 cm x 0.6 m and falls at 9 m/s (the speed of a real raindrop);
    /// lit by the ambient x rain_additional_ambient (the profile's own rain light: 5 by day, 18 at night) x exposure. Drops
    /// inside the cab the player sits in are not drawn. One draw call, 4 vertices a drop, nothing allocated a frame.
    /// </summary>
    public sealed class EtsRain : MonoBehaviour
    {
        public const int Drops = 6000;
        public static readonly Vector3 Box = new Vector3(24f, 16f, 24f);
        public const float FallSpeed = 9f;
        public const float Width = 0.018f, Length = 0.6f;

        /// <summary>The rain now: rain_intensity of the weather (0 dry) and its light (linear, x exposure).</summary>
        public static float Intensity;
        public static Color Light;

        /// <summary>The wind's push on the drops (m/s, world), from nothing in the files: none.</summary>
        public static Vector3 Wind = Vector3.zero;

        private static EtsRain _instance;
        private MeshRenderer _renderer;
        private Material _material;
        private Vector3 _drift;
        private bool _failed;
        private bool _logged;

        private static int _boxId, _centreId, _fallId, _driftId, _sizeId, _cabId, _cabExtentId, _colourId;

        public static void Ensure(Transform parent)
        {
            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("Ets Rain");

            if (parent != null)
            {
                host.transform.SetParent(parent, false);
            }

            _instance = host.AddComponent<EtsRain>();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            if (_renderer != null && _renderer.GetComponent<MeshFilter>() != null && _renderer.GetComponent<MeshFilter>().sharedMesh != null)
            {
                Destroy(_renderer.GetComponent<MeshFilter>().sharedMesh);
            }
        }

        private bool Build()
        {
            Shader shader = EtsMaterialCache.DecalShader != null ? EtsMaterialCache.DecalShader : Shader.Find("SomniumEuroTruck/Decal");

            if (shader == null || shader.FindPropertyIndex("_LampMode") < 0)
            {
                Debug.LogWarning("[SomniumEuroTruck] Rain: the scene's decal shader has no rain mode - no falling rain.");
                return false;
            }

            Texture2D drop = EtsEngine.Textures != null ? EtsEngine.Textures.Load("/material/environment/rain.tobj", false) : null;
            _material = new Material(shader) { name = "ets rain (rain.mat)" };

            if (drop != null)
            {
                _material.SetTexture("_BaseMap", drop);
            }

            _material.SetFloat("_LampMode", 2f);
            _material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            _material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            _material.SetFloat("_Cull", (float)CullMode.Off);
            _material.renderQueue = (int)RenderQueue.Transparent + 50;

            // The drops: a random seed in the unit box per quad (cosmetic, the same on every machine), its rank in alpha.
            var random = new System.Random(20261014);
            var positions = new Vector3[Drops * 4];
            var uvs = new Vector2[Drops * 4];
            var colours = new Color32[Drops * 4];
            var indices = new int[Drops * 6];

            for (int i = 0; i < Drops; i++)
            {
                var seed = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                var rank = new Color32(255, 255, 255, (byte)random.Next(0, 256));

                for (int k = 0; k < 4; k++)
                {
                    positions[i * 4 + k] = seed;
                    colours[i * 4 + k] = rank;
                }

                uvs[i * 4] = new Vector2(0f, 0f);
                uvs[i * 4 + 1] = new Vector2(1f, 0f);
                uvs[i * 4 + 2] = new Vector2(1f, 1f);
                uvs[i * 4 + 3] = new Vector2(0f, 1f);
                indices[i * 6] = i * 4;
                indices[i * 6 + 1] = i * 4 + 1;
                indices[i * 6 + 2] = i * 4 + 2;
                indices[i * 6 + 3] = i * 4;
                indices[i * 6 + 4] = i * 4 + 2;
                indices[i * 6 + 5] = i * 4 + 3;
            }

            var mesh = new Mesh { name = "ets rain drops", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = positions;
            mesh.uv = uvs;
            mesh.colors32 = colours;
            mesh.normals = new Vector3[Drops * 4];
            mesh.triangles = indices;

            // The vertex program places the drops round the head: never culled.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
            mesh.UploadMeshData(true);

            var holder = new GameObject("rain drops");
            holder.transform.SetParent(transform, false);
            holder.AddComponent<MeshFilter>().sharedMesh = mesh;
            _renderer = holder.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.allowOcclusionWhenDynamic = false;
            _renderer.enabled = false;

            _boxId = Shader.PropertyToID("_EtsRainBox");
            _centreId = Shader.PropertyToID("_EtsRainCentre");
            _fallId = Shader.PropertyToID("_EtsRainFall");
            _driftId = Shader.PropertyToID("_EtsRainDrift");
            _sizeId = Shader.PropertyToID("_EtsRainSize");
            _cabId = Shader.PropertyToID("_EtsRainCabFromWorld");
            _cabExtentId = Shader.PropertyToID("_EtsRainCabExtent");
            _colourId = Shader.PropertyToID("_LampColor");
            return true;
        }

        private void LateUpdate()
        {
            if (_failed)
            {
                return;
            }

            float intensity = Intensity;

            if (_renderer == null)
            {
                if (intensity <= 0.001f)
                {
                    return;
                }

                try
                {
                    if (!Build())
                    {
                        _failed = true;
                        return;
                    }
                }
                catch (Exception e)
                {
                    _failed = true;
                    Debug.LogWarning("[SomniumEuroTruck] Rain: could not be built: " + e.Message);
                    return;
                }
            }

            bool on = intensity > 0.001f;

            if (_renderer.enabled != on)
            {
                _renderer.enabled = on;
            }

            if (!on)
            {
                return;
            }

            Transform head = EtsTruckRig.LocalView();
            Vector3 centre = head != null ? head.position : Vector3.zero;
            Vector3 velocity = new Vector3(Wind.x, -FallSpeed, Wind.z);
            _drift += velocity * Time.deltaTime;
            _drift.x = Wrap(_drift.x, Box.x);
            _drift.y = Wrap(_drift.y, Box.y);
            _drift.z = Wrap(_drift.z, Box.z);

            Shader.SetGlobalVector(_boxId, new Vector4(Box.x, Box.y, Box.z, 0f));
            Shader.SetGlobalVector(_centreId, new Vector4(centre.x, centre.y, centre.z, Mathf.Clamp01(intensity)));
            Shader.SetGlobalVector(_fallId, new Vector4(velocity.x, velocity.y, velocity.z, 0f));
            Shader.SetGlobalVector(_driftId, new Vector4(_drift.x, _drift.y, _drift.z, 0f));
            Shader.SetGlobalVector(_sizeId, new Vector4(Width, Length, 0f, 0f));
            _material.SetVector(_colourId, new Vector4(Light.r, Light.g, Light.b, 0.8f));

            // The cab the player sits in keeps the rain out: a box round the seat in the truck's frame (2.6 m wide, from 2.2 m
            // under the eyes to 1 m over them, 1.5 m behind to 1.3 m ahead - the windscreen is 0.8-1 m ahead of the eyes).
            EtsTruckRig cab = EtsLocalTruck.Occupied;

            if (cab != null && head != null)
            {
                Transform truck = cab.transform;
                Vector3 middle = head.position + truck.up * -0.6f + truck.forward * -0.1f;
                Matrix4x4 fromWorld = Matrix4x4.TRS(middle, truck.rotation, Vector3.one).inverse;
                Shader.SetGlobalMatrix(_cabId, fromWorld);
                Shader.SetGlobalVector(_cabExtentId, new Vector4(1.3f, 1.6f, 1.4f, 1f));
            }
            else
            {
                Shader.SetGlobalVector(_cabExtentId, Vector4.zero);
            }

            if (!_logged)
            {
                _logged = true;
                Debug.Log("[SomniumEuroTruck] Rain: falling - " + Drops + " drops of rain.tobj in a " + Box.x + " x " + Box.y + " x " + Box.z + " m box round the head, " +
                          (intensity * 100f).ToString("0") + " % shown (rain_intensity), light " + Light.ToString("0.000") + " (ambient x rain_additional_ambient x exposure)" +
                          (cab != null ? ", kept out of the cab" : "") + ".");
            }
        }

        private static float Wrap(float v, float size)
        {
            return v - Mathf.Floor(v / size) * size;
        }
    }
}
