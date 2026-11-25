using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Builds a truck (owner C; PLAN.md §3.1, §7.C): EtsModelViewer.LoadRoutine and the truck half of
    /// SetUpDriving, moved here without the wiring, as a coroutine sliced over frames.
    ///
    /// Three ways in:
    /// - <see cref="Build(Recipe, Transform, bool, Action{EtsTruckRig}, Action{string})"/>: what to
    ///   build spelled out. EtsModelViewer fills a Recipe from its scene fields and drains this
    ///   enumerator inside its own LoadRoutine, so today's truck is built by the same statements in
    ///   the same order, yields in the same frames and writes the same log lines to the same list it
    ///   always reported (the DAF's "Built 28 pieces (5 skipped), 45315 vertices ..." line).
    /// - <see cref="Build(EtsTruckIdentity, Transform, bool, Action{EtsTruckRig}, Action{string})"/>
    ///   (§7.C): a preview truck from EtsTruckCatalog, for the new flow's own trucks and puppets.
    /// - <see cref="BuildDisplay"/>: a preview truck to look at (the Truck Preference studio): no
    ///   controller, no mirrors, no probe, no door.
    ///
    /// TWO PACES (Recipe.FrameBudgetMs). 0, the scene recipe: the old four slices (read the model; build
    /// the exterior; the wheels; the interior and the driving set-up), 53/380/39/143 ms cold for the DAF
    /// (checker probe 1871) - acceptable only while the whole world is still loading behind the old front
    /// end. More than 0, every preview build (PLAN §8 C2: a truck is spawned from the tablet while the
    /// player stands in the world, and in VR at 90 Hz a frame has 11 ms in all): the build is cut into
    /// steps of one item each and yields whenever this frame has spent the budget. What cannot be split
    /// goes to a worker thread (EtsWorker, the traffic's own pattern, EtsTrafficModels):
    ///   1. the preview save and the vehicle spec, read and parsed on the worker with an uncached reader
    ///      (EtsTruckAssembly.UncachedReader: ReadSii's cache is a Dictionary the main thread writes).
    ///      Measured on the frame (probe 2030, mono, cold): 1.2-6.8 ms and 0.9-10.7 ms per truck;
    ///   2. every model file the truck will show (chassis, cabin, wheels, interior, dashboard, add-ons:
    ///      18-47 files a truck) read and parsed on the worker, adopted into EtsEngine's cache on the frame
    ///      (a dictionary insert). On the frame they cost 44-226 ms a truck, up to 50.6 ms for one file
    ///      (scania_rcab_2009/truck.pmd; median 1.1 ms, 140 of 544 over 2 ms);
    ///   3. every texture its materials will load, read and decoded on the worker (EtsTextureCache.Prepare)
    ///      and uploaded on the frame one at a time (EtsTextureCache.Adopt) - the traffic's AskTextures rule,
    ///      which mirrors EtsMaterialCache.Build;
    ///   4. every material built on the frame, one a step; every mesh, one piece a step - both into the
    ///      caches EtsModelBuilder.Build reads (EtsMaterialCache.Load; EtsModelBuilder.BuildMesh /
    ///      BuildTexgenMesh with the name and uv choice Build itself passes), so the builds that follow only
    ///      make GameObjects;
    ///   5. the objects: the chassis, the cabin, one add-on, one wheel, the interior, the dashboard, one
    ///      interior add-on, the driving set-up - each a step, under an inactive root that is switched on in
    ///      one step at the end, so a half-built truck is never drawn.
    /// The same statements build the truck in both paces; the sliced pace only adds steps 1-4 in front and
    /// yields between items, so the objects, names, meshes and materials come out the same (probe 2031, a
    /// throwaway Unity 6000.3.5f2 project: all 18 previews, sliced against unsliced, equal node for node,
    /// mesh, material, controller, body and wheels; the scene's DAF equal to the Phase 1 build). What one frame
    /// costs is logged at the end ("Sliced: ... worst frame ... ms (step)"): the item that cannot be cut - one
    /// model's objects (EtsModelBuilder.Build makes a model's every part at once), one mesh, one texture upload,
    /// one material - is the floor. Measured by probe 2031 (one MoveNext per 11 ms frame, D3D11 device):
    /// - a truck takes 17-104 frames (up to 1.7 s); 15-220 ms of script on the frame in all (the scene's DAF
    ///   unsliced: 68/184/37/127 ms in four frames - the old 684 ms client frame, cold);
    /// - most frames stay at the 1.5 ms budget (median 0.03-1.6 ms); 2-12 frames of a warm build go over 2 ms,
    ///   up to 30 of a cold one;
    /// - the worst frame of a warm build (the 17 trucks after the first in one process) without a collection in
    ///   it is 2.4-8.7 ms: the largest single mesh, one material definition read, a model's objects, or one
    ///   finished worker job adopted (a texture upload); the first truck in a fresh process 11-15 ms ("chassis objects",
    ///   "driving set-up": first use of Unity's physics and object code and the JIT). Frames with a garbage
    ///   collection reached 12-32 ms in the batch run, which collects in one go; the client's GC is incremental
    ///   (its ProjectSettings: gcIncremental 1).
    /// Compiling the truck code ahead on the worker (RuntimeHelpers.PrepareMethod) was tried and measured: it did
    /// not lower the worst frame (A/B, 2 trucks x 2 runs), so it is not done.
    ///
    /// PUPPETS (Phase 3, C3; forPuppet on the identity overload): no mirrors, no reflection probe, the interior's objects
    /// deferred to the rig (Recipe.LazyInterior; its files and GPU data are still prepared here, so the cab later costs
    /// objects only: 1-3 frames, worst 1.4-3.7 ms, 0 archive reads, probes 2250/2251), an engine sound of its own faded
    /// with distance, the door handle, kinematic (SetPuppet). Across frames a puppet's build hands the global paint back
    /// (Pace.HoldPaint). What a visit keeps (PureCache, ColumnCache, AdoptedMasks, with the engine's model, texture,
    /// material and mesh caches) makes a second truck of the same identity read nothing from the archives: probe 2250,
    /// cold process, one step per 11 ms frame - DAF 363 reads, 65-72 frames, worst 10-15 ms ("driving set-up", the first
    /// truck of the process); Scania 2016 391 reads, 41-61 frames, worst 3.8-4.8 ms; the DAF again 0 reads, 3 frames,
    /// worst 2.0-2.6 ms; two cold trucks built in the same frames, worst 6.3-10.4 ms (one at a time keeps it lower).
    /// A holder destroyed mid-build (a player leaving) ends the build through `failed` (BuildPreview).
    ///
    /// What is new against the viewer, all of it no-op on the DAF (probe 1862 checks each):
    /// - a separate cabin model is built at the truck's origin (nine of the eighteen preview trucks
    ///   have one; the DAF's cabin is a variant of its truck.pmd);
    /// - disc, hub and nuts wheel models hang beside the tyre (seven trucks; the DAF has a rim);
    /// - wheel locators are hung unturned (the MAN TGX's right rear is modelled -90 degrees, the
    ///   Magnum's 180; every DAF wheel locator is unturned already);
    /// - both seats are read by EtsTruckSeats.Find into DriverSeatLocal and PassengerSeatLocal;
    /// - the interior prefers interior* locators over the chassis's "cabin" pivot
    ///   (EtsTruckAssembly.ChooseInteriorLocator; the DAF takes interior2 either way);
    /// - an add-on is filed as exterior or interior by which of its two models is being hung, not by
    ///   comparing paths (Phase 2): 16 of the 18 preview trucks name the SAME file as both, the sun
    ///   shield on 14 of them (probe 2030), and the path test filed the outside copy as an interior one,
    ///   so ShowFromInside(false) hid the sun shield from the chase view. Every one of the 73 pairs on the
    ///   18 trucks has a locator on both sides (probe 2033), so each switches as a pair (probe 2031: 438
    ///   switches right, 0 wrong); the DAF now files 19 exterior and 5 interior add-ons (was 18 and 6).
    /// </summary>
    public static class EtsTruckFactory
    {
        /// <summary>
        /// What to build. EtsModelViewer's fields, one for one (EtsModelViewer.cs, "Model" to "Driving"),
        /// so the scene's truck is described exactly as the scene says; FromPreview fills one for a
        /// preview truck.
        /// </summary>
        public sealed class Recipe
        {
            public string ModelPath;
            public string Look = "default";
            public string Variants = "";
            public bool Wheels = true;
            public string TireModel = DefaultTire;
            public string TireLook = "gestone";
            public string RimModel = DefaultRim;
            public bool Interior = true;
            public string InteriorModel;
            public string InteriorVariant = "standard";
            public string InteriorLook = "look1";
            public string InteriorLocator = "interior2, interior1, cabin";
            public Color Paint = Color.white;
            public bool PaintFromGame;
            public bool Drivable = true;
            public string TruckId;
            public string AssemblyFile;
            public bool UseAssembly = true;

            /// <summary>
            /// Read engine, gearbox, chassis and tyres from the parts the save chose (EtsVehicleSpec.Load
            /// with the assembly) rather than from data.sii's fallback list. The scene's DAF has always
            /// driven on its fallbacks - the MX-11 270 at 1,800 Nm (Player.log 2026-09-22, "Driving:
            /// daf.xf_euro6: ... 1800 Nm") - while its showroom save fits the MX-13 375, 510 hp and
            /// 2,500 Nm ([gameui §3.2]); the Scania R 6x4's save has a three-axle chassis its fallback
            /// has not. On for preview trucks (the new flow): the Truck Preference page shows the save's
            /// rows ("510 hp (375kW)", "2,500 Nm/1,000-1,425 rpm"), and the truck it spawns must be the one
            /// it describes. Probe 2032 ran both DAF engines on the controller's own drive model (unladen tractor,
            /// flat, full throttle, 50 Hz): fallbacks 1,800 Nm / ZF 12TX2420TD 0-50 km/h 8.1 s, 0-80 km/h 14.4 s;
            /// the save's 2,500 Nm / ZF 12AS2331TD R 7.8 s and 12.3 s; 0-30 km/h 5.2 s for both (the light rear
            /// axle's grip limits the low gears, not the engine); both stable (pitch at most 1.2 degrees, the
            /// front axle keeps at least 55 % of the load, no wheel ever leaves the ground). So the new flow keeps
            /// it on: the truck is the one its page describes, and it drives no less steadily. Whether the feel is
            /// right is for the first client drive (PLAN §12). Neither engine is speed-limited by the model (top
            /// 151 and 161 km/h there).
            /// Off for the scene's truck, which keeps today's feel on the old flow.
            /// </summary>
            public bool SpecFromSave;

            public bool Reflections = true;
            public float ReflectionInterval = 2f;

            /// <summary>Give the truck its door handle through EtsTruckDoor.Create(rig). The scene's truck gets its handle from the viewer's wiring instead.</summary>
            public bool Door;

            /// <summary>
            /// Build the interior's objects later, when EtsTruckRig.SetInteriorVisible(true) first asks (C3; [net §2.6]:
            /// "the interior only when the local player boards it or comes within InteriorRadius = 25 m"). Its files,
            /// textures, materials and meshes are still prepared with the truck (sliced, on the worker), so the later
            /// build only makes objects; its mount (the eye) is chosen now. Set for puppets by the identity overload.
            /// The interior is 45-60 % of a truck's vertices (probe 2030) and most of its objects (the cab add-ons and
            /// the dashboard): a puppet nobody is near draws none of it, and appears in fewer frames.
            /// </summary>
            public bool LazyInterior;

            /// <summary>
            /// Script time a frame may spend, in milliseconds; 0 keeps the old four slices (the scene's truck
            /// on the old flow). See the class notes. FromPreview sets <see cref="SlicedFrameBudgetMs"/>.
            /// </summary>
            public float FrameBudgetMs;

            public EtsTruckIdentity Identity = new EtsTruckIdentity { Preview = -1 };
            public string Stem;

            /// <summary>The lines written as the build goes; the viewer reports this list.</summary>
            public List<string> Log = new List<string>();

            /// <summary>Called where the viewer always reported (its status line and the log); null for silence.</summary>
            public Action Report;
        }

        /// <summary>The generic wheels a truck gets when its save names none (the scene's defaults).</summary>
        public const string DefaultTire = "/vehicle/truck/upgrade/wheel/tire/front_1.pmd";
        public const string DefaultRim = "/vehicle/truck/upgrade/wheel/rim/front_1.pmd";

        /// <summary>The scene's reflection interval (Euro Truck Scene.unity, Ets Model Viewer: _reflectionInterval 1).</summary>
        public const float PreviewReflectionInterval = 1f;

        /// <summary>
        /// A sliced build's budget: the traffic's own (EtsTraffic.FrameBudgetMs 1.5), under the ~2 ms a world
        /// component may take of a 90 Hz VR frame (PLAN, code rules). A step that alone takes longer (one texture
        /// upload, one material) still runs whole; the build log names the worst.
        /// </summary>
        public const float SlicedFrameBudgetMs = 1.5f;

        /// <summary>A preview truck's recipe: everything from its save; nothing of the scene's DAF.</summary>
        public static Recipe FromPreview(EtsPreviewTruck truck, EtsTruckIdentity id)
        {
            var recipe = new Recipe
            {
                AssemblyFile = truck.PreviewPath,
                TruckId = truck.TruckId,
                UseAssembly = true,
                SpecFromSave = true,
                ReflectionInterval = PreviewReflectionInterval,
                Door = true,
                FrameBudgetMs = SlicedFrameBudgetMs,
                Identity = id,
                Stem = truck.Stem,
                PaintFromGame = id.PaintRgb < 0
            };

            if (id.PaintRgb >= 0)
            {
                recipe.Paint = new Color(((id.PaintRgb >> 16) & 0xFF) / 255f, ((id.PaintRgb >> 8) & 0xFF) / 255f, (id.PaintRgb & 0xFF) / 255f, 1f);
            }

            return recipe;
        }

        /// <summary>
        /// A preview truck by identity (PLAN §7.C), sliced (FromPreview). Needs the archives mounted (EtsEngine)
        /// and the catalogue loaded (EtsTruckCatalog.Current, loaded here when missing - 21 ms plus the locale's
        /// 35 ms on the frame, probe 1862; the Truck Preference page has always loaded it first). Fails, through
        /// `failed`, on an index out of range, a catalogue hash that is not this install's (the index would name
        /// another truck), a truck the harness could not verify (EtsPreviewTruck.BuildVerified), a model that
        /// cannot be read, and a truck left without an EtsTruckController (no vehicle spec): `done` always gets
        /// a drivable rig. The rig's root is `parent`'s child at local identity; the caller places it
        /// (EtsTruckRig.PlaceWhenGroundExists does it the way the viewer always has).
        /// </summary>
        public static IEnumerator Build(EtsTruckIdentity id, Transform parent, bool forPuppet,
                                        Action<EtsTruckRig> done, Action<string> failed)
        {
            return BuildPreview(id, parent, forPuppet, false, true, done, failed);
        }

        /// <summary>
        /// A preview truck to be looked at, not driven: the Truck Preference studio (D, [gameui §3.2]). Sliced like
        /// every preview build, from the same save (paint included), but with no EtsTruckController, no mirrors, no
        /// reflection probe and no door handle - nothing that renders on its own, simulates or can be clicked into.
        /// `interior` false leaves the cab empty (the studio's camera is outside; the interior is 45-60 % of a
        /// truck's vertices, probe 2030). IsPuppet is true on the rig, so nothing treats it as the local truck.
        /// </summary>
        public static IEnumerator BuildDisplay(EtsTruckIdentity id, Transform parent, bool interior,
                                               Action<EtsTruckRig> done, Action<string> failed)
        {
            return BuildPreview(id, parent, true, true, interior, done, failed);
        }

        private static IEnumerator BuildPreview(EtsTruckIdentity id, Transform parent, bool forPuppet, bool display, bool interior,
                                                Action<EtsTruckRig> done, Action<string> failed)
        {
            if (!EtsEngine.Mount(null, out string mountFailure))
            {
                failed?.Invoke(mountFailure);
                yield break;
            }

            EtsTruckCatalog catalog = EtsTruckCatalog.Current;

            if (catalog == null)
            {
                catalog = EtsTruckCatalog.Load(EtsEngine.Files);

                // Its 21-56 ms are this frame's; the build starts on the next.
                yield return null;
            }

            if (catalog == null || id.Preview < 0 || id.Preview >= catalog.Trucks.Count)
            {
                failed?.Invoke("No preview truck " + id.Preview + " (the catalogue lists " + (catalog != null ? catalog.Trucks.Count : 0) + ").");
                yield break;
            }

            if ((catalog.Hash & 0xFF) != (id.CatalogueHash & 0xFF))
            {
                failed?.Invoke("Truck " + id.Preview + " comes from a catalogue with hash " + (id.CatalogueHash & 0xFF) + "; this install's is " + (catalog.Hash & 0xFF) + ".");
                yield break;
            }

            EtsPreviewTruck truck = catalog.Trucks[id.Preview];

            if (!truck.BuildVerified)
            {
                failed?.Invoke("Truck " + truck.Stem + " is not verified to build (EtsTruckCatalog.VerifiedStems).");
                yield break;
            }

            Recipe recipe = FromPreview(truck, id);

            if (display)
            {
                recipe.Drivable = false;
                recipe.Door = false;
                recipe.Reflections = false;
                recipe.Interior = interior;
            }
            else if (forPuppet)
            {
                recipe.LazyInterior = true;
            }

            EtsTruckRig built = null;
            IEnumerator build = Build(recipe, parent, forPuppet, rig => built = rig, failed);
            bool hadParent = parent != null;

            while (true)
            {
                bool more;

                // The global paint as the world left it before this frame's share (Pace.HoldPaint hands the same value back
                // at every frame end). A step that throws may have set the puppet's paint: this frame's value goes back, not
                // the one from when the build began - the own truck may have finished meanwhile and left its paint there,
                // which its job trailer is built with (C3 checker, probe 2255 B: the build-start value clobbered it).
                Color paintBefore = EtsMaterialCache.PaintColor;

                try
                {
                    more = build.MoveNext();
                }
                catch (Exception e)
                {
                    // A puppet's holder is A's, and a remote player can leave while their truck is still being built: the
                    // holder is destroyed with the half-built truck under it, and the next step throws on a destroyed object
                    // (C2 checker, 2036g). The caller hears `failed` either way, never silence.
                    if (forPuppet)
                    {
                        EtsMaterialCache.PaintColor = paintBefore;
                    }

                    bool gone = hadParent && parent == null;
                    string reason = gone ? "Truck " + truck.Stem + ": its holder was removed during the build." : "Truck " + truck.Stem + ": the build stopped: " + e.GetType().Name + ": " + e.Message;

                    if (!gone)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Truck factory: " + reason + "\n" + e);
                    }

                    failed?.Invoke(reason);
                    yield break;
                }

                if (!more)
                {
                    break;
                }

                yield return build.Current;
            }

            if (built == null)
            {
                // The recipe build has logged and called `failed`.
                yield break;
            }

            if (!display && built.Controller == null)
            {
                // A preview truck is always asked for drivable. Without its vehicle spec (the recipe build
                // logged why) it could be neither driven nor puppeted, and every caller of `done` (A's
                // SpawnOwnTruck, the puppets) reads rig.Controller - so it is a failure here, not a rig.
                // The scene's truck (the recipe overload) keeps today's behaviour: a model with no driving.
                if (Application.isPlaying) UnityEngine.Object.Destroy(built.gameObject);
                else UnityEngine.Object.DestroyImmediate(built.gameObject);
                failed?.Invoke("Truck " + truck.Stem + " (" + truck.TruckId + "): no vehicle spec, so it cannot be driven.");
                yield break;
            }

            done?.Invoke(built);
        }

        // ------------------------------------------------------------------ the build

        /// <summary>What the step lists hand back (iterators have no out parameters).</summary>
        private sealed class Tally
        {
            public int Count;
            public Transform SteeringWheel;
        }

        /// <summary>
        /// Builds from a recipe under `parent` (local identity; the caller places it). `done` gets the rig;
        /// `failed` a reason, after the reason was logged. Callers that must keep their own frame timing
        /// drain it themselves (EtsModelViewer: while (e.MoveNext()) yield return e.Current).
        /// </summary>
        public static IEnumerator Build(Recipe recipe, Transform parent, bool forPuppet,
                                        Action<EtsTruckRig> done, Action<string> failed)
        {
            List<string> log = recipe.Log ?? (recipe.Log = new List<string>());
            var pace = new Pace(recipe.FrameBudgetMs);
            bool sliced = pace.Sliced;
            var clock = Stopwatch.StartNew();
            var total = Stopwatch.StartNew();
            EtsTruckAssembly assembly = null;
            string modelPath = recipe.ModelPath;
            string variants = recipe.Variants ?? "";
            string interiorModel = recipe.InteriorModel, interiorLook = recipe.InteriorLook, interiorVariant = recipe.InteriorVariant;
            string tireModel = recipe.TireModel, rimModel = recipe.RimModel, tireLook = recipe.TireLook;
            Color paintColor = recipe.Paint;
            PureRead pure = null;
            Func<bool> within = pace.Within;
            ForgetIfRemounted();

            if (sliced && forPuppet)
            {
                // Every step that makes a material sets the global paint first, in its own frame; between frames the
                // global is handed back to the rest of the world (the own truck's trailer, a truck built alongside).
                pace.HoldPaint();
            }

            if (recipe.UseAssembly && !string.IsNullOrEmpty(recipe.AssemblyFile))
            {
                string assemblyFailure = null;

                if (sliced)
                {
                    // 1. The save and the spec: from this visit's cache when a truck of this save was built before (C3,
                    // the class notes' "per identity"), else read on the worker.
                    pace.Begin("save and spec");
                    string pureKey = PureKey(recipe);
                    PureRead read = CachedPure(pureKey);
                    PureRead job = null;

                    if (read == null)
                    {
                        job = new PureRead();
                        PureRead waiting = job;
                        EtsFileSystem files = EtsEngine.Files;
                        string savePath = recipe.AssemblyFile, truckId = recipe.TruckId;
                        bool fromSave = recipe.SpecFromSave, drivable = recipe.Drivable;
                        Worker.Post(() => ReadPure(files, savePath, truckId, fromSave, drivable), (result, error) =>
                        {
                            waiting.Result = result as PureRead;
                            waiting.Error = error;
                            waiting.Done = true;
                        });
                    }

                    pace.End();

                    while (job != null && !job.Done)
                    {
                        pace.EndFrame();
                        yield return null;
                        pace.BeginFrame();
                        Drain(within);
                    }

                    if (job != null)
                    {
                        read = job.Result;

                        // A read the worker finished is kept for the next truck of the same save; a thrown one is not.
                        if (read != null && job.Error == null)
                        {
                            RememberPure(pureKey, read);
                        }
                    }

                    assembly = read != null ? read.Assembly : null;
                    assemblyFailure = read != null ? read.AssemblyFailure : "the worker failed: " + (job != null && job.Error != null ? job.Error.GetType().Name + ": " + job.Error.Message : "no result");
                    pure = read;
                }
                else
                {
                    assembly = EtsTruckAssembly.Load(EtsEngine.Files, recipe.AssemblyFile, out assemblyFailure);
                }

                if (assembly == null)
                {
                    log.Add("Assembly: " + assemblyFailure);
                }
                else
                {
                    foreach (string note in assembly.Notes) log.Add("Assembly: " + note);
                    if (!string.IsNullOrEmpty(assembly.ChassisModel)) modelPath = assembly.ChassisModel;
                    if (!string.IsNullOrEmpty(assembly.ChassisVariant) && !string.IsNullOrEmpty(assembly.CabinVariant)) variants = assembly.ChassisVariant + ", " + assembly.CabinVariant;
                    if (!string.IsNullOrEmpty(assembly.InteriorModel)) { interiorModel = assembly.InteriorModel; interiorLook = assembly.InteriorLook; interiorVariant = assembly.InteriorVariant; }
                    if (!string.IsNullOrEmpty(assembly.FrontTire)) tireModel = assembly.FrontTire;
                    if (!string.IsNullOrEmpty(assembly.FrontRim)) rimModel = assembly.FrontRim;
                    if (!string.IsNullOrEmpty(assembly.TireLook)) tireLook = assembly.TireLook;
                    if (recipe.PaintFromGame && assembly.TryGetPaintLinear(out float r, out float g, out float b)) paintColor = new Color(r, g, b, 1f).gamma;
                    log.Add("Assembly " + recipe.AssemblyFile + ": cabin " + assembly.CabinVariant + ", interior " + assembly.InteriorVariant + " " + assembly.InteriorLook + ", " + assembly.Addons.Count + " add-ons.");
                }
            }

            if (string.IsNullOrEmpty(modelPath))
            {
                string reason = "No chassis model: " + (recipe.AssemblyFile ?? "(no save)") + " names none and the recipe has none.";
                log.Add(reason);
                recipe.Report?.Invoke();
                failed?.Invoke(reason);
                yield break;
            }

            if (sliced)
            {
                // 2. Every model file, read and parsed on the worker, adopted as each arrives.
                pace.Begin("model files");
                var models = new ModelWait();
                AskModels(ModelPaths(recipe, assembly, modelPath, tireModel, rimModel, interiorModel), models);

                if (recipe.Interior && !string.IsNullOrEmpty(interiorModel))
                {
                    AskColumns(assembly, models);
                }

                pace.End();

                while (models.Pending > 0)
                {
                    pace.EndFrame();
                    yield return null;
                    pace.BeginFrame();
                    Drain(within);
                }

                foreach (string miss in models.Failed) log.Add("Model file " + miss);
            }

            pace.Begin("chassis model");
            EtsModelData model = EtsEngine.LoadModel(modelPath, out string failure);
            pace.End();

            if (model == null)
            {
                log.Add(failure);
                recipe.Report?.Invoke();
                failed?.Invoke(failure);
                yield break;
            }

            log.Add(modelPath + ": " + model.Pieces.Length + " pieces, " + model.Parts.Length + " parts, " +
                    model.Locators.Length + " locators, looks [" + string.Join(", ", model.Looks) + "], variants [" +
                    string.Join(", ", model.Variants) + "] - parsed in " + clock.ElapsedMilliseconds + " ms.");
            recipe.Report?.Invoke();

            if (!sliced)
            {
                yield return null;
            }

            // The chassis def's look where the model has it (every preview chassis says "default", as
            // the scene does), else the recipe's.
            string look = assembly != null && !string.IsNullOrEmpty(assembly.ChassisLook) && model.LookIndex(assembly.ChassisLook) >= 0 ? assembly.ChassisLook : recipe.Look;
            string[] chassisVariants = variants.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            EtsModelData cabinModel = null;
            string cabinFailure = null;

            if (assembly != null && assembly.SeparateCabin)
            {
                pace.Begin("cabin model");
                cabinModel = EtsEngine.LoadModel(assembly.CabinModel, out cabinFailure);
                pace.End();
            }

            if (sliced)
            {
                // 3 and 4. Textures on the worker, then materials and meshes one a step, into the caches the
                // object builds below read.
                pace.Begin("plan");
                List<PieceUse> uses = PlanPieces(recipe, assembly, model, look, chassisVariants, cabinModel, tireModel, rimModel, tireLook,
                                                 interiorModel, interiorLook, interiorVariant);
                var prepared = new PrepareCount();
                pace.End();

                IEnumerator prepare = PreparePieces(uses, paintColor, pace, prepared);

                while (prepare.MoveNext())
                {
                    yield return prepare.Current;
                }

                log.Add("Prepared " + prepared.Materials + " materials, " + prepared.Textures + " textures (" + (Worker.Threaded ? "read on a worker thread" : "no worker thread: " + Worker.Failure) + ") and " + uses.Count + " pieces in " + clock.ElapsedMilliseconds + " ms.");
            }

            clock.Restart();

            // Set before any material is built, because materials are cached on first use and a
            // paint colour applied afterwards would miss every surface already made.
            EtsMaterialCache.PaintColor = paintColor;

            if (!ReferenceEquals(parent, null) && parent == null)
            {
                // The holder was destroyed while the files were read (a remote player left mid-build, A removes their
                // puppet's holder): built now, the truck would land at the scene root and outlive its player.
                string reason = "The truck's holder was removed during the build.";
                log.Add(reason);
                pace.ReleasePaint();
                failed?.Invoke(reason);
                yield break;
            }

            pace.Begin("chassis objects");
            var root = new GameObject("Ets Model");

            // Sliced, the truck is put together out of sight and switched on in one step at the end.
            if (sliced)
            {
                root.SetActive(false);
            }

            root.transform.SetParent(parent, false);
            EtsTruckRig rig = root.AddComponent<EtsTruckRig>();

            var options = new EtsModelBuilder.Options
            {
                Look = look,
                Variants = chassisVariants
            };

            EtsModelBuilder.Result built = EtsModelBuilder.Build(model, options, root.transform, EtsEngine.Materials);
            pace.End();

            // In Unity's frame: ToUnity negates z, so the game's min and max swap on that axis.
            Vector3 chassisA = EtsModelBuilder.ToUnity(model.BoundsMin), chassisB = EtsModelBuilder.ToUnity(model.BoundsMax);
            Vector3 boundsMin = Vector3.Min(chassisA, chassisB), boundsMax = Vector3.Max(chassisA, chassisB);

            if (pace.Due)
            {
                pace.EndFrame();
                yield return null;
                pace.BeginFrame();
            }

            // A cabin of its own: built at the truck's origin (its locators are in the truck's frame),
            // its locators added to the chassis's so add-ons, the interior and the seats find them.
            if (assembly != null && assembly.SeparateCabin)
            {
                if (cabinModel == null)
                {
                    log.Add("Cabin: " + cabinFailure);
                }
                else
                {
                    pace.Begin("cabin objects");
                    EtsMaterialCache.PaintColor = paintColor;
                    string cabinLook = !string.IsNullOrEmpty(assembly.CabinLook) && cabinModel.LookIndex(assembly.CabinLook) >= 0 ? assembly.CabinLook : recipe.Look;
                    EtsModelBuilder.Result cabin = EtsModelBuilder.Build(cabinModel, new EtsModelBuilder.Options { Look = cabinLook, Variants = new[] { assembly.CabinVariant ?? "default" } }, root.transform, EtsEngine.Materials);

                    foreach (KeyValuePair<string, Transform> entry in cabin.Locators)
                    {
                        if (!built.Locators.ContainsKey(entry.Key)) built.Locators[entry.Key] = entry.Value;
                    }

                    Vector3 cabinMin = EtsModelBuilder.ToUnity(cabinModel.BoundsMin), cabinMax = EtsModelBuilder.ToUnity(cabinModel.BoundsMax);
                    boundsMin = Vector3.Min(boundsMin, Vector3.Min(cabinMin, cabinMax));
                    boundsMax = Vector3.Max(boundsMax, Vector3.Max(cabinMin, cabinMax));
                    log.Add("Cabin " + assembly.CabinModel + " (" + assembly.CabinVariant + "): " + cabin.PiecesDrawn + " pieces, " + cabin.Vertices + " vertices, " + cabin.Locators.Count + " locators.");
                    pace.End();
                }
            }

            if (assembly != null)
            {
                var exterior = new Tally();
                IEnumerator<bool> steps = MountExterior(assembly, rig, built, log, exterior, paintColor, pace);

                while (steps.MoveNext())
                {
                    if (pace.Due)
                    {
                        pace.EndFrame();
                        yield return null;
                        pace.BeginFrame();
                    }
                }

                log.Add("Add-ons: " + exterior.Count + " exterior models hung on the truck.");
            }

            log.Add("Built " + built.PiecesDrawn + " pieces (" + built.PiecesSkipped + " skipped), " + built.Vertices +
                    " vertices, " + built.Triangles + " triangles, " + EtsEngine.Materials.Count + " materials, " +
                    EtsEngine.Textures.Count + " textures in " + clock.ElapsedMilliseconds + " ms.");
            recipe.Report?.Invoke();

            if (!sliced)
            {
                yield return null;
            }

            if (recipe.Wheels)
            {
                EtsMaterialCache.PaintColor = paintColor;
                var wheels = new Tally();
                IEnumerator<bool> steps = HangWheels(assembly, built, tireModel, rimModel, tireLook, log, wheels, paintColor, pace);

                while (steps.MoveNext())
                {
                    if (pace.Due)
                    {
                        pace.EndFrame();
                        yield return null;
                        pace.BeginFrame();
                    }
                }

                log.Add("Wheels on " + wheels.Count + " locators.");
                recipe.Report?.Invoke();

                if (!sliced)
                {
                    yield return null;
                }
            }

            Transform interiorMount = null;
            Transform steeringWheel = null;
            GameObject interiorRoot = null;

            // A puppet's interior is built when someone comes near or boards it (C3; LazyInterior), everything else's now.
            // The mount is chosen now either way: it is the driver's eye (SetUpTruck), which a passenger's camera needs
            // (EyeLocalFor) before the cab is built.
            InteriorJob lazyInterior = null;

            if (recipe.Interior && !string.IsNullOrEmpty(interiorModel))
            {
                EtsMaterialCache.PaintColor = paintColor;
                EtsModelData interior = EtsEngine.LoadModel(interiorModel, out failure);

                if (interior != null)
                {
                    pace.Begin("interior objects");
                    Transform mount = ChooseInteriorMount(recipe, built, root.transform, log, out string mountName);
                    interiorMount = mount != root.transform ? mount : null;
                    var job = new InteriorJob
                    {
                        Model = interior, Look = interiorLook, Variant = interiorVariant, Mount = mount, MountName = mountName,
                        Assembly = assembly, Rig = rig, Paint = paintColor, Log = log
                    };

                    if (recipe.LazyInterior)
                    {
                        pace.End();
                        lazyInterior = job;
                        log.Add("Interior: " + interiorModel + " on " + mountName + ", built when someone comes near or gets in (puppet).");
                    }
                    else
                    {
                        // The interior's first step ("interior objects") was begun above and ends inside BuildInterior; each
                        // `true` it yields is a step boundary where this frame may end.
                        IEnumerator<bool> steps = BuildInterior(job, pace, false);

                        while (steps.MoveNext())
                        {
                            if (pace.Due)
                            {
                                pace.EndFrame();
                                yield return null;
                                pace.BeginFrame();
                            }
                        }

                        interiorRoot = job.Root;
                        steeringWheel = job.SteeringWheel;
                    }
                }
                else
                {
                    log.Add("Interior: " + failure);
                }

                recipe.Report?.Invoke();

                if (pace.Due)
                {
                    pace.EndFrame();
                    yield return null;
                    pace.BeginFrame();
                }
            }

            EtsTruckController truck = null;
            string seatsFrom = null;

            if (recipe.Drivable)
            {
                pace.Begin("driving set-up");
                EtsVehicleSpec spec = pure != null ? pure.Spec : null;
                string specFailure = pure != null ? pure.SpecFailure : null;
                truck = SetUpTruck(recipe, assembly, model, cabinModel, built, root, tireModel, boundsMin, boundsMax, steeringWheel, interiorMount, log,
                                   pure != null, spec, specFailure, out seatsFrom);
                pace.End();
            }

            rig.Init(truck, recipe.Identity, forPuppet, recipe.Stem, assembly, model, built.Locators, interiorRoot, seatsFrom);
            rig.SetPaint(paintColor);

            if (lazyInterior != null)
            {
                rig.SetLazyInterior(new LazyInterior(lazyInterior, recipe.FrameBudgetMs > 0f ? recipe.FrameBudgetMs : SlicedFrameBudgetMs, recipe.Stem ?? modelPath));
            }

            if (truck != null && forPuppet)
            {
                // Kinematic, placed by transform from the network, solid to walkers and (until A says otherwise,
                // EtsTruckRig.UpdateVehicleContacts) to the own truck: SetPuppet sets EtsLayers.BodyExcludes.
                truck.SetPuppet(true);
            }

            if (sliced)
            {
                if (pace.Due)
                {
                    pace.EndFrame();
                    yield return null;
                    pace.BeginFrame();
                }

                // Every object at once: until now nothing of it was drawn or simulated.
                pace.Begin("switch on");
                root.SetActive(true);
                pace.End();
            }

            if (truck != null)
            {
                if (!forPuppet)
                {
                    // The own truck's mirrors and reflection probe (PLAN §3.1). The mirrors' View (which
                    // camera mode shows them) is the caller's to set on the old flow (the viewer's truck
                    // camera); on the new flow EtsLocalTruck.SetOwn points it at the local driver kit's.
                    pace.Begin("mirrors and probe");
                    EtsMirrors mirrors = root.GetComponent<EtsMirrors>();

                    if (mirrors == null)
                    {
                        mirrors = root.AddComponent<EtsMirrors>();
                    }

                    mirrors.Truck = truck;
                    mirrors.Root = root.transform;
                    rig.SetMirrors(mirrors);

                    // New flow: the local truck camera from the start, not only from SetOwn. A View of null renders all
                    // three mirrors every frame (EtsMirrors.LateUpdate), and A stands the truck on its spot - waiting for
                    // ground, up to 30 s (EtsTruckRig.PlaceWhenGroundExists) - before it becomes the own truck.
                    if (EtsFeatures.NewFlow && mirrors.View == null)
                    {
                        mirrors.View = EtsTruckRig.LocalCamera();
                    }

                    // Now, with the whole truck under the root (the interior and its add-ons included), rather than
                    // at the component's Start; it looks again by itself for a while and when the rig asks (Rescan).
                    mirrors.Build();

                    if (recipe.Reflections)
                    {
                        rig.SetUpReflections(recipe.ReflectionInterval);
                    }

                    pace.End();
                }

                if (recipe.Door)
                {
                    // Puppets too (B's rule, PLAN §5.5 "Passenger" step 1): clicking another player's truck asks for its seat.
                    pace.Begin("door");
                    EtsTruckDoor.Create(rig);
                    pace.End();
                }

                if (forPuppet)
                {
                    // Its own engine note, faded with distance and silent beyond 300 m or while the local player sits in it
                    // (then the per-player sound on 'Ets Net' plays it from the cab): [net §2.6] "no audio beyond the
                    // engine loop".
                    pace.Begin("engine sound");
                    EtsTruckAudio.ForPuppet(rig);
                    pace.End();
                }
            }

            if (sliced && forPuppet)
            {
                // A puppet or a studio truck must not leave its paint in the global every later truckpaint
                // material is made with (the own truck's always has, as it did before the factory). The pace has
                // already handed the global back at every frame end (Pace.HoldPaint); this is the last frame's.
                pace.ReleasePaint();
            }

            pace.EndFrame();

            if (sliced)
            {
                log.Add("Sliced: " + total.ElapsedMilliseconds + " ms over " + pace.Frames + " frames, " + pace.MainThread.ToString("0") +
                        " ms of it on the frame; worst frame " + pace.WorstFrame.ToString("0.0") + " ms (" + pace.WorstFrameWhat + "), worst single step " +
                        pace.WorstStep.ToString("0.0") + " ms (" + pace.WorstStepWhat + "); budget " + recipe.FrameBudgetMs.ToString("0.0") + " ms a frame.");
                LastPace = pace.Frames + " frames, worst " + pace.WorstFrame.ToString("0.0") + " ms (" + pace.WorstFrameWhat + ")";
            }

            if (recipe.Report == null)
            {
                // A truck built for the new flow logs once, all of it, rather than a growing report.
                Debug.Log("[SomniumEuroTruck] Truck factory: " + (recipe.Stem ?? modelPath) + (forPuppet ? " (puppet)" : "") + " built in " +
                          total.ElapsedMilliseconds + " ms.\n" + string.Join("\n", log.ToArray()));
            }

            done?.Invoke(rig);
        }

        /// <summary>The last sliced build's frame summary, for probes and the heartbeat ("N frames, worst W ms (step)").</summary>
        public static string LastPace { get; private set; } = "";

        // ------------------------------------------------------------------ the interior: now, or later for a puppet

        /// <summary>One interior to build: what BuildInterior reads, and what it hands back (Root, SteeringWheel).</summary>
        internal sealed class InteriorJob
        {
            public EtsModelData Model;
            public string Look, Variant, MountName;
            public Transform Mount;
            public EtsTruckAssembly Assembly;
            public EtsTruckRig Rig;
            public Color Paint;
            public List<string> Log;
            public GameObject Root;
            public Transform SteeringWheel;
        }

        /// <summary>
        /// Where the interior hangs: the chassis (or cabin) locator EtsTruckAssembly.ChooseInteriorLocator picks, else the
        /// truck's origin (logged with the candidates seen). The viewer's own rule, moved here unchanged.
        /// </summary>
        private static Transform ChooseInteriorMount(Recipe recipe, EtsModelBuilder.Result built, Transform root, List<string> log, out string mountName)
        {
            Transform mount = root;
            mountName = "the origin";
            var shown = new List<string>(built.Locators.Keys);
            string chosen = EtsTruckAssembly.ChooseInteriorLocator(shown, recipe.InteriorLocator);

            if (chosen != null && built.Locators.TryGetValue(chosen, out Transform locator))
            {
                mount = locator;
                mountName = "locator " + chosen;
            }

            if (mount == root)
            {
                var names = new List<string>();

                foreach (string key in built.Locators.Keys)
                {
                    if (key.Contains("inter") || key.Contains("cab"))
                    {
                        names.Add(key);
                    }
                }

                log.Add("No interior locator found; candidates seen: " + string.Join(", ", names.ToArray()));
            }

            return mount;
        }

        /// <summary>
        /// The interior model, its add-ons (the steering column first) and the animated dashboard, one step each; `true`
        /// marks a step boundary where the frame may end. The statements are the build's own, in its order, so the
        /// truck built now is unchanged (probe 2031 and the scene DAF's fingerprint). `later`: a puppet's cab built after
        /// the truck (LazyInterior) - the first step begins its own timing and sets the paint, and the interior and the
        /// dashboard are kept out of sight until the whole cab is there (the rig shows it, EtsTruckRig.UpdateInterior).
        /// </summary>
        private static IEnumerator<bool> BuildInterior(InteriorJob job, Pace pace, bool later)
        {
            if (later)
            {
                pace.Begin("interior objects");
                EtsMaterialCache.PaintColor = job.Paint;
            }

            var interiorOptions = new EtsModelBuilder.Options
            {
                Look = job.Look,
                Variants = new[] { job.Variant, "default" },
                CreateLocators = true
            };

            EtsModelBuilder.Result inside = EtsModelBuilder.Build(job.Model, interiorOptions, job.Mount, EtsEngine.Materials);
            job.Root = inside.Root;

            if (later && job.Root != null)
            {
                job.Root.SetActive(false);
            }

            pace.End();
            yield return true;

            if (job.Assembly != null)
            {
                // The animated dashboard comes first: its skeleton is where the steering wheel hangs.
                EtsModelData animated = null;

                if (!string.IsNullOrEmpty(job.Assembly.InteriorAnimatedModel))
                {
                    animated = EtsEngine.LoadModel(job.Assembly.InteriorAnimatedModel, out string animatedFailure);

                    if (animated == null)
                    {
                        job.Log.Add("Dashboard model: " + animatedFailure);
                    }
                }

                var insideAddons = new Tally();
                IEnumerator<bool> steps = MountInterior(job.Assembly, job.Rig, inside, animated, job.Log, insideAddons, job.Paint, pace);

                while (steps.MoveNext())
                {
                    yield return true;
                }

                job.SteeringWheel = insideAddons.SteeringWheel;
                job.Log.Add("Interior add-ons: " + insideAddons.Count + (job.SteeringWheel != null ? ", steering wheel on " + job.SteeringWheel.name : "") + ".");

                if (animated != null)
                {
                    pace.Begin("dashboard objects");
                    EtsMaterialCache.PaintColor = job.Paint;
                    EtsModelBuilder.Result dash = EtsModelBuilder.Build(animated, new EtsModelBuilder.Options { Look = job.Look, Variants = new[] { job.Variant, "default" }, CreateLocators = false }, job.Mount, EtsEngine.Materials);

                    if (later && dash.Root != null)
                    {
                        dash.Root.SetActive(false);
                    }

                    job.Rig.AddInteriorPart(dash.Root);

                    // Working needles, lamps, stalks and the cluster display (EtsInstruments; own truck and puppets alike).
                    // Mount never throws and does no work here - the build runs in the dashboard's own Update - but a
                    // failure must never cost the truck its cab, so it is fenced anyway.
                    try
                    {
                        EtsInstruments.Mount(job.Rig, dash.Root, animated, job.Assembly);
                    }
                    catch (Exception e)
                    {
                        job.Log.Add("Instruments could not be mounted: " + e.GetType().Name + ": " + e.Message);
                    }

                    job.Log.Add("Dashboard instruments: " + dash.PiecesDrawn + " pieces, " + animated.Bones.Length + " bones.");
                    pace.End();
                }
            }

            job.Log.Add("Interior: " + inside.PiecesDrawn + " pieces, " + inside.Vertices + " vertices, on " + job.MountName + ".");
        }

        /// <summary>
        /// A puppet's interior waiting to be built (Recipe.LazyInterior), stepped by its rig's Update while the rig wants it
        /// (EtsTruckRig.UpdateInterior): each Step spends at most the build's frame budget, then hands the frame back, as the
        /// truck's own build does. Everything it draws was prepared with the truck, so it reads no file; its cost is the
        /// objects - the interior model's (one step that cannot be cut), then one add-on a step, then the dashboard. It
        /// logs once when done ("... interior built in N frames, worst W ms (step)").
        /// </summary>
        internal sealed class LazyInterior
        {
            private readonly InteriorJob _job;
            private readonly Pace _pace;
            private readonly string _name;
            private IEnumerator<bool> _steps;
            private readonly Stopwatch _clock = new Stopwatch();

            internal LazyInterior(InteriorJob job, float budgetMs, string name)
            {
                _job = job;
                _job.Log = new List<string>();
                _pace = new Pace(budgetMs);
                _name = name;
            }

            public bool Done { get; private set; }
            public bool Started => _steps != null;
            public GameObject Root => _job.Root;
            public Transform SteeringWheel => _job.SteeringWheel;

            /// <summary>Frames spent so far and the worst, for the log and probes.</summary>
            public int Frames { get; private set; }
            public double WorstFrameMs => _pace.WorstFrame;
            public string WorstFrameWhat => _pace.WorstFrameWhat;

            /// <summary>One frame's share. True once the whole interior is built (then it does nothing more).</summary>
            public bool Step()
            {
                if (Done)
                {
                    return true;
                }

                if (_steps == null)
                {
                    _steps = BuildInterior(_job, _pace, true);
                    _pace.HoldPaint();
                    _clock.Start();
                }

                _pace.BeginFrame();
                Frames++;
                bool more;

                try
                {
                    do
                    {
                        more = _steps.MoveNext();
                    }
                    while (more && !_pace.Due);
                }
                catch (Exception e)
                {
                    // Half a cab is left out of sight (its parts were built inactive); the truck itself is unharmed.
                    more = false;
                    _job.Log.Add("the interior build stopped: " + e.GetType().Name + ": " + e.Message);
                    Debug.LogWarning("[SomniumEuroTruck] Truck factory: " + _name + " (puppet): the interior build stopped: " + e);
                }

                _pace.EndFrame();

                if (!more)
                {
                    Done = true;
                    _pace.ReleasePaint();
                    Debug.Log("[SomniumEuroTruck] Truck factory: " + _name + " (puppet) interior built in " + _clock.ElapsedMilliseconds + " ms over " + Frames +
                              " frames, worst frame " + _pace.WorstFrame.ToString("0.0") + " ms (" + _pace.WorstFrameWhat + ").\n" + string.Join("\n", _job.Log.ToArray()));
                }

                return Done;
            }
        }

        // ------------------------------------------------------------------ preparing pieces (the truck's steps 3-4; the puppet trailer's)

        private sealed class PrepareCount
        {
            public int Materials;
            public int Textures;
        }

        /// <summary>
        /// Steps 3 and 4 of a sliced build (class notes): every texture the pieces' materials load, read and decoded on the
        /// worker and uploaded as each arrives; then every material, one a step; then every mesh, one piece a step - into the
        /// caches the object builds read. Yields null where a frame ends. Moved out of Build unchanged (same steps, same yields,
        /// same paint set before each) so the puppet trailer (EtsTrailer.BuildPuppet) prepares the same way.
        /// </summary>
        private static IEnumerator PreparePieces(List<PieceUse> uses, Color paintColor, Pace pace, PrepareCount count)
        {
            Func<bool> within = pace.Within;
            var materials = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PieceUse use in uses) if (!string.IsNullOrEmpty(use.Material) && seen.Add(use.Material)) materials.Add(use.Material);
            var textures = new TextureWait();

            foreach (string material in materials)
            {
                pace.Begin("definition " + material);
                EtsMaterialDef def = EtsEngine.Materials.GetDefinition(material);
                if (def != null) AskTextures(def, textures);
                pace.End();

                if (pace.Due)
                {
                    pace.EndFrame();
                    yield return null;
                    pace.BeginFrame();
                    Drain(within);
                }
            }

            while (textures.Pending > 0)
            {
                pace.EndFrame();
                yield return null;
                pace.BeginFrame();
                Drain(within);
            }

            foreach (string material in materials)
            {
                pace.Begin("material " + material);
                EtsMaterialCache.PaintColor = paintColor;
                EtsEngine.Materials.Load(material);
                pace.End();

                if (pace.Due)
                {
                    pace.EndFrame();
                    yield return null;
                    pace.BeginFrame();
                }
            }

            foreach (PieceUse use in uses)
            {
                pace.Begin("mesh " + use.Index + " of " + use.Model.Path);
                EtsMaterialCache.PaintColor = paintColor;
                MakeMesh(use);
                pace.End();

                if (pace.Due)
                {
                    pace.EndFrame();
                    yield return null;
                    pace.BeginFrame();
                }
            }

            count.Materials = materials.Count;
            count.Textures = textures.Asked;
        }

        /// <summary>
        /// The puppet trailer's files, textures, materials and meshes, sliced like a truck's (EtsTrailer.BuildPuppet): its
        /// chassis model with the look and variant EtsTrailer.Create builds ("default"), and its wheel model on each side
        /// its wheel locators need. Afterwards Create only makes objects. With the paint CreatePuppet will build it with.
        /// A second trailer of the same kind reads nothing and passes through in a frame or two.
        /// </summary>
        internal static IEnumerator PrepareTrailer(string modelPath, string wheelPath, Color paint, List<string> log)
        {
            ForgetIfRemounted();
            var pace = new Pace(SlicedFrameBudgetMs);
            pace.HoldPaint();
            Func<bool> within = pace.Within;
            pace.Begin("trailer files");
            var models = new ModelWait();
            AskModels(new List<string> { modelPath, wheelPath }, models);
            pace.End();

            while (models.Pending > 0)
            {
                pace.EndFrame();
                yield return null;
                pace.BeginFrame();
                Drain(within);
            }

            foreach (string miss in models.Failed) log.Add("Trailer model file " + miss);
            pace.Begin("trailer plan");
            EtsModelData model = string.IsNullOrEmpty(modelPath) ? null : EtsEngine.LoadModel(modelPath, out _);
            EtsModelData wheel = string.IsNullOrEmpty(wheelPath) ? null : EtsEngine.LoadModel(wheelPath, out _);
            var uses = new List<PieceUse>();

            if (model != null)
            {
                string[] variants = { "default" };
                AddPieces(uses, model, "default", variants);
                var sides = new HashSet<string>(StringComparer.Ordinal);

                foreach (EtsModelLocator locator in EtsTruckAssembly.ShownLocators(model, variants))
                {
                    if (wheel == null || locator.Name == null || !locator.Name.StartsWith("wheel_", StringComparison.Ordinal)) continue;
                    string side = locator.Position[0] < 0f ? "left" : "right";
                    if (sides.Add(side)) AddPieces(uses, wheel, "default", new[] { side, "default" });
                }
            }

            pace.End();
            IEnumerator prepare = PreparePieces(uses, paint, pace, new PrepareCount());

            while (prepare.MoveNext())
            {
                yield return prepare.Current;
            }

            pace.EndFrame();
            pace.ReleasePaint();
            log.Add("Trailer prepared: " + uses.Count + " pieces over " + pace.Frames + " frames, worst frame " + pace.WorstFrame.ToString("0.0") + " ms (" + pace.WorstFrameWhat + ").");
        }

        // ------------------------------------------------------------------ what a visit keeps (C3: "cached per identity")

        /// <summary>
        /// The save and spec each truck save was read into, by PureKey: a second truck of the same identity (another player
        /// with the same truck, a respawn, the own truck after a puppet of it) skips step 1, which is the only archive read
        /// the engine caches (models, textures, materials, meshes, EtsEngine / EtsModelBuilder) did not already save it.
        /// Shared, not copied: EtsTruckAssembly and EtsVehicleSpec are read-only once loaded (nothing outside their
        /// files writes a field of either; checked by grep, C3). Cleared by Shutdown and when the archives are mounted
        /// anew (ForgetIfRemounted), as the engine's own caches are.
        /// </summary>
        private static readonly Dictionary<string, PureRead> PureCache = new Dictionary<string, PureRead>(StringComparer.Ordinal);

        /// <summary>A column animation read once per visit (SteeringWheelMount reads it on every build otherwise): the parsed animation, or why not.</summary>
        private sealed class ColumnRead
        {
            public EtsAnimation Animation;
            public string Failure;
            public bool Missing;
        }

        private static readonly Dictionary<string, ColumnRead> ColumnCache = new Dictionary<string, ColumnRead>(StringComparer.Ordinal);

        /// <summary>
        /// Textures whose mask plane the worker has already handed over (AskTexture): EtsTextureCache keeps the plane, but has
        /// no way to ask whether it has one, so without this every build asked the worker to read masked textures again.
        /// </summary>
        private static readonly HashSet<string> AdoptedMasks = new HashSet<string>(StringComparer.Ordinal);

        private static EtsMaterialCache _cachesFor;

        private static string PureKey(Recipe recipe)
        {
            return recipe.AssemblyFile + "|" + recipe.TruckId + "|" + (recipe.SpecFromSave ? "save" : "fallback") + "|" + (recipe.Drivable ? "drive" : "look");
        }

        private static PureRead CachedPure(string key)
        {
            return PureCache.TryGetValue(key, out PureRead read) ? read : null;
        }

        private static void RememberPure(string key, PureRead read)
        {
            PureCache[key] = read;
        }

        /// <summary>A new mount is a new EtsMaterialCache (EtsEngine.Mount/Unmount): what this visit kept belongs to the old one.</summary>
        private static void ForgetIfRemounted()
        {
            if (!ReferenceEquals(_cachesFor, EtsEngine.Materials))
            {
                PureCache.Clear();
                ColumnCache.Clear();
                AdoptedMasks.Clear();
                _cachesFor = EtsEngine.Materials;
            }
        }

        /// <summary>How many truck saves this visit has kept (one per identity's save, whatever its paint), for probes and the log.</summary>
        public static int CachedSaves => PureCache.Count;

        /// <summary>
        /// Turns the built model into a vehicle: the game's definitions for this truck, the wheel
        /// locators as wheels, the tyre model's height as the wheel radius, the interior's origin as
        /// the eye and the cabin's driver* locators as the seats. (EtsModelViewer.SetUpDriving up to the
        /// wiring, which stays with the caller.) `specRead`: the sliced build read the spec on its worker
        /// already (spec, or null with specFailure); otherwise it is read here.
        /// </summary>
        private static EtsTruckController SetUpTruck(Recipe recipe, EtsTruckAssembly assembly, EtsModelData model, EtsModelData cabinModel,
                                                     EtsModelBuilder.Result built, GameObject root, string tireModel, Vector3 boundsMinUnity,
                                                     Vector3 boundsMaxUnity, Transform steeringWheel, Transform interiorMount, List<string> log,
                                                     bool specRead, EtsVehicleSpec readSpec, string readFailure, out string seatsFrom)
        {
            seatsFrom = null;
            string truckId = TruckIdFor(recipe.TruckId, assembly);
            EtsVehicleSpec spec;
            string failure;

            if (specRead)
            {
                spec = readSpec;
                failure = readFailure;
            }
            else
            {
                spec = recipe.SpecFromSave && assembly != null
                    ? EtsVehicleSpec.Load(EtsEngine.Files, truckId, assembly.Parts, out failure)
                    : EtsVehicleSpec.Load(EtsEngine.Files, truckId, out failure);
            }

            if (spec == null)
            {
                log.Add("Driving: " + failure);
                return null;
            }

            foreach (string note in spec.Notes)
            {
                log.Add("Driving: " + note);
            }

            // Tyre radius from the tyre model's own height; the DAF's 315/70 R22.5 measures 0.51 m.
            float radius = 0.51f;
            EtsModelData tire = string.IsNullOrEmpty(tireModel) ? null : EtsEngine.LoadModel(tireModel, out string tireFailure);

            if (tire != null && tire.BoundsMax[1] - tire.BoundsMin[1] > 0.2f)
            {
                radius = (tire.BoundsMax[1] - tire.BoundsMin[1]) * 0.5f;
            }

            Bounds bounds;

            if (cabinModel == null)
            {
                // Exactly as it always was: the chassis model's box, in the game's frame, z negated.
                Vector3 boundsCentre = EtsModelBuilder.ToUnity((model.BoundsMin[0] + model.BoundsMax[0]) * 0.5f, (model.BoundsMin[1] + model.BoundsMax[1]) * 0.5f, (model.BoundsMin[2] + model.BoundsMax[2]) * 0.5f);
                var boundsSize = new Vector3(model.BoundsMax[0] - model.BoundsMin[0], model.BoundsMax[1] - model.BoundsMin[1], model.BoundsMax[2] - model.BoundsMin[2]);
                bounds = new Bounds(boundsCentre, boundsSize);
            }
            else
            {
                // Chassis and cabin together.
                bounds = new Bounds((boundsMinUnity + boundsMaxUnity) * 0.5f, boundsMaxUnity - boundsMinUnity);
            }

            EtsTruckController truck = root.AddComponent<EtsTruckController>();
            truck.Configure(spec, built.Locators, radius, bounds);
            truck.SteeringWheel = steeringWheel;
            truck.FifthWheel = FindLocator(built.Locators, "hook_t");

            if (interiorMount != null)
            {
                // The interior model's origin is the driver's eye: the game hangs it on the cabin's interior
                // locator, 2.81 m up on the DAF, and the camera unit's head offset is measured from there.
                truck.EyeLocal = root.transform.InverseTransformPoint(interiorMount.position);
            }
            else if (built.Locators.TryGetValue("driver_0", out Transform seat))
            {
                // Without an interior, the driver locator is the seat; the eye sits about 1.3 m above it.
                truck.EyeLocal = root.transform.InverseTransformPoint(seat.position) + new Vector3(0f, 1.3f, 0.4f);
            }

            // Both seats from the cabin's driver* locators ([elev §5]): the cabin model when it has one
            // of its own, else the shared truck model with the cabin's variants. Both are modelled in the
            // truck's frame and built at its origin, so a locator's position is its truck-local seat.
            EtsModelData seatModel = cabinModel ?? model;
            string lhd = assembly != null ? assembly.CabinVariant : null;
            string rhd = assembly != null ? assembly.CabinVariantUk : null;

            if (assembly == null)
            {
                // No save: the recipe's variants, the cabin one being whichever the model shows the seat in.
                lhd = LastVariant(recipe.Variants);
            }

            if (string.IsNullOrEmpty(rhd) && seatModel != null && !string.IsNullOrEmpty(lhd))
            {
                rhd = seatModel.VariantIndex(lhd + "_uk") >= 0 ? lhd + "_uk" : seatModel.VariantIndex("uk") >= 0 ? "uk" : null;
            }

            if (EtsTruckSeats.Find(seatModel, lhd, rhd, out float[] driverSeat, out float[] passengerSeat, out seatsFrom))
            {
                truck.DriverSeatLocal = EtsModelBuilder.ToUnity(driverSeat);
                truck.PassengerSeatLocal = EtsModelBuilder.ToUnity(passengerSeat);
            }

            log.Add("Seats: driver " + truck.DriverSeatLocal.ToString("0.000") + ", passenger " + truck.PassengerSeatLocal.ToString("0.000") + " (" + seatsFrom + ").");
            log.Add("Driving: " + spec + "; " + truck.Wheels.Count + " wheels of radius " + radius.ToString("0.00") + " m.");
            return truck;
        }

        /// <summary>The definition folder the spec is read from: the recipe's, else the save's data.sii folder.</summary>
        private static string TruckIdFor(string recipeTruckId, EtsTruckAssembly assembly)
        {
            return string.IsNullOrEmpty(recipeTruckId) && assembly != null ? assembly.TruckId : recipeTruckId;
        }

        private static string LastVariant(string variants)
        {
            string[] names = (variants ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return names.Length > 0 ? names[names.Length - 1] : null;
        }

        /// <summary>
        /// Tyre over rim on every wheel locator, one locator a step. The locator carries the side: the
        /// model's own rotation turns a wheel built for one side round for the other.
        /// </summary>
        private static IEnumerator<bool> HangWheels(EtsTruckAssembly assembly, EtsModelBuilder.Result built, string tireModel, string rimModel, string tireLook,
                                                    List<string> log, Tally hung, Color paint, Pace pace)
        {
            EtsModelData tire = string.IsNullOrEmpty(tireModel) ? null : EtsEngine.LoadModel(tireModel, out string tireFailure);
            EtsModelData rim = string.IsNullOrEmpty(rimModel) ? null : EtsEngine.LoadModel(rimModel, out string rimFailure);
            EtsModelData rearTire = assembly != null && !string.IsNullOrEmpty(assembly.RearTire) ? EtsEngine.LoadModel(assembly.RearTire, out string rearTireFailure) : null;
            EtsModelData rearRim = assembly != null && !string.IsNullOrEmpty(assembly.RearRim) ? EtsEngine.LoadModel(assembly.RearRim, out string rearRimFailure) : null;
            List<EtsModelData> frontExtras = LoadAll(assembly != null ? assembly.FrontWheelExtras : null);
            List<EtsModelData> rearExtras = LoadAll(assembly != null ? assembly.RearWheelExtras : null);

            if (tire == null && rim == null)
            {
                log.Add("No wheel models could be loaded.");
                yield break;
            }

            // A snapshot of the pairs: the loop below turns locators, never adds or removes one.
            var locators = new List<KeyValuePair<string, Transform>>(built.Locators);

            foreach (KeyValuePair<string, Transform> entry in locators)
            {
                if (!entry.Key.StartsWith("wheel_", StringComparison.Ordinal))
                {
                    continue;
                }

                pace.Begin("wheel " + entry.Key);
                EtsMaterialCache.PaintColor = paint;

                // Wheel models carry a "left" and a "right" variant, and the side is the model's job.
                // Measured on the DAF: the front wheels sit at x = -1.09 and +1.09, and the game's frame
                // has +x on the driver's right, so a negative x is the left side of the truck. Unity's
                // frame keeps x, so the built locator's local x carries the same sign. Every DAF wheel
                // locator is unturned; the MAN TGX's right rear is turned -90 degrees and the Magnum's
                // 180 (probe 1861), which would stand a wheel across the axle or hub-side in - so a turned
                // one is set straight, and the side comes from x as on every other wheel.
                if (Quaternion.Angle(entry.Value.localRotation, Quaternion.identity) > 0.01f)
                {
                    entry.Value.localRotation = Quaternion.identity;
                }

                string side = entry.Value.localPosition.x < 0f ? "left" : "right";
                bool rear = entry.Key.StartsWith("wheel_r", StringComparison.Ordinal);
                EtsModelData tireHere = rear && rearTire != null ? rearTire : tire;
                EtsModelData rimHere = rear && rearRim != null ? rearRim : rim;

                if (tireHere != null)
                {
                    EtsModelBuilder.Build(tireHere, new EtsModelBuilder.Options { Look = tireLook, Variants = new[] { side, "default" }, CreateLocators = false },
                        entry.Value, EtsEngine.Materials);
                }

                if (rimHere != null)
                {
                    EtsModelBuilder.Build(rimHere, new EtsModelBuilder.Options { Look = "default", Variants = new[] { side, "default" }, CreateLocators = false },
                        entry.Value, EtsEngine.Materials);
                }

                foreach (EtsModelData extra in rear && rearExtras.Count > 0 ? rearExtras : frontExtras)
                {
                    EtsModelBuilder.Build(extra, new EtsModelBuilder.Options { Look = "default", Variants = new[] { side, "default" }, CreateLocators = false },
                        entry.Value, EtsEngine.Materials);
                }

                hung.Count++;
                pace.End();
                yield return true;
            }
        }

        private static List<EtsModelData> LoadAll(List<string> paths)
        {
            var models = new List<EtsModelData>();

            if (paths == null)
            {
                return models;
            }

            foreach (string path in paths)
            {
                EtsModelData model = EtsEngine.LoadModel(path, out string failure);
                if (model != null) models.Add(model);
            }

            return models;
        }

        // ------------------------------------------------------------------ add-ons (from EtsTruckAssembly)

        /// <summary>Hangs every add-on with an exterior model on the truck, one a step; `placed` counts them.</summary>
        private static IEnumerator<bool> MountExterior(EtsTruckAssembly assembly, EtsTruckRig rig, EtsModelBuilder.Result truck, List<string> log, Tally placed, Color paint, Pace pace)
        {
            var missing = new List<string>();

            foreach (EtsTruckAssembly.Addon addon in assembly.Addons)
            {
                if (string.IsNullOrEmpty(addon.ExteriorModel)) continue;
                Transform locator = FindLocator(truck.Locators, addon.Slot);

                if (locator == null)
                {
                    missing.Add(addon.Slot);
                    continue;
                }

                pace.Begin("add-on " + addon.Slot);
                EtsMaterialCache.PaintColor = paint;

                if (Mount(addon.ExteriorModel, addon, locator, rig, log, false))
                {
                    placed.Count++;
                }

                pace.End();
                yield return true;
            }

            if (missing.Count > 0)
            {
                log.Add("No locator for add-ons: " + string.Join(", ", missing.ToArray()) + ".");
            }
        }

        /// <summary>Hangs every add-on with an interior model inside the cab, one a step; the steering wheel's mount goes in `placed`.</summary>
        private static IEnumerator<bool> MountInterior(EtsTruckAssembly assembly, EtsTruckRig rig, EtsModelBuilder.Result interior, EtsModelData animated, List<string> log,
                                                       Tally placed, Color paint, Pace pace)
        {
            var missing = new List<string>();
            pace.Begin("steering column");
            Transform wheelMount = SteeringWheelMount(assembly, interior, animated, log);
            pace.End();
            yield return true;

            foreach (EtsTruckAssembly.Addon addon in assembly.Addons)
            {
                if (string.IsNullOrEmpty(addon.InteriorModel)) continue;
                Transform locator = addon.Slot == "steering_w" && wheelMount != null ? wheelMount : FindLocator(interior.Locators, addon.Slot);

                if (locator == null)
                {
                    missing.Add(addon.Slot);
                    continue;
                }

                pace.Begin("interior add-on " + addon.Slot);
                EtsMaterialCache.PaintColor = paint;

                if (Mount(addon.InteriorModel, addon, locator, rig, log, true))
                {
                    placed.Count++;

                    if (addon.Slot == "steering_w")
                    {
                        placed.SteeringWheel = locator;
                    }
                }

                pace.End();
                yield return true;
            }

            if (missing.Count > 0)
            {
                log.Add("No interior locator for: " + string.Join(", ", missing.ToArray()) + ".");
            }
        }

        /// <summary>
        /// Where the game really hangs the steering wheel. The interior model's steering_w locator
        /// is only the column's base, and its rotation is stale on most trucks (on the DAF it would
        /// point the wheel's face at the floor). The wheel sits on the animated dashboard's bone of
        /// the same name, whose +Y is the wheel's axis toward the driver, once the column's up/down
        /// and front/back adjustments are set to the defaults the interior definition gives.
        /// Checked on all fifteen demo trucks: every axis comes out up and back, 37 to 65 degrees
        /// from vertical. The wheel model lies flat with its face up, so it needs only this frame.
        /// </summary>
        private static Transform SteeringWheelMount(EtsTruckAssembly assembly, EtsModelBuilder.Result interior, EtsModelData animated, List<string> log)
        {
            if (animated == null || interior == null || interior.Root == null)
            {
                return null;
            }

            int bone = animated.FindBone("steering_w");

            if (bone < 0)
            {
                return null;
            }

            var pose = new EtsSkeletonPose(animated);
            int moved = ApplyColumnAnimation(pose, animated, assembly.WheelUpDownAnim, assembly.WheelUpDownDefault, log) +
                        ApplyColumnAnimation(pose, animated, assembly.WheelFrontBackAnim, assembly.WheelFrontBackDefault, log);
            var position = new float[3];
            var rotation = new float[4];
            var axis = new float[3];

            if (!pose.TryWorld(bone, position, rotation))
            {
                return null;
            }

            EtsSkeletonPose.Rotate(rotation, new[] { 0f, 1f, 0f }, axis);
            Vector3 axisUnity = EtsModelBuilder.ToUnity(axis[0], axis[1], axis[2]).normalized;

            // The wheel's top is the highest point of its rim: straight up, pushed into the wheel's
            // plane. The model's top is its -Z, which is +Z once in Unity, so LookRotation fits.
            Vector3 top = Vector3.up - axisUnity * Vector3.Dot(Vector3.up, axisUnity);

            if (top.sqrMagnitude < 1e-4f)
            {
                top = Vector3.forward;
            }

            var mount = new GameObject("bone:steering_w");
            mount.transform.SetParent(interior.Root.transform, false);
            mount.transform.localPosition = EtsModelBuilder.ToUnity(position[0], position[1], position[2]);
            mount.transform.localRotation = Quaternion.LookRotation(top.normalized, axisUnity);
            log.Add("Steering wheel: on the animated model's steering_w bone with the column at its default adjustment (" + moved + " bone" + (moved == 1 ? "" : "s") + " moved), axis " + Vector3.Angle(Vector3.up, axisUnity).ToString("0") + " deg from vertical, centre " + mount.transform.localPosition.ToString("0.00") + " from the eye.");
            return mount.transform;
        }

        private static int ApplyColumnAnimation(EtsSkeletonPose pose, EtsModelData animated, string path, float t01, List<string> log)
        {
            if (string.IsNullOrEmpty(path) || EtsEngine.Files == null)
            {
                return 0;
            }

            // Read and parsed once a visit (C3): a second truck of the same interior reads no file, and a sliced build has
            // them from its worker already (AskColumns). The same lines are logged on every build, from the kept outcome.
            ForgetIfRemounted();

            if (!ColumnCache.TryGetValue(path, out ColumnRead read))
            {
                read = ReadColumn(EtsEngine.Files, path);
                ColumnCache[path] = read;
            }

            if (read.Missing)
            {
                log.Add("Column animation missing: " + path);
                return 0;
            }

            if (read.Animation == null)
            {
                log.Add("Column animation " + path + ": " + read.Failure);
                return 0;
            }

            try
            {
                if (read.Animation.SkeletonHash != animated.SkeletonHash)
                {
                    log.Add("Column animation " + path + " was made for another skeleton; the column stays at its bind pose.");
                    return 0;
                }

                return pose.Apply(read.Animation, t01);
            }
            catch (Exception e)
            {
                log.Add("Column animation " + path + ": " + e.Message);
                return 0;
            }
        }

        /// <summary>
        /// Builds one add-on model on its locator and files it as `interior` says. The flag is which of the
        /// accessory's two models is being hung (MountExterior passes false, MountInterior true). It used to be
        /// worked out by comparing the path with the add-on's interior_model, which files BOTH copies as
        /// interior whenever exterior_model and interior_model name the same file: the sun shield of 14 of the
        /// 18 preview trucks, the Scania 2016s' interior light bar and the FH 2012's intake bar (probe 2030).
        /// ShowFromInside then hid the outside sun shield in the chase view (the DAF's, on every drive since the
        /// add-ons were hung) and showed it from the cab.
        /// </summary>
        private static bool Mount(string modelPath, EtsTruckAssembly.Addon addon, Transform locator, EtsTruckRig rig, List<string> log, bool interior)
        {
            EtsModelData model = EtsEngine.LoadModel(modelPath, out string failure);

            if (model == null)
            {
                log.Add("Add-on " + addon.Slot + ": " + failure);
                return false;
            }

            EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
            {
                Look = AddonLook(model, addon),
                Variants = new[] { addon.Variant, "default" },
                CreateLocators = false
            }, locator, EtsEngine.Materials);

            if (built != null && built.Root != null)
            {
                built.Root.name = "addon " + addon.Slot;
                (interior ? rig.InteriorAddons : rig.ExteriorAddons).Add(new EtsTruckRig.Mounted { Slot = addon.Slot, Root = built.Root, HasBoth = !string.IsNullOrEmpty(addon.ExteriorModel) && !string.IsNullOrEmpty(addon.InteriorModel) });
            }

            return built != null && built.PiecesDrawn > 0;
        }

        /// <summary>The add-on's look where its model has it, else the model's first.</summary>
        private static string AddonLook(EtsModelData model, EtsTruckAssembly.Addon addon)
        {
            return model.Looks.Length > 0 && Array.IndexOf(model.Looks, addon.Look) < 0 ? model.Looks[0] : addon.Look;
        }

        /// <summary>The locator for a slot: an exact name, then the slot with a suffix ("mirror1", "r_bumper_02").</summary>
        public static Transform FindLocator(Dictionary<string, Transform> locators, string slot)
        {
            if (locators.TryGetValue(slot, out Transform exact))
            {
                return exact;
            }

            Transform best = null;

            foreach (KeyValuePair<string, Transform> entry in locators)
            {
                if (entry.Key.StartsWith(slot, StringComparison.Ordinal) && (best == null || entry.Key.Length < best.name.Length))
                {
                    best = entry.Value;
                }
            }

            return best;
        }

        /// <summary>FindLocator on names alone (the plan's locators exist only as model data yet): exact, then prefix.</summary>
        private static bool HasLocator(HashSet<string> names, string slot)
        {
            if (names.Contains(slot))
            {
                return true;
            }

            foreach (string name in names)
            {
                if (name.StartsWith(slot, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ the sliced pace

        /// <summary>
        /// One sliced build's clock: this frame's script time against the budget, the frame count, and the worst
        /// frame and the worst single step, named, for the build's log line. Without a budget it never asks for a
        /// frame (the old slices are the build's own yields).
        /// </summary>
        private sealed class Pace
        {
            private readonly double _budget;
            private readonly Stopwatch _frame = new Stopwatch();
            private string _what = "start";
            private double _whatStart;

            public readonly bool Sliced;
            public int Frames = 1;
            public double MainThread, WorstFrame, WorstStep;
            public string WorstFrameWhat = "", WorstStepWhat = "";

            public Pace(float budgetMs)
            {
                Sliced = budgetMs > 0f;
                _budget = budgetMs;
                _frame.Start();
            }

            /// <summary>This frame has spent its budget: yield before the next step.</summary>
            public bool Due => Sliced && _frame.Elapsed.TotalMilliseconds >= _budget;

            /// <summary>For EtsWorker.Drain: hand over another finished job while the frame has budget left.</summary>
            public bool Within()
            {
                return !Sliced || _frame.Elapsed.TotalMilliseconds < _budget;
            }

            public void Begin(string what)
            {
                if (!Sliced) return;
                _what = what;
                _whatStart = _frame.Elapsed.TotalMilliseconds;
            }

            public void End()
            {
                if (!Sliced) return;
                double ms = _frame.Elapsed.TotalMilliseconds - _whatStart;

                if (ms > WorstStep)
                {
                    WorstStep = ms;
                    WorstStepWhat = _what;
                }
            }

            public void EndFrame()
            {
                double ms = _frame.Elapsed.TotalMilliseconds;
                MainThread += ms;

                if (ms > WorstFrame)
                {
                    WorstFrame = ms;
                    WorstFrameWhat = _what;
                }

                if (_holdPaint)
                {
                    EtsMaterialCache.PaintColor = _outsidePaint;
                }
            }

            private bool _holdPaint;
            private Color _outsidePaint;

            /// <summary>
            /// A puppet's (or a studio truck's, or a trailer's) build: EtsMaterialCache.PaintColor is a global that each of its
            /// material steps sets to this truck's paint within the frame; at every frame end it is handed back as the frame
            /// found it, so nothing built elsewhere between two frames (the own truck's job trailer, EtsJobs, builds its
            /// materials with whatever the global holds) takes a remote player's colour. The own truck keeps the old rule:
            /// its paint stays in the global, as it always has.
            /// </summary>
            public void HoldPaint()
            {
                _holdPaint = true;
                _outsidePaint = EtsMaterialCache.PaintColor;
            }

            /// <summary>The end of a held build: the global as the last frame found it, and no more holding.</summary>
            public void ReleasePaint()
            {
                if (_holdPaint)
                {
                    EtsMaterialCache.PaintColor = _outsidePaint;
                    _holdPaint = false;
                }
            }

            public void BeginFrame()
            {
                Frames++;
                _frame.Restart();

                if (_holdPaint)
                {
                    _outsidePaint = EtsMaterialCache.PaintColor;
                }

                // Until the next Begin, this frame's time is Drain's: the worker's finished reads adopted on the frame (a
                // texture upload, a parsed model). The yield itself is not counted (the clock restarts above), so a worst
                // frame under this name is that hand-over, not idle waiting (C2 checker: the log said "waiting for the
                // worker" for 4.6-14.2 ms frames).
                _what = "adopting the worker's reads";
            }
        }

        // ------------------------------------------------------------------ the worker's side

        private static EtsWorker _worker;

        /// <summary>
        /// The factory's one background thread, started on the first sliced build. Its own, not the traffic's:
        /// the traffic drains its worker under its own budget from its own Update, and a truck's jobs must come
        /// back to the truck's build. Stopped by <see cref="Shutdown"/> (EtsLocalTruck.Reset, from EtsNet.OnDestroy)
        /// so no thread outlives the visit; a later build starts a new one.
        /// </summary>
        private static EtsWorker Worker
        {
            get
            {
                if (_worker == null)
                {
                    _worker = new EtsWorker("Ets truck reader");

                    if (!_worker.Threaded)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Truck factory: no background thread (" + _worker.Failure + "); reads run on the frame, one a frame.");
                    }
                }

                return _worker;
            }
        }

        /// <summary>Stops the worker (leaving the world). Builds still running afterwards start a new one.</summary>
        public static void Shutdown()
        {
            _worker?.Dispose();
            _worker = null;
            PureCache.Clear();
            ColumnCache.Clear();
            AdoptedMasks.Clear();
            _cachesFor = null;
        }

        private static void Drain(Func<bool> within)
        {
            _worker?.Drain(within);
        }

        /// <summary>The save and the spec, read on the worker (the result) or waited for (the job).</summary>
        private sealed class PureRead
        {
            public EtsTruckAssembly Assembly;
            public string AssemblyFailure;
            public EtsVehicleSpec Spec;
            public string SpecFailure;
            public volatile bool Done;
            public PureRead Result;
            public Exception Error;
        }

        /// <summary>Worker thread: EtsTruckAssembly.Load and EtsVehicleSpec.Load exactly as the frame would call them, through the uncached reader.</summary>
        private static PureRead ReadPure(EtsFileSystem files, string savePath, string recipeTruckId, bool specFromSave, bool drivable)
        {
            var read = new PureRead();
            Func<string, EtsSiiFile> reader = EtsTruckAssembly.UncachedReader(files);
            read.Assembly = EtsTruckAssembly.Load(reader, savePath, out read.AssemblyFailure);

            if (drivable)
            {
                string truckId = TruckIdFor(recipeTruckId, read.Assembly);
                read.Spec = specFromSave && read.Assembly != null
                    ? EtsVehicleSpec.Load(files, reader, truckId, read.Assembly.Parts, out read.SpecFailure)
                    : EtsVehicleSpec.Load(files, reader, truckId, null, out read.SpecFailure);
            }

            return read;
        }

        private sealed class ModelWait
        {
            public int Pending;
            public readonly List<string> Failed = new List<string>();
        }

        /// <summary>Every model file the build will load, in the order it loads them, the recipe's fallbacks where the save names none.</summary>
        private static List<string> ModelPaths(Recipe recipe, EtsTruckAssembly assembly, string modelPath, string tireModel, string rimModel, string interiorModel)
        {
            var paths = new List<string> { modelPath };

            if (assembly != null && assembly.SeparateCabin) paths.Add(assembly.CabinModel);

            if (assembly != null)
            {
                foreach (EtsTruckAssembly.Addon addon in assembly.Addons) paths.Add(addon.ExteriorModel);
            }

            if (recipe.Wheels)
            {
                paths.Add(tireModel);
                paths.Add(rimModel);

                if (assembly != null)
                {
                    paths.Add(assembly.RearTire);
                    paths.Add(assembly.RearRim);
                    paths.AddRange(assembly.FrontWheelExtras);
                    paths.AddRange(assembly.RearWheelExtras);
                }
            }
            else if (recipe.Drivable)
            {
                paths.Add(tireModel); // the wheel radius (SetUpTruck)
            }

            if (recipe.Interior && !string.IsNullOrEmpty(interiorModel))
            {
                paths.Add(interiorModel);

                if (assembly != null)
                {
                    paths.Add(assembly.InteriorAnimatedModel);
                    foreach (EtsTruckAssembly.Addon addon in assembly.Addons) paths.Add(addon.InteriorModel);
                }
            }

            return paths;
        }

        private static void AskModels(List<string> paths, ModelWait wait)
        {
            EtsFileSystem files = EtsEngine.Files;
            var asked = new HashSet<string>(StringComparer.Ordinal);

            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || EtsEngine.IsModelLoaded(path) || !asked.Add(EtsEngine.ModelKey(path)))
                {
                    continue;
                }

                string p = path;
                wait.Pending++;
                Worker.Post(() =>
                {
                    EtsModelData read = EtsEngine.ReadModel(files, p, out string why);
                    return (object)read ?? why ?? "no reason given";
                }, (result, error) =>
                {
                    wait.Pending--;

                    if (!EtsEngine.IsMounted)
                    {
                        return;
                    }

                    if (result is EtsModelData model)
                    {
                        EtsEngine.AdoptModel(p, model);
                    }
                    else
                    {
                        wait.Failed.Add(p + ": " + (error != null ? error.GetType().Name + ": " + error.Message : result as string));
                    }
                });
            }
        }

        /// <summary>
        /// The steering column's two animations (SteeringWheelMount: up/down and front/back), read and parsed on the worker
        /// with the model files and kept for the visit (ColumnCache). Without this a puppet's lazy cab read both on the
        /// frame it was built (probe 2250: 2 archive reads, 3.9 ms, "steering column").
        /// </summary>
        private static void AskColumns(EtsTruckAssembly assembly, ModelWait wait)
        {
            if (assembly == null)
            {
                return;
            }

            AskColumn(assembly.WheelUpDownAnim, wait);
            AskColumn(assembly.WheelFrontBackAnim, wait);
        }

        private static void AskColumn(string path, ModelWait wait)
        {
            if (string.IsNullOrEmpty(path) || ColumnCache.ContainsKey(path))
            {
                return;
            }

            EtsFileSystem files = EtsEngine.Files;
            EtsMaterialCache generation = EtsEngine.Materials;
            wait.Pending++;
            Worker.Post(() => ReadColumn(files, path), (result, error) =>
            {
                wait.Pending--;

                if (result is ColumnRead read && ReferenceEquals(generation, EtsEngine.Materials) && !ColumnCache.ContainsKey(path))
                {
                    ColumnCache[path] = read;
                }
            });
        }

        /// <summary>A column animation read and parsed (worker or frame): the animation, or why not. Never throws.</summary>
        private static ColumnRead ReadColumn(EtsFileSystem files, string path)
        {
            var read = new ColumnRead();
            byte[] bytes = files.Read(path);

            if (bytes == null)
            {
                read.Missing = true;
                return read;
            }

            try
            {
                read.Animation = EtsAnimation.Parse(bytes, path);
            }
            catch (Exception e)
            {
                read.Failure = e.Message;
            }

            return read;
        }

        private sealed class TextureWait
        {
            public int Pending;
            public int Asked;
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// The textures EtsMaterialCache.Build will load for this material - base (with its reflection-mask plane
        /// where the material is masked), second layer, normal map - asked of the worker. Materials Build skips
        /// (shadow-only, night, helpers) ask for nothing. The rule is the traffic's (EtsTrafficModels.AskTextures,
        /// owner A), which is EtsMaterialCache.Build's own texture calls; a texture asked here that Build does not
        /// load only costs its memory, one Build loads that was not asked is read on the frame as it always was.
        /// </summary>
        private static void AskTextures(EtsMaterialDef def, TextureWait wait)
        {
            string effect = def.Effect ?? string.Empty;

            if (effect.Length == 0 || def.EffectHas("shadowonly") || effect.StartsWith("eut2.none", StringComparison.Ordinal) ||
                def.EffectHas("shadowmap") || def.EffectHas("mlaaweight") || def.EffectHas(".night") || def.EffectHas("fakeshadow") ||
                def.EffectHas("decalshadow") || def.EffectHas("water"))
            {
                return;
            }

            bool glass = def.EffectHas("glass") || def.EffectHas("window");
            bool overlay = !glass && def.EffectHas("decal.over");
            bool cutout = !glass && !overlay && (def.EffectHas(".a.") || effect.EndsWith(".a", StringComparison.Ordinal) ||
                                                 def.EffectHas(".a_") || def.EffectHas("alpha") || def.EffectHas("leaves") ||
                                                 def.EffectHas("decal") || def.EffectHas("fence") || def.EffectHas("grass") ||
                                                 def.EffectHas("flare"));
            bool masked = !glass && !overlay && !cutout && !def.EffectHas("lamp") && (def.EffectHas(".env") || def.EffectHas("nofresnel"));

            AskTexture(def.BaseTexture, false, masked, wait);

            string second = EtsMaterialCache.SecondLayer(def, out _);
            if (second != null && !def.EffectHas("decal.over")) AskTexture(second, false, false, wait);

            // amod's decal mask (layer mode 5, EtsMaterialCache.Build), so it is not read on the frame.
            if (second != null && def.EffectHas("amod") && !def.EffectHas("iamod") && def.Textures.TryGetValue("texture_mask", out string amodMask)) AskTexture(amodMask, false, false, wait);

            if (def.NormalTexture != null && !def.EffectHas("tsnmapuv")) AskTexture(def.NormalTexture, true, false, wait);
        }

        private static void AskTexture(string tobjPath, bool linear, bool withMask, TextureWait wait)
        {
            if (string.IsNullOrEmpty(tobjPath))
            {
                return;
            }

            // A texture already uploaded is asked for again only for its mask plane (Adopt then keeps the plane and
            // hands back the texture it has), so the mask is never read on the frame.
            string key = tobjPath + (linear ? "|linear" : "") + (withMask ? "|mask" : "");

            // A masked texture whose plane was handed over before (this visit, AdoptedMasks) is loaded and its plane kept:
            // nothing to read (C3).
            bool loaded = EtsEngine.Textures.IsLoaded(tobjPath, linear);

            if ((loaded && (!withMask || AdoptedMasks.Contains(key))) || !wait.Keys.Add(key))
            {
                return;
            }

            EtsFileSystem files = EtsEngine.Files;
            EtsTextureCache cache = EtsEngine.Textures;
            wait.Pending++;
            wait.Asked++;
            Worker.Post(() => EtsTextureCache.Prepare(files, tobjPath, linear, withMask), (result, error) =>
            {
                wait.Pending--;

                if (result is EtsTextureCache.Prepared prepared && EtsEngine.IsMounted)
                {
                    // A texture that could not be read says so in Prepared.Failure, which the cache reports.
                    EtsEngine.Textures.Adopt(prepared);

                    if (withMask && ReferenceEquals(cache, EtsEngine.Textures))
                    {
                        AdoptedMasks.Add(key);
                    }
                }
                else if (error != null && EtsEngine.IsMounted)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Truck factory: texture " + tobjPath + " could not be prepared (" + error.GetType().Name + ": " + error.Message + "); the material reads it on the frame.");
                }
            });
        }

        // ------------------------------------------------------------------ the plan: which pieces the builds will draw

        /// <summary>One piece a model build will draw, with the material path its look gives it.</summary>
        private sealed class PieceUse
        {
            public EtsModelData Model;
            public EtsModelPiece Piece;
            public int Index;
            public string Material;
        }

        /// <summary>
        /// Every piece the object builds below will draw, model by model with the options each is built with: the
        /// chassis, a separate cabin, the exterior add-ons that have a locator, the tyre, rim and extras per side the
        /// wheel locators need, the interior, its add-ons and the dashboard. Pure model data: the locators exist only
        /// as names yet (EtsTruckAssembly.ShownLocators, the pure twin of EtsModelBuilder's).
        /// </summary>
        private static List<PieceUse> PlanPieces(Recipe recipe, EtsTruckAssembly assembly, EtsModelData model, string look, string[] chassisVariants,
                                                 EtsModelData cabinModel, string tireModel, string rimModel, string tireLook,
                                                 string interiorModel, string interiorLook, string interiorVariant)
        {
            var uses = new List<PieceUse>();
            AddPieces(uses, model, look, chassisVariants);
            var exteriorNames = new HashSet<string>(StringComparer.Ordinal);
            var wheelSides = new List<KeyValuePair<string, string>>();

            foreach (EtsModelLocator locator in EtsTruckAssembly.ShownLocators(model, chassisVariants))
            {
                exteriorNames.Add(locator.Name);
            }

            if (cabinModel != null)
            {
                string cabinLook = !string.IsNullOrEmpty(assembly.CabinLook) && cabinModel.LookIndex(assembly.CabinLook) >= 0 ? assembly.CabinLook : recipe.Look;
                string[] cabinVariants = { assembly.CabinVariant ?? "default" };
                AddPieces(uses, cabinModel, cabinLook, cabinVariants);

                foreach (EtsModelLocator locator in EtsTruckAssembly.ShownLocators(cabinModel, cabinVariants))
                {
                    exteriorNames.Add(locator.Name);
                }
            }

            if (assembly != null)
            {
                foreach (EtsTruckAssembly.Addon addon in assembly.Addons)
                {
                    if (string.IsNullOrEmpty(addon.ExteriorModel) || !HasLocator(exteriorNames, addon.Slot)) continue;
                    EtsModelData addonModel = EtsEngine.LoadModel(addon.ExteriorModel, out _);
                    if (addonModel != null) AddPieces(uses, addonModel, AddonLook(addonModel, addon), new[] { addon.Variant, "default" });
                }
            }

            if (recipe.Wheels)
            {
                // The wheel locators and the side each gets: x < 0 is the left (HangWheels), read off the model data
                // the way the built locator's localPosition carries it (ToUnity keeps x).
                var chassisLocators = new List<EtsModelLocator>(EtsTruckAssembly.ShownLocators(model, chassisVariants));
                if (cabinModel != null) chassisLocators.AddRange(EtsTruckAssembly.ShownLocators(cabinModel, new[] { assembly.CabinVariant ?? "default" }));
                var sides = new HashSet<string>(StringComparer.Ordinal);

                foreach (EtsModelLocator locator in chassisLocators)
                {
                    if (locator.Name == null || !locator.Name.StartsWith("wheel_", StringComparison.Ordinal)) continue;
                    bool rear = locator.Name.StartsWith("wheel_r", StringComparison.Ordinal);
                    string side = locator.Position[0] < 0f ? "left" : "right";
                    if (sides.Add((rear ? "r" : "f") + side)) wheelSides.Add(new KeyValuePair<string, string>(rear ? "r" : "f", side));
                }

                EtsModelData tire = EtsEngine.LoadModel(tireModel, out _);
                EtsModelData rim = EtsEngine.LoadModel(rimModel, out _);
                EtsModelData rearTire = assembly != null && !string.IsNullOrEmpty(assembly.RearTire) ? EtsEngine.LoadModel(assembly.RearTire, out _) : null;
                EtsModelData rearRim = assembly != null && !string.IsNullOrEmpty(assembly.RearRim) ? EtsEngine.LoadModel(assembly.RearRim, out _) : null;
                List<EtsModelData> frontExtras = LoadAll(assembly != null ? assembly.FrontWheelExtras : null);
                List<EtsModelData> rearExtras = LoadAll(assembly != null ? assembly.RearWheelExtras : null);

                if (tire != null || rim != null)
                {
                    foreach (KeyValuePair<string, string> wheel in wheelSides)
                    {
                        bool rear = wheel.Key == "r";
                        string[] sideVariants = { wheel.Value, "default" };
                        EtsModelData tireHere = rear && rearTire != null ? rearTire : tire;
                        EtsModelData rimHere = rear && rearRim != null ? rearRim : rim;
                        if (tireHere != null) AddPieces(uses, tireHere, tireLook, sideVariants);
                        if (rimHere != null) AddPieces(uses, rimHere, "default", sideVariants);
                        foreach (EtsModelData extra in rear && rearExtras.Count > 0 ? rearExtras : frontExtras) AddPieces(uses, extra, "default", sideVariants);
                    }
                }
            }

            if (recipe.Interior && !string.IsNullOrEmpty(interiorModel))
            {
                EtsModelData interior = EtsEngine.LoadModel(interiorModel, out _);

                if (interior != null)
                {
                    string[] interiorVariants = { interiorVariant, "default" };
                    AddPieces(uses, interior, interiorLook, interiorVariants);

                    if (assembly != null)
                    {
                        var insideNames = new HashSet<string>(StringComparer.Ordinal);
                        foreach (EtsModelLocator locator in EtsTruckAssembly.ShownLocators(interior, interiorVariants)) insideNames.Add(locator.Name);
                        EtsModelData animated = string.IsNullOrEmpty(assembly.InteriorAnimatedModel) ? null : EtsEngine.LoadModel(assembly.InteriorAnimatedModel, out _);
                        bool wheelBone = animated != null && animated.FindBone("steering_w") >= 0;

                        foreach (EtsTruckAssembly.Addon addon in assembly.Addons)
                        {
                            if (string.IsNullOrEmpty(addon.InteriorModel)) continue;
                            if (!(addon.Slot == "steering_w" && wheelBone) && !HasLocator(insideNames, addon.Slot)) continue;
                            EtsModelData addonModel = EtsEngine.LoadModel(addon.InteriorModel, out _);
                            if (addonModel != null) AddPieces(uses, addonModel, AddonLook(addonModel, addon), new[] { addon.Variant, "default" });
                        }

                        if (animated != null)
                        {
                            AddPieces(uses, animated, interiorLook, interiorVariants);
                        }
                    }
                }
            }

            return uses;
        }

        /// <summary>The pieces EtsModelBuilder.Build draws of a model with these options: its look (the first when not found), its variants' parts, never "coll".</summary>
        private static void AddPieces(List<PieceUse> into, EtsModelData model, string lookName, string[] variantNames)
        {
            int lookIndex = Math.Max(0, model.LookIndex(lookName));
            string[] lookMaterials = lookIndex < model.LookMaterials.Length ? model.LookMaterials[lookIndex] : new string[0];
            List<int> variants = EtsTruckAssembly.VariantIndices(model, variantNames);

            foreach (EtsModelPart part in model.Parts)
            {
                if (!EtsTruckAssembly.PartShown(part, variants))
                {
                    continue;
                }

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    EtsModelPiece piece = model.Pieces[p];
                    into.Add(new PieceUse
                    {
                        Model = model,
                        Piece = piece,
                        Index = p,
                        Material = piece.MaterialIndex < lookMaterials.Length ? lookMaterials[piece.MaterialIndex] : null
                    });
                }
            }
        }

        /// <summary>
        /// The mesh EtsModelBuilder.Build will ask its cache for, made now: the same name ("piece " + index) and the
        /// same choice between the texgen mesh and the plain one with the effect's base uv set. Pieces Build will not
        /// draw (their material is null: shadow-only and the like) and leaves (their own mesh path) are left to it.
        /// </summary>
        private static void MakeMesh(PieceUse use)
        {
            EtsMaterialDef def = EtsEngine.Materials.GetDefinition(use.Material);

            if (EtsEngine.Materials.Load(use.Material) == null || EtsModelBuilder.IsLeaves(def))
            {
                return;
            }

            if (def != null && def.EffectHas(".tg0"))
            {
                EtsModelBuilder.BuildTexgenMesh(use.Piece, "piece " + use.Index, def);
            }
            else
            {
                EtsModelBuilder.BuildMesh(use.Piece, "piece " + use.Index, EtsMaterialCache.BaseUvChannel(def, use.Piece.UvChannels));
            }
        }
    }
}
