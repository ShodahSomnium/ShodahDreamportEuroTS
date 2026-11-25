using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The game's own models, read out of its archives and standing in the world - the first
    /// thing in this Dreamport that is Euro Truck Simulator inside Somnium rather than beside it.
    ///
    /// Nothing here is shipped with the world. The truck is decoded at runtime from the player's
    /// own base.scs: its descriptor names the materials, the materials name the textures, the
    /// geometry file holds the vertices, and the wheels are separate models hung on the chassis
    /// at the locators the model itself provides. If the game is not installed, the label says
    /// so and nothing else happens.
    ///
    /// A truck in this game is not one model with one variant. The same truck.pmd carries a
    /// chassis variant and a cabin variant, and a configured truck shows both at once; the
    /// wheels come from the upgrade catalogue as a tyre model over a rim model. The defaults
    /// below are the DAF XF Euro 6 as the game's own definition files describe it.
    ///
    /// Since Phase 1 (PLAN.md §3.1) this is the scene's truck CONFIGURATION and today's bootstrap:
    /// the building itself is EtsTruckFactory's (the same statements, moved), fed a Recipe made of
    /// the fields below (SceneRecipe). What stays here is what makes the one scene truck the local
    /// player's - the camera, sound, jobs, menu, front end, seat, wheel grab, door, network and VR
    /// dash wiring (SetUpDriving) - and standing it in the verified TransiNet bay
    /// (PlaceWhenGroundExists, unchanged). From Phase 2 the scene sets _loadOnStart to 0 and the
    /// new flow builds trucks through the factory by preview identity instead.
    ///
    /// The two flows (Phase 2, PLAN §3.1, §3.2; EtsFeatures.NewFlow):
    /// - Old flow (the fallback): exactly the above, at scene start, whatever _loadOnStart says. The scene
    ///   holds 0 since Phase 2 (Euro Truck Scene.unity :1105, A's edit, because the new flow must not build
    ///   a truck at start and the scene overrides code defaults), and the old flow had always loaded here:
    ///   reading the field would leave the fallback with no truck at all.
    /// - New flow: NO truck at start in the client (_loadOnStart 1 would be a test switch that builds the
    ///   configured truck on arrival; the scene has 0). The player's truck comes from the tablet (Company > Truck Preference
    ///   -> EtsNet.SpawnOwnTruck -> EtsTruckFactory.Build by identity -> EtsLocalTruck.SetOwn), and the
    ///   driver kit is the one set of per-player singletons EtsNet.Awake ensured on 'Ets Net' (seat, camera,
    ///   wheel grab, sound, jobs, menu, route advisor): nothing is added to this object, so there is never a
    ///   second menu, job board or seat. What only this object has - the scene's status text - is handed to
    ///   whichever truck becomes the own one (OnOwnChanged: the speed line, the job line, the menu's font; the
    ///   VR dash plate is EtsRouteAdvisor's, owner D). In the editor there is no Somnium client, so no tablet
    ///   to choose a truck on: once the session has put the player in the world (EtsSession InWorld; at once without a session), the
    ///   configured truck is spawned through the factory by the same identity entry point the tablet uses,
    ///   made the own truck and stood on the chosen yard's first spot, so Play mode drives as before.
    /// </summary>
    public class EtsModelViewer : MonoBehaviour
    {
        [Header("Euro Truck Simulator 2")]
        [Tooltip("Install folder. Leave empty to search every Steam library on this machine.")]
        [SerializeField]
        private string _installRoot = "";

        [Header("Model")]
        [Tooltip("Descriptor path inside the archives. The geometry file is found beside it.")]
        [SerializeField]
        private string _modelPath = "/vehicle/truck/daf_xf_euro6/truck.pmd";

        [Tooltip("Look, which selects the material set. 'default' on most models.")]
        [SerializeField]
        private string _look = "default";

        [Tooltip("Variants to show together, comma separated. A truck is one chassis variant " +
                 "and one cabin variant.")]
        [SerializeField]
        private string _variants = "chs_4x2, cabin_c";

        [Header("Wheels")]
        [Tooltip("Hang tyre and rim models on every locator named wheel_*.")]
        [SerializeField]
        private bool _wheels = true;

        [SerializeField]
        private string _tireModel = "/vehicle/truck/upgrade/wheel/tire/front_1.pmd";

        [SerializeField]
        private string _tireLook = "gestone";

        [SerializeField]
        private string _rimModel = "/vehicle/truck/upgrade/wheel/rim/front_1.pmd";

        [Header("Interior")]
        [Tooltip("Load the cabin interior model as well. Its origin is the truck's origin.")]
        [SerializeField]
        private bool _interior = true;

        [SerializeField]
        private string _interiorModel = "/vehicle/truck/daf_xf_euro6/interior/model.pmd";

        [SerializeField]
        private string _interiorVariant = "standard_low";

        [SerializeField]
        private string _interiorLook = "look1";

        [Tooltip("Locators on the truck the interior may hang from, first found wins. The " +
                 "interior's own origin sits at cabin height - its bounds run two metres below " +
                 "it and one above - so it belongs on the cabin's interior locator, not at the " +
                 "truck's origin. On the DAF the EU cabin's locator is interior2 and the UK " +
                 "cabin's is interior1, read off the model's part ranges. Empty puts it at the origin.")]
        [SerializeField]
        private string _interiorLocator = "interior2, interior1, cabin";

        [Header("Paint")]
        [Tooltip("Colour for surfaces that use the game's truckpaint effect. The game paints those " +
                 "from the paint job definition and their texture is only a mask, so without a " +
                 "colour they come out white.")]
        [SerializeField]
        private Color _paintColor = new Color(0.62f, 0.08f, 0.06f, 1f);

        [Header("Presentation")]
        [Tooltip("Degrees per second of slow turn, so the whole model can be seen from one spot. " +
                 "Zero to hold still.")]
        [SerializeField]
        private float _turntableDegreesPerSecond = 0f;

        [Tooltip("In the editor, with no camera in the scene, make one that orbits the model. " +
                 "Somnium supplies its own camera and this does nothing there.")]
        [SerializeField]
        private bool _createCameraIfNone = true;

        [Header("Output")]
        [SerializeField]
        private TMP_Text _status;

        [Tooltip("Old flow: not read - the scene's truck is always built at start. New flow: also build the configured " +
                 "truck as the player's own in the client once they are in the world (a test switch; the truck normally " +
                 "comes from the tablet, and the editor always builds it).")]
        [SerializeField]
        private bool _loadOnStart = true;

        [Header("Driving")]
        [Tooltip("Make the truck drivable: a rigid body with raycast wheels set up from the game's chassis, engine, gearbox and physics definitions.")]
        [SerializeField]
        private bool _drivable = true;

        [Tooltip("Truck definition folder under /def/vehicle/truck, named brand.model.")]
        [SerializeField]
        private string _truckId = "daf.xf_euro6";

        [Tooltip("A saved vehicle listing every accessory (the game showroom preview). Sets the cabin, interior, wheels and add-ons.")]
        [SerializeField]
        private string _assemblyFile = "/def/initial_save/preview/daf_xf_euro6.sui";

        [SerializeField]
        private bool _useAssembly = true;

        [Tooltip("Take the paint colour from the saved vehicle instead of the colour above.")]
        [SerializeField]
        private bool _paintFromGame = false;

        /// <summary>The truck camera, watched for the cab/chase switch that swaps paired add-on models.</summary>
        private EtsTruckCamera _view;
        private EtsTruckCamera.Mode _shownView = (EtsTruckCamera.Mode)(-1);
        [Header("Reflections")]
        [Tooltip("A realtime reflection probe over the cab, refreshed at this interval, so chrome and paint mirror the surroundings as they do in the game.")]
        [SerializeField] private bool _reflections = true;
        [SerializeField] private float _reflectionInterval = 2f;

        private EtsTruckController _truck;
        private EtsTruckRig _rig;

        /// <summary>The drivable truck, once set up.</summary>
        public EtsTruckController Truck => _truck;

        /// <summary>The built truck (EtsTruckFactory's rig), once set up.</summary>
        public EtsTruckRig Rig => _rig;

        private GameObject _root;
        private readonly List<string> _log = new List<string>();

        /// <summary>The built model, once loaded.</summary>
        public GameObject Root => _root;

        /// <summary>The new flow's editor truck (OwnTruckRoutine); null in the client and on the old flow.</summary>
        private EtsTruckRig _editorRig;
        private bool _subscribed;

        private void OnEnable()
        {
            // Static events: EtsLocalTruck.Reset (EtsNet.OnDestroy) drops every subscriber when the visit ends,
            // and OnDisable takes this one off first whenever this object goes before 'Ets Net'.
            if (EtsFeatures.NewFlow && !_subscribed)
            {
                _subscribed = true;
                EtsLocalTruck.OwnChanged += OnOwnChanged;
            }
        }

        private void OnDisable()
        {
            if (_subscribed)
            {
                _subscribed = false;
                EtsLocalTruck.OwnChanged -= OnOwnChanged;
            }
        }

        private void Start()
        {
            // Old flow: at start, as always (see the class notes for why _loadOnStart is not read there).
            // New flow: nothing in the client unless _loadOnStart asks for it (a test switch; the scene has 0);
            // the editor's stand-in for the tablet's spawn.
            if (!EtsFeatures.NewFlow || Application.isEditor || _loadOnStart)
            {
                Load();
            }
        }

        /// <summary>
        /// Old flow: builds and wires the scene's truck (LoadRoutine). New flow: builds the configured truck as the
        /// local player's own once they are in the world (OwnTruckRoutine) - what Start does in the editor.
        /// </summary>
        public void Load()
        {
            StopAllCoroutines();
            StartCoroutine(EtsFeatures.NewFlow ? OwnTruckRoutine() : LoadRoutine());
        }

        /// <summary>
        /// The scene's truck as a factory recipe: these fields one for one, the scene's values winning
        /// over the code defaults as always (Euro Truck Scene.unity, Ets Model Viewer). The engine and
        /// gearbox stay data.sii's fallbacks (SpecFromSave off), as the scene's truck has always driven.
        /// The handle is made by SetUpDriving's own EtsTruckDoor.Create, so the recipe asks for none.
        /// </summary>
        public EtsTruckFactory.Recipe SceneRecipe()
        {
            return new EtsTruckFactory.Recipe
            {
                ModelPath = _modelPath,
                Look = _look,
                Variants = _variants,
                Wheels = _wheels,
                TireModel = _tireModel,
                TireLook = _tireLook,
                RimModel = _rimModel,
                Interior = _interior,
                InteriorModel = _interiorModel,
                InteriorVariant = _interiorVariant,
                InteriorLook = _interiorLook,
                InteriorLocator = _interiorLocator,
                Paint = _paintColor,
                PaintFromGame = _paintFromGame,
                Drivable = _drivable,
                TruckId = _truckId,
                AssemblyFile = _assemblyFile,
                UseAssembly = _useAssembly,
                SpecFromSave = false,
                Reflections = _reflections,
                ReflectionInterval = _reflectionInterval,
                Door = false
            };
        }

        private IEnumerator LoadRoutine()
        {
            _log.Clear();

            if (_root != null)
            {
                Destroy(_root);
                _root = null;
            }

            _rig = null;
            _truck = null;

            Report("Opening the game's archives ...");
            yield return null;

            var clock = Stopwatch.StartNew();

            if (!EtsEngine.Mount(_installRoot, out string failure))
            {
                Report(failure);
                yield break;
            }

            _log.Add("Mounted " + EtsEngine.Files.MountCount + " archives from " + EtsEngine.InstallRoot +
                     " in " + clock.ElapsedMilliseconds + " ms.");
            Report(string.Join("\n", _log.ToArray()));
            yield return null;

            // The truck itself. The factory writes to this viewer's log and reports where this
            // routine always reported, and it is drained here rather than started as a coroutine of
            // its own, so every yield falls in the same frame as when the building was inline.
            EtsTruckFactory.Recipe recipe = SceneRecipe();
            recipe.Log = _log;
            recipe.Report = () => Report(string.Join("\n", _log.ToArray()));
            EtsTruckRig rig = null;
            IEnumerator build = EtsTruckFactory.Build(recipe, transform, false, built => rig = built, reason => { });

            while (build.MoveNext())
            {
                yield return build.Current;
            }

            if (rig == null)
            {
                // The factory has logged and reported why.
                yield break;
            }

            _rig = rig;
            _root = rig.gameObject;
            _truck = rig.Controller;

            if (_truck != null)
            {
                SetUpDriving();
            }

            if (_createCameraIfNone && Application.isEditor && Camera.main == null && rig.Model != null)
            {
                CreateOrbitCamera(rig.Model);
            }

            _log.Add("Right-drag orbits, wheel zooms (editor camera).");
            Report(string.Join("\n", _log.ToArray()));
        }

        // ------------------------------------------------------------------ the new flow

        /// <summary>
        /// New flow: the configured truck as the local player's own - the editor's stand-in for Company > Truck
        /// Preference (PLAN §3.1 "the editor keeps working"), or the client's when _loadOnStart asks for it. Waits
        /// until the session has put the player in the world (EtsSession InWorld: the HQ choice made, the yard built,
        /// the arrival done; at once when there is no session), then builds through EtsTruckFactory:
        /// - by identity when EtsTruckCatalog lists the configured save (the DAF XF Euro 6's daf_xf_euro6.sui is
        ///   preview 1 of 18): the tablet's own entry point, so Play mode runs what the client runs - sliced, the
        ///   save's engine (SpecFromSave), the door handle, mirrors and probe - painted with the configured colour
        ///   (or the save's with _paintFromGame);
        /// - else from the scene recipe with the same new-flow settings.
        /// It is stood on the chosen yard's first spot (EtsHqCatalog: garage, then parking, then a trailer bay - PLAN
        /// §3.6), else where the old flow would stand it round the map's centre, once there is ground
        /// (EtsTruckRig.PlaceWhenGroundExists), and then becomes EtsLocalTruck.Own, which the driver kit on 'Ets Net'
        /// binds to - EtsNet.SpawnRoutine's order. Without ground it is removed, as a spawn is.
        /// </summary>
        private IEnumerator OwnTruckRoutine()
        {
            _log.Clear();

            while (EtsSession.Instance != null && EtsSession.Instance.Current != EtsPhase.InWorld)
            {
                yield return null;
            }

            if (EtsLocalTruck.Own != null)
            {
                Report("Truck: the player has a truck already (" + EtsLocalTruck.Own.name + "); the configured one is not built.");
                yield break;
            }

            if (!EtsEngine.Mount(_installRoot, out string failure))
            {
                Report(failure);
                yield break;
            }

            string stem = System.IO.Path.GetFileNameWithoutExtension(_assemblyFile ?? "");
            EtsTruckCatalog catalog = EtsTruckCatalog.Current;

            if (catalog == null && _useAssembly)
            {
                // 21 ms, plus the locale's 35 ms when nothing has loaded it (probe 1862); the build starts next frame.
                catalog = EtsTruckCatalog.Load(EtsEngine.Files);
                yield return null;
            }

            int index = _useAssembly && catalog != null ? catalog.IndexOf(stem) : -1;
            EtsTruckRig rig = null;
            string why = null;
            IEnumerator build;

            if (index >= 0)
            {
                EtsTruckIdentity id = catalog.Identity(stem, _paintFromGame ? EtsTruckIdentity.SavePaint : Rgb(_paintColor));
                _log.Add("Truck: the configured " + stem + " (preview " + index + " of " + catalog.Trucks.Count + ") through EtsTruckFactory's identity entry point.");
                build = EtsTruckFactory.Build(id, transform, false, built => rig = built, reason => why = reason);
            }
            else
            {
                EtsTruckFactory.Recipe recipe = SceneRecipe();
                recipe.SpecFromSave = true;
                recipe.Door = true;
                recipe.FrameBudgetMs = EtsTruckFactory.SlicedFrameBudgetMs;
                _log.Add("Truck: " + (_assemblyFile ?? _modelPath) + " is not a preview truck; built from the scene's configuration.");
                build = EtsTruckFactory.Build(recipe, transform, false, built => rig = built, reason => why = reason);
            }

            while (build.MoveNext())
            {
                yield return build.Current;
            }

            if (rig == null || rig.Controller == null)
            {
                if (rig != null)
                {
                    Destroy(rig.gameObject);
                }

                _log.Add("Truck: " + (why ?? "the factory built no drivable truck") + ".");
                Report(string.Join("\n", _log.ToArray()));
                yield break;
            }

            Vector3 position = rig.transform.position;
            float yaw = rig.transform.eulerAngles.y;
            string where = "on the road";

            if (!YardSpot(out Vector3 spot, out float spotYaw, out string spotWhere))
            {
                FindStartSpot(ref position, ref yaw, ref where);
            }
            else
            {
                position = spot;
                yaw = spotYaw;
                where = spotWhere;
            }

            // Placed first and made the own truck after, in EtsNet.SpawnRoutine's order: the kit binds to a truck that
            // stands on the ground (B's seat parks it and shows its door only once it is placed).
            bool placed = false;
            yield return rig.PlaceWhenGroundExists(position, yaw, SeatHasPlayer(), 20f, ok => placed = ok);

            if (!placed || rig == null || rig.Controller == null)
            {
                if (rig != null)
                {
                    Destroy(rig.gameObject);
                }

                _log.Add("Truck: no ground ever appeared under " + position.ToString("0.0") + " (" + where + ") within 30 s; nothing was dropped through the map.");
                Report(string.Join("\n", _log.ToArray()));
                yield break;
            }

            if (EtsLocalTruck.Own != null)
            {
                // The tablet's truck arrived while this one was being built (Play mode with the fallback pages).
                Destroy(rig.gameObject);
                _log.Add("Truck: the player chose a truck meanwhile; the configured one is not kept.");
                Report(string.Join("\n", _log.ToArray()));
                yield break;
            }

            // Set before SetOwn, so OnOwnChanged knows this one as the editor's and keeps it.
            _editorRig = rig;
            _rig = rig;
            _root = rig.gameObject;
            _truck = rig.Controller;
            EtsLocalTruck.SetOwn(rig);
            _log.Add("Truck " + where + " at " + position.ToString("0.0") + ", the player's own. W/S drive, A/D steer, Space brake, C camera.");
            Report(string.Join("\n", _log.ToArray()));
        }

        /// <summary>
        /// New flow: the truck the local player owns changed (A's spawn or despawn, or the editor's truck). What the
        /// per-player singletons on 'Ets Net' cannot know is handed over here, because this object holds the scene's
        /// status text: the truck's speed line and the job line go into it as on the old flow (SetUpDriving), and the menu
        /// takes its font from the truck's line. Only unset slots are filled, so an owner that binds its own wins.
        /// The headset's speed plate (EtsVrDash) is NOT attached here: D's EtsRouteAdvisor on 'Ets Net' attaches one to
        /// the truck the local player sits in (EtsRouteAdvisor.Bind, on OccupiedChanged), at the same place over the
        /// wheel; a second one from here gave the own truck two plates in one spot (z-fighting text) once the player
        /// got in (C2 checker, Phase 2).
        /// The editor's truck stops being this viewer's once it is no longer the own truck: EtsNet.RemoveOwn (a
        /// despawn, or the tablet spawning another) sets Own to null and then destroys it itself, so here it is only
        /// forgotten; a rig put in its place without that step (no such caller today) would leave two trucks, so the
        /// editor's is removed then.
        /// </summary>
        private void OnOwnChanged(EtsTruckRig rig)
        {
            if (!ReferenceEquals(_editorRig, null) && !ReferenceEquals(rig, _editorRig))
            {
                EtsTruckRig replaced = _editorRig;
                _editorRig = null;
                _rig = null;
                _root = null;
                _truck = null;

                if (rig != null && replaced != null)
                {
                    Destroy(replaced.gameObject);
                }
            }

            if (rig == null || rig.Controller == null)
            {
                return;
            }

            EtsTruckController truck = rig.Controller;

            if (_status != null)
            {
                if (truck.Hud == null)
                {
                    truck.Hud = _status;
                }

                EtsJobs jobs = EtsJobs.Instance;

                if (jobs != null && jobs.Hud == null)
                {
                    jobs.Hud = _status;
                }
            }
        }

        /// <summary>The chosen HQ yard's first truck stand (EtsHqCatalog, PLAN §3.6), in Unity coordinates round the map's centre.</summary>
        private static bool YardSpot(out Vector3 position, out float yaw, out string where)
        {
            position = default;
            yaw = 0f;
            where = null;
            EtsSession session = EtsSession.Instance;
            EtsMapViewer mapViewer = EtsMapViewer.Current;

            if (session == null || !session.HasChoice || mapViewer == null ||
                !EtsHqCatalog.TryGetCity(session.Choice.City, out EtsHqCity city) || city.Spots == null || city.Spots.Length == 0)
            {
                return false;
            }

            EtsHqSpot first = city.Spots[0];
            position = EtsMapBuilder.ToWorld(new[] { first.GameX, first.GameY, first.GameZ }, mapViewer.CenterGame);
            yaw = first.Yaw;
            where = first.Type == EtsPrefabDescriptor.SpawnGarage ? "in the " + city.Name + " yard's garage" :
                    first.Type == EtsPrefabDescriptor.SpawnParking ? "in the " + city.Name + " yard's parking bay" : "at the " + city.Name + " yard's trailer bay";
            return true;
        }

        /// <summary>The local seat on 'Ets Net' (EtsTruckSeat.Ensure) has a Somnium player: stand the truck parked, to be clicked into.</summary>
        private static bool SeatHasPlayer()
        {
            EtsNet net = EtsNet.Instance;
            EtsTruckSeat seat = net != null ? net.GetComponent<EtsTruckSeat>() : null;
            return seat != null && seat.HasPlayer;
        }

        /// <summary>A colour as the identity's 0xRRGGBB (EtsTruckFactory.FromPreview turns it back, to 1/255).</summary>
        private static int Rgb(Color colour)
        {
            int r = Mathf.RoundToInt(Mathf.Clamp01(colour.r) * 255f), g = Mathf.RoundToInt(Mathf.Clamp01(colour.g) * 255f), b = Mathf.RoundToInt(Mathf.Clamp01(colour.b) * 255f);
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// Makes the built truck the local player's: a camera that follows, the mirrors' view, sound,
        /// jobs, the menu, the front end, the seat, the wheel grab, the door handle, the network and the
        /// VR dash, all bound to this one truck (PLAN §3.1: from Phase 2 each of these is a per-player
        /// singleton that rebinds on EtsLocalTruck instead). The truck is dropped onto the ground once
        /// the map has been built, in the bay the game itself would start in.
        /// </summary>
        private void SetUpDriving()
        {
            _truck.Hud = _status;

            var camera = gameObject.GetComponent<EtsTruckCamera>();

            if (camera == null)
            {
                camera = gameObject.AddComponent<EtsTruckCamera>();
            }

            camera.Truck = _truck;
            _view = camera;

            // A headset's eyes are in the cab (EtsTruckSeat), so the mirrors and the interior
            // sound, which both follow this mode, are the cab's too.
            if (EtsVr.Present)
            {
                camera.View = EtsTruckCamera.Mode.Interior;
            }

            // The factory hung the mirrors and the reflection probe on the truck; which view shows
            // the mirrors is this camera's.
            if (_rig.Mirrors != null)
            {
                _rig.Mirrors.View = camera;
            }

            // Sound, jobs and the menu ride on this object too.
            var audio = gameObject.GetComponent<EtsTruckAudio>() ?? gameObject.AddComponent<EtsTruckAudio>();
            audio.Truck = _truck;
            audio.Camera = camera;
            var jobs = gameObject.GetComponent<EtsJobs>() ?? gameObject.AddComponent<EtsJobs>();
            jobs.Truck = _truck;
            jobs.Hud = _status;
            var menu = gameObject.GetComponent<EtsMenu>() ?? gameObject.AddComponent<EtsMenu>();
            menu.Truck = _truck;
            menu.Camera = camera;
            menu.Audio = audio;
            menu.Jobs = jobs;

            // The front end: the game's own loading screen, from /ui/loading.sii over one of the
            // game's own photographs, and then straight to the job board. There is no launchpad -
            // see the note in EtsFrontEnd for why it was taken out.
            // The route advisor - the game's sat nav, bottom right - reads the job and the route.
            var advisor = gameObject.GetComponent<EtsRouteAdvisor>() ?? gameObject.AddComponent<EtsRouteAdvisor>();
            advisor.Truck = _truck;
            advisor.Jobs = jobs;

            var frontEnd = gameObject.GetComponent<EtsFrontEnd>() ?? gameObject.AddComponent<EtsFrontEnd>();
            frontEnd.Menu = menu;
            frontEnd.Truck = _truck;
            frontEnd.Jobs = jobs;
            frontEnd.Advisor = advisor;
            frontEnd.TruckRoot = _root;

            // In Somnium, the local player rides in the driver seat; in the editor this finds no player and stays quiet.
            var seatComponent = gameObject.GetComponent<EtsTruckSeat>();

            if (seatComponent == null)
            {
                seatComponent = gameObject.AddComponent<EtsTruckSeat>();
            }

            seatComponent.Truck = _truck;
            menu.Seat = seatComponent;

            // In a headset the wheel can be taken in the hands (grip on the rim); the seat drives it.
            var wheelGrab = gameObject.GetComponent<EtsSteeringGrab>() ?? gameObject.AddComponent<EtsSteeringGrab>();
            wheelGrab.Truck = _truck;
            wheelGrab.Seat = seatComponent;
            seatComponent.Wheel = wheelGrab;

            // Getting in is clicking the truck (or pointing at it and pulling the trigger in VR).
            EtsTruckDoor.Create(gameObject, _truck, seatComponent, menu);

            // In a room, the truck is shared: one driver writes it, everybody else draws it (EtsNet).
            EtsNet.Attach(gameObject, _truck, seatComponent, menu, jobs, _status != null ? _status.font : null);

            // In a headset the status line has no screen corner to sit in; speed, gear and job go
            // on a plate above the wheel instead. Nothing is built without a headset.
            EtsVrDash.Attach(_truck, jobs, _status != null ? _status.font : null);

            // The seat EtsTruckSeat has always been given: the truck's driver_0 locator. The factory
            // reads the same point (the left driver* locator, EtsTruckSeats.Find) into
            // _truck.DriverSeatLocal, which B's seat moves to (SeatLocalFor) in Phase 2.
            if (_rig.Locators.TryGetValue("driver_0", out Transform driverSeat))
            {
                seatComponent.SeatLocal = _root.transform.InverseTransformPoint(driverSeat.position);
            }

            StartCoroutine(PlaceWhenGroundExists());
        }

        /// <summary>
        /// How far AHEAD along a trailer point's own axis the game stands the tractor, so it sits
        /// where it would couple rather than on top of the trailer. Measured from the 1.61 demo's
        /// own game.log.txt, "[actor] Initialized at [14994.0;46.8;-15235.2]": in the frame of the
        /// first type-1 point of /prefab/tradeaux_warehouse/tradeaux.ppd - the TransiNet yard in
        /// Szczecin, the one the world picks - that is +13.68 m along the axis, 0.03 m across and
        /// 0.01 m in height.
        ///
        /// This replaced -18.34 m, measured on 1.48 at euroacres in Vienna. That reading's SIGN was
        /// an artefact: the spawn quaternion was read x y z w instead of w first, which turns most
        /// points round 180 degrees, so "behind" was really "ahead". Its magnitude was a different
        /// job's trailer. The distance does depend on the trailer being coupled, so a job with a
        /// different trailer may sit off by a metre or two; deriving it from the trailer's and the
        /// truck's own coupling locators is not done yet.
        /// </summary>
        private const float TrailerCouplingOffset = 13.68f;

        private IEnumerator PlaceWhenGroundExists()
        {
            // The map builds over a second or two; wait for it, then stand the truck on the road
            // nearest the centre, facing along it.
            float deadline = Time.time + 20f;

            while (!EtsMapViewer.AnyCompleted && Time.time < deadline)
            {
                yield return null;
            }

            yield return new WaitForFixedUpdate();

            Vector3 position = _root.transform.position;
            float yaw = _root.transform.eulerAngles.y;
            string where = "on the road";
            FindStartSpot(ref position, ref yaw, ref where);

            // NOTHING is placed until there is ground under it. The world streams in round the
            // truck, so the yard chosen above may not have been built yet when this runs; setting
            // the truck down anyway is what dropped it through the map. So: wait for a ray to find
            // something solid under the spot, and only then put the truck on it. If the ground
            // never arrives, the truck stays where it already stood rather than falling.
            bool grounded = false;

            // In Somnium the player starts on foot and gets in by clicking the truck (EtsTruckSeat),
            // so it is set down parked: frozen, braked, solid. In the editor there is no avatar to
            // walk, the truck stays driven, and nothing changes there.
            var seat = GetComponent<EtsTruckSeat>();

            if (seat != null && seat.HasPlayer)
            {
                _truck.SetDriven(false);
            }

            for (int attempt = 0; attempt < 120 && !grounded; attempt++)
            {
                if (Physics.Raycast(position + Vector3.up * 200f, Vector3.down, 600f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    grounded = _truck.PlaceOnGround(position, yaw);
                }

                if (!grounded)
                {
                    yield return new WaitForSeconds(0.25f);
                }
            }

            if (grounded)
            {
                _log.Add("Truck " + where + " at " + position.ToString("0.0") + ". W/S drive, A/D steer, Space brake, C camera.");
            }
            else
            {
                _log.Add("Truck: no ground ever appeared under " + position.ToString("0.0") +
                         " (" + where + "); left where it was rather than dropped through the map.");
            }

            Report(string.Join("\n", _log.ToArray()));
        }

        /// <summary>
        /// Where the old flow stands its truck (and the new flow's editor truck when the session names no yard):
        /// the nearest trailer bay, parking bay or garage to the map's centre, else the nearest long road. Moved out
        /// of PlaceWhenGroundExists unchanged (Phase 2), so both flows share it; `position`, `yaw` and `where` keep
        /// what the caller gave them when the map is not open or nothing is found.
        /// </summary>
        private static void FindStartSpot(ref Vector3 position, ref float yaw, ref string where)
        {
            EtsMapViewer mapViewer = EtsMapViewer.Current;

            if (mapViewer != null && mapViewer.Map != null)
            {
                float best = float.MaxValue;
                Vector3 centre = mapViewer.CenterGame;

                // Start where the GAME stands a truck: a prefab spawn point.
                //
                // Euro Truck Simulator does not put a vehicle down on a map node. Every prefab
                // descriptor carries a table of spawn points - position, facing and what each one
                // is for - and the game uses the one matching what it is placing. Type 13 is where
                // a truck parks at a company, 10 a parking bay, 14 a garage. They sit at y = 0 in
                // the prefab's own frame, which means they are ON the prefab's paved surface, so
                // put through the prefab's own transform they land on ground that exists.
                //
                // The previous attempt used company.SpawnPoints[0], a map NODE uid, and assumed a
                // truck could stand on it. It could not: the truck was dropped into empty air and
                // fell out of the world. This reads the same table the game reads.
                // NOT the company point. Type 13 is the company's MAP MARKER - the spot the game
                // stands the Company ITEM on, which is why every one of the 33 companies round the
                // start has its own node sitting exactly on its type-13 point, to 0.00 m. Standing
                // the truck there put it on the marker rather than in the yard.
                //
                // The game starts a job on a type-1 point - the trailer's loading place - and stands
                // the tractor ahead of it along that point's own axis so it can couple. Measured from
                // the 1.61 demo's game.log.txt, "[actor] Initialized at [14994.0;46.8;-15235.2]":
                // with the descriptor read at its real 36-byte stride and w-first rotation, this walk
                // picks the TransiNet yard's first trailer bay, 13.5 m from the centre, and the actor
                // resolves to +13.68 m along it, 0.03 m across, 0.01 m in height. (Read the old way -
                // 32-byte stride - only three spots survived in 3 km and the nearest was a hotel's
                // parking bay 393 m away.) Type 1 first, then a parking or garage bay if none.
                int[] wanted = { EtsPrefabDescriptor.SpawnTrailer, EtsPrefabDescriptor.SpawnParking, EtsPrefabDescriptor.SpawnGarage };

                foreach (EtsMapSector sector in mapViewer.Map.SectorsAround(centre.x, centre.z, 3000f))
                {
                    foreach (EtsMapPrefab prefab in sector.Prefabs)
                    {
                        if (!EtsMapBuilder.TryPrefabSpawn(mapViewer.Map, prefab, centre, wanted,
                                out Vector3 spot, out float spotYaw, out int type))
                        {
                            continue;
                        }

                        float d = spot.sqrMagnitude;

                        if (d < best)
                        {
                            best = d;

                            // Ahead along the trailer point's own axis, the way the game does it, so
                            // the tractor stands where it would couple rather than on the trailer.
                            if (type == EtsPrefabDescriptor.SpawnTrailer)
                            {
                                spot += Quaternion.Euler(0f, spotYaw, 0f) * Vector3.forward * TrailerCouplingOffset;
                            }

                            position = spot;
                            yaw = spotYaw;
                            where = type == EtsPrefabDescriptor.SpawnTrailer ? "in a company loading bay" :
                                    type == EtsPrefabDescriptor.SpawnGarage ? "in a garage" : "in a parking bay";
                        }
                    }
                }

                // No prefab spawn point in range: the nearest road will do.
                if (best == float.MaxValue)
                {
                    foreach (EtsMapSector sector in mapViewer.Map.SectorsAround(centre.x, centre.z, 400f))
                    {
                        foreach (EtsMapRoad road in sector.Roads)
                        {
                            EtsMapNode node = mapViewer.Map.FindNode(road.StartNode);
                            if (node == null || road.Length < 40f) continue;
                            float dx = node.Position[0] - centre.x, dz = node.Position[2] - centre.z;
                            float d = dx * dx + dz * dz;

                            if (d < best)
                            {
                                best = d;
                                Vector3 forward = EtsMapBuilder.ToWorldDirection(node.Forward());
                                position = EtsMapBuilder.ToWorld(node.Position, centre) + forward * 20f;
                                yaw = Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y;
                            }
                        }
                    }
                }
            }
        }

        private void CreateOrbitCamera(EtsModelData model)
        {
            var cameraObject = new GameObject("Ets Viewer Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 2000f;
            camera.clearFlags = CameraClearFlags.Skybox;

            Vector3 center = _root.transform.TransformPoint(
                (EtsModelBuilder.ToUnity(model.BoundsMin) + EtsModelBuilder.ToUnity(model.BoundsMax)) * 0.5f);
            float radius = (EtsModelBuilder.ToUnity(model.BoundsMax) - EtsModelBuilder.ToUnity(model.BoundsMin)).magnitude * 0.5f;

            var orbit = cameraObject.AddComponent<EtsOrbitCamera>();
            orbit.Target = center;
            orbit.Distance = Mathf.Max(3f, radius * 2.2f);

            if (FindAnyObjectByType<Light>() == null)
            {
                var lightObject = new GameObject("Ets Viewer Light");
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                lightObject.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            }
        }

        private void Update()
        {
            if (_root != null && _turntableDegreesPerSecond != 0f && _truck == null)
            {
                _root.transform.Rotate(0f, _turntableDegreesPerSecond * Time.deltaTime, 0f, Space.World);
            }

            // Accessories with both an exterior and an interior model show one or the other, by
            // where the camera is - the game never draws both, and both at once doubled the mirrors.
            if (_rig != null && _rig.Assembly != null && _view != null && _view.View != _shownView)
            {
                _shownView = _view.View;
                _rig.ShowFromInside(_shownView == EtsTruckCamera.Mode.Interior);
            }
        }

        private void Report(string message)
        {
            if (_status != null)
            {
                _status.text = message;
            }

            Debug.Log("[SomniumEuroTruck] " + message);
        }
    }


    /// <summary>
    /// A small editor-only orbit camera for looking at a loaded model: right mouse drag turns,
    /// the wheel zooms, middle drag pans. Somnium's own rig replaces it in the world.
    /// </summary>
    public class EtsOrbitCamera : MonoBehaviour
    {
        public Vector3 Target;
        public float Distance = 10f;
        public float Yaw = 35f;
        public float Pitch = 15f;

        private void LateUpdate()
        {
            if (Input.GetMouseButton(1))
            {
                Yaw += Input.GetAxis("Mouse X") * 3f;
                Pitch = Mathf.Clamp(Pitch - Input.GetAxis("Mouse Y") * 3f, -80f, 80f);
            }

            if (Input.GetMouseButton(2))
            {
                Target -= transform.right * Input.GetAxis("Mouse X") * Distance * 0.02f;
                Target -= transform.up * Input.GetAxis("Mouse Y") * Distance * 0.02f;
            }

            Distance = Mathf.Clamp(Distance * (1f - Input.mouseScrollDelta.y * 0.1f), 0.5f, 500f);

            Quaternion rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            transform.position = Target - rotation * Vector3.forward * Distance;
            transform.rotation = rotation;
        }
    }
}
