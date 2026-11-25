using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Which truck a player drives, as it crosses the wire: an index into the sorted preview stems, the
    /// catalogue hash's low 8 bits (a mismatch means the two installs differ) and the paint (owner C; PLAN.md
    /// §3.1, §7.C, C12: the 18 /def/initial_save/preview/*.sui files [gameui §3.2], not [net]'s folder indexes).
    /// </summary>
    public struct EtsTruckIdentity
    {
        /// <summary>PaintRgb for "the paint the preview save itself gives" (its paint_job base_color).</summary>
        public const int SavePaint = -1;

        public int Preview;         // index into EtsTruckCatalog.Trucks (sorted stems); -1 = none
        public int CatalogueHash;   // low 8 bits of EtsTruckCatalog.Hash
        public int PaintRgb;        // 0xRRGGBB; SavePaint (-1) = the save's own colour

        /// <summary>Field-by-field equality: the whole contract.</summary>
        public bool Same(EtsTruckIdentity other)
        {
            return Preview == other.Preview && CatalogueHash == other.CatalogueHash && PaintRgb == other.PaintRgb;
        }
    }

    /// <summary>One of the preview trucks, as the Truck Preference page lists it (owner C; PLAN §3.1, §7.C; [gameui §3.2]).</summary>
    public sealed class EtsPreviewTruck
    {
        /// <summary>
        /// Stem: "daf_xf_euro6". PreviewPath: /def/initial_save/preview/daf_xf_euro6.sui. TruckId: the
        /// definition folder, "daf.xf_euro6". Brand: the brand token the logo template takes as %0
        /// ("daf"; txt.brand.logo.left.bottom, /ui/template/text.sii). BrandLogoMaterial: that logo,
        /// /material/ui/brand_logo_left_bottom/daf.mat, or null when the install has none.
        /// </summary>
        public string Stem, PreviewPath, TruckId, Brand, BrandLogoMaterial;

        /// <summary>The brand as the game writes it ("DAF", "Mercedes-Benz"): data.sii info[0] through the locale.</summary>
        public string BrandName;

        /// <summary>
        /// The eight rows of txt.truck_prefer.description (/ui/template/text.sii:641) in its order: Series,
        /// Chassis, Cabin, Steering, Engine power, Engine torque, Gearbox, Fuel tank. Labels are the locale's
        /// td_info_* words ("Series:"); values are resolved through EtsLocale.
        /// </summary>
        public string[] SpecLabels, SpecValues;      // 8 rows each, through EtsLocale

        /// <summary>
        /// The truck may be offered. True when the harness parse of the whole truck passed (probe 1862:
        /// save, chassis and cabin models, wheels, interior locator, both seats, spec, cab mirrors and the
        /// steering wheel's bone all found) - EtsTruckCatalog.VerifiedStems. A truck that fails is greyed
        /// on the Truck Preference page ("Not yet available").
        /// </summary>
        public bool BuildVerified;

        /// <summary>Seen driving in the client (PLAN U8). Only the DAF XF Euro 6 so far (Player.log 2026-09-22).</summary>
        public bool ClientConfirmed;
    }

    /// <summary>
    /// The preview trucks read from the player's install, sorted by stem, each with its brand.model id, logo
    /// and 8 spec rows, plus a 32-bit hash over the sorted stems (owner C; PLAN.md §3.1, §7.C; probe 1645 and
    /// [gameui §3.2]'s table are the reference, probe 1862 checks against both). Pure: a worker thread may
    /// load it. About 90 small definition reads (five per truck plus a fuel tank default for two).
    /// </summary>
    public sealed class EtsTruckCatalog
    {
        public const string PreviewFolder = "/def/initial_save/preview";

        /// <summary>The locale keys of the eight description rows, in the template's order.</summary>
        public static readonly string[] SpecKeys =
        {
            "td_info_series", "td_info_chassis", "td_info_cab", "td_info_steering",
            "td_info_engine", "td_info_torque", "td_info_trans", "td_info_tank"
        };

        /// <summary>
        /// The Steering row's value. Every preview truck is left-hand drive (each save's cabin variant is
        /// the EU one, probe 1861). The locale's word is td_info_steering_left = "Left-hand" (and
        /// td_info_steering_right = "Right-hand"; probes 1897 and 1899c, S/phase1/DX/probe), which settles
        /// Q-D6; this constant is only the fallback when no locale is loaded.
        /// </summary>
        public const string LeftHand = "Left-hand";

        /// <summary>The locale key of the Steering row's value (probe 1899c).</summary>
        public const string LeftHandKey = "td_info_steering_left";

        /// <summary>
        /// The trucks whose whole build the harness could follow (probe 1862, 2026-09-22, ETS2 Demo 1.61.1.0):
        /// the preview save, the chassis model and a cabin model or variant, four or more wheel locators
        /// with a tyre, an interior locator, both seats, the vehicle spec, the steering wheel's bone and at
        /// least two mirror surfaces. BuildVerified is membership here.
        /// </summary>
        public static readonly string[] VerifiedStems =
        {
            "daf_xf", "daf_xf_euro6", "iveco_hiway", "iveco_stralis", "man_tgx", "man_tgx_euro6_4x2",
            "mercedes_actros", "mercedes_actros_2014", "renault_magnum", "renault_premium", "renault_t",
            "scania_r", "scania_r_2016", "scania_r_6x4", "scania_s_2016", "scania_streamline",
            "volvo_fh16", "volvo_fh16_2012"
        };

        /// <summary>Trucks seen driving in the client (PLAN U8): the scene's DAF only.</summary>
        public static readonly string[] ClientConfirmedStems = { "daf_xf_euro6" };

        public static EtsTruckCatalog Current { get; private set; }

        private readonly List<EtsPreviewTruck> _trucks = new List<EtsPreviewTruck>();

        public IReadOnlyList<EtsPreviewTruck> Trucks => _trucks;

        /// <summary>FNV-1a over the sorted stems; its low 8 bits travel in EtsTruckIdentity.CatalogueHash.</summary>
        public int Hash { get; private set; }

        /// <summary>Why a preview could not be read, one line each.</summary>
        public readonly List<string> Notes = new List<string>();

        /// <summary>
        /// Reads every preview save and becomes Current. The locale is loaded first if nobody has
        /// (EtsLocale.Load); without it the rows keep their @@key@@ form. Null when the folder is missing.
        /// </summary>
        public static EtsTruckCatalog Load(EtsFileSystem files)
        {
            if (files == null || !files.TryList(PreviewFolder, out List<string> names, out _))
            {
                return null;
            }

            if (!EtsLocale.Loaded)
            {
                EtsLocale.Load(files);
            }

            var stems = new List<string>();

            foreach (string name in names)
            {
                if (!name.EndsWith(".sui", StringComparison.Ordinal) || name == "vehicle.sui" || name == "locked_screens.sui")
                {
                    continue;
                }

                stems.Add(name.Substring(0, name.Length - 4));
            }

            stems.Sort(StringComparer.Ordinal);
            var catalog = new EtsTruckCatalog { Hash = HashOf(stems) };

            foreach (string stem in stems)
            {
                catalog._trucks.Add(Read(files, stem, catalog.Notes));
            }

            Current = catalog;
            return catalog;
        }

        public int IndexOf(string stem)
        {
            for (int i = 0; i < _trucks.Count; i++)
            {
                if (string.Equals(_trucks[i].Stem, stem, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The identity for a stem and paint (0xRRGGBB, or EtsTruckIdentity.SavePaint); Preview -1 when the stem is not listed.</summary>
        public EtsTruckIdentity Identity(string stem, int paintRgb)
        {
            return new EtsTruckIdentity { Preview = IndexOf(stem), CatalogueHash = Hash & 0xFF, PaintRgb = paintRgb };
        }

        /// <summary>32-bit FNV-1a over each stem followed by a newline, in the order given (sorted, for the catalogue).</summary>
        public static int HashOf(IList<string> stems)
        {
            uint hash = 2166136261u;

            foreach (string stem in stems)
            {
                foreach (char c in stem)
                {
                    hash = (hash ^ c) * 16777619u;
                }

                hash = (hash ^ '\n') * 16777619u;
            }

            return unchecked((int)hash);
        }

        private static EtsPreviewTruck Read(EtsFileSystem files, string stem, List<string> notes)
        {
            string path = PreviewFolder + "/" + stem + ".sui";
            var truck = new EtsPreviewTruck
            {
                Stem = stem,
                PreviewPath = path,
                SpecLabels = new string[SpecKeys.Length],
                SpecValues = new string[SpecKeys.Length],
                BuildVerified = Array.IndexOf(VerifiedStems, stem) >= 0,
                ClientConfirmed = Array.IndexOf(ClientConfirmedStems, stem) >= 0
            };

            for (int i = 0; i < SpecKeys.Length; i++)
            {
                truck.SpecLabels[i] = EtsLocale.Word(SpecKeys[i]);
            }

            EtsSiiFile save = files.ReadSii(path);

            if (save == null)
            {
                notes.Add(stem + ": save unreadable");
                truck.BuildVerified = false;
                return truck;
            }

            // The five definitions the rows come from, by the folder of each accessory's data_path
            // (EtsTruckAssembly.SlotOf: the unit names differ from save to save, the folders do not).
            EtsSiiUnit data = null, chassis = null, cabin = null, engine = null, transmission = null;
            string dataPath = null;

            foreach (EtsSiiUnit unit in save.Units)
            {
                string dp = unit.GetString("data_path", null);

                if (dp == null)
                {
                    continue;
                }

                string slot = EtsTruckAssembly.SlotOf(dp);

                if (slot != "data" && slot != "chassis" && slot != "cabin" && slot != "engine" && slot != "transmission")
                {
                    continue;
                }

                EtsSiiFile d = files.ReadSii(dp);
                EtsSiiUnit def = d != null && d.Units.Count > 0 ? d.Units[0] : null;

                if (def == null)
                {
                    notes.Add(stem + ": missing " + dp);
                    continue;
                }

                if (slot == "data") { data = def; dataPath = dp; }
                else if (slot == "chassis") chassis = def;
                else if (slot == "cabin") cabin = def;
                else if (slot == "engine") engine = def;
                else transmission = def;
            }

            truck.TruckId = EtsTruckAssembly.TruckIdOf(dataPath);
            truck.Brand = truck.TruckId != null && truck.TruckId.IndexOf('.') > 0 ? truck.TruckId.Substring(0, truck.TruckId.IndexOf('.')) : truck.TruckId;

            if (truck.Brand != null)
            {
                string logo = "/material/ui/brand_logo_left_bottom/" + truck.Brand + ".mat";
                truck.BrandLogoMaterial = files.Exists(logo) ? logo : null;
            }

            List<string> info = data?.GetArray("info");
            List<string> engineInfo = engine?.GetArray("info");
            truck.BrandName = info != null && info.Count > 0 ? Text(info[0]) : truck.Brand;
            truck.SpecValues[0] = info != null && info.Count > 1 ? Text(info[1]) : "";
            truck.SpecValues[1] = Text(chassis?.GetString("name", null));
            truck.SpecValues[2] = Text(cabin?.GetString("name", null));
            truck.SpecValues[3] = EtsLocale.TryWord(LeftHandKey, out string leftHand) ? EtsLocale.Resolve(leftHand) : LeftHand;
            truck.SpecValues[4] = engineInfo != null && engineInfo.Count > 0 ? Text(engineInfo[0]) : "";
            truck.SpecValues[5] = engineInfo != null && engineInfo.Count > 2 ? Text(engineInfo[1]) + "/" + Text(engineInfo[2]) :
                                  engineInfo != null && engineInfo.Count > 1 ? Text(engineInfo[1]) : "";
            truck.SpecValues[6] = Text(transmission?.GetString("name", null));
            float tank = TankLitres(files, chassis);
            truck.SpecValues[7] = tank > 0f ? Litres(tank) : "";

            if (truck.TruckId == null || chassis == null || cabin == null || engine == null || transmission == null)
            {
                notes.Add(stem + ": incomplete (data " + (data != null) + ", chassis " + (chassis != null) + ", cabin " + (cabin != null) +
                          ", engine " + (engine != null) + ", transmission " + (transmission != null) + ")");
                truck.BuildVerified = false;
            }

            return truck;
        }

        /// <summary>
        /// The chassis's fuel tank: its tank_size, else the fuel_tank_size of the /accessory/tank/ entry in
        /// its defaults[]. The Scania R and S 2016 chassis give no tank_size; the R defaults to
        /// tank/4x2_1070.sii (1070 l) and the S to tank/4x2_1290.sii (1290 l, "750 + 540 l") - probe 1863.
        /// [gameui §3.2]'s table gives the S 1070 "as R 2016", an assumption the files do not bear out.
        /// 0 when neither.
        /// </summary>
        private static float TankLitres(EtsFileSystem files, EtsSiiUnit chassis)
        {
            if (chassis == null)
            {
                return 0f;
            }

            float size = chassis.GetFloat("tank_size", 0f);

            if (size > 0f)
            {
                return size;
            }

            foreach (string entry in chassis.GetArray("defaults"))
            {
                string path = EtsSiiValue.Unquote(entry);

                if (path == null || path.IndexOf("/accessory/tank/", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                EtsSiiFile d = files.ReadSii(path);
                EtsSiiUnit def = d != null && d.Units.Count > 0 ? d.Units[0] : null;
                float litres = def != null ? def.GetFloat("fuel_tank_size", 0f) : 0f;

                if (litres > 0f)
                {
                    return litres;
                }
            }

            return 0f;
        }

        /// <summary>
        /// "1,400 l". The number is grouped with the locale's digit-grouping character (dg, ","), as the
        /// game groups every number [gameui §2.1]; the " l" unit is not a locale word and how the game
        /// prints this row is not in the files (the %7 is filled by the engine).
        /// </summary>
        private static string Litres(float litres)
        {
            string group = EtsLocale.Loaded ? EtsLocale.Word("dg") : ",";
            int whole = (int)Math.Round(litres);
            string digits = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var text = new System.Text.StringBuilder(digits.Length + 4);

            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0)
                {
                    text.Append(group);
                }

                text.Append(digits[i]);
            }

            return text.Append(" l").ToString(); // " l": not in the locale
        }

        private static string Text(string value)
        {
            if (value == null)
            {
                return "";
            }

            string unquoted = EtsSiiValue.Unquote(value) ?? value;
            return EtsLocale.Resolve(unquoted);
        }
    }

    /// <summary>
    /// Which trailer a truck pulls, as it crosses the wire (owner C; PLAN.md §7.C, [net §2.4] TrailerIdentity);
    /// used by EtsTrailer.CreatePuppet (Phase 3).
    /// </summary>
    public struct EtsTrailerIdentity { public int Family, Variant, Cargo, CompanyHash; }
}
