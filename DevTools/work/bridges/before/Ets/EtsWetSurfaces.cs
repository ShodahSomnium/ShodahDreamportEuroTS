using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Wet roads (2026-10-14, time of day and weather): the road and ground materials that carry a "reflection" - the game's
    /// WET-weather reflectivity (asphalt 1.0 in 206 of 239 road materials; ground mostly 0) - get the game's soaked look while
    /// the road is wet.
    ///
    /// The game's law (the deferred reflection pass /effect/deferred/_shd/eea84f9cc27dd2d0e0b428104341d608.glsl.fso; r = the
    /// material's reflection, written by its attribute pass as material0_data[2].x; W = the wetness, reflection_strengths.z):
    ///   porosity = r (1 - specular); diffuse x (1 - 0.55 W porosity);
    ///   gloss: specular power p becomes 500 (1 + (1 - 0.4 r W)(p / 500 - 1)) (15 -> 209 soaked);
    ///   the wet lighting weighs in by saturate(5 W) x r, and the environment (sky cube, planar reflection near the road)
    ///   is added by r x saturate(0.1 + 0.3 sum(specular)) x Fresnel(0.04 + 0.96 (1 - n.v)^5).
    /// Here on URP Lit / the two-layer shader: _BaseColor dimmed by the first, smoothness raised toward the soaked gloss
    /// (Blinn-Phong power to smoothness: 1 - (2 / (p + 2))^0.25) by the wet weight, and the environment reflection switched on
    /// while wet (it is off on these materials when dry: EtsMaterialCache) - so the road mirrors the sky probe at a grazing
    /// angle with Unity's own Fresnel. The planar reflection of the world is not drawn (VR cost).
    ///
    /// Main thread. Update touches the materials only when the wetness moved by 0.02 (a few hundred SetFloat calls, a few
    /// times a minute at most while it rains or dries).
    /// </summary>
    public static class EtsWetSurfaces
    {
        private sealed class Entry
        {
            public Material Material;
            public float Reflection;
            public float Specular;
            public float Power;
            public float DrySmoothness;
            public Color DryColour;
            public bool DryEnvOff;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Dictionary<Material, Entry> ByMaterial = new Dictionary<Material, Entry>();
        private static float _applied = 0f;

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        private static readonly int EnvironmentReflections = Shader.PropertyToID("_EnvironmentReflections");
        private const string EnvOff = "_ENVIRONMENTREFLECTIONS_OFF";

        public static int Count => Entries.Count;

        /// <summary>The wetness the materials show now.</summary>
        public static float Applied => _applied;

        /// <summary>EtsMaterialCache: a road or ground material with a wet reflection (its effect's specular and shininess as read).</summary>
        public static void Register(Material material, float reflection, float specular, float shininess)
        {
            if (material == null || reflection <= 0f || !material.HasProperty(Smoothness) || !material.HasProperty(BaseColor) || ByMaterial.ContainsKey(material))
            {
                return;
            }

            var entry = new Entry
            {
                Material = material,
                Reflection = Mathf.Clamp(reflection, 0f, 2f),
                Specular = specular,
                Power = Mathf.Max(1f, shininess),
                DrySmoothness = material.GetFloat(Smoothness),
                DryColour = material.GetColor(BaseColor),
                DryEnvOff = material.IsKeywordEnabled(EnvOff)
            };

            Entries.Add(entry);
            ByMaterial[material] = entry;

            if (_applied > 0f)
            {
                Set(entry, _applied);
            }
        }

        /// <summary>A copy of a registered material (EtsMaterialCache's per-vertex-colour twins) gets the same treatment.</summary>
        public static void Copy(Material source, Material copy)
        {
            // The copy is registered with its own colour (a vertex-colour tint) as it stands - copies are made as the map
            // streams in, mostly dry; one made in the rain keeps a little of the wet dimming until the next dry spell.
            if (source != null && copy != null && copy != source && ByMaterial.TryGetValue(source, out Entry e) && !ByMaterial.ContainsKey(copy))
            {
                // Made while wet: take the wet dimming and gloss back out first, so the copy dries to its own dry look.
                if (_applied > 0f && copy.HasProperty(BaseColor))
                {
                    float dim = Mathf.Max(0.05f, Dim(e, _applied));
                    Color c = copy.GetColor(BaseColor);
                    copy.SetColor(BaseColor, new Color(c.r / dim, c.g / dim, c.b / dim, c.a));
                    copy.SetFloat(Smoothness, e.DrySmoothness);
                }

                Register(copy, e.Reflection, e.Specular, e.Power);
            }
        }

        /// <summary>The road wetness now (0 dry .. rain_max_wetness); materials change when it moved by 0.02 or reached 0.</summary>
        public static void Update(float wetness)
        {
            wetness = Mathf.Clamp01(wetness);

            if (Mathf.Abs(wetness - _applied) < 0.02f && !(wetness == 0f && _applied > 0f))
            {
                return;
            }

            _applied = wetness;

            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                Entry e = Entries[i];

                if (e.Material == null)
                {
                    ByMaterial.Remove(e.Material);
                    Entries.RemoveAt(i);
                    continue;
                }

                Set(e, wetness);
            }
        }

        /// <summary>The soaked diffuse dimming: 1 - 0.55 W r (1 - specular); the specular is the effect's x the texture's alpha, taken as a half.</summary>
        private static float Dim(Entry e, float w)
        {
            float specular = Mathf.Clamp01(e.Specular * 0.5f);
            return Mathf.Clamp01(1f - 0.55f * w * e.Reflection * (1f - specular));
        }

        private static void Set(Entry e, float w)
        {
            float r = e.Reflection;
            float dim = Dim(e, w);
            float weight = Mathf.Clamp01(5f * w) * Mathf.Clamp01(r);
            float power = 500f * (1f + (1f - 0.4f * Mathf.Clamp01(r) * w) * (Mathf.Min(e.Power, 500f) / 500f - 1f));
            float wetSmooth = 1f - Mathf.Pow(2f / (power + 2f), 0.25f);
            Color colour = e.DryColour * dim;
            colour.a = e.DryColour.a;
            e.Material.SetColor(BaseColor, colour);
            e.Material.SetFloat(Smoothness, Mathf.Lerp(e.DrySmoothness, Mathf.Max(e.DrySmoothness, wetSmooth), weight));

            bool reflect = weight > 0.05f || !e.DryEnvOff;

            if (reflect && e.Material.IsKeywordEnabled(EnvOff))
            {
                e.Material.DisableKeyword(EnvOff);
                e.Material.SetFloat(EnvironmentReflections, 1f);
            }
            else if (!reflect && !e.Material.IsKeywordEnabled(EnvOff))
            {
                e.Material.EnableKeyword(EnvOff);
                e.Material.SetFloat(EnvironmentReflections, 0f);
            }
        }

        public static string Describe()
        {
            return Entries.Count + " road/ground materials with a wet reflection, now at wetness " + _applied.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
