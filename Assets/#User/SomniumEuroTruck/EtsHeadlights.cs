using System;
using System.Globalization;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Headlights and wipers of the local player's own truck (2026-10-14).
    ///
    /// LIGHT CAST. Only the own truck throws real light (two spot lights, no shadows: the VR budget). Where and how much is the
    /// truck's head_light accessory (/def/vehicle/truck/&lt;truck&gt;/head_light/standard.sii, else the folder's first file):
    /// reflectors_offset (0, 0.82, -2.98 on the Scania S) and reflectors_distance 2.09 place the two lamps; low_beam_*_rot
    /// (pitch -3.5 / -2.5, yaw 5 / -2 degrees) aims them; low_beam_color (6.0, 5.4, 3.2 linear: halogen) x the world's
    /// exposure is the light, low_beam_range 80 m its reach and low_beam_angle 65 its cone; the main beam the hi_beam_* ones
    /// (12.0, 10.8, 6.5, 180 m). The beam masks (trucklight_mask_*.tobj, a 2:1 cut-off pattern) are not projected: a plain
    /// cone.
    ///
    /// AUTOMATIC. Until the player uses L or K (EtsTruckController), the own truck's dipped beams follow the lamps (dusk, night)
    /// and rain - a headset has no light stalk. The wipers run while it rains (rain_intensity above 0.05) and the engine
    /// runs, until Shift+P sets them by hand; one sweep out and back takes 1.4 s (the engine's speed is not in the files).
    /// Their phase drives the cab's wiper_anim (EtsInstruments) and other players see the switch on the wire (TruckWipers).
    /// </summary>
    public sealed class EtsHeadlights : MonoBehaviour
    {
        public const float WipePeriod = 1.4f;

        private sealed class Beam
        {
            public Vector3 Offset;
            public float Distance;
            public Vector3 LowLeft, LowRight, HighLeft, HighRight;
            public Color Low, High;
            public float LowRange, HighRange, LowAngle, HighAngle;
            public string Source;
        }

        private static EtsHeadlights _instance;
        private EtsTruckController _truck;
        private Beam _beam;
        private Light _left, _right;
        private float _wipe;
        private bool _logged, _loggedWipers;

        public static void Ensure(Transform parent)
        {
            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("Ets Headlights");

            if (parent != null)
            {
                host.transform.SetParent(parent, false);
            }

            _instance = host.AddComponent<EtsHeadlights>();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            DropLights();
        }

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                enabled = false;
                DropLights();
                Debug.LogWarning("[SomniumEuroTruck] Headlights: stopped after an error: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Tick()
        {
            EtsTruckRig rig = EtsLocalTruck.Own;
            EtsTruckController truck = rig != null ? rig.Controller : null;

            if (truck != _truck)
            {
                DropLights();
                _truck = truck;
                _beam = truck != null ? ReadBeam(rig) : null;
            }

            if (truck == null)
            {
                return;
            }

            bool running = truck.EngineRunning;

            // Automatic until the player switches by hand.
            if (!truck.LightsByHand)
            {
                int want = running && EtsDayNight.TrafficHeadlights ? 2 : 0;

                if (truck.Lights != want)
                {
                    truck.Lights = want;
                    truck.HighBeam = false;
                }
            }

            if (!truck.WipersByHand)
            {
                truck.Wipers = running && EtsDayNight.Rain > 0.05f;
            }

            Wipe(truck);

            // The cast light: only where it can be seen (the dipped beam's light is under 2 % of the sun's by day).
            EtsClimate day = EtsDayNight.Day;
            float exposure = EtsClimate.AppliedExposure;
            bool beams = truck.Lights >= 2 || truck.HighBeam;
            bool dark = day != null && day.Elevation < 8f;

            if (_beam == null || !beams || !dark)
            {
                SetLights(false);
                return;
            }

            if (_left == null)
            {
                _left = MakeLight("head light left");
                _right = MakeLight("head light right");
            }

            bool high = truck.HighBeam;
            Color colour = high ? _beam.High : _beam.Low;
            float peak = Mathf.Max(1e-4f, colour.maxColorComponent);
            Color hue = colour / peak;
            hue.a = 1f;
            Place(_left, truck.transform, -1f, high ? _beam.HighLeft : _beam.LowLeft);
            Place(_right, truck.transform, 1f, high ? _beam.HighRight : _beam.LowRight);

            Shape(_left, hue, peak * exposure, high);
            Shape(_right, hue, peak * exposure, high);

            SetLights(true);

            if (!_logged)
            {
                _logged = true;
                Debug.Log("[SomniumEuroTruck] Headlights: own truck's " + (high ? "main" : "dipped") + " beams cast (two spot lights, no shadows) from " + _beam.Source +
                          ": colour " + colour.ToString("0.00") + " x exposure " + exposure.ToString("0.###", CultureInfo.InvariantCulture) + ", range " +
                          _left.range.ToString("0", CultureInfo.InvariantCulture) + " m, cone " + _left.spotAngle.ToString("0", CultureInfo.InvariantCulture) + " deg; lights " +
                          (truck.LightsByHand ? "set by hand (L/K)" : "automatic (lamps on or rain)") + ".");
            }
        }


        private void Shape(Light light, Color hue, float intensity, bool high)
        {
            light.color = EtsClimate.ToGamma(hue);
            light.intensity = intensity;
            light.range = high ? _beam.HighRange : _beam.LowRange;
            light.spotAngle = high ? _beam.HighAngle : _beam.LowAngle;
            light.innerSpotAngle = light.spotAngle * 0.55f;
        }
        private void Wipe(EtsTruckController truck)
        {
            // Running: sweep after sweep. Switched off: the sweep in progress finishes and the blades park.
            if (truck.Wipers || _wipe > 0f)
            {
                _wipe += Time.deltaTime / WipePeriod;

                if (_wipe >= 1f)
                {
                    _wipe = truck.Wipers ? _wipe - 1f : 0f;
                }
            }

            float t = _wipe;
            truck.WiperPhase = t < 0.5f ? t * 2f : 2f - t * 2f;

            if (truck.Wipers && !_loggedWipers)
            {
                _loggedWipers = true;
                Debug.Log("[SomniumEuroTruck] Wipers: running (" + (truck.WipersByHand ? "by hand, Shift+P" : "automatic: rain " + EtsDayNight.Rain.ToString("0.00", CultureInfo.InvariantCulture)) +
                          "), a sweep every " + WipePeriod.ToString("0.0", CultureInfo.InvariantCulture) + " s on the cab's wiper_anim.");
            }
        }

        private static void Place(Light light, Transform truck, float side, Vector3 rot)
        {
            // Game frame (x right, y up, z back) to Unity (z forward): x stays, z turns; a positive game yaw turns left, a
            // negative game pitch down - both negated in Unity's Euler angles.
            EtsHeadlights self = _instance;
            Beam beam = self != null ? self._beam : null;

            if (beam == null)
            {
                return;
            }

            var local = new Vector3(side * beam.Distance * 0.5f, beam.Offset.y, -beam.Offset.z);
            light.transform.SetParent(truck, false);
            light.transform.localPosition = local;
            light.transform.localRotation = Quaternion.Euler(-rot.x, -rot.y, rot.z);
        }

        private Light MakeLight(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            Light light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.cullingMask = ~0;
            light.enabled = false;
            return light;
        }

        private void SetLights(bool on)
        {
            if (_left != null && _left.enabled != on) _left.enabled = on;
            if (_right != null && _right.enabled != on) _right.enabled = on;
        }

        private void DropLights()
        {
            if (_left != null) Destroy(_left.gameObject);
            if (_right != null) Destroy(_right.gameObject);
            _left = _right = null;
        }

        /// <summary>The truck's head_light accessory: standard.sii in its folder, else the first file there.</summary>
        private static Beam ReadBeam(EtsTruckRig rig)
        {
            EtsFileSystem files = EtsEngine.Files;
            string id = rig != null && rig.Assembly != null ? rig.Assembly.TruckId : null;

            if (files == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            string folder = "/def/vehicle/truck/" + id + "/head_light";
            string path = folder + "/standard.sii";

            if (!files.Exists(path))
            {
                path = null;

                if (files.TryList(folder, out var names, out _))
                {
                    foreach (string name in names)
                    {
                        if (name.EndsWith(".sii", StringComparison.Ordinal))
                        {
                            path = folder + "/" + name;
                            break;
                        }
                    }
                }
            }

            EtsSiiFile sii = path != null ? files.ReadSii(path) : null;
            EtsSiiUnit unit = null;

            if (sii != null)
            {
                foreach (EtsSiiUnit u in sii.OfType("accessory_head_lights_data"))
                {
                    unit = u;
                    break;
                }
            }

            if (unit == null)
            {
                return null;
            }

            return new Beam
            {
                Source = path,
                Offset = V3(unit, "reflectors_offset", new Vector3(0f, 0.8f, -3f)),
                Distance = unit.GetFloat("reflectors_distance", 2.1f),
                LowLeft = V3(unit, "low_beam_left_rot", new Vector3(-3.5f, 5f, 0f)),
                LowRight = V3(unit, "low_beam_right_rot", new Vector3(-2.5f, -2f, 0f)),
                HighLeft = V3(unit, "hi_beam_left_rot", new Vector3(-3.5f, 5f, 0f)),
                HighRight = V3(unit, "hi_beam_right_rot", new Vector3(-2.5f, -2f, 0f)),
                Low = C3(unit, "low_beam_color", new Color(6f, 5.4f, 3.2f)),
                High = C3(unit, "hi_beam_color", new Color(12f, 10.8f, 6.5f)),
                LowRange = unit.GetFloat("low_beam_range", 80f),
                HighRange = unit.GetFloat("hi_beam_range", 180f),
                LowAngle = Mathf.Clamp(unit.GetFloat("low_beam_angle", 65f), 5f, 170f),
                HighAngle = Mathf.Clamp(unit.GetFloat("hi_beam_angle", 65f), 5f, 170f)
            };
        }

        private static Vector3 V3(EtsSiiUnit unit, string key, Vector3 fallback)
        {
            float[] f = unit.GetFloats(key);
            return f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : fallback;
        }

        private static Color C3(EtsSiiUnit unit, string key, Color fallback)
        {
            float[] f = unit.GetFloats(key);
            return f.Length >= 3 ? new Color(f[0], f[1], f[2], 1f) : fallback;
        }
    }
}
