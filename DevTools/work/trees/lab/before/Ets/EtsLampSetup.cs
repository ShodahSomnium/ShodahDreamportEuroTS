using System;
using System.Collections.Generic;
using System.Globalization;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Which lamp of a vehicle burns where, and in what colour, as the game decides it (2026-10-04, vehicle indicator lamps).
    ///
    /// The game does not light a vehicle's lamps with separate lamp objects. Every lens is a piece of the model with the
    /// "eut2.lamp" effect (1,662 pieces in the demo's 6,647 vehicle models: trucks, trailers, AI cars), and its shader
    /// program decides per texel what glows (/effect/eut2/lamp/eut2.lamp.rfx, "defpost" pass, forward_add / post_add:
    /// /effect/eut2/_shd/e8270e473ec3d07227ce490b07b385fd.glsl.vso + 505acf8833b01726fe5018c637660f14.glsl.fso):
    ///   - the lens's second texture, texture_mask ("Lamp Mask", uv stream texcoord1), is read through
    ///     fract(texcoord1); the WHOLE part of texcoord1.u is a group index (0-5, clamped), and the group picks 4
    ///     colours from lamp_color_array[24] - one per mask channel R, G, B, A;
    ///   - glow = sum over channels of colour[group*4 + c] x pow(mask.c, lamp_contrast) (alpha with contrast x 2.2, the
    ///     sRGB curve the alpha plane of an sRGB texture does not get), times 0.125 + 0.875 nz^2, nz the view-space
    ///     normal's z (full facing the camera, an eighth edge-on); added to the lit lens.
    ///   - The engine fills lamp_color_array from the vehicle's lamp setup: /def/vehicle/truck/&lt;truck&gt;/lamp_setup.sii
    ///     where a truck has one (10 of the demo's trucks), else /def/default_vehicle_lamp_setup.sii (exe strings:
    ///     "Failed to load default lamp color definitions!"). wire_composites[g] is group g; its channel_r/g/b/a_wire
    ///     names the wire that lights that channel (default: group 0 front left = R front_left_turn_signal, G high_beam,
    ///     B low_beam, A front_positional; 1 front right; 2 rear left = R rear_left_turn_signal, G brake, B reverse,
    ///     A rear_positional; 3 rear right; 4 middle = B daytime_running, A side_positional), and color_* gives each
    ///     wire's colour in linear HDR (default turn signals 32, 8.194, 0.025; brake 35, 0.35, 0.025; reverse 11 x3;
    ///     the Scania S/R 2016 front turn signals 48, 12.291, 0.0375). A wire switched off contributes black.
    ///   - Measured on the models (probe ind/Side): group 0 and 2 lie on the vehicle's left (x &lt; 0) in 584 + 527 lamp
    ///     pieces, 1 and 3 on its right in 641 + 524; the rest are accessories hung turned round (rear bumpers and lights
    ///     built facing forward). The side repeaters are front-turn-signal texels of groups 0/1 (Scania f_fender).
    ///
    /// Pure (no engine call): the harness reads it offline. Wire numbers are this world's (Wire*); the game's other wires
    /// (beams, positional, daytime running) are kept as names but this world drives none of them.
    /// </summary>
    public sealed class EtsLampSetup
    {
        public const int WireFrontLeftTurn = 0, WireFrontRightTurn = 1, WireRearLeftTurn = 2, WireRearRightTurn = 3, WireBrake = 4, WireReverse = 5;

        /// <summary>2026-10-14 (headlights): the beams, positional and daytime-running wires (default_vehicle_lamp_setup.sii colours: high 64, low 25, front positional 4, rear 4/0.1/0.004, side 4/0.7/0.004, daytime 25).</summary>
        public const int WireHighBeam = 6, WireLowBeam = 7, WireFrontPositional = 8, WireRearPositional = 9, WireSidePositional = 10, WireDaytime = 11;
        public const int WireCount = 12;

        /// <summary>The game's wire names for this world's wires (vehicle_lamp_wire_composite channel_*_wire values).</summary>
        public static readonly string[] WireNames =
        {
            "front_left_turn_signal", "front_right_turn_signal", "rear_left_turn_signal", "rear_right_turn_signal", "brake", "reverse",
            "high_beam", "low_beam", "front_positional", "rear_positional", "side_positional", "daytime_running"
        };

        /// <summary>The lamp setup attribute that holds each wire's colour.</summary>
        private static readonly string[] ColourKeys =
        {
            "color_front_turn_signal", "color_front_turn_signal", "color_rear_turn_signal", "color_rear_turn_signal", "color_brake", "color_reverse",
            "color_high_beam", "color_low_beam", "color_front_positional", "color_rear_positional", "color_side_positional", "color_daytime_running"
        };

        /// <summary>/def/default_vehicle_lamp_setup.sii (demo 1.61), for when the file cannot be read.</summary>
        private static readonly float[][] DefaultColours =
        {
            new[] { 32f, 8.194f, 0.025f }, new[] { 32f, 8.194f, 0.025f }, new[] { 32f, 8.194f, 0.025f }, new[] { 32f, 8.194f, 0.025f },
            new[] { 35f, 0.35f, 0.025f }, new[] { 11f, 11f, 11f },
            new[] { 64f, 64f, 64f }, new[] { 25f, 25f, 25f }, new[] { 4f, 4f, 4f }, new[] { 4f, 0.1f, 0.004f }, new[] { 4f, 0.7f, 0.004f }, new[] { 25f, 25f, 25f }
        };

        private static readonly string[][] DefaultComposites =
        {
            new[] { "front_left_turn_signal", "high_beam", "low_beam", "front_positional" },
            new[] { "front_right_turn_signal", "high_beam", "low_beam", "front_positional" },
            new[] { "rear_left_turn_signal", "brake", "reverse", "rear_positional" },
            new[] { "rear_right_turn_signal", "brake", "reverse", "rear_positional" },
            new[] { "none", "none", "daytime_running", "side_positional" }
        };

        /// <summary>Groups the shader can address (lamp_color_array[24] / 4).</summary>
        public const int Groups = 6;

        /// <summary>The file it came from ("built-in default" when none could be read).</summary>
        public string Source = "built-in default";

        /// <summary>Linear HDR colour per wire (r, g, b).</summary>
        public readonly float[][] Colours = new float[WireCount][];

        /// <summary>This world's wire for group * 4 + channel, -1 for a wire it does not drive (or none).</summary>
        public readonly int[] WireOf = new int[Groups * 4];

        /// <summary>The game's wire names per group * 4 + channel, as read (for the log).</summary>
        public readonly string[] Named = new string[Groups * 4];

        /// <summary>A short, stable key for caches (the source file).</summary>
        public string Key => Source;

        private EtsLampSetup()
        {
            for (int i = 0; i < WireOf.Length; i++)
            {
                WireOf[i] = -1;
                Named[i] = "none";
            }

            for (int w = 0; w < WireCount; w++)
            {
                Colours[w] = (float[])DefaultColours[w].Clone();
            }

            for (int g = 0; g < DefaultComposites.Length; g++)
            {
                for (int c = 0; c < 4; c++)
                {
                    SetWire(g, c, DefaultComposites[g][c]);
                }
            }
        }

        private void SetWire(int group, int channel, string name)
        {
            Named[group * 4 + channel] = string.IsNullOrEmpty(name) ? "none" : name;
            WireOf[group * 4 + channel] = Array.IndexOf(WireNames, name);
        }

        private static readonly Dictionary<string, EtsLampSetup> Cache = new Dictionary<string, EtsLampSetup>(StringComparer.Ordinal);
        private static readonly object CacheLock = new object();

        /// <summary>
        /// The lamp setup the game gives a truck (brand.model, e.g. "scania.s_2016"; null or unknown for trailers and AI
        /// vehicles, which use the default). `read` returns a parsed file or null (EtsFileSystem.ReadSii). Never null.
        /// </summary>
        public static EtsLampSetup For(string truckId, Func<string, EtsSiiFile> read)
        {
            string own = string.IsNullOrEmpty(truckId) ? null : "/def/vehicle/truck/" + truckId + "/lamp_setup.sii";
            string key = own ?? "default";

            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out EtsLampSetup cached))
                {
                    return cached;
                }
            }

            EtsLampSetup setup = null;

            if (read != null)
            {
                try
                {
                    if (own != null)
                    {
                        setup = Parse(read(own), own);
                    }

                    if (setup == null)
                    {
                        setup = Parse(read("/def/default_vehicle_lamp_setup.sii"), "/def/default_vehicle_lamp_setup.sii");
                    }
                }
                catch (Exception)
                {
                    setup = null;
                }
            }

            if (setup == null)
            {
                setup = new EtsLampSetup();
            }

            lock (CacheLock)
            {
                Cache[key] = setup;
            }

            return setup;
        }

        /// <summary>A vehicle_lamp_setup file read the game's way; null when it holds none.</summary>
        public static EtsLampSetup Parse(EtsSiiFile file, string path)
        {
            if (file == null)
            {
                return null;
            }

            EtsSiiUnit unit = null;

            foreach (EtsSiiUnit u in file.OfType("vehicle_lamp_setup"))
            {
                unit = u;
                break;
            }

            if (unit == null)
            {
                return null;
            }

            var setup = new EtsLampSetup { Source = path };

            for (int w = 0; w < WireCount; w++)
            {
                float[] c = unit.GetFloats(ColourKeys[w]);

                if (c.Length >= 3)
                {
                    setup.Colours[w] = new[] { c[0], c[1], c[2] };
                }
            }

            List<string> composites = unit.GetArray("wire_composites");

            if (composites.Count > 0)
            {
                for (int i = 0; i < setup.WireOf.Length; i++)
                {
                    setup.WireOf[i] = -1;
                    setup.Named[i] = "none";
                }

                string[] channelKeys = { "channel_r_wire", "channel_g_wire", "channel_b_wire", "channel_a_wire" };

                for (int g = 0; g < composites.Count && g < Groups; g++)
                {
                    EtsSiiUnit composite = file.Find(composites[g]);

                    if (composite == null)
                    {
                        continue;
                    }

                    for (int c = 0; c < 4; c++)
                    {
                        setup.SetWire(g, c, composite.GetString(channelKeys[c], "none"));
                    }
                }
            }

            return setup;
        }

        /// <summary>"group 0 R front_left_turn_signal ..." for the wires this world drives, for the log.</summary>
        public string Describe()
        {
            var text = new System.Text.StringBuilder();
            const string channels = "RGBA";

            for (int i = 0; i < WireOf.Length; i++)
            {
                if (WireOf[i] >= 0)
                {
                    text.Append(text.Length > 0 ? ", " : "").Append(i / 4).Append(channels[i % 4]).Append('=').Append(Named[i]);
                }
            }

            CultureInfo inv = CultureInfo.InvariantCulture;
            text.Append("; turn front (").Append(string.Join(", ", Array.ConvertAll(Colours[WireFrontLeftTurn], f => f.ToString("0.###", inv))))
                .Append(") rear (").Append(string.Join(", ", Array.ConvertAll(Colours[WireRearLeftTurn], f => f.ToString("0.###", inv))))
                .Append(") brake (").Append(string.Join(", ", Array.ConvertAll(Colours[WireBrake], f => f.ToString("0.###", inv)))).Append(')');
            return text.ToString();
        }
    }
}
