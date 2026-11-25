using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>What a cab animation is driven by (EtsInstrumentKit.Classify); indexes EtsCabState.Values.</summary>
    public enum EtsCabSignal
    {
        None = 0,

        // Gauges, in the unit the definition's *_anim_min / *_anim_max are written in.
        Speed, Rpm, WaterTemp, OilTemp, Fuel, AdBlue, AirPressure, BrakePressure, OilPressure, Battery, Consumption, Turbo, ExhaustTemp,

        // Warning and indicator lamps: 0 off, 1 on.
        LeftBlinkerLamp, RightBlinkerLamp, HazardLamp, LowBeamLamp, HighBeamLamp, PositionLightsLamp, ParkingBrakeLamp,
        LowFuelLamp, LowAdBlueLamp, LowAirLamp, LowOilLamp, RetarderLamp, BatteryLamp, WaterTempLamp, OffLamp,

        // Controls, already as the animation's 0..1.
        BlinkerStalk, ParkingStick, LightsSwitch, HighBeamStalk, Ignition, Transmission, HazardSwitch,
        Zero, Half,

        Count
    }

    /// <summary>How a binding is counted and logged.</summary>
    public enum EtsInstrumentKind { Needle, Lamp, Control, Rest }

    /// <summary>One animation key of an interior definition, resolved: its animation, what drives it and how.</summary>
    public sealed class EtsInstrumentBinding
    {
        public string Key;
        public string AnimationPath;
        public EtsAnimation Animation;
        public EtsCabSignal Signal;
        public EtsInstrumentKind Kind;

        /// <summary>The signal's value at the animation's start (0) and end (1); from the definition, else the kind's default.</summary>
        public float Min, Max = 1f;

        /// <summary>True when the definition gave no *_anim_max and a plausible range was assumed (the documented default, 0..1, would peg the needle).</summary>
        public bool RangeAssumed;

        /// <summary>For Rest bindings: the constant point of the animation (the column's default adjustment, a closed window).</summary>
        public float Fixed;

        /// <summary>The animation's 0..1 for a signal value.</summary>
        public float T(float value)
        {
            if (Kind == EtsInstrumentKind.Rest) return Fixed;
            if (Kind != EtsInstrumentKind.Needle) return value;
            float span = Max - Min;
            if (Math.Abs(span) < 1e-6f) return 0f;
            float t = (value - Min) / span;
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }
    }

    /// <summary>
    /// Everything a truck's working instruments need from the game files, read once per interior definition and
    /// shared by every truck with that cab. Pure data, no UnityEngine: built on a worker thread (EtsInstruments) and
    /// by the harness (instruments probes 1-6), which is how it is checked against all 18 Truck Preference trucks.
    ///
    /// What the files say (1.61.1.0, instruments probe 1; the SCS modding wiki page "Truck Interior Animations and
    /// IDs" for the meaning of 0 and 1):
    ///  - The interior definition (accessory_interior_data, e.g. /def/vehicle/truck/daf.xf_euro6/interior/standard.sii
    ///    with its @include animations.sui) names an animated_model (anim.pmd: a skinned model with a skeleton - 51
    ///    bones on the DAF XF Euro 6, 12 pieces, 4,587 vertices) and one .pma per instrument: speedometer_anim,
    ///    tachometer_anim, water_temp_anim, fuel_anim, air_pressure_anim, oil_pressure_anim, ad_blue_anim,
    ///    consumption_anim with *_anim_min / *_anim_max (the value at the animation's start and end: DAF 0..160 km/h,
    ///    0..2500 rpm, 30..110 degC; fuel and AdBlue are the tank's share, 0..1); button_* / indicator_* warning lamps
    ///    (two keyframes, 0 off, 1 on: the lamp bone slides 0.3 m - 1.6 m on the DAF XF - from behind the cluster to
    ///    its window); and the controls (blinker_anim 0 left / 0.5 rest / 1 right, parking_stick_anim 0 released /
    ///    1 applied, lights_anim 0 off / 0.5 parking / 1 headlights, engine_start_anim 0 off / 0.33 electricity /
    ///    0.67 running / 1 starting, auto_transmission_stick_anim 0 R / 0.5 N / 1 D, wiper_anim 0 = parked).
    ///  - The needles and lamps are vertices of anim.pmg skinned to those bones: piece +88 is four u8 bone indices per
    ///    vertex and +92 four u8 weights summing to 255 (every vertex of all 17 cabs; up to 2,139 vertices with more
    ///    than one influence on the Volvo FH16 2012, so the cab is drawn skinned, not cut into rigid parts).
    ///  - A keyframe is a bone's transform relative to its parent (the same frame as the bone's bind TRS), so the pose
    ///    is the bind pose with each driven bone's local transform replaced by its sample.
    /// </summary>
    public sealed class EtsInstrumentKit
    {
        public string TruckName;
        public string InteriorPath;
        public string ModelPath;
        public EtsModelData Model;
        public readonly List<EtsInstrumentBinding> Bindings = new List<EtsInstrumentBinding>();

        /// <summary>Per piece: 4 bone indices and 4 weights (0..255) per vertex; null when the piece has none.</summary>
        public byte[][] SkinIndices = new byte[0][];
        public byte[][] SkinWeights = new byte[0][];

        /// <summary>Bones some binding animates; bones that move (driven, or under a driven bone); pieces with a vertex on a moving bone.</summary>
        public bool[] BoneDriven = new bool[0];
        public bool[] BoneMoves = new bool[0];
        public bool[] PieceMoves = new bool[0];

        /// <summary>Per bone: how many vertices it moves (any weight), and whether another bone hangs under it. A lamp bone
        /// with a few vertices and no children is hidden by a zero scale while off (EtsInstruments).</summary>
        public int[] BoneVertices = new int[0];
        public bool[] BoneHasChildren = new bool[0];

        /// <summary>Litres, from the chassis definition's tank_size / adblue_tank_size (a plausible default without one).</summary>
        public float TankLitres = 800f;
        public float AdBlueLitres = 80f;

        public int Needles, Lamps, Controls, Rests;

        /// <summary>"speed, rpm, water, fuel, ..." for the log line.</summary>
        public string BoundSummary = "";

        /// <summary>Keys the definition names that are not driven (unknown, missing file, other skeleton), for the log.</summary>
        public readonly List<string> Skipped = new List<string>();
        public readonly List<string> Notes = new List<string>();

        /// <summary>Set when nothing can be driven; the dashboard then stays as the factory built it.</summary>
        public string Failure;

        public int MovingPieces
        {
            get
            {
                int n = 0;
                foreach (bool m in PieceMoves) if (m) n++;
                return n;
            }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Reads a kit. Never throws: a failure is in Failure (and a bad animation is only skipped). `read` returns an
        /// archive file's bytes or null, `readSii` a parsed definition or null - both must be safe on the calling thread
        /// (EtsTruckAssembly.UncachedReader on a worker). `model` is the parsed animated model (shared, read-only).
        /// </summary>
        public static EtsInstrumentKit Build(Func<string, byte[]> read, Func<string, EtsSiiFile> readSii, EtsModelData model,
                                             string interiorPath, string chassisPath, string truckName)
        {
            var kit = new EtsInstrumentKit { TruckName = truckName, InteriorPath = interiorPath, Model = model, ModelPath = model != null ? model.Path : null };

            try
            {
                BuildInto(kit, read, readSii, model, interiorPath, chassisPath);
            }
            catch (Exception e)
            {
                kit.Failure = e.GetType().Name + ": " + e.Message;
            }

            return kit;
        }

        private static void BuildInto(EtsInstrumentKit kit, Func<string, byte[]> read, Func<string, EtsSiiFile> readSii, EtsModelData model,
                                      string interiorPath, string chassisPath)
        {
            if (model == null || model.Bones.Length == 0)
            {
                kit.Failure = "the dashboard model has no skeleton (" + (model != null ? model.Path : "none") + ")";
                return;
            }

            EtsSiiFile sii = string.IsNullOrEmpty(interiorPath) ? null : readSii(interiorPath);
            EtsSiiUnit def = sii != null && sii.Units.Count > 0 ? sii.Units[0] : null;

            if (def == null)
            {
                kit.Failure = "interior definition not found: " + (interiorPath ?? "none");
                return;
            }

            int boneCount = model.Bones.Length;
            kit.BoneDriven = new bool[boneCount];
            var bound = new List<string>();
            var keys = new List<string>(def.Attributes.Keys);
            keys.Sort(StringComparer.Ordinal);

            foreach (string key in keys)
            {
                EtsSiiValue value = def.Attributes[key];

                if (value == null || value.IsArray)
                {
                    continue;
                }

                string path = EtsSiiValue.Unquote(value.Raw ?? "");

                if (!path.EndsWith(".pma", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EtsInstrumentKind kind;
                float fixedT;
                EtsCabSignal signal = Classify(key, out kind, out fixedT);

                if (signal == EtsCabSignal.None)
                {
                    kit.Skipped.Add(key + " (not driven)");
                    continue;
                }

                byte[] bytes = read(path);

                if (bytes == null)
                {
                    kit.Skipped.Add(key + " (missing " + path + ")");
                    continue;
                }

                EtsAnimation anim;

                try
                {
                    anim = EtsAnimation.Parse(bytes, path);
                }
                catch (Exception e)
                {
                    kit.Skipped.Add(key + " (" + e.Message + ")");
                    continue;
                }

                if (anim.SkeletonHash != model.SkeletonHash)
                {
                    kit.Skipped.Add(key + " (made for another skeleton)");
                    continue;
                }

                bool indicesOk = true;

                for (int k = 0; k < anim.BoneCount; k++)
                {
                    if (anim.BoneIndices[k] >= boneCount) indicesOk = false;
                }

                if (!indicesOk)
                {
                    kit.Skipped.Add(key + " (bone index past the skeleton)");
                    continue;
                }

                var binding = new EtsInstrumentBinding { Key = key, AnimationPath = path, Animation = anim, Signal = signal, Kind = kind, Fixed = fixedT };

                if (kind == EtsInstrumentKind.Needle)
                {
                    string stem = key.EndsWith("_anim", StringComparison.Ordinal) ? key : key;
                    bool hasMax = def.Has(stem + "_max");
                    DefaultRange(signal, out float dMin, out float dMax);
                    binding.Min = def.GetFloat(stem + "_min", hasMax ? 0f : dMin);
                    binding.Max = hasMax ? def.GetFloat(stem + "_max", dMax) : dMax;
                    binding.RangeAssumed = !hasMax && dMax != 1f;
                }
                else if (kind == EtsInstrumentKind.Rest && key == "wheel_updown_anim")
                {
                    binding.Fixed = Clamp01(def.GetFloat("wheel_updown_anim_default", 0.5f));
                }
                else if (kind == EtsInstrumentKind.Rest && key == "wheel_frontback_anim")
                {
                    binding.Fixed = Clamp01(def.GetFloat("wheel_frontback_anim_default", 0.5f));
                }

                kit.Bindings.Add(binding);

                for (int k = 0; k < anim.BoneCount; k++)
                {
                    kit.BoneDriven[anim.BoneIndices[k]] = true;
                }

                switch (kind)
                {
                    case EtsInstrumentKind.Needle: kit.Needles++; bound.Add(ShortName(key) + (binding.RangeAssumed ? "*" : "")); break;
                    case EtsInstrumentKind.Lamp: kit.Lamps++; bound.Add(ShortName(key)); break;
                    case EtsInstrumentKind.Control: kit.Controls++; break;
                    default: kit.Rests++; break;
                }
            }

            // The skeleton: which bones move (driven, or hanging under a driven one).
            kit.BoneMoves = new bool[boneCount];

            for (int b = 0; b < boneCount; b++)
            {
                int guard = 0;

                for (int c = b; c >= 0 && c < boneCount && guard <= boneCount; c = model.Bones[c].Parent, guard++)
                {
                    if (kit.BoneDriven[c])
                    {
                        kit.BoneMoves[b] = true;
                        break;
                    }
                }
            }

            ReadSkin(kit, read, model);

            // Tanks, from the chassis the save names.
            EtsSiiFile chassis = string.IsNullOrEmpty(chassisPath) ? null : readSii(chassisPath);
            EtsSiiUnit chassisDef = chassis != null && chassis.Units.Count > 0 ? chassis.Units[0] : null;

            if (chassisDef != null)
            {
                kit.TankLitres = Math.Max(50f, chassisDef.GetFloat("tank_size", kit.TankLitres));
                kit.AdBlueLitres = Math.Max(10f, chassisDef.GetFloat("adblue_tank_size", kit.AdBlueLitres));
            }

            kit.BoundSummary = string.Join(", ", bound.ToArray());

            if (kit.Bindings.Count == 0)
            {
                kit.Failure = "the interior definition names no animation this world can drive";
            }
            else if (kit.MovingPieces == 0)
            {
                kit.Failure = "no dashboard vertex hangs on a driven bone (skin streams " + (kit.SkinIndices.Length > 0 ? "read" : "missing") + ")";
            }
        }

        /// <summary>The skin streams of the animated model's pmg, per piece; checked against the parsed model.</summary>
        private static void ReadSkin(EtsInstrumentKit kit, Func<string, byte[]> read, EtsModelData model)
        {
            string pmd = model.Path ?? "";
            string pmgPath = pmd.EndsWith(".pmd", StringComparison.OrdinalIgnoreCase) ? pmd.Substring(0, pmd.Length - 4) + ".pmg" : pmd;
            byte[] g = read(pmgPath);
            int pieces = model.Pieces.Length;
            kit.SkinIndices = new byte[pieces][];
            kit.SkinWeights = new byte[pieces][];
            kit.PieceMoves = new bool[pieces];
            kit.BoneVertices = new int[model.Bones.Length];
            kit.BoneHasChildren = new bool[model.Bones.Length];

            for (int b = 0; b < model.Bones.Length; b++)
            {
                int parent = model.Bones[b].Parent;
                if (parent >= 0 && parent < model.Bones.Length && parent != b) kit.BoneHasChildren[parent] = true;
            }

            if (g == null || g.Length < 112)
            {
                kit.Notes.Add("pmg missing: " + pmgPath);
                return;
            }

            int pieceCount = (int)BitConverter.ToUInt32(g, 4);
            int piecesOffset = (int)BitConverter.ToUInt32(g, 84);

            if (pieceCount != pieces || piecesOffset < 0 || piecesOffset + (long)pieceCount * 100 > g.Length)
            {
                kit.Notes.Add("pmg piece table disagrees with the model (" + pieceCount + " vs " + pieces + ")");
                return;
            }

            int boneCount = model.Bones.Length;

            for (int p = 0; p < pieces; p++)
            {
                int o = piecesOffset + p * 100;
                int vcount = (int)BitConverter.ToUInt32(g, o + 4);
                int stride = (int)BitConverter.ToUInt32(g, o + 60);
                int s88 = BitConverter.ToInt32(g, o + 88);
                int s92 = BitConverter.ToInt32(g, o + 92);

                if (vcount != model.Pieces[p].VertexCount || s88 < 0 || s92 < 0 || stride <= 0 ||
                    s88 + (long)(vcount - 1) * stride + 4 > g.Length || s92 + (long)(vcount - 1) * stride + 4 > g.Length)
                {
                    continue;
                }

                var idx = new byte[vcount * 4];
                var wgt = new byte[vcount * 4];
                bool moves = false;

                for (int v = 0; v < vcount; v++)
                {
                    int sum = 0;

                    for (int k = 0; k < 4; k++)
                    {
                        byte bone = g[s88 + v * stride + k];
                        byte weight = g[s92 + v * stride + k];

                        if (bone >= boneCount)
                        {
                            bone = 0;
                            weight = 0;
                        }

                        idx[v * 4 + k] = bone;
                        wgt[v * 4 + k] = weight;
                        sum += weight;

                        if (weight > 0) kit.BoneVertices[bone]++;

                        if (weight > 0 && kit.BoneMoves[bone])
                        {
                            moves = true;
                        }
                    }

                    if (sum == 0)
                    {
                        // No influence at all: pinned to the root bone as drawn.
                        idx[v * 4] = 0;
                        wgt[v * 4] = 255;
                    }
                }

                kit.SkinIndices[p] = idx;
                kit.SkinWeights[p] = wgt;
                kit.PieceMoves[p] = moves;
            }
        }

        /// <summary>
        /// A definition key's signal and kind. Rest keys hold a fixed point (fixedT): the documented rest position, so the
        /// geometry shows what the game shows with nothing touched - not the bind pose, which on the DAF XF Euro 6 puts
        /// the needles 16 degrees past zero and every warning lamp half-way out of the cluster (probe 348).
        /// </summary>
        public static EtsCabSignal Classify(string key, out EtsInstrumentKind kind, out float fixedT)
        {
            kind = EtsInstrumentKind.Needle;
            fixedT = 0f;

            switch (key)
            {
                case "speedometer_anim": return EtsCabSignal.Speed;
                case "tachometer_anim": return EtsCabSignal.Rpm;
                case "water_temp_anim": return EtsCabSignal.WaterTemp;
                case "oil_temp_anim":
                case "diff_temp_anim": return EtsCabSignal.OilTemp;
                case "fuel_anim": return EtsCabSignal.Fuel;
                case "ad_blue_anim": return EtsCabSignal.AdBlue;
                case "air_pressure_anim":
                case "general_air_press_anim": return EtsCabSignal.AirPressure;
                case "brake_pressure_anim": return EtsCabSignal.BrakePressure;
                case "oil_pressure_anim": return EtsCabSignal.OilPressure;
                case "battery_anim": return EtsCabSignal.Battery;
                case "consumption_anim": return EtsCabSignal.Consumption;
                case "turbo_anim": return EtsCabSignal.Turbo;
                case "exhaust_temp_anim": return EtsCabSignal.ExhaustTemp;
            }

            kind = EtsInstrumentKind.Lamp;

            switch (key)
            {
                case "button_left_blinker": return EtsCabSignal.LeftBlinkerLamp;
                case "button_right_blinker": return EtsCabSignal.RightBlinkerLamp;
                case "button_4way": return EtsCabSignal.HazardLamp;
                case "button_lights": return EtsCabSignal.LowBeamLamp;
                case "button_high_beam_lights": return EtsCabSignal.HighBeamLamp;
                case "indicator_position_lights": return EtsCabSignal.PositionLightsLamp;
                case "button_parking_brake": return EtsCabSignal.ParkingBrakeLamp;
                case "button_low_fuel":
                case "indicator_low_fuel": return EtsCabSignal.LowFuelLamp;
                case "indicator_low_adblue": return EtsCabSignal.LowAdBlueLamp;
                case "indicator_low_air_pressure":
                case "button_low_air_psi": return EtsCabSignal.LowAirLamp;
                case "indicator_low_oil_pressure":
                case "button_low_oil_psi": return EtsCabSignal.LowOilLamp;
                case "indicator_retarder":
                case "button_jakebrake": return EtsCabSignal.RetarderLamp;
                case "button_battery_warning": return EtsCabSignal.BatteryLamp;
                case "button_water_temp": return EtsCabSignal.WaterTempLamp;
            }

            if (key.StartsWith("indicator_", StringComparison.Ordinal) || key.StartsWith("button_", StringComparison.Ordinal))
            {
                // Axle lift, differential lock, generic, warning, stop, aux lights, cruise control, blind spot, weigh:
                // nothing in this world switches them, so they are held off (hidden) rather than left half-way out at
                // the bind pose.
                return EtsCabSignal.OffLamp;
            }

            kind = EtsInstrumentKind.Control;

            switch (key)
            {
                case "blinker_anim": return EtsCabSignal.BlinkerStalk;
                case "parking_stick_anim":
                case "parking_stick_handbrake_anim":
                case "parking_stick_electric_anim": return EtsCabSignal.ParkingStick;
                case "lights_anim": return EtsCabSignal.LightsSwitch;
                case "high_beam_anim": return EtsCabSignal.HighBeamStalk;
                case "engine_start_anim": return EtsCabSignal.Ignition;
                case "auto_transmission_stick_anim": return EtsCabSignal.Transmission;
                case "flasher_4way_anim": return EtsCabSignal.HazardSwitch;
            }

            kind = EtsInstrumentKind.Rest;

            switch (key)
            {
                case "wheel_updown_anim":
                case "wheel_frontback_anim": fixedT = 0.5f; return EtsCabSignal.Zero;
                case "left_window_stick_anim":
                case "right_window_stick_anim":
                case "left_window_stick_manual_anim":
                case "right_window_stick_manual_anim": fixedT = 0.5f; return EtsCabSignal.Half;
                case "wiper_anim":
                case "wiper_stick_anim":
                case "retarder_stick_anim":
                case "engine_brake_stick_anim":
                case "light_horn_stick_anim":
                case "differential_lock_stick_anim":
                case "lift_axle_stick_anim":
                case "lift_axle_trailer_stick_anim":
                case "trailer_brake_stick_anim": fixedT = 0f; return EtsCabSignal.Zero;
            }

            // Windows, shades, beacons, gadgets, the wheel itself (hung by the factory): left as modelled.
            return EtsCabSignal.None;
        }

        /// <summary>A gauge's range when the definition gives no *_anim_max (the documented default 0..1 only fits fuel and AdBlue).</summary>
        public static void DefaultRange(EtsCabSignal signal, out float min, out float max)
        {
            min = 0f;

            switch (signal)
            {
                case EtsCabSignal.Speed: max = 125f; break;
                case EtsCabSignal.Rpm: max = 2500f; break;
                case EtsCabSignal.WaterTemp: min = 30f; max = 110f; break;
                case EtsCabSignal.OilTemp: min = 30f; max = 130f; break;
                case EtsCabSignal.AirPressure: max = 10f; break;
                case EtsCabSignal.BrakePressure: max = 10f; break;
                case EtsCabSignal.OilPressure: max = 8f; break;
                case EtsCabSignal.Battery: min = 16f; max = 32f; break;
                case EtsCabSignal.Consumption: max = 120f; break;
                case EtsCabSignal.Turbo: max = 3f; break;
                case EtsCabSignal.ExhaustTemp: max = 700f; break;
                default: max = 1f; break;
            }
        }

        private static string ShortName(string key)
        {
            switch (key)
            {
                case "speedometer_anim": return "speed";
                case "tachometer_anim": return "rpm";
                case "water_temp_anim": return "water";
                case "fuel_anim": return "fuel";
                case "ad_blue_anim": return "adblue";
                case "air_pressure_anim": return "air";
                case "oil_pressure_anim": return "oil";
            }

            string s = key;
            if (s.StartsWith("button_", StringComparison.Ordinal)) s = s.Substring(7);
            else if (s.StartsWith("indicator_", StringComparison.Ordinal)) s = s.Substring(10);
            if (s.EndsWith("_anim", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 5);
            return s;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }

        /// <summary>
        /// The main screen of a display piece, in Unity's frame (the game's mirrored on z, as EtsModelBuilder builds it):
        /// the largest connected patch of its triangles (the DAF XF Euro 6's display piece is five: the 11 x 6 cm centre
        /// screen and four small arcs and strips in the gauges, probe 5), as a rectangle on its plane - centre, right,
        /// up and the normal toward the driver (who looks along +z), with its width and height. Null when the piece has
        /// no usable patch. Pure.
        /// </summary>
        public static EtsScreenRect FindScreen(EtsModelPiece piece)
        {
            if (piece == null || piece.Positions == null || piece.Indices == null || piece.VertexCount < 3)
            {
                return null;
            }

            int n = piece.VertexCount;
            var parent = new int[n];

            for (int v = 0; v < n; v++) parent[v] = v;

            // Vertices joined by triangles, and those at the same spot (split for their uvs), are one patch.
            int[] idx = piece.Indices;

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                if (idx[t] >= n || idx[t + 1] >= n || idx[t + 2] >= n) continue;
                Union(parent, idx[t], idx[t + 1]);
                Union(parent, idx[t], idx[t + 2]);
            }

            var area = new double[n];

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if (a >= n || b >= n || c >= n) continue;
                double ux = piece.Positions[b * 3] - piece.Positions[a * 3], uy = piece.Positions[b * 3 + 1] - piece.Positions[a * 3 + 1], uz = piece.Positions[b * 3 + 2] - piece.Positions[a * 3 + 2];
                double wx = piece.Positions[c * 3] - piece.Positions[a * 3], wy = piece.Positions[c * 3 + 1] - piece.Positions[a * 3 + 1], wz = piece.Positions[c * 3 + 2] - piece.Positions[a * 3 + 2];
                double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
                area[Find(parent, a)] += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
            }

            int best = -1;

            for (int v = 0; v < n; v++)
            {
                if (parent[v] == v && (best < 0 || area[v] > area[best])) best = v;
            }

            if (best < 0 || area[best] < 1e-5)
            {
                return null;
            }

            // The patch's facing: its vertex normals (or its triangles' when it has none), toward the driver.
            double nx = 0, ny = 0, nz = 0;

            for (int v = 0; v < n; v++)
            {
                if (Find(parent, v) != best) continue;

                if (piece.Normals != null)
                {
                    nx += piece.Normals[v * 3];
                    ny += piece.Normals[v * 3 + 1];
                    nz -= piece.Normals[v * 3 + 2];
                }
            }

            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);

            if (len < 1e-6)
            {
                nx = 0; ny = 0.45; nz = -0.9;
                len = Math.Sqrt(ny * ny + nz * nz);
            }

            nx /= len; ny /= len; nz /= len;

            if (nz > 0) { nx = -nx; ny = -ny; nz = -nz; }

            // Up: world up in the plane; right = up x forward, forward being into the screen (-normal), as Unity's LookRotation has it.
            double upx = -nx * ny, upy = 1 - ny * ny, upz = -nz * ny;
            len = Math.Sqrt(upx * upx + upy * upy + upz * upz);

            if (len < 1e-3)
            {
                upx = 0; upy = 0; upz = 1; len = 1;
            }

            upx /= len; upy /= len; upz /= len;
            double fx = -nx, fy = -ny, fz = -nz;
            double rx = upy * fz - upz * fy, ry = upz * fx - upx * fz, rz = upx * fy - upy * fx;
            len = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            rx /= len; ry /= len; rz /= len;

            double minR = double.MaxValue, maxR = double.MinValue, minU = double.MaxValue, maxU = double.MinValue, depth = double.MinValue;
            int count = 0;

            for (int v = 0; v < n; v++)
            {
                if (Find(parent, v) != best) continue;
                double px = piece.Positions[v * 3], py = piece.Positions[v * 3 + 1], pz = -piece.Positions[v * 3 + 2];
                double r = px * rx + py * ry + pz * rz, u = px * upx + py * upy + pz * upz, d = px * nx + py * ny + pz * nz;
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (d > depth) depth = d;
                count++;
            }

            double cr = (minR + maxR) * 0.5, cu = (minU + maxU) * 0.5;

            return new EtsScreenRect
            {
                Centre = new[] { (float)(rx * cr + upx * cu + nx * depth), (float)(ry * cr + upy * cu + ny * depth), (float)(rz * cr + upz * cu + nz * depth) },
                Right = new[] { (float)rx, (float)ry, (float)rz },
                Up = new[] { (float)upx, (float)upy, (float)upz },
                Normal = new[] { (float)nx, (float)ny, (float)nz },
                Width = (float)(maxR - minR),
                Height = (float)(maxU - minU),
                Vertices = count,
                Area = (float)area[best]
            };
        }

        private static int Find(int[] parent, int v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }

            return v;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        // ------------------------------------------------------------------ the driver's controls (VR cab, 2026-09-25)

        /// <summary>
        /// Where the blinker stalk, the parking brake lever and the hazard switch are on this dashboard, from the game's
        /// own files: the vertices of the animated model that hang (at least half their weight) on the bones the control's
        /// .pma drives, or on bones under them, in the pieces the interior variant shows (`pieceShown`, by piece index;
        /// null = all). Measured by the VR cab scout on all 18 Truck Preference trucks of the 1.61 demo
        /// (cabscout/out1.txt): the stalk swings up for right (t = 1) and down for left (t = 0) on every one; the lever
        /// comes down and back toward the driver when applied.
        ///  - The main bone is the animated bone that turns furthest over the animation (the Scania R's stalk
        ///    animation drives three bones; joint29 is the stalk); its origin is the pivot.
        ///  - The tip is the control's vertex farthest from the pivot, with the bone it hangs on most: a point that
        ///    rides on that bone whatever the pose. The centre is the vertices' mean (for a switch).
        ///  - A cab with no flasher_4way_anim but a button_4way lamp that moves less than 3 cm (the Mercedes Actros and
        ///    Actros 2014: the lamp sits in the switch) takes that lamp as the hazard switch.
        /// Positions are Unity's frame (the game's mirrored on z, as EtsModelBuilder builds), in the dashboard root at the
        /// bind pose - the frame the bind poses are taken from (EtsInstruments.MakeSkeleton). Pure; never throws.
        /// </summary>
        public List<EtsCabControl> FindControls(bool[] pieceShown)
        {
            var found = new List<EtsCabControl>();

            try
            {
                if (Model == null || Model.Bones.Length == 0)
                {
                    return found;
                }

                int n = Model.Bones.Length;
                double[] wp = new double[n * 3], wq = new double[n * 4];
                BindPose(Model.Bones, wp, wq);

                foreach (EtsCabSignal signal in new[] { EtsCabSignal.BlinkerStalk, EtsCabSignal.ParkingStick, EtsCabSignal.HazardSwitch })
                {
                    // Each control on its own: one that cannot be measured (a truck's odd skin data) costs only itself.
                    try
                    {
                        EtsCabControl control = null;

                        for (int b = 0; b < Bindings.Count && control == null; b++)
                        {
                            EtsInstrumentBinding binding = Bindings[b];

                            if (binding.Kind == EtsInstrumentKind.Control && binding.Signal == signal)
                            {
                                control = Measure(b, signal, false, pieceShown, wp, wq);
                            }
                        }

                        // No hazard switch animation: the 4-way lamp, where it sits in the switch (it barely moves when lit).
                        for (int b = 0; b < Bindings.Count && control == null && signal == EtsCabSignal.HazardSwitch; b++)
                        {
                            EtsInstrumentBinding binding = Bindings[b];

                            if (binding.Key == "button_4way" && Travel(binding.Animation, out float metres, out float degrees) && metres < 0.03f && degrees < 10f)
                            {
                                control = Measure(b, signal, true, pieceShown, wp, wq);
                            }
                        }

                        if (control != null)
                        {
                            found.Add(control);
                        }
                    }
                    catch (Exception e)
                    {
                        Notes.Add("controls: " + signal + ": " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Notes.Add("controls: " + e.GetType().Name + ": " + e.Message);
            }

            return found;
        }

        private EtsCabControl Measure(int b, EtsCabSignal signal, bool lamp, bool[] pieceShown, double[] wp, double[] wq)
        {
            EtsInstrumentBinding binding = Bindings[b];
            EtsAnimation anim = binding.Animation;
            int n = Model.Bones.Length;

            // Bones this animation moves: driven by it, or hanging under one it drives.
            // Bone numbers are checked here as MakeSkeleton checks them (a skin index is a byte, the skeleton may be smaller).
            var driven = new bool[n];

            for (int k = 0; k < anim.BoneCount; k++)
            {
                int bi = anim.BoneIndices[k];
                if (bi >= 0 && bi < n) driven[bi] = true;
            }

            var moving = new bool[n];

            for (int bone = 0; bone < n; bone++)
            {
                int guard = 0;

                for (int c = bone; c >= 0 && c < n && guard <= n; c = Model.Bones[c].Parent, guard++)
                {
                    if (driven[c]) { moving[bone] = true; break; }
                }
            }

            // The main bone: the one that turns furthest, frame to frame (the first one for a switch that only slides).
            int main = -1;
            double bestTurn = -1;

            for (int k = 0; k < anim.BoneCount; k++)
            {
                if (anim.BoneIndices[k] < 0 || anim.BoneIndices[k] >= n)
                {
                    continue;
                }

                if (main < 0)
                {
                    main = anim.BoneIndices[k];
                }

                double turn = 0;

                for (int f = 1; f < anim.FrameCount; f++)
                {
                    double dot = 0;
                    for (int i = 0; i < 4; i++) dot += anim.Rotations[((f - 1) * anim.BoneCount + k) * 4 + i] * anim.Rotations[(f * anim.BoneCount + k) * 4 + i];
                    turn += 2 * Math.Acos(Math.Min(1.0, Math.Abs(dot)));
                }

                if (turn > bestTurn + 1e-9)
                {
                    bestTurn = turn;
                    main = anim.BoneIndices[k];
                }
            }

            if (main < 0)
            {
                return null;
            }

            var control = new EtsCabControl
            {
                Signal = signal, Key = binding.Key, Binding = b, MainBone = main, CentreBone = main, FromLamp = lamp,
                Momentary = IsMomentary(anim), PieceVertices = new int[Model.Pieces.Length][]
            };

            double px = wp[main * 3], py = wp[main * 3 + 1], pz = wp[main * 3 + 2];
            control.Pivot[0] = (float)px; control.Pivot[1] = (float)py; control.Pivot[2] = (float)pz;
            double cx = 0, cy = 0, cz = 0, far = -1;
            int tipPiece = -1, tipVertex = -1;
            var list = new List<int>();

            for (int p = 0; p < Model.Pieces.Length && p < SkinIndices.Length; p++)
            {
                byte[] idx = SkinIndices[p], wgt = SkinWeights[p];
                EtsModelPiece piece = Model.Pieces[p];

                if (idx == null || wgt == null || piece == null || piece.Positions == null || (pieceShown != null && (p >= pieceShown.Length || !pieceShown[p])))
                {
                    continue;
                }

                list.Clear();

                for (int v = 0; v < piece.VertexCount; v++)
                {
                    int on = 0;

                    for (int k = 0; k < 4; k++)
                    {
                        int w = wgt[v * 4 + k];
                        int bone = idx[v * 4 + k];
                        if (w > 0 && bone < n && moving[bone]) on += w;
                    }

                    if (on < 128)
                    {
                        continue;
                    }

                    list.Add(v);
                    double x = piece.Positions[v * 3], y = piece.Positions[v * 3 + 1], z = -piece.Positions[v * 3 + 2];
                    cx += x; cy += y; cz += z;
                    double d = (x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz);

                    if (d > far)
                    {
                        far = d;
                        tipPiece = p;
                        tipVertex = v;
                    }
                }

                if (list.Count > 0)
                {
                    control.PieceVertices[p] = list.ToArray();
                    control.Vertices += list.Count;
                }
            }

            if (control.Vertices == 0)
            {
                return null;
            }

            cx /= control.Vertices; cy /= control.Vertices; cz /= control.Vertices;
            control.Centre[0] = (float)cx; control.Centre[1] = (float)cy; control.Centre[2] = (float)cz;
            EtsModelPiece tp = Model.Pieces[tipPiece];
            control.Tip[0] = tp.Positions[tipVertex * 3];
            control.Tip[1] = tp.Positions[tipVertex * 3 + 1];
            control.Tip[2] = -tp.Positions[tipVertex * 3 + 2];
            control.Length = (float)Math.Sqrt(far);

            // The tip's own bone: the heaviest of its weights.
            int heaviest = -1, bestWeight = -1;

            for (int k = 0; k < 4; k++)
            {
                int w = SkinWeights[tipPiece][tipVertex * 4 + k];
                if (w > bestWeight) { bestWeight = w; heaviest = SkinIndices[tipPiece][tipVertex * 4 + k]; }
            }

            control.TipBone = heaviest >= 0 && heaviest < n ? heaviest : main;

            // How thick the control is round its pivot-to-tip line, and how far it reaches from its centre.
            double ax = control.Tip[0] - px, ay = control.Tip[1] - py, az = control.Tip[2] - pz;
            double len2 = ax * ax + ay * ay + az * az;
            double thick = 0, extent = 0;

            for (int p = 0; p < control.PieceVertices.Length; p++)
            {
                int[] verts = control.PieceVertices[p];
                if (verts == null) continue;
                float[] pos = Model.Pieces[p].Positions;

                foreach (int v in verts)
                {
                    double x = pos[v * 3], y = pos[v * 3 + 1], z = -pos[v * 3 + 2];
                    double s = len2 > 1e-12 ? ((x - px) * ax + (y - py) * ay + (z - pz) * az) / len2 : 0;
                    s = s < 0 ? 0 : s > 1 ? 1 : s;
                    double qx = x - (px + ax * s), qy = y - (py + ay * s), qz = z - (pz + az * s);
                    thick = Math.Max(thick, Math.Sqrt(qx * qx + qy * qy + qz * qz));
                    extent = Math.Max(extent, Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz)));
                }
            }

            control.Thickness = (float)thick;
            control.Extent = (float)extent;
            return control;
        }

        /// <summary>
        /// The skeleton at its bind pose in Unity's frame, parents first: position (x, y, -z) and rotation (-x, -y, z, w)
        /// relative to the parent, as EtsInstruments.MakeSkeleton sets its Transforms (scale one), composed to the root.
        /// </summary>
        private static void BindPose(EtsModelBone[] bones, double[] wp, double[] wq)
        {
            int n = bones.Length;
            var done = new bool[n];

            for (int pass = 0; pass <= n; pass++)
            {
                bool any = false;

                for (int b = 0; b < n; b++)
                {
                    if (done[b]) continue;
                    int parent = bones[b].Parent;
                    bool root = parent < 0 || parent >= n || parent == b || pass == n;
                    if (!root && !done[parent]) continue;

                    float[] t = bones[b].Translation, r = bones[b].Rotation;
                    double lx = t[0], ly = t[1], lz = -t[2];
                    double qx = -r[0], qy = -r[1], qz = r[2], qw = r[3];
                    double ql = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                    if (ql > 1e-9) { qx /= ql; qy /= ql; qz /= ql; qw /= ql; } else { qx = qy = qz = 0; qw = 1; }

                    if (root)
                    {
                        wp[b * 3] = lx; wp[b * 3 + 1] = ly; wp[b * 3 + 2] = lz;
                        wq[b * 4] = qx; wq[b * 4 + 1] = qy; wq[b * 4 + 2] = qz; wq[b * 4 + 3] = qw;
                    }
                    else
                    {
                        double px = wq[parent * 4], py = wq[parent * 4 + 1], pz = wq[parent * 4 + 2], pw = wq[parent * 4 + 3];
                        Rotate(px, py, pz, pw, lx, ly, lz, out double rx, out double ry, out double rz);
                        wp[b * 3] = wp[parent * 3] + rx; wp[b * 3 + 1] = wp[parent * 3 + 1] + ry; wp[b * 3 + 2] = wp[parent * 3 + 2] + rz;
                        wq[b * 4] = pw * qx + px * qw + py * qz - pz * qy;
                        wq[b * 4 + 1] = pw * qy + py * qw + pz * qx - px * qz;
                        wq[b * 4 + 2] = pw * qz + pz * qw + px * qy - py * qx;
                        wq[b * 4 + 3] = pw * qw - px * qx - py * qy - pz * qz;
                    }

                    done[b] = true;
                    any = true;
                }

                if (!any) break;
            }
        }

        private static void Rotate(double x, double y, double z, double w, double vx, double vy, double vz, out double rx, out double ry, out double rz)
        {
            double tx = 2 * (y * vz - z * vy), ty = 2 * (z * vx - x * vz), tz = 2 * (x * vy - y * vx);
            rx = vx + w * tx + (y * tz - z * ty);
            ry = vy + w * ty + (z * tx - x * tz);
            rz = vz + w * tz + (x * ty - y * tx);
        }

        /// <summary>How far an animation moves its bones between its first and last frame (the most of any bone).</summary>
        public static bool Travel(EtsAnimation anim, out float metres, out float degrees)
        {
            metres = 0f;
            degrees = 0f;

            if (anim == null || anim.FrameCount < 1 || anim.BoneCount < 1)
            {
                return false;
            }

            int last = anim.FrameCount - 1;

            for (int k = 0; k < anim.BoneCount; k++)
            {
                double dot = 0, move = 0;
                for (int i = 0; i < 4; i++) dot += anim.Rotations[k * 4 + i] * anim.Rotations[(last * anim.BoneCount + k) * 4 + i];
                for (int i = 0; i < 3; i++) { double d = anim.Translations[(last * anim.BoneCount + k) * 3 + i] - anim.Translations[k * 3 + i]; move += d * d; }
                degrees = Math.Max(degrees, (float)(2 * Math.Acos(Math.Min(1.0, Math.Abs(dot))) * 180 / Math.PI));
                metres = Math.Max(metres, (float)Math.Sqrt(move));
            }

            return true;
        }

        /// <summary>
        /// A press-and-return switch: the first and last frames are one pose (within 1 degree and 1 mm for every
        /// bone) and some frame between them is not (2 degrees or 1.5 mm off). The Scania R/S 2016 and Renault T hazard
        /// switches and the Renault T and Volvo FH16 2012 parking brakes are drawn so (cabscout/out1.txt: the FH16 2012 lever
        /// swings 15 degrees and back while its button stays 0.8 mm in); a 0-or-1 state never moves them, so the cab plays
        /// the animation through once when the driver presses one (EtsInstruments.PlayPress).
        /// </summary>
        public static bool IsMomentary(EtsAnimation anim)
        {
            if (anim == null || anim.FrameCount < 3 || !Travel(anim, out float metres, out float degrees) || metres > 0.001f || degrees > 1f)
            {
                return false;
            }

            for (int f = 1; f < anim.FrameCount - 1; f++)
            {
                for (int k = 0; k < anim.BoneCount; k++)
                {
                    double dot = 0, move = 0;
                    for (int i = 0; i < 4; i++) dot += anim.Rotations[k * 4 + i] * anim.Rotations[(f * anim.BoneCount + k) * 4 + i];
                    for (int i = 0; i < 3; i++) { double d = anim.Translations[(f * anim.BoneCount + k) * 3 + i] - anim.Translations[k * 3 + i]; move += d * d; }

                    if (2 * Math.Acos(Math.Min(1.0, Math.Abs(dot))) * 180 / Math.PI > 2.0 || Math.Sqrt(move) > 0.0015)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The hazard switch of a cab whose dashboard has no animated one: the centre of the hazard-triangle decal on the
        /// static interior model, in that model's Unity frame (= the dashboard's, both hang on the interior locator).
        /// Found by the VR cab scout from the game's files (cabscout/isl_*.txt, islands_daf.txt: the uv island on each
        /// interior piece that maps the triangle icon of the cab's dashboard atlas, the atlases decoded to PNG). False for
        /// any other interior - no guess is made; the Iveco Hi-Way's only candidate (a bone named warn_button with no
        /// hazard icon at its uvs) is left out.
        /// </summary>
        public static bool HazardDecal(string interiorModelPath, out float x, out float y, out float z)
        {
            x = y = z = 0f;

            switch (interiorModelPath)
            {
                case "/vehicle/truck/daf_xf_euro6/interior/model.pmd": x = 0.551f; y = -0.617f; z = 0.377f; return true;        // piece 24, atlas cell u 0.125-0.156 v 0.063-0.125
                case "/vehicle/truck/iveco_stralis/interior/model.pmd": x = 0.268f; y = -0.342f; z = 0.862f; return true;
                case "/vehicle/truck/man_tgx/interior/model.pmd": x = 0.255f; y = -0.506f; z = 0.797f; return true;             // piece 7
                case "/vehicle/truck/renault_magnum_2009/interior/model.pmd": x = -0.408f; y = -0.474f; z = 0.608f; return true; // piece 1
                case "/vehicle/truck/renault_premium/interior/model.pmd": x = 0.438f; y = -0.337f; z = 0.821f; return true;     // piece 17
                case "/vehicle/truck/scania_rcab_2009/interior/model.pmd": x = -0.180f; y = -0.495f; z = 0.555f; return true;   // piece 51 (R, R 6x4, Streamline)
            }

            return false;
        }

        /// <summary>One line for the log / probes.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Needles).Append(" needles, ").Append(Lamps).Append(" lamps, ").Append(Controls).Append(" controls, ").Append(Rests).Append(" held at rest; ")
              .Append(MovingPieces).Append(" of ").Append(PieceMoves.Length).Append(" dashboard pieces skinned");
            if (Skipped.Count > 0) sb.Append("; not driven: ").Append(string.Join(", ", Skipped.ToArray()));
            if (Notes.Count > 0) sb.Append("; ").Append(string.Join("; ", Notes.ToArray()));
            return sb.ToString();
        }
    }

    /// <summary>A display's main screen as a rectangle on its plane, Unity frame (EtsInstrumentKit.FindScreen).</summary>
    public sealed class EtsScreenRect
    {
        public float[] Centre, Right, Up, Normal;
        public float Width, Height, Area;
        public int Vertices;
    }

    /// <summary>
    /// One driver's control of the animated dashboard (EtsInstrumentKit.FindControls): positions in Unity's frame, in the
    /// dashboard root at the bind pose; bones by skeleton index.
    /// </summary>
    public sealed class EtsCabControl
    {
        public EtsCabSignal Signal;
        public string Key;
        public int Binding = -1;
        public int MainBone = -1;
        public int TipBone = -1;
        public int CentreBone = -1;
        public float[] Pivot = new float[3];
        public float[] Tip = new float[3];
        public float[] Centre = new float[3];

        /// <summary>Tip from the pivot; the farthest vertex from the pivot-to-tip line; the farthest vertex from the centre (m).</summary>
        public float Length, Thickness, Extent;

        public int Vertices;

        /// <summary>A press-and-return switch (EtsInstrumentKit.IsMomentary).</summary>
        public bool Momentary;

        /// <summary>The hazard switch taken from the button_4way lamp inside it (no flasher_4way_anim).</summary>
        public bool FromLamp;

        /// <summary>Per piece of the animated model, the control's vertex indices; null where it has none.</summary>
        public int[][] PieceVertices;
    }

    /// <summary>
    /// The cab's live values, from what the truck controller knows (speed, rpm, pedals, engine, indicators, parking
    /// brake) plus a small model of what it does not: fuel and AdBlue that drain with the engine's work, air that
    /// builds after a start and is spent by braking, coolant and oil that warm up, oil pressure, battery voltage,
    /// boost and consumption. Needles are damped the way real gauges are, so they sweep up when the ignition comes on.
    /// Pure and allocation-free; Step once a frame.
    /// </summary>
    public sealed class EtsCabState
    {
        /// <summary>Indexed by EtsCabSignal.</summary>
        public readonly float[] Values = new float[(int)EtsCabSignal.Count];

        // The modelled quantities (true values; Values holds what the needles show).
        public float FuelShare = 0.72f;
        public float AdBlueShare = 0.8f;
        public float AirBar = 3.2f;
        public float WaterC = 55f;
        public float OilC = 50f;
        public float TankLitres = 800f;
        public float AdBlueLitres = 80f;
        public float IdleRpm = 650f;
        public float MaxRpm = 2200f;

        /// <summary>Headlights: 0 off, 1 parking lights, 2 dipped; and main beam. Nothing in the world switches them yet.</summary>
        public int Lights;
        public bool HighBeam;

        private bool _wasRunning;
        private float _starting;
        private float _lastBrake;
        private float _consumptionShown;

        /// <summary>Starts every needle at its rest (engine off, cold).</summary>
        public EtsCabState(float tankLitres, float adBlueLitres, float fuelShare)
        {
            TankLitres = tankLitres > 1f ? tankLitres : 800f;
            AdBlueLitres = adBlueLitres > 1f ? adBlueLitres : 80f;
            FuelShare = fuelShare;
        }

        /// <param name="indicator">-1 left, 0 off, 1 right, 2 hazards (EtsTruckController.Indicator).</param>
        /// <param name="gear">Positive forward, negative reverse, 0 neutral.</param>
        public void Step(float dt, float speedKmh, float rpm, float throttle, float brake, bool engineRunning, bool electricity,
                         bool parkingBrake, int indicator, bool indicatorLit, int gear)
        {
            if (!(dt > 0f)) dt = 0f;
            if (dt > 0.25f) dt = 0.25f;
            if (float.IsNaN(speedKmh)) speedKmh = 0f;
            if (float.IsNaN(rpm)) rpm = 0f;
            throttle = Clamp(throttle, 0f, 1f);
            brake = Clamp(brake, 0f, 1f);
            electricity |= engineRunning;

            if (engineRunning && !_wasRunning) _starting = 1.2f;
            _wasRunning = engineRunning;
            if (_starting > 0f) _starting -= dt;

            float load = engineRunning ? Clamp(throttle * (0.35f + 0.65f * Clamp((rpm - IdleRpm) / Math.Max(1f, MaxRpm - IdleRpm), 0f, 1f)), 0f, 1f) : 0f;

            // Fuel: about 2.5 l/h at idle, up to 75 l/h flat out (a 13 l diesel at ~450 hp); AdBlue about 5 % of it.
            float litresPerHour = engineRunning ? 2.5f * Math.Max(0.5f, rpm / Math.Max(1f, IdleRpm)) + 72f * load : 0f;
            float litres = litresPerHour * dt / 3600f;
            FuelShare = Math.Max(0f, FuelShare - litres / TankLitres);
            AdBlueShare = Math.Max(0f, AdBlueShare - 0.05f * litres / AdBlueLitres);

            // Air: the compressor builds about 0.2 bar/s while the engine runs, to 10 bar; a brake application spends
            // some, and a parked truck leaks slowly.
            if (engineRunning) AirBar += (10f - AirBar) * Math.Min(1f, dt * (0.03f + 0.02f * rpm / 1000f)) + (AirBar < 10f ? 0.04f * dt : 0f);
            else AirBar -= 0.002f * dt;
            if (brake > _lastBrake + 0.05f) AirBar -= (brake - _lastBrake) * 0.6f;
            _lastBrake = brake;
            AirBar = Clamp(AirBar, 0f, 10.2f);

            // Coolant toward 85-90 degC while running (faster with load), back toward 20 when off; oil lags it.
            float waterTarget = engineRunning ? 86f + 6f * load : 20f;
            WaterC += (waterTarget - WaterC) * Math.Min(1f, dt * (engineRunning ? 0.012f + 0.02f * load : 0.002f));
            OilC += (WaterC + (engineRunning ? 8f * load : 0f) - OilC) * Math.Min(1f, dt * 0.008f);

            float oilBar = engineRunning ? 1.6f + 3.6f * Clamp((rpm - IdleRpm) / Math.Max(1f, MaxRpm - IdleRpm), 0f, 1f) * (WaterC < 60f ? 1.15f : 1f) : 0f;
            float volts = engineRunning ? 28.2f : electricity ? 24.6f : 0f;
            float boost = engineRunning ? 2.4f * load * Clamp(rpm / 1400f, 0f, 1f) : 0f;
            float consumption = speedKmh > 5f ? litresPerHour / speedKmh * 100f : (engineRunning ? litresPerHour : 0f);
            _consumptionShown += (consumption - _consumptionShown) * Math.Min(1f, dt * 1.5f);

            // What the needles show. Without electricity every needle rests at its start.
            float e = electricity ? 1f : 0f;
            Damp(EtsCabSignal.Speed, speedKmh * e, dt, 8f);
            Damp(EtsCabSignal.Rpm, (engineRunning ? rpm : 0f), dt, 10f);
            Damp(EtsCabSignal.WaterTemp, electricity ? WaterC : 0f, dt, 1.5f);
            Damp(EtsCabSignal.OilTemp, electricity ? OilC : 0f, dt, 1.5f);
            Damp(EtsCabSignal.Fuel, FuelShare * e, dt, 1.2f);
            Damp(EtsCabSignal.AdBlue, AdBlueShare * e, dt, 1.2f);
            Damp(EtsCabSignal.AirPressure, AirBar * e, dt, 2.5f);
            Damp(EtsCabSignal.BrakePressure, (brake * 8f + (parkingBrake ? 0f : 0f)) * e, dt, 6f);
            Damp(EtsCabSignal.OilPressure, oilBar, dt, 3f);
            Damp(EtsCabSignal.Battery, volts, dt, 2f);
            Damp(EtsCabSignal.Consumption, _consumptionShown * e, dt, 2f);
            Damp(EtsCabSignal.Turbo, boost, dt, 4f);
            Damp(EtsCabSignal.ExhaustTemp, engineRunning ? 180f + 380f * load : 20f, dt, 0.3f);

            bool left = indicatorLit && (indicator < 0 || indicator == 2);
            bool right = indicatorLit && (indicator == 1 || indicator == 2);
            bool lampTest = electricity && _starting > 0f;
            Set(EtsCabSignal.LeftBlinkerLamp, electricity && left);
            Set(EtsCabSignal.RightBlinkerLamp, electricity && right);
            Set(EtsCabSignal.HazardLamp, electricity && indicator == 2 && indicatorLit);
            Set(EtsCabSignal.LowBeamLamp, electricity && Lights >= 2);
            Set(EtsCabSignal.HighBeamLamp, electricity && HighBeam);
            Set(EtsCabSignal.PositionLightsLamp, electricity && Lights >= 1);
            Set(EtsCabSignal.ParkingBrakeLamp, electricity && (parkingBrake || lampTest));
            Set(EtsCabSignal.LowFuelLamp, electricity && (FuelShare < 0.12f || lampTest));
            Set(EtsCabSignal.LowAdBlueLamp, electricity && (AdBlueShare < 0.1f || lampTest));
            Set(EtsCabSignal.LowAirLamp, electricity && (AirBar < 5.2f || lampTest));
            Set(EtsCabSignal.LowOilLamp, electricity && (!engineRunning || oilBar < 0.8f || lampTest));
            Set(EtsCabSignal.RetarderLamp, false);
            Set(EtsCabSignal.BatteryLamp, electricity && (!engineRunning || lampTest));
            Set(EtsCabSignal.WaterTempLamp, electricity && (WaterC > 105f || lampTest));
            Values[(int)EtsCabSignal.OffLamp] = 0f;

            Values[(int)EtsCabSignal.BlinkerStalk] = indicator < 0 ? 0f : indicator == 1 ? 1f : 0.5f;
            Values[(int)EtsCabSignal.ParkingStick] = parkingBrake ? 1f : 0f;
            Values[(int)EtsCabSignal.LightsSwitch] = Lights <= 0 ? 0f : Lights == 1 ? 0.5f : 1f;
            Values[(int)EtsCabSignal.HighBeamStalk] = HighBeam ? 1f : 0f;
            Values[(int)EtsCabSignal.Ignition] = _starting > 0f ? 1f : engineRunning ? 0.67f : electricity ? 0.33f : 0f;
            Values[(int)EtsCabSignal.Transmission] = gear > 0 ? 1f : gear < 0 ? 0f : 0.5f;
            Values[(int)EtsCabSignal.HazardSwitch] = indicator == 2 ? 1f : 0f;
            Values[(int)EtsCabSignal.Zero] = 0f;
            Values[(int)EtsCabSignal.Half] = 0.5f;
        }

        private void Damp(EtsCabSignal signal, float target, float dt, float rate)
        {
            int i = (int)signal;
            Values[i] += (target - Values[i]) * Math.Min(1f, dt * rate);
        }

        private void Set(EtsCabSignal signal, bool on)
        {
            Values[(int)signal] = on ? 1f : 0f;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
