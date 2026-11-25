using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A complete truck as the game assembles one: a saved vehicle (the showroom preview save,
    /// /def/initial_save/preview/daf_xf_euro6.sui) lists every accessory by its definition path -
    /// chassis, cabin, interior, engine, wheels, paint job and some twenty add-ons (mirrors, door
    /// steps, bumpers, sun shield, intakes, fenders, badges, mud flaps, cables, the steering wheel).
    /// Each add-on definition names an exterior model and sometimes an interior model; the add-on's
    /// slot is the name of the locator it hangs on, on the truck for exterior models and on the
    /// interior for interior ones, matched exactly first and by prefix second (the DAF's rear bumper
    /// locator is "r_bumper_02").
    ///
    /// Pure: this reads the files and nothing else, so the harness runs it over every preview truck
    /// (probe 1862). The models are built and hung by EtsTruckFactory; what one built truck hung is
    /// remembered by its EtsTruckRig.
    ///
    /// THE SLOT IS THE DEFINITION'S FOLDER, not the save unit's name. The DAF XF Euro 6's save names
    /// its units after their slots (".chassis", ".mirror"), and this class used to read the slot from
    /// there. The other 17 preview saves do not (probe 1860): Iveco's are "ivhiway.chassis",
    /// "ivhiway.sounshld" (sic) and "ivhiway.swheel", the MAN TGX Euro 6's are "_nameless.3ff.0ad0".
    /// Read that way, none of their chassis, cabins, interiors, wheels or steering wheels were found,
    /// and the scene's DAF model stood in for all of them. The folder is what every save agrees on:
    /// /def/vehicle/truck/iveco.hiway/accessory/sunshld/shape1.sii is a "sunshld", whatever the unit
    /// is called. On the DAF the two agree for every chassis, cabin, interior and add-on unit (probe
    /// 1860: the only differences are the data, sound and wheel units, which are read by type), so
    /// its truck is read exactly as before (probe 1862 compares every field).
    /// </summary>
    public sealed class EtsTruckAssembly
    {
        public sealed class Addon
        {
            public string Slot;
            public string ExteriorModel;
            public string InteriorModel;
            public string Look = "default";
            public string Variant = "default";
            public string DefinitionPath;
        }

        public string Path;

        /// <summary>The truck's data.sii (/def/vehicle/truck/daf.xf_euro6/data.sii).</summary>
        public string DataPath;

        /// <summary>The truck definition folder's name, brand.model ("daf.xf_euro6"); null when the save has no data unit.</summary>
        public string TruckId;

        /// <summary>The definition files the save chose, by slot: chassis, cabin, interior, engine, transmission, ftire, rtire.</summary>
        public readonly Dictionary<string, string> Parts = new Dictionary<string, string>(StringComparer.Ordinal);

        public string ChassisModel;
        public string ChassisVariant;
        public string ChassisLook;

        /// <summary>
        /// The cabin's own model. Nine of the eighteen preview trucks keep chassis and cabin in one
        /// truck.pmd and pick each by variant (the DAF: chs_4x2 and cabin_a); the other nine have a
        /// cabin model of their own (MAN, Mercedes, Renault T, Scania 2016, Volvo; probe 1861). Both
        /// kinds are modelled in the truck's frame - the man_tgx cabin's driver locator stands at
        /// y 1.47, as the DAF's does in its shared model - so a separate cabin hangs at the truck's
        /// origin, not on the chassis's "cabin" locator (that is the cabin's suspension pivot).
        /// </summary>
        public string CabinModel;
        public string CabinVariant;

        /// <summary>The right-hand-drive twin of the cabin variant (the def's variant_uk), where the passenger seat is found.</summary>
        public string CabinVariantUk;
        public string CabinLook;

        public string InteriorModel;
        public string InteriorLook;
        public string InteriorVariant;
        public string InteriorAnimatedModel;

        /// <summary>The steering column's adjustment animations and where the game parks them, 0 to 1.</summary>
        public string WheelUpDownAnim;
        public float WheelUpDownDefault = 0.5f;
        public string WheelFrontBackAnim;
        public float WheelFrontBackDefault = 0.5f;
        public string FrontTire;
        public string FrontRim;
        public string RearTire;
        public string RearRim;
        public string TireLook = "gestone";

        /// <summary>
        /// Wheel parts hung beside tyre and rim. Seven preview trucks build a wheel from a disc, a hub
        /// and nuts instead of one rim model (f_disc, f_hub, f_nuts under /def/vehicle; probe 1860:
        /// the MAN TGX Euro 6, Renault T, Scania R and S 2016, Streamline and Volvo FH 2012). The disc
        /// stands in the rim's place; hub and nuts are listed here.
        /// </summary>
        public readonly List<string> FrontWheelExtras = new List<string>();
        public readonly List<string> RearWheelExtras = new List<string>();

        public float[] PaintBaseColor;
        public readonly List<Addon> Addons = new List<Addon>();
        public readonly List<string> Notes = new List<string>();

        /// <summary>Whether the cabin is a model of its own rather than a variant of the chassis model.</summary>
        public bool SeparateCabin => !string.IsNullOrEmpty(CabinModel) && !string.Equals(Normalise(CabinModel), Normalise(ChassisModel), StringComparison.Ordinal);

        public static EtsTruckAssembly Load(EtsFileSystem files, string savePath, out string failure)
        {
            return Load(files.ReadSii, savePath, out failure);
        }

        /// <summary>
        /// Load with the definition reader given: EtsFileSystem.ReadSii on the main thread (the overload above), or
        /// <see cref="UncachedReader"/> on a worker thread. ReadSii keeps its parsed files in a Dictionary that the
        /// main thread writes whenever anything reads a definition (the map, the jobs, the menus), so a second
        /// thread must not touch it; the sliced truck build (EtsTruckFactory, PLAN §3.1 C2) reads the save and
        /// the vehicle spec on its worker through the uncached reader instead. Same parse, same result: the
        /// reader only decides whether the parsed file is kept.
        /// </summary>
        public static EtsTruckAssembly Load(Func<string, EtsSiiFile> readSii, string savePath, out string failure)
        {
            failure = null;
            EtsSiiFile save = readSii(savePath);

            if (save == null || save.Units.Count == 0)
            {
                failure = "vehicle save not found: " + savePath;
                return null;
            }

            var assembly = new EtsTruckAssembly { Path = savePath };

            foreach (EtsSiiUnit unit in save.Units)
            {
                string dataPath = unit.GetString("data_path", null);

                if (string.IsNullOrEmpty(dataPath))
                {
                    continue;
                }

                EtsSiiFile definition = readSii(dataPath);
                EtsSiiUnit def = definition != null && definition.Units.Count > 0 ? definition.Units[0] : null;

                if (def == null)
                {
                    assembly.Notes.Add("missing " + dataPath);
                    continue;
                }

                string slot = SlotOf(dataPath);

                switch (unit.Type)
                {
                    case "vehicle_accessory":
                        if (slot == "data")
                        {
                            assembly.DataPath = dataPath;
                            assembly.TruckId = TruckIdOf(dataPath);
                        }
                        else if (slot == "chassis")
                        {
                            // detail_model on most trucks; the DAF XF 105, both Actros and the Renault T
                            // name theirs "model" (probe 1861).
                            assembly.Parts["chassis"] = dataPath;
                            assembly.ChassisModel = def.GetString("detail_model", null) ?? def.GetString("model", null);
                            assembly.ChassisVariant = Token(def.GetString("variant", null));
                            assembly.ChassisLook = Token(def.GetString("look", null));
                        }
                        else if (slot == "cabin")
                        {
                            assembly.Parts["cabin"] = dataPath;
                            assembly.CabinModel = def.GetString("detail_model", null) ?? def.GetString("model", null);
                            assembly.CabinVariant = Token(def.GetString("variant", null));
                            assembly.CabinVariantUk = Token(def.GetString("variant_uk", null));
                            assembly.CabinLook = Token(def.GetString("look", null));
                        }
                        else if (slot == "interior")
                        {
                            assembly.Parts["interior"] = dataPath;
                            assembly.InteriorModel = def.GetString("model", null);
                            assembly.InteriorLook = Token(def.GetString("look", "look1"));
                            assembly.InteriorVariant = Token(def.GetString("variant", "standard"));
                            assembly.InteriorAnimatedModel = def.GetString("animated_model", null);
                            assembly.WheelUpDownAnim = def.GetString("wheel_updown_anim", null);
                            assembly.WheelUpDownDefault = def.GetFloat("wheel_updown_anim_default", 0.5f);
                            assembly.WheelFrontBackAnim = def.GetString("wheel_frontback_anim", null);
                            assembly.WheelFrontBackDefault = def.GetFloat("wheel_frontback_anim_default", 0.5f);
                        }
                        else if (slot == "engine" || slot == "transmission")
                        {
                            assembly.Parts[slot] = dataPath;
                        }
                        break;

                    case "vehicle_wheel_accessory":
                        string wheelModel = def.GetString("model", null);

                        if (slot == "f_tire") { assembly.Parts["ftire"] = dataPath; assembly.FrontTire = wheelModel; assembly.TireLook = def.GetString("look", assembly.TireLook); }
                        else if (slot == "f_rim") assembly.FrontRim = wheelModel;
                        else if (slot == "r_tire") { assembly.Parts["rtire"] = dataPath; assembly.RearTire = wheelModel; }
                        else if (slot == "r_rim") assembly.RearRim = wheelModel;
                        else if (slot == "f_disc") { if (assembly.FrontRim == null) assembly.FrontRim = wheelModel; }
                        else if (slot == "r_disc") { if (assembly.RearRim == null) assembly.RearRim = wheelModel; }
                        else if ((slot == "f_hub" || slot == "f_nuts") && !string.IsNullOrEmpty(wheelModel)) assembly.FrontWheelExtras.Add(wheelModel);
                        else if ((slot == "r_hub" || slot == "r_nuts") && !string.IsNullOrEmpty(wheelModel)) assembly.RearWheelExtras.Add(wheelModel);
                        break;

                    case "vehicle_paint_job_accessory":
                        float[] chosen = unit.GetFloats("base_color");
                        assembly.PaintBaseColor = chosen.Length >= 3 ? chosen : def.GetFloats("base_color");
                        break;

                    default:
                        string exterior = def.GetString("exterior_model", null);
                        string interior = def.GetString("interior_model", null);

                        if (!string.IsNullOrEmpty(exterior) || !string.IsNullOrEmpty(interior))
                        {
                            assembly.Addons.Add(new Addon
                            {
                                Slot = slot,
                                ExteriorModel = exterior,
                                InteriorModel = interior,
                                Look = def.GetString("look", "default"),
                                Variant = def.GetString("variant", "default"),
                                DefinitionPath = dataPath
                            });
                        }
                        break;
                }
            }

            AddDefaults(assembly, readSii);
            return assembly;
        }

        /// <summary>
        /// The parts an add-on brings with it (2026-10-04, vehicle lamps): an accessory definition's defaults[] names parts the
        /// game fits with it when the save does not name that slot itself. The Scania R and S 2016 preview saves name no
        /// r_light and no f_fendr_chs: their rear bumper (r_bumper/stock_blk.sii) brings r_light/stock.sii - the truck's
        /// whole rear lamp cluster, hung on the chassis model's r_light locator (chassis/chs_4x2.pmd, turned 180 degrees) -
        /// and the front fender brings f_fendr_chs. Without them the Scania had no rear lamps at all (probe ind/Def over
        /// all 18 preview saves: only these two trucks lack defaults). Only slots no add-on of the save fills are added.
        /// </summary>
        private static void AddDefaults(EtsTruckAssembly assembly, Func<string, EtsSiiFile> readSii)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);

            foreach (Addon addon in assembly.Addons)
            {
                present.Add(addon.Slot);
            }

            foreach (string part in assembly.Parts.Keys)
            {
                present.Add(part);
            }

            int count = assembly.Addons.Count;

            for (int i = 0; i < count; i++)
            {
                EtsSiiFile definition = readSii(assembly.Addons[i].DefinitionPath);
                EtsSiiUnit def = definition != null && definition.Units.Count > 0 ? definition.Units[0] : null;

                if (def == null)
                {
                    continue;
                }

                foreach (string path in def.GetArray("defaults"))
                {
                    string slot = SlotOf(path);

                    if (string.IsNullOrEmpty(slot) || present.Contains(slot))
                    {
                        continue;
                    }

                    EtsSiiFile brought = readSii(path);
                    EtsSiiUnit unit = brought != null && brought.Units.Count > 0 ? brought.Units[0] : null;
                    string exterior = unit != null ? unit.GetString("exterior_model", null) : null;
                    string interior = unit != null ? unit.GetString("interior_model", null) : null;

                    if (string.IsNullOrEmpty(exterior) && string.IsNullOrEmpty(interior))
                    {
                        continue;
                    }

                    present.Add(slot);
                    assembly.Addons.Add(new Addon
                    {
                        Slot = slot,
                        ExteriorModel = exterior,
                        InteriorModel = interior,
                        Look = unit.GetString("look", "default"),
                        Variant = unit.GetString("variant", "default"),
                        DefinitionPath = path
                    });
                    assembly.Notes.Add("default " + slot + " from " + assembly.Addons[i].Slot + ": " + path);
                }
            }
        }

        /// <summary>
        /// EtsFileSystem.ReadSii without its cache: the same normalised path ("/" + EtsHashFs.Normalize), the same
        /// EtsSii.Parse with includes read through the file system, null for a missing file. Safe on a worker
        /// thread: EtsFileSystem.ReadText is (the archives' entry tables are written only while mounting and every
        /// stream read is under EtsHashFs's lock; EtsWorker's notes), and EtsSii.Parse has no shared state but its
        /// read-only DecodeEscapes switch.
        /// </summary>
        public static Func<string, EtsSiiFile> UncachedReader(EtsFileSystem files)
        {
            return path =>
            {
                string p = "/" + EtsHashFs.Normalize(path);
                string text = files.ReadText(p);
                return text == null ? null : EtsSii.Parse(text, p, files.ReadText);
            };
        }

        /// <summary>
        /// An accessory's slot from its definition path: the folder the file sits in
        /// (/def/vehicle/truck/daf.xf_euro6/accessory/mirror/shape1.sii is "mirror",
        /// /def/vehicle/f_tire/1.sii is "f_tire"), or "data" for the truck's own data.sii.
        /// </summary>
        public static string SlotOf(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath))
            {
                return "";
            }

            int slash = dataPath.LastIndexOf('/');

            if (slash < 0)
            {
                return "";
            }

            if (string.Equals(dataPath.Substring(slash + 1), "data.sii", StringComparison.OrdinalIgnoreCase))
            {
                return "data";
            }

            // A file at the root ("/shape1.sii") has no folder. Without this the search below finds the
            // same leading slash and Substring gets a length of -1 (checker probe 1870 threw on it).
            if (slash == 0)
            {
                return "";
            }

            int before = dataPath.LastIndexOf('/', slash - 1);
            return before < 0 ? "" : dataPath.Substring(before + 1, slash - before - 1);
        }

        /// <summary>"daf.xf_euro6" from /def/vehicle/truck/daf.xf_euro6/data.sii.</summary>
        public static string TruckIdOf(string dataPath)
        {
            const string prefix = "/def/vehicle/truck/";

            if (string.IsNullOrEmpty(dataPath) || !dataPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            int end = dataPath.IndexOf('/', prefix.Length);
            return end < 0 ? null : dataPath.Substring(prefix.Length, end - prefix.Length);
        }

        /// <summary>
        /// A variant or look name as a token. SCS tokens are lower case and the files are not always:
        /// the Scania 2016 cabins say "variant: Default" of a model whose variants are "default" and
        /// "uk" (probe 1861), and the model's list is matched exactly. Every DAF name is lower case already.
        /// </summary>
        public static string Token(string value)
        {
            return value?.Trim().ToLowerInvariant();
        }

        private static string Normalise(string path)
        {
            return path == null ? null : "/" + EtsHashFs.Normalize(path);
        }

        /// <summary>The save's paint colour as the file holds it, linear.</summary>
        public bool TryGetPaintLinear(out float r, out float g, out float b)
        {
            r = g = b = 1f;

            if (PaintBaseColor == null || PaintBaseColor.Length < 3)
            {
                return false;
            }

            r = PaintBaseColor[0];
            g = PaintBaseColor[1];
            b = PaintBaseColor[2];
            return true;
        }

        // ------------------------------------------------------------------ locators and seats

        /// <summary>
        /// The variant indices a model is built with, exactly as EtsModelBuilder.Build chooses them:
        /// the named variants the model has, else its first variant, else none (a model with no
        /// variants shows every part).
        /// </summary>
        public static List<int> VariantIndices(EtsModelData model, IEnumerable<string> variants)
        {
            var indices = new List<int>();

            if (variants != null)
            {
                foreach (string variant in variants)
                {
                    if (variant == null) continue;
                    int index = model.VariantIndex(variant.Trim());
                    if (index >= 0) indices.Add(index);
                }
            }

            if (indices.Count == 0 && model.Variants.Length > 0)
            {
                indices.Add(0);
            }

            return indices;
        }

        /// <summary>Whether EtsModelBuilder.Build shows a part (and makes its locators) for these variant indices; "coll" never.</summary>
        public static bool PartShown(EtsModelPart part, List<int> variantIndices)
        {
            if (part.Name == "coll")
            {
                return false;
            }

            bool visible = variantIndices.Count == 0;

            foreach (int variant in variantIndices)
            {
                visible |= part.IsVisible(variant);
            }

            return visible;
        }

        /// <summary>
        /// The locators EtsModelBuilder.Build makes for these variants, in the order it makes them,
        /// first name wins - the pure twin of EtsModelBuilder.Result.Locators.
        /// </summary>
        public static List<EtsModelLocator> ShownLocators(EtsModelData model, IEnumerable<string> variants)
        {
            var result = new List<EtsModelLocator>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            List<int> indices = VariantIndices(model, variants);

            foreach (EtsModelPart part in model.Parts)
            {
                if (!PartShown(part, indices)) continue;

                for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < model.Locators.Length; l++)
                {
                    EtsModelLocator locator = model.Locators[l];

                    if (locator.Name != null && seen.Add(locator.Name))
                    {
                        result.Add(locator);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The locator the interior model hangs on, from the names of the locators the truck shows in
        /// their build order. The interior's origin is the driver's eye and belongs on the cabin's own
        /// interior locator: interior2 on the DAF XF Euro 6, "interior" on most, interior_0 on the
        /// Magnum, interior_r and interior_s on the Scania 2016 cabins (probe 1861). The configured
        /// names come first, as they always have, but only those that are interior locators; then any
        /// interior* the truck shows; only then the rest of the configured list ("cabin"). The order
        /// matters on a separate cabin: its chassis has a "cabin" locator too - the cabin pivot, 1.4 m
        /// below the eye on the MAN TGX - which the old order took before the cabin's "interior". On
        /// the DAF interior2 comes first either way. Null when none is found.
        /// </summary>
        public static string ChooseInteriorLocator(IList<string> shownNames, string configured)
        {
            var present = new HashSet<string>(shownNames, StringComparer.Ordinal);
            var configuredNames = new List<string>((configured ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            configuredNames.AddRange(new[] { "interior2", "interior1", "cabin" });

            foreach (string name in configuredNames)
            {
                if (name.StartsWith("interior", StringComparison.Ordinal) && present.Contains(name)) return name;
            }

            foreach (string name in shownNames)
            {
                if (name.StartsWith("interior", StringComparison.Ordinal)) return name;
            }

            foreach (string name in configuredNames)
            {
                if (present.Contains(name)) return name;
            }

            return null;
        }
    }

    /// <summary>
    /// Where a truck's two seats are, from its own model ([elev §5], probes 1622 and 1861). The game has
    /// no seat definition (probe 1623); a cabin carries a "driver*" locator on each side, the left one
    /// in the parts its left-hand-drive variant shows and the right one in its right-hand-drive twin's
    /// parts (the DAF XF Euro 6: driver_0 in cab_eu, driver_1 in cab_uk). The left one is the driver's
    /// seat - every preview truck is left-hand drive - and the right one, the seat a British driver
    /// sits in, is the passenger's. Names vary (driver_0, driver, driver1, two called "driver" on the
    /// Scania 2016 and the Volvo FH), so the name is only a prefix and the side is the sign of x.
    /// </summary>
    public static class EtsTruckSeats
    {
        /// <summary>
        /// Finds both seats in the cabin model's frame (the game's: +x right, -z forward). The driver:
        /// the first driver* locator with x &lt; 0 in the parts the left-hand-drive variant shows; else
        /// in any part. The passenger: of the driver* locators with x &gt; 0 - in the right-hand-drive
        /// variant's parts first, else in any part - the one nearest the driver's seat mirrored in x;
        /// with none, the driver's seat mirrored. The Iveco Stralis shows why the variant matters: its
        /// chassis parts carry two more driver* locators for other cabins (driver_b, driver_c, 10 cm
        /// lower) that the a_cabin variant does not show. False when the model has no driver* locator.
        /// </summary>
        public static bool Find(EtsModelData cabin, string lhdVariant, string rhdVariant, out float[] driver, out float[] passenger, out string how)
        {
            driver = null;
            passenger = null;
            how = "no driver* locator";

            if (cabin == null)
            {
                return false;
            }

            List<int> lhd = EtsTruckAssembly.VariantIndices(cabin, new[] { lhdVariant });
            List<int> rhd = string.IsNullOrEmpty(rhdVariant) || cabin.VariantIndex(rhdVariant) < 0 ? null : new List<int> { cabin.VariantIndex(rhdVariant) };
            EtsModelLocator left = Pick(cabin, lhd, -1, null) ?? Pick(cabin, null, -1, null);
            EtsModelLocator right;

            if (left == null)
            {
                right = (rhd != null ? Pick(cabin, rhd, +1, null) : null) ?? Pick(cabin, null, +1, null);

                if (right == null)
                {
                    return false;
                }

                passenger = (float[])right.Position.Clone();
                driver = new[] { -right.Position[0], right.Position[1], right.Position[2] };
                how = "passenger " + right.Name + ", driver mirrored from it";
                return true;
            }

            driver = (float[])left.Position.Clone();
            float[] mirrored = { -driver[0], driver[1], driver[2] };
            right = (rhd != null ? Pick(cabin, rhd, +1, mirrored) : null) ?? Pick(cabin, null, +1, mirrored);

            if (right == null)
            {
                passenger = mirrored;
                how = "driver " + left.Name + ", passenger mirrored from it";
                return true;
            }

            passenger = (float[])right.Position.Clone();
            how = "driver " + left.Name + ", passenger " + right.Name;
            return true;
        }

        /// <summary>A driver* locator on one side (sign of x), in the shown parts or (null) in any part; nearest to a point when given, else the first.</summary>
        private static EtsModelLocator Pick(EtsModelData model, List<int> variants, int side, float[] near)
        {
            EtsModelLocator best = null;
            float bestDistance = float.MaxValue;

            foreach (EtsModelPart part in model.Parts)
            {
                if (variants != null && !EtsTruckAssembly.PartShown(part, variants)) continue;

                for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < model.Locators.Length; l++)
                {
                    EtsModelLocator locator = model.Locators[l];

                    if (locator.Name == null || !locator.Name.StartsWith("driver", StringComparison.Ordinal) || Math.Sign(locator.Position[0]) != side)
                    {
                        continue;
                    }

                    if (near == null)
                    {
                        return locator;
                    }

                    float dx = locator.Position[0] - near[0], dy = locator.Position[1] - near[1], dz = locator.Position[2] - near[2];
                    float d = dx * dx + dy * dy + dz * dz;

                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = locator;
                    }
                }
            }

            return best;
        }
    }
}
