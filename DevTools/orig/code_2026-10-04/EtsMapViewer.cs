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
    /// A piece of the game's Europe, read out of the map sectors and built in the world: roads
    /// bent along their splines from the game's own road models, junctions and yards stood on
    /// their nodes, buildings and props where the map puts them.
    ///
    /// Nothing is shipped: sectors, definitions and models all come from the player's archives
    /// at runtime. A centre in the game's coordinates is placed at Unity's origin, and everything
    /// within the radius is built around it, a few items per frame so the world stays responsive.
    ///
    /// The default centre is a T-junction in the first sector of the demo map, chosen because it
    /// is the place the sector format was decoded against and every node of it was checked by
    /// hand. Terrain is not built yet; a flat ground plane stands in for it.
    /// </summary>
    public class EtsMapViewer : MonoBehaviour
    {
        [Header("Euro Truck Simulator 2")]
        [Tooltip("Install folder. Leave empty to search every Steam library on this machine.")]
        [SerializeField]
        private string _installRoot = "";

        [Header("Where")]
        [Tooltip("Centre of the built area, in the game's own coordinates (metres; x east, z south). " +
                 "It is placed at Unity's origin.\n\n" +
                 "The default is WHERE THE GAME ITSELF STARTS YOU. Read out of the demo's own log " +
                 "while a fresh profile took its first job: \"[actor] Initialized at " +
                 "[14994.0;46.8;-15235.2]\" - the TransiNet yard in Szczecin. The start follows the " +
                 "profile's HQ city, picked when the profile is made (this player's 1.61 save: " +
                 "hq_city szczecin; their 1.48 profile had Vienna). (\"Wizard skipped.\" in that log " +
                 "is the controller wizard, not the HQ choice.)\n\n" +
                 "The figures below were measured on the 1.48 map and are kept for reference:\n" +
                 "  Stuttgart  (-4637, 78, 10039)  88 %\n" +
                 "  Frankfurt  (-5584, 65, 2497)   84 %\n" +
                 "  Kiel       (1137, 9, -21609)   82 %\n" +
                 "  Linz       (13636, 66, 13905)  79 %\n" +
                 "  Calais     (-30340, 42, -5006) 77 %\n" +
                 "  Erfurt     (2824, 45, -2033)   74 %\n" +
                 "  Hannover   (-2004, 9, -9085)   71 %\n" +
                 "  Hamburg    (-1690, 19, -16356) 69 %\n" +
                 "Open country is far sparser - a rural depot measured 28 % - so start in a city.")]
        [SerializeField]
        private Vector3 _centerGame = new Vector3(14994f, 47f, -15235f);

        [Tooltip("Build everything whose node lies within this many metres of the centre.")]
        [Range(100f, 6000f)]
        [SerializeField]
        private float _radius = 1200f;

        [Header("What")]
        [SerializeField]
        private bool _roads = true;

        [SerializeField]
        private bool _prefabs = true;

        [SerializeField]
        private bool _models = true;

        [Tooltip("The ground: terrain strips beside roads and the map's terrain items, from the sector .data files.")]
        [SerializeField]
        private bool _terrain = true;

        [Tooltip("Trees and bushes from the vegetation bands of road sides and terrain items.")]
        [SerializeField]
        private bool _vegetation = true;

        [Tooltip("Small details: the map's sign items (street lamps, traffic signs, boards), the lamps and signs prefabs carry, and working traffic lights.")]
        [SerializeField]
        private bool _details = true;

        [Header("Streaming")]
        [Tooltip("After the first build, keep building the map around this transform as it moves. Empty: the truck, once there is one.")]
        [SerializeField]
        private Transform _follow;

        [SerializeField]
        private bool _stream = true;

        [Tooltip("Metres around the followed transform within which the SMALL items are built: small models, signs and lamps, fences and poles (a model or sign also stops at the map's own view distance for it). Never less than the build radius above. The large items go out to the far radius below.")]
        [SerializeField]
        private float _streamRadius = 1200f;

        [Tooltip("Metres around the followed transform within which the LARGE items are built: roads, terrain, junctions and yards, house rows, and models or signs at least 8 m tall or 25 m across. The game's fog ends at about 2.2 km (EtsClimate), so the land reaches into it instead of stopping at a visible edge. Never less than the stream radius above.")]
        [Range(100f, 6000f)]
        [SerializeField]
        private float _farRadius = 2150f;

        [Tooltip("Metres beyond which a LARGE item is removed again; every item gets the same margin past its own build radius (this minus the far radius, at least 200 m), so the road you just drove is not torn up behind you and nothing is built and removed over and over at the edge.")]
        [SerializeField]
        private float _unloadRadius = 2600f;

        [Tooltip("Milliseconds of streaming work per frame WHILE DRIVING: taking the worker thread's reads (texture uploads), building, batching and removing items. One step always runs. Raised to the frame ms below while ground within 600 m of the truck is still missing, so it never drives onto a hole.")]
        [Range(0.5f, 20f)]
        [SerializeField]
        private float _streamBudgetMs = 2.5f;

        [Tooltip("Seconds of travel: the stream builds first what lies nearest to where the truck will be this far ahead (its speed times this, along its heading), and keeps what lies within reach of that point.")]
        [Range(0f, 15f)]
        [SerializeField]
        private float _leadSeconds = 6f;

        [Tooltip("Read every model, texture, material and prefab descriptor an item needs on a worker thread BEFORE it is built (the load and the stream), so a frame only uploads and builds. Off: the builds read on the frame, as before.")]
        [SerializeField]
        private bool _prefetch = true;

        [Tooltip("Items built per frame WHILE DRIVING. Small, so streaming a new chunk in never stutters the drive; the one-time load at the start uses the larger figure below.")]
        [Range(1, 60)]
        [SerializeField]
        private int _streamItemsPerFrame = 6;

        [Tooltip("Milliseconds of streaming per frame WHILE DRIVING when ground within 600 m of the truck is still missing (otherwise the stream budget above applies): a frame ends at this or at the item count above, whichever comes first. In a headset a frame is 11 ms in all.")]
        [Range(1f, 100f)]
        [SerializeField]
        private float _streamFrameMs = 4f;

        private struct BuiltItem
        {
            public GameObject Object;
            public float X, Z;

            /// <summary>Removed once both the followed point and the point ahead are further than this, squared (UnloadSquared).</summary>
            public float UnloadSquared;
        }

        private readonly Dictionary<ulong, BuiltItem> _built = new Dictionary<ulong, BuiltItem>();

        /// <summary>
        /// Items that cannot be built at all, so streaming stops trying. The demo's map still names
        /// models from content it does not ship (dlc_balt_824, rus_1z003 and the like), and their
        /// descriptors resolve to nothing. Without this the streamer retried every one of them
        /// twice a second for ever: about 190 failures a pass, for no result.
        /// </summary>
        private readonly HashSet<ulong> _unbuildable = new HashSet<ulong>();

        /// <summary>True when an item is already built, or known to be unbuildable.</summary>
        private bool Known(ulong uid)
        {
            return _built.ContainsKey(uid) || _unbuildable.Contains(uid);
        }

        /// <summary>Each built item's root object (by instance id) to its map uid, for <see cref="DescribeMapItem"/>.</summary>
        private readonly Dictionary<int, ulong> _uidByRoot = new Dictionary<int, ulong>();

        /// <summary>
        /// Which map item a scene object belongs to - "map item 002935E3FA80A5F2 'building bld_model.77'" -
        /// found by walking up to the built item's root; null for anything the map did not build (the
        /// truck, the ground fill, the garage). For the truck's blocked-report (EtsTruckController).
        /// </summary>
        public static string DescribeMapItem(Transform part)
        {
            EtsMapViewer viewer = Current;

            if (viewer == null)
            {
                return null;
            }

            for (Transform t = part; t != null; t = t.parent)
            {
                if (viewer._uidByRoot.TryGetValue(t.gameObject.GetInstanceID(), out ulong uid))
                {
                    return "map item " + uid.ToString("X16") + " '" + t.name + "'";
                }
            }

            return null;
        }

        [Tooltip("Cap on placed models, nearest first. A city sector alone holds thousands.")]
        [SerializeField]
        private int _maxModels = 3000;

        [Tooltip("Mesh colliders on roads and prefabs, so a truck can drive on them.")]
        [SerializeField]
        private bool _colliders = true;

        [Tooltip("A flat plane under everything, at the lowest node height, until terrain is built.")]
        [SerializeField]
        private bool _groundPlane = true;

        [Tooltip("Fill the ground the map leaves out. It only describes terrain beside roads and terrain items, which is 28 to 72 per cent of the land depending on where you stand; the rest is open sky from any height.")]
        [SerializeField]
        private bool _groundFill = true;

        [Header("Presentation")]
        [Tooltip("Items built per frame while loading.")]
        [Range(1, 200)]
        [SerializeField]
        private int _itemsPerFrame = 25;

        [Tooltip("Milliseconds of building per frame while loading, at most (one item always goes in): a frame ends at this or at the item count above, whichever comes first, so the loading screen keeps moving. One slow item can still take longer on its own.")]
        [Range(4f, 1000f)]
        [SerializeField]
        private float _loadFrameMs = 12f;

        [Tooltip("In the editor, with no camera in the scene, make one that flies: WASD, right " +
                 "mouse to look, shift for speed. Somnium supplies its own camera in the world.")]
        [SerializeField]
        private bool _createCameraIfNone = true;

        [Header("Output")]
        [SerializeField]
        private TMP_Text _status;

        [SerializeField]
        private bool _loadOnStart = true;

        [Header("Rendering")]
        [Tooltip("SomniumEuroTruck/Decal: lane lines and dirt decals fade by vertex alpha as in the game. Referenced here so the world bundle ships it.")]
        [SerializeField]
        private Shader _decalShader;

        [Tooltip("SomniumEuroTruck/Multi: the game's two-layer surfaces (mult/amod/iamod) lay a second texture over the first through uv1. A third of the materials in view use one. Referenced here so the world bundle ships it.")]
        [SerializeField]
        private Shader _multiShader;

        [Tooltip("SomniumEuroTruck/Sky: the climate profile's own HDR sky panorama, fog colour below the horizon. Referenced here so the world bundle ships it, like the decal and two-layer shaders.")]
        [SerializeField]
        private Shader _skyShader;

        [Tooltip("SomniumEuroTruck/Leaves: the game's leaves effect - camera-facing crown cards, wind from the vertex colour, colour = red x 2, the game's 0.05 alpha discard. Referenced here so the world bundle ships it. Empty (and not found): trees keep URP Lit, with a grey tint from the vertex colour's red only.")]
        [SerializeField]
        private Shader _leavesShader;

        [Tooltip("How far the wind sways a leaf card at full strength, metres. The game's amplitude is an engine constant not in its files; 0.08-0.15 m stays comfortable in a headset.")]
        [Range(0f, 0.5f)]
        [SerializeField]
        private float _windAmplitude = 0.12f;

        [Tooltip("Set Unity's fog from the game's nice-weather climate profile instead of the scene's template fog.")]
        [SerializeField]
        private bool _applyClimate = true;

        [Tooltip("Unity's fog mode for the game's fog (EtsClimate). Linear, from the game's fog_offset to the fitted end, follows the game's exp2 curve best. The world bundle keeps only the fog variants of the mode the scene is saved with (Lighting > Environment > Fog on, Mode Linear; keep Graphics > Fog Modes on Automatic); when the scene's fog is on, its mode is used instead of this one.")]
        [SerializeField]
        private FogMode _fogMode = FogMode.Linear;

        [Tooltip("The game's tone curve, colour grading and bloom as a URP post-processing Volume (EtsPostFx). Off: the client's untonemapped picture.")]
        [SerializeField]
        private bool _postFx = true;

        [Tooltip("Extra exposure in EV on top of the climate's own (0 = the game's). Raise a little if the whole picture reads too dark.")]
        [Range(-2f, 2f)]
        [SerializeField]
        private float _postExposure;

        // The fog the bundle can draw (integrator, 2026-09-23). Graphics > Fog Modes is Automatic (the Somnium SDK's
        // setting), so the bundle build keeps only the fog shader variants of the mode the world's scene is SAVED
        // with, and none at all when the scene's fog is off (Editor.log: Multi/Decal/Leaves Forward 16 -> 4 at
        // built-in stripping). The scene's own fog is read once, the moment its render settings first take over
        // (Awake if it is already active, else when the client activates it), before anything here writes them;
        // when it is on, its mode is the one used, whatever _fogMode says, so a scene saved in another mode still fogs.
        private bool _sceneFogRead;
        private bool _sceneFogOn;
        private FogMode _sceneFogMode = FogMode.Linear;

        /// <summary>_fogMode, unless the world's scene was saved with fog on in another mode (see _sceneFogRead).</summary>
        private FogMode FogModeInUse => _sceneFogRead && _sceneFogOn ? _sceneFogMode : _fogMode;

        private GameObject _root;

        // AnyCompleted, LoadProgress and Current are properties with private setters, as PLAN.md §7.F lists them:
        // the whole world reads them and only the map viewer may write them (Phase 0; they were public fields).

        /// <summary>Set once any map viewer has finished building, so the truck knows there is ground.</summary>
        public static bool AnyCompleted { get; private set; }

        /// <summary>How far the first build has got, 0 to 1, for the game's loading screen.</summary>
        public static float LoadProgress { get; private set; }

        /// <summary>What the build is doing, in words, for the loading screen to say.</summary>
        public static string LoadStage = "Opening the archives";

        /// <summary>The most recently started map viewer.</summary>
        public static EtsMapViewer Current { get; private set; }

        public Vector3 CenterGame => _centerGame;
        private EtsMap _map;
        private readonly List<string> _log = new List<string>();

        /// <summary>The built map, once loaded.</summary>
        public GameObject Root => _root;

        /// <summary>The loaded map, for anything that wants to ask it about nodes.</summary>
        public EtsMap Map => _map;

        /// <summary>The builder's options, so other components can convert positions the same way.</summary>
        public EtsMapBuilder.Options BuildOptions { get; private set; }

        // ---------------------------------------------------------------- opening and loading (PLAN.md §7.F)

        // Mounting the archives (Open) is split from building round a centre (LoadAt), so the HQ stage can
        // show the map before anyone has chosen where to go (PLAN.md §5.5: Open() at scene start, LoadAt(origin)
        // from EtsSession). While the scene's _loadOnStart is 1, Start still calls Load(), which opens and builds
        // in exactly the frames it always has: the "Opening" report, one frame, the mount, the climate and the
        // report, one frame, then the sectors (LoadRoutine).

        /// <summary>Whether the archives are mounted and the map index read: <see cref="Map"/> is non-null.</summary>
        public bool IsOpen => _map != null;

        /// <summary>The climate Open applied (the game's nice-weather profile, see EtsClimate), or null. For late readers of <see cref="ClimateApplied"/>.</summary>
        public EtsClimate Climate { get; private set; }

        /// <summary>
        /// Mounts the game's archives, reads the map index and applies the climate - builds nothing.
        /// Synchronous, so <see cref="Map"/> is set when it returns (EtsMapIndex and the HQ catalogue read it
        /// straight away). It costs what the first frames of every load always cost: the client log shows
        /// the 18 archives of the 1.61 demo mounted in 146-345 ms (Player.log, "Mounted 18 archives"). Does
        /// nothing when already open. On a failure the reason is reported: when the archives could not be
        /// mounted IsOpen stays false; a fault after the map index was read (the sky or the climate) leaves
        /// IsOpen true without a climate, and the report says so.
        /// </summary>
        public void Open()
        {
            if (_map != null)
            {
                return;
            }

            Current = this;
            _log.Clear();

            // Called from other components' start-up (the session, the HQ stage): a fault here is reported,
            // not thrown into them.
            try
            {
                if (OpenArchives())
                {
                    Report(string.Join("\n", _log.ToArray()));
                }
            }
            catch (Exception e)
            {
                Report("Opening the game failed: " + e.GetType().Name + ": " + e.Message + (_map != null ? " (the map index was read)" : ""));
            }
        }

        /// <summary>
        /// Sets the centre (game coordinates; it becomes Unity's origin) and builds and streams round it,
        /// opening first if nothing has. Asking again for the centre already being built does nothing; a new
        /// centre throws the old build away (its meshes freed, <see cref="AnyCompleted"/> false until the new
        /// one is ready) and builds the new one.
        /// </summary>
        public void LoadAt(Vector3 centerGame)
        {
            if (_loadStarted && (centerGame - _centerGame).sqrMagnitude < 1f)
            {
                return;
            }

            _centerGame = centerGame;
            Load();
        }

        /// <summary>
        /// Raised on the main thread each time a climate has been applied to the scene (in Open), with the
        /// viewer and the climate: the post-processing volume (EtsPostFx, Phase 4) takes its values from it.
        /// A subscriber that throws is logged and does not stop the load. Static: subscribers must remove
        /// themselves, because statics outlive a visit to the world.
        /// </summary>
        public static event Action<EtsMapViewer, EtsClimate> ClimateApplied;

        /// <summary>
        /// What to stream round instead of the truck or the walking player: the arrival point while the
        /// session is Loading (A sets it, PLAN U19; null afterwards). The ground fill follows it too.
        ///
        /// U19 checked in the code: the stream ignores height. StreamRoutine converts the focus to game x and
        /// z only (p.x + centre.x, centre.z - p.z) and every build and unload test is a horizontal distance
        /// (DistanceSquared(float[], x, z), and the unload loop's dx, dz), so a player standing in the HQ void
        /// 600 m above the yard streams the yard below. EtsGroundFill's rays, though, start 400 m above its
        /// focus and reach 1200 m down, so from the void they would start above the void's floor: another reason
        /// to set this during Loading.
        ///
        /// NonSerialized: a run-time value, and the scene must not carry one over (the scene overrides code
        /// defaults for serialized fields).
        /// </summary>
        [NonSerialized]
        public Transform StreamFocusOverride;

        /// <summary>Whether a load has been started (by Load or LoadAt) on this viewer.</summary>
        private bool _loadStarted;

        /// <summary>
        /// The statics start each visit clean. The world's assembly stays loaded between visits to the world (persist
        /// §2.3), so AnyCompleted and LoadProgress would otherwise still say "built, 100 %" from the last visit
        /// before this one has read a sector: the truck placement (EtsTruckRig.PlaceWhenGroundExists,
        /// EtsModelViewer), the traffic and the ground fill all wait on AnyCompleted for ground, and EtsSession's
        /// LoadDone on both. Current is set here so readers find this viewer from the first frame.
        /// </summary>
        private void Awake()
        {
            Current = this;
            AnyCompleted = false;
            LoadProgress = 0f;
            LoadStage = "Opening the archives";

            // Render settings are per scene: see ApplyClimate. Removed in OnDestroy (a static event).
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnActiveSceneChanged;
            HookOffscreenCameras(true);

            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene() == gameObject.scene)
            {
                ReadSceneFog();
            }
        }

        /// <summary>
        /// The world's scene's own fog, as saved (see _sceneFogRead): read once, while the scene's render settings
        /// are active and before ApplyClimate writes them, and reported, because it decides whether any fog can draw.
        /// </summary>
        private void ReadSceneFog()
        {
            if (_sceneFogRead)
            {
                return;
            }

            try
            {
                _sceneFogOn = RenderSettings.fog;
                _sceneFogMode = RenderSettings.fogMode;
                _sceneFogRead = true;

                if (_sceneFogOn)
                {
                    Debug.Log("[SomniumEuroTruck] Fog: the world's scene is saved with fog ON, mode " + _sceneFogMode +
                              " - the bundle keeps that mode's fog shader variants; the game's fog is drawn in " + _sceneFogMode +
                              (_sceneFogMode != _fogMode ? " (not the map viewer's " + _fogMode + ": its variants were stripped)." : "."));
                }
                else
                {
                    Debug.LogWarning("[SomniumEuroTruck] Fog: the world's scene is saved with fog OFF, so the bundle build stripped every fog shader " +
                                     "variant (Graphics > Fog Modes Automatic) and NO fog can draw, whatever the climate sets. Fix in Unity: " +
                                     "Lighting > Environment > Other Settings > Fog on, Mode Linear, save the scene, rebuild.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Fog: reading the scene's fog failed (" + e.GetType().Name + ": " + e.Message + "); using " + _fogMode + ".");
            }
        }

        /// <summary>
        /// The new flow (EtsFeatures.NewFlow, Phase 2): the scene's _loadOnStart is 0, so only the archives are
        /// opened here - the HQ stage, EtsMapIndex and the HQ catalogue read the map before anyone has chosen a city -
        /// and the build waits for EtsSession's LoadAt(origin). EtsSession.Start opens it too; Open is idempotent.
        ///
        /// NewFlow false (the fallback): the old front end has no one to call LoadAt, so the map loads round the
        /// scene's centre whatever _loadOnStart says - the scene carries 0 since Phase 2 (A's edit), and without this
        /// the fallback build would have no map at all. _loadOnStart 1 still loads in either flow (the editor, a test
        /// scene).
        /// </summary>
        private void Start()
        {
            // The map's movers - pedestrians, animals, birds, flags, turbines (EtsLife): one per world, follows Current.
            try
            {
                EtsLife.Attach(this);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Life could not start: " + e.GetType().Name + ": " + e.Message);
            }

            if (_loadOnStart || !EtsFeatures.NewFlow)
            {
                Load();
            }
            else
            {
                Open();
            }
        }

        private void OnDestroy()
        {
            // Static subscriptions outlive the scene: the vegetation globals stop with the map they serve.
            EtsVegetationLook.Stop();
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            HookOffscreenCameras(false);

            // The reader thread stops with the map; the statics say "no map" until the next visit's viewer builds one.
            if (_map != null)
            {
                _map.Close();
            }

            // The prefetch thread too: a background thread outlives the scene otherwise.
            if (_pf != null)
            {
                _pf.Close();
                _pf = null;
            }

            if (Current == this)
            {
                Current = null;
                AnyCompleted = false;
                LoadProgress = 0f;
            }
        }

        // ------------------------------------------------------------------ offscreen pictures without a tone curve

        private static Action<UnityEngine.Rendering.ScriptableRenderContext, Camera> _beginOffscreen;
        private static Action<UnityEngine.Rendering.ScriptableRenderContext, Camera> _endOffscreen;
        private static Camera _offscreenScaled;

        /// <summary>
        /// A camera that draws into a texture with no post-processing - the mirrors (EtsMirrors: LDR, no post), the route
        /// advisor's map (ARGB32) - sees the climate with no tone curve, clipped at 1. Since 2026-09-25 the exposure is the
        /// game's, 2.4 x the one those pictures were tuned under, which would turn their sky and sunlit ground white. So round
        /// such a camera the sun, ambient, fog colour and sky go down to EtsClimate.OffscreenScale (0.64 at 10:00: the sky
        /// stays under 1 and the picture, once the main camera's tone curve draws the glass, matches the windscreen view) and
        /// back to the phase's own after it, as EtsGarageScene does for the truck studio. Reflection-probe, scene-view and
        /// post-processed cameras are left alone. Cost: a few property reads a camera a frame; one ScaleLights before and
        /// after each such camera; nothing allocated. Subscribed for this viewer's lifetime (static events outlive the scene).
        /// </summary>
        private static void HookOffscreenCameras(bool on)
        {
            if (_beginOffscreen == null)
            {
                _beginOffscreen = BeginOffscreenCamera;
                _endOffscreen = EndOffscreenCamera;
            }

            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= _beginOffscreen;
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= _endOffscreen;
            _offscreenScaled = null;

            if (on)
            {
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += _beginOffscreen;
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += _endOffscreen;
            }
        }

        private static void BeginOffscreenCamera(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game || camera.targetTexture == null)
            {
                return;
            }

            if (camera.TryGetComponent(out UnityEngine.Rendering.Universal.UniversalAdditionalCameraData data) && data.renderPostProcessing)
            {
                return;
            }

            _offscreenScaled = camera;
            EtsClimate.ScaleLights(EtsClimate.OffscreenScale);
        }

        private static void EndOffscreenCamera(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
        {
            if (!ReferenceEquals(_offscreenScaled, null) && ReferenceEquals(camera, _offscreenScaled))
            {
                _offscreenScaled = null;
                EtsClimate.ScaleLights(EtsClimate.PhaseScale);
            }
        }

        /// <summary>
        /// Builds round the current centre (opening first if nothing has), throwing any earlier build away. From the
        /// call on, AnyCompleted is false and LoadProgress 0 until this build is done - set here, before the first
        /// frame of the load, so a caller that asks on the same frame (EtsSession's LoadDone) never sees an
        /// earlier build's "done".
        /// </summary>
        public void Load()
        {
            StopAllCoroutines();
            _loadStarted = true;
            AnyCompleted = false;
            LoadProgress = 0f;
            LoadStage = _map != null ? "Reading the map" : "Opening the archives";
            StartCoroutine(LoadRoutine());
        }

        /// <summary>
        /// The opening half of a load: shaders, the archives, the map index, the sky and the climate. The
        /// same work in the same order as the first frame of the load has always done. False when the
        /// archives could not be mounted (the reason is reported).
        /// </summary>
        private bool OpenArchives()
        {
            var clock = Stopwatch.StartNew();

            // The decal shader must be known before the first material is built (they are cached).
            if (_decalShader == null)
            {
                _decalShader = Shader.Find("SomniumEuroTruck/Decal");
            }

            if (_decalShader != null)
            {
                EtsMaterialCache.DecalShader = _decalShader;
                _log.Add("Decal shader: " + _decalShader.name + (_decalShader.isSupported ? "" : " (NOT SUPPORTED)") + ".");
            }

            if (_multiShader == null)
            {
                _multiShader = Shader.Find("SomniumEuroTruck/Multi");
            }

            if (_multiShader != null)
            {
                EtsMaterialCache.MultiShader = _multiShader;
                _log.Add("Two-layer shader: " + _multiShader.name + (_multiShader.isSupported ? "" : " (NOT SUPPORTED)") + ".");
            }
            else
            {
                _log.Add("Two-layer shader MISSING - buildings will draw with only their base texture.");
            }

            // The game's leaves effect (Phase A of the environment design): known before the first material is
            // made, like the decal and two-layer shaders, because materials are cached.
            if (_leavesShader == null)
            {
                _leavesShader = Shader.Find("SomniumEuroTruck/Leaves");
            }

            EtsMaterialCache.LeavesShader = _leavesShader;
            _log.Add(_leavesShader != null
                ? "Leaves shader: " + _leavesShader.name + (_leavesShader.isSupported ? "" : " (NOT SUPPORTED - trees stay on URP Lit)") + "; wind " + _windAmplitude.ToString("0.00") + " m."
                : "Leaves shader MISSING - trees draw with URP Lit and only their stored geometry (thin crowns, no sprites).");

            // The per-camera billboard frame and the wind for the leaves shader (EtsVegetationLook), started with
            // the shader rather than after the climate, so a fault in the sky or climate below cannot leave the
            // cards without their camera axes. Harmless without the shader: it only sets global shader values
            // nothing else reads.
            EtsVegetationLook.WindAmplitude = _windAmplitude;
            EtsVegetationLook.Start();

            if (!EtsEngine.Mount(_installRoot, out string failure))
            {
                Report(failure);
                return false;
            }

            _map = new EtsMap(EtsEngine.Files);

            if (_skyShader == null)
            {
                _skyShader = Shader.Find("SomniumEuroTruck/Sky");
            }

            EtsSky.SkyShader = _skyShader;
            _log.Add(_skyShader != null ? "Sky shader: " + _skyShader.name + (_skyShader.isSupported ? "" : " (NOT SUPPORTED)") + "." : "Sky shader MISSING - the procedural sky is kept.");

            if (_applyClimate)
            {
                // The game's own climate for the scene's centre (its sector's climate token) at the new career's
                // start time (/def/initial_save game_time 600, Monday 10:00): the sun where it stands then, the two
                // sun profiles either side of it blended, the clear-sky slot - see EtsClimate. The session clock
                // does not run (EtsSessionWire.ClockScale 0), so the light is fixed for the visit; LoadAt looks again
                // for the chosen city.
                EtsClimate climate = EtsClimate.Load(EtsEngine.Files, _centerGame.x, _centerGame.z, -1, FarRadius);

                if (climate != null)
                {
                    _log.Add(climate.ToString());
                    ApplyClimate(climate, _log, false);
                }
            }

            _log.Add("Mounted " + EtsEngine.Files.MountCount + " archives in " + clock.ElapsedMilliseconds + " ms; " + _map.SectorCount + " map sectors available.");
            return true;
        }

        /// <summary>
        /// Fog, sun, ambient and the game's sky into Unity's render settings, and the "Render settings" line
        /// into <paramref name="log"/>. Unity keeps render settings PER SCENE and writes them to the ACTIVE one.
        /// In the new flow Open runs in Start, while the client still has its loader scene active (Player.log
        /// 2026-09-23: the "Render settings ... skybox SomniumEuroTruck/Sky" line comes before "World scene is
        /// loaded and activated"), so everything went to the loader scene and was unloaded with it: no sky, no
        /// fog, the scene's own sun. <paramref name="again"/>: put the climate Open read on the active scene again
        /// (LoadAt, and the world's scene becoming active) - the panorama is reused, not decoded again.
        /// </summary>
        private void ApplyClimate(EtsClimate climate, List<string> log, bool again)
        {
            climate.Apply(EtsClimate.FindSun(), FogModeInUse);
            log.Add(again ? EtsSky.Reapply(EtsEngine.Files, climate, RenderSettings.sun, transform) : EtsSky.Apply(EtsEngine.Files, climate, RenderSettings.sun, transform));
            Climate = climate;
            ApplyPost(climate, log);

            // For the next client log: what Unity actually ended up with.
            UnityEngine.Rendering.SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
            Light sun = RenderSettings.sun;
            log.Add("Render settings: climate '" + climate.Climate + "' " + climate.Profile + " slot " + climate.Slot + " at " + climate.ClockText() +
                    " (sun " + climate.Elevation.ToString("0.0") + " deg up, azimuth " + climate.Azimuth.ToString("0") + ", exposure x" + climate.Exposure.ToString("0.0000") +
                    "); fog " + RenderSettings.fogMode + (RenderSettings.fogMode == FogMode.Linear
                        ? " " + RenderSettings.fogStartDistance.ToString("0") + "-" + RenderSettings.fogEndDistance.ToString("0") + " m"
                        : " " + RenderSettings.fogDensity.ToString("0.000000")) +
                    " colour " + RenderSettings.fogColor.ToString("0.00") + " (game: " + climate.GameFog(300f).ToString("0%") + " at 300 m, " + climate.GameFog(1000f).ToString("0%") +
                    " at 1 km; Unity " + climate.UnityFog(300f, RenderSettings.fogMode).ToString("0%") + " / " + climate.UnityFog(1000f, RenderSettings.fogMode).ToString("0%") +
                    "); ambient " + RenderSettings.ambientMode +
                    " probe DC " + sh[0, 0].ToString("0.000") + "/" + sh[1, 0].ToString("0.000") + "/" + sh[2, 0].ToString("0.000") + " " + climate.AmbientFitNote +
                    (EtsClimate.NearFieldAmbient ? ", near-field x" + EtsClimate.AmbientNear.ToString("0.0") + " within " + EtsClimate.AmbientNearRange.ToString("0") + " m (world shaders)" : ", no near-field factor") +
                    "; reflection intensity " + RenderSettings.reflectionIntensity.ToString("0.##") + "; fog colour " + (EtsClimate.DirectionalFog ? "by the view's angle to the sun (world shaders)" : "Unity's one") +
                    "; sun " + (sun != null ? sun.intensity.ToString("0.00") + " " + sun.color.ToString("0.00") + " shadow " + sun.shadowStrength.ToString("0.00") +
                                              " pointing " + sun.transform.forward.ToString("0.00") : "none") +
                    "; skybox " + (RenderSettings.skybox != null ? RenderSettings.skybox.shader.name : "none") + "; far clip " + EtsClimate.Visibility.ToString("0") + " m.");

            UnityEngine.SceneManagement.Scene active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            log.Add("Render settings written to the active scene '" + active.name + "'" +
                    (active == gameObject.scene ? " (the world's)." : " - NOT the world's scene '" + gameObject.scene.name + "': set again when it becomes active and when the city is built."));

            RaiseClimateApplied(climate);
        }

        /// <summary>
        /// The game's tone curve, grading and bloom (EtsPostFx) from the climate: a global Volume under this
        /// viewer, so it lives in the world's scene whichever scene is active. Its weight is EtsFlow's
        /// (EtsPostFx.PhaseWeight: 0 in the HQ stage and loading screens).
        /// </summary>
        private void ApplyPost(EtsClimate climate, List<string> log)
        {
            if (!_postFx)
            {
                log.Add("Look: post volume off (the map viewer's _postFx).");
                return;
            }

            try
            {
                EtsPostFx.PostExposure = _postExposure;
                EtsPostFx fx = EtsPostFx.Create(transform, climate, UnityEngine.XR.XRSettings.enabled);
                log.Add(fx != null ? fx.Summary : "Look: post volume not made.");
            }
            catch (Exception e)
            {
                log.Add("Look: post volume failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// The climate again, on the active scene, reported at once (LoadAt, the world's scene activated).
        /// <paramref name="recentre"/>: the build's centre is chosen - look up the climate there (its sector's
        /// climate, latitude and so the sun) and apply that one if it differs from the one Open read.
        /// </summary>
        private void ReapplyClimate(string why, bool recentre = false)
        {
            if (!_applyClimate || Climate == null)
            {
                return;
            }

            try
            {
                var lines = new List<string>();
                EtsClimate climate = Climate;
                bool fresh = false;

                if (recentre && EtsEngine.Files != null)
                {
                    EtsClimate here = EtsClimate.Load(EtsEngine.Files, _centerGame.x, _centerGame.z, climate.GameMinutes, FarRadius);

                    if (here != null && here.Key != climate.Key)
                    {
                        climate = here;
                        fresh = true;
                        lines.Add(here.ToString());
                    }
                }

                ApplyClimate(climate, lines, !fresh);
                Debug.Log("[SomniumEuroTruck] Map: climate set again (" + why + ").\n" + string.Join("\n", lines.ToArray()));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Map: setting the climate again (" + why + ") failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The client activates the world's scene after Start: its render settings then take over.</summary>
        private void OnActiveSceneChanged(UnityEngine.SceneManagement.Scene previous, UnityEngine.SceneManagement.Scene next)
        {
            if (this != null && next == gameObject.scene)
            {
                ReadSceneFog();
                ReapplyClimate("the world's scene became active");
            }
        }

        private void RaiseClimateApplied(EtsClimate climate)
        {
            Action<EtsMapViewer, EtsClimate> handlers = ClimateApplied;

            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<EtsMapViewer, EtsClimate>)handler)(this, climate);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Map: a ClimateApplied handler failed (" + handler.Method.DeclaringType?.Name + "." + handler.Method.Name + "): " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// Forgets a previous build before a new centre is built: its runtime meshes freed (the models' shared
        /// ones kept, EtsModelBuilder.IsShared, as the stream's unload does), its bookkeeping cleared, and the
        /// ground fill told to measure again. Only runs when there was a build - never on the first load.
        /// </summary>
        private void ForgetBuild()
        {
            foreach (MeshFilter filter in _root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && !EtsModelBuilder.IsShared(filter.sharedMesh) && !EtsSemaphores.IsSharedMesh(filter.sharedMesh)) Destroy(filter.sharedMesh);
            }

            _built.Clear();
            _uidByRoot.Clear();
            ResetStream();

            // An item refused round the old centre may have been refused for a neighbour sector not read yet (a road
            // whose far node lay beyond the read ring); round the new centre it gets one more try.
            _unbuildable.Clear();
            AnyCompleted = false;
            LoadProgress = 0f;
            LoadStage = "Reading the map";

            EtsGroundFill fill = GetComponent<EtsGroundFill>();

            if (fill != null)
            {
                fill.Invalidate();
            }
        }

        private IEnumerator LoadRoutine()
        {
            bool opened = _map != null;

            if (!opened)
            {
                _log.Clear();
            }

            if (_root != null)
            {
                ForgetBuild();
                Destroy(_root);
                _root = null;
            }

            Current = this;

            if (!opened)
            {
                Report("Opening the game's archives ...");
                yield return null;

                // Open() may have run in the frame just yielded: with _loadOnStart set and NewFlow on, Start begins this
                // routine and EtsSession.Start then calls Open. A second OpenArchives would replace the EtsMap that
                // EtsMapIndex and the HQ catalogue already hold (every sector read twice, into two caches) and raise
                // ClimateApplied twice.
                if (_map == null && !OpenArchives())
                {
                    yield break;
                }

                Report(string.Join("\n", _log.ToArray()));
                yield return null;
            }
            else
            {
                // The new flow: Open ran in Start, before the client activated the world's scene, so its climate
                // and sky went to the loader scene (see ApplyClimate). Put them on the world's scene now that the
                // city is built - the HQ void is an unlit black sphere and stays black round the player. The climate
                // is looked up again for the chosen city (its sector's climate, its latitude and so the sun).
                ReapplyClimate("the city is built round game (" + _centerGame.x.ToString("0") + ", " + _centerGame.z.ToString("0") + ")", true);
            }

            // The sectors within the radius + 500 m (the far node of a road or strip that starts inside the radius
            // must be read too), on the map's reader thread (EtsMap.ReadAhead): a frame never waits on a sector.
            // They are all read before the first item is built, as before - only no longer inside one frame. When
            // the whole-map index has already passed this city (the master's usual case, EtsMapIndex) they are all
            // read and this takes no frame at all.
            var clock = Stopwatch.StartNew();
            var sectors = new List<EtsMapSector>();
            int frames = 0;

            while (!_map.TryReadAround(_centerGame.x, _centerGame.z, _radius + 500f, sectors, out int ready, out int total))
            {
                LoadProgress = 0.10f * ready / Mathf.Max(1, total);
                LoadStage = "Reading the map";
                frames++;
                yield return null;
            }

            int declared = 0, found = 0, nodes = 0;

            foreach (EtsMapSector sector in sectors)
            {
                declared += sector.DeclaredItems;
                found += sector.FoundItems;
                nodes += sector.Nodes.Count;
            }

            _log.Add("Read " + sectors.Count + " sectors round game (" + _centerGame.x.ToString("0") + ", " + _centerGame.z.ToString("0") + ") in " + clock.ElapsedMilliseconds + " ms over " + frames +
                     " frames" + (frames > 0 ? " (" + (_map.ReaderThreaded ? "reader thread" : "NO reader thread: one sector a frame") + ")" : " (all read already)") + ": " +
                     found + " of " + declared + " items, " + nodes + " nodes." + ReadFailureNote());
            Report(string.Join("\n", _log.ToArray()));
            yield return null;

            BuildOptions = new EtsMapBuilder.Options
            {
                CenterGame = _centerGame,
                Roads = _roads,
                Prefabs = _prefabs,
                Models = _models,
                Colliders = _colliders,
                MaxModels = _maxModels,
                Terrain = _terrain,
                Vegetation = _vegetation,
                Details = _details
            };

            _root = new GameObject("Ets Map");
            _root.transform.SetParent(transform, false);

            // The small details (traffic lights, prefab lamps and signs) are built a few a frame by the driver on this
            // object, which also runs the traffic lights; it says when its queue has drained.
            if (_details)
            {
                EtsDetailDriver.Ensure(gameObject);
                EtsDetailDriver.Drained = LogDetails;
            }

            var stats = new EtsMapBuilder.Stats();
            float radiusSquared = _radius * _radius;
            float minY = float.MaxValue;
            int budget = _itemsPerFrame;
            clock.Restart();
            _loadFrames = 0;
            TakeItemCost();
            LoadFrameStarted();
            _parents.Clear();

            // Nearest first, so the model cap drops the far ones (worked out here, before the prefetch, which asks for them in this order).
            List<KeyValuePair<float, EtsMapModel>> modelCandidates = _models ? NearestModels(sectors, radiusSquared) : null;

            // Everything the builds below read from the archives - models, textures, material files, prefab descriptors - asked
            // of the prefetch thread now, in the order they are built, so the reads run beside the builds instead of inside them
            // (a single item used to hold a frame for up to 1179 ms, "prefab 11bus", Player.log 2026-09-23). Each item then waits
            // for its own reads (LoadWaits) - the loading screen keeps drawing - and is built once they are in.
            _loadNeeds.Clear();
            _loadWaited = 0;
            _loadTimedOut = 0;
            int postedBefore = 0;

            if (_prefetch)
            {
                if (_pf == null)
                {
                    _pf = new Prefetch(_map) { WarmVegetation = _vegetation };
                }

                postedBefore = _pf.Posted;
                IEnumerator asking = AskForLoad(sectors, radiusSquared, modelCandidates);

                while (asking.MoveNext())
                {
                    yield return null;
                    LoadFrameStarted();
                }
            }

            // Roads first: they are the skeleton, and the thing a truck needs.
            if (_roads)
            {
                var roadsParent = new GameObject("roads").transform;
                roadsParent.SetParent(_root.transform, false);

                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapRoad road in sector.Roads)
                    {
                        EtsMapNode start = _map.FindNode(road.StartNode);

                        if (start == null || !Within(start.Position, radiusSquared))
                        {
                            continue;
                        }

                        minY = Mathf.Min(minY, start.Position[1]);
                        for (float since = -1f; LoadWaits(road.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                        Mark();
                        Register(road, KindRoad, EtsMapBuilder.BuildRoad(_map, road, BuildOptions, roadsParent, stats), start.Position);

                        if (LoadFrameFull(ref budget))
                        {
                            LoadProgress = 0.10f + 0.25f * Mathf.Clamp01(stats.Roads / 400f); LoadStage = "Building roads";
                            ReportProgress("roads", stats);
                            yield return null;
                            LoadFrameStarted();
                        }
                    }
                }
            }

            // Terrain items: ground between two nodes where no road runs.
            if (_terrain)
            {
                var terrainParent = new GameObject("terrain items").transform;
                terrainParent.SetParent(_root.transform, false);

                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapBezierPatch patch in sector.BezierPatches)
                    {
                        EtsMapNode node = _map.FindNode(patch.NodeUid);

                        if (node == null || !Within(node.Position, radiusSquared))
                        {
                            continue;
                        }

                        for (float since = -1f; LoadWaits(patch.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                        Mark();
                        Register(patch, KindPatch, EtsTerrainBuilder.BuildBezierPatch(_map, patch, BuildOptions, terrainParent, stats), node.Position);
                    }

                    foreach (EtsMapTerrainItem item in sector.Terrains)
                    {
                        EtsMapNode start = _map.FindNode(item.StartNode);

                        if (start == null || !Within(start.Position, radiusSquared))
                        {
                            continue;
                        }

                        for (float since = -1f; LoadWaits(item.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                        Mark();
                        Register(item, KindTerrain, EtsTerrainBuilder.BuildTerrainItem(_map, item, BuildOptions, terrainParent, stats), start.Position);

                        if (LoadFrameFull(ref budget))
                        {
                            LoadProgress = 0.35f + 0.20f * Mathf.Clamp01(stats.TerrainItems / 320f); LoadStage = "Building terrain";
                            ReportProgress("terrain", stats);
                            yield return null;
                            LoadFrameStarted();
                        }
                    }
                }
            }

            if (_prefabs)
            {
                var prefabsParent = new GameObject("prefabs").transform;
                prefabsParent.SetParent(_root.transform, false);

                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapPrefab prefab in sector.Prefabs)
                    {
                        EtsMapNode node = prefab.NodeUids.Length > 0 ? _map.FindNode(prefab.NodeUids[0]) : null;

                        if (node == null || !Within(node.Position, radiusSquared))
                        {
                            continue;
                        }

                        minY = Mathf.Min(minY, node.Position[1]);
                        for (float since = -1f; LoadWaits(prefab.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                        Mark();
                        Register(prefab, KindPrefab, EtsMapBuilder.BuildPrefab(_map, prefab, BuildOptions, prefabsParent, stats), node.Position);

                        if (LoadFrameFull(ref budget))
                        {
                            LoadProgress = 0.55f + 0.10f * Mathf.Clamp01(stats.Prefabs / 110f); LoadStage = "Building junctions and yards";
                            ReportProgress("prefabs", stats);
                            yield return null;
                            LoadFrameStarted();
                        }
                    }
                }
            }

            if (_models)
            {
                var modelsParent = new GameObject("models").transform;
                modelsParent.SetParent(_root.transform, false);

                // Nearest first, so the cap drops the far ones (NearestModels, worked out before the prefetch).
                List<KeyValuePair<float, EtsMapModel>> candidates = modelCandidates;
                int placed = 0;

                foreach (KeyValuePair<float, EtsMapModel> candidate in candidates)
                {
                    if (placed++ >= _maxModels)
                    {
                        stats.Notes.Add("model cap of " + _maxModels + " reached; " + (candidates.Count - _maxModels) + " left out.");
                        break;
                    }

                    EtsMapNode modelNode = _map.FindNode(candidate.Value.NodeUid);
                    for (float since = -1f; LoadWaits(candidate.Value.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                    Mark();
                    Register(candidate.Value, KindModel, EtsMapBuilder.BuildModel(_map, candidate.Value, BuildOptions, modelsParent, stats), modelNode != null ? modelNode.Position : null);

                    if (LoadFrameFull(ref budget))
                    {
                        LoadProgress = 0.65f + 0.30f * Mathf.Clamp01(stats.Models / 1650f); LoadStage = "Placing buildings and props";
                        ReportProgress("models", stats);
                        yield return null;
                        LoadFrameStarted();
                    }
                }

                // Runs: a model repeated along the curve between two nodes. Crash barriers,
                // fences, walls, kerbs, powerlines and painted stripes - the detail that makes a
                // road look like a road rather than a ribbon through empty ground.
                var runsParent = new GameObject("runs").transform;
                runsParent.SetParent(_root.transform, false);

                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapBuilding item in sector.Buildings)
                    {
                        EtsMapNode node = _map.FindNode(item.StartNode);

                        if (node == null || DistanceSquared(node.Position) > radiusSquared)
                        {
                            continue;
                        }

                        for (float since = -1f; LoadWaits(item.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                        Mark();
                        Register(item, KindBuilding, EtsRunBuilder.BuildBuilding(_map, item, BuildOptions, runsParent, stats), node.Position);

                        if (LoadFrameFull(ref budget))
                        {
                            LoadStage = "Placing fences and barriers";
                            ReportProgress("runs", stats);
                            yield return null;
                            LoadFrameStarted();
                        }
                    }

                    foreach (EtsMapCurve item in sector.Curves)
                    {
                        EtsMapNode node = _map.FindNode(item.NodeUid);

                        if (node == null || DistanceSquared(node.Position) > radiusSquared)
                        {
                            continue;
                        }

                        for (float since = -1f; LoadWaits(item.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                        Mark();
                        Register(item, KindCurve, EtsRunBuilder.BuildCurve(_map, item, BuildOptions, runsParent, stats), node.Position);

                        if (LoadFrameFull(ref budget))
                        {
                            LoadStage = "Placing fences and barriers";
                            ReportProgress("runs", stats);
                            yield return null;
                            LoadFrameStarted();
                        }
                    }
                }
            }

            // Signs: the map's street lamps, traffic signs, boards and bins (sign items, type 36), each on its node, out
            // to its own view distance. Left out of static batching: they share their meshes with every other copy.
            if (_details)
            {
                var signsParent = new GameObject("signs").transform;
                signsParent.SetParent(_root.transform, false);

                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapSign item in sector.Signs)
                    {
                        EtsMapNode node = _map.FindNode(item.NodeUid);

                        if (node == null)
                        {
                            continue;
                        }

                        float d = DistanceSquared(node.Position);

                        if (d > radiusSquared || d > ViewDistanceSquared(item))
                        {
                            continue;
                        }

                        for (float since = -1f; LoadWaits(item.Uid, ref since);) { yield return null; LoadFrameStarted(); }
                        Mark();
                        Register(item, KindSign, EtsSignBuilder.BuildSign(_map, item, BuildOptions, signsParent), node.Position, false);

                        if (LoadFrameFull(ref budget))
                        {
                            LoadStage = "Placing signs and lamps";
                            ReportProgress("signs", stats);
                            yield return null;
                            LoadFrameStarted();
                        }
                    }
                }
            }

            if (_groundPlane && !_terrain)
            {
                float y = minY == float.MaxValue ? _centerGame.y : minY;
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "ground (stand-in for terrain)";
                ground.transform.SetParent(_root.transform, false);
                ground.transform.position = new Vector3(0f, y - _centerGame.y - 0.3f, 0f);
                ground.transform.localScale = Vector3.one * (_radius * 2f / 10f);
                Material groundMaterial = EtsEngine.Materials.Load(GroundMaterialPath());

                if (groundMaterial != null)
                {
                    ground.GetComponent<MeshRenderer>().sharedMaterial = groundMaterial;
                }
            }

            _log.Add("Built " + stats + " in " + clock.ElapsedMilliseconds + " ms over " + _loadFrames + " frames" + TakeItemCost() + "; " + EtsEngine.Materials.Count + " materials, " + EtsEngine.Textures.Count + " textures " +
                     "(per-vertex colour on " + EtsMaterialCache.VertexAoPieces + " pieces, " + EtsMaterialCache.VertexAoMaterials + " two-layer twins); " +
                     (_pf != null && _prefetch
                         ? "prefetch " + (_pf.Threaded ? "on a worker thread" : "on the frame (NO thread)") + ": " + (_pf.Posted - postedBefore) + " reads asked, " + _loadWaited + " items waited for them, " +
                           _loadTimedOut + " built without them after " + LoadWaitLimit.ToString("0") + " s" + _pf.TakeFailures()
                         : "prefetch off") + ".");
            _loadNeeds.Clear();
            _log.Add("Ground: " + EtsTerrainBuilder.PlainMeshes + " single-brush meshes, " + EtsTerrainBuilder.PairMeshes + " blended-pair meshes covering " +
                     EtsTerrainBuilder.BlendedQuads + " quads" + (EtsTerrainBuilder.PairMeshes == 0 ? " - NO blending; the two-layer shader was not used for terrain" : "") +
                     "; collision meshes with degenerate triangles taken out " + EtsTerrainBuilder.CollidersCleaned + ", left with none " + EtsTerrainBuilder.CollidersEmpty + ".");
            _log.Add(EtsTerrainBuilder.Diagnose());
            _log.Add("Prefabs stood on node 0's full rotation (EtsMapBuilder.TryPrefabFrame): " + EtsMapBuilder.PrefabsTilted + " tilted, " +
                     EtsMapBuilder.PrefabsLevel + " level, " + EtsMapBuilder.PrefabsTiltRefused + " kept yaw-only (the tilt missed their own map nodes).");
            _log.Add("Runs: " + EtsRunBuilder.Report() + "; invisible ground quads built as collision " + EtsTerrainBuilder.InvisibleQuads + "; skirts under open terrain edges " + EtsTerrainBuilder.SkirtSegments + " (" + EtsTerrainBuilder.SkirtsNotNeeded + " edges met or over a road).");
            _log.Add(EtsMapBuilder.RoadsReport());

            foreach (string note in EtsRunBuilder.Notes)
            {
                _log.Add("  " + note);
            }

            foreach (string note in stats.Notes)
            {
                _log.Add("  " + note);
            }

            if (_details)
            {
                // Its own line (the report above is one long message); once more when the prefab details have drained.
                LogDetails();
            }

            LoadProgress = 1f;

            LoadStage = "Ready";

            AnyCompleted = true;

            if (_stream)
            {
                StartCoroutine(StreamRoutine());
            }

            // The map only describes terrain beside roads and terrain items: measured on the demo,
            // that is 72 % of the ground at the map centre but 28 % round the Tree-ET depot. The
            // game hides the rest behind its own terrain and fog, seen from a cab; from any height
            // it is open sky. Fill what the files leave out.
            if (_groundFill)
            {
                EtsGroundFill fill = gameObject.GetComponent<EtsGroundFill>() ?? gameObject.AddComponent<EtsGroundFill>();
                fill.Follow = _follow;
                // Out to where large items stream (into the fog), not the 1.2 km load radius: the fill is
                // 250 m tiles built nearest first under a per-frame budget (EtsGroundFill).
                fill.Extent = FarRadius;
            }

            if (_createCameraIfNone && Application.isEditor && NoScreenCamera())
            {
                CreateFlyCamera();
                _log.Add("Fly camera: WASD moves, right mouse looks, shift is fast, Q/E down/up.");
            }

            Report(string.Join("\n", _log.ToArray()));
        }

        /// <summary>
        /// Static-batches an item so its many small meshes draw as a few: roads, shoulders, terrain
        /// strips and models. Plants are left out - they share one mesh per species, and batching
        /// would copy that mesh into every road. So is any other piece drawn with the leaves shader (a
        /// map model that names a vegetation_model, EtsMapBuilder.BuildModel): its mesh bounds are
        /// grown by the billboard size (EtsModelBuilder.LeavesMeshStreams) and it reads uv set 2, and a
        /// combined mesh recomputes its bounds from the stored vertices, which for a sprite LOD are a
        /// strip 1 cm wide.
        /// </summary>
        private static void CombineStatic(GameObject root)
        {
            var parts = new List<GameObject>();
            Shader leaves = EtsMaterialCache.LeavesShader;

            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
            {
                bool plant = leaves != null && renderer.sharedMaterial != null && renderer.sharedMaterial.shader == leaves;

                for (Transform t = renderer.transform; !plant && t != null && t != root.transform; t = t.parent)
                {
                    if (t.name.StartsWith("plant ", StringComparison.Ordinal)) { plant = true; break; }

                    // A prefab's details (EtsSemaphores, EtsSignBuilder) are built after this runs, but a stream
                    // pass can batch a prefab again; their lamps switch on and off and they share their meshes.
                    if (t.name.StartsWith("detail ", StringComparison.Ordinal)) { plant = true; break; }
                }

                if (!plant) parts.Add(renderer.gameObject);
            }

            if (parts.Count > 1)
            {
                StaticBatchingUtility.Combine(parts.ToArray(), root);
            }
        }

        // ---------------------------------------------------------------- load frames

        /// <summary>Frames the current build has taken so far (for the "Built" line).</summary>
        private int _loadFrames;

        /// <summary>Time spent building in the current frame of the load.</summary>
        private readonly Stopwatch _loadFrameClock = new Stopwatch();

        /// <summary>
        /// Whether this frame of the first build has done enough: _itemsPerFrame items, or _loadFrameMs of work,
        /// whichever comes first (at least one item always). Resets the item count when it says yes.
        /// </summary>
        private bool LoadFrameFull(ref int budget)
        {
            if (--budget > 0 && _loadFrameClock.Elapsed.TotalMilliseconds < _loadFrameMs)
            {
                return false;
            }

            budget = _itemsPerFrame;
            return true;
        }

        /// <summary>A new frame of the first build begins (after its yield).</summary>
        private void LoadFrameStarted()
        {
            _loadFrames++;
            _loadFrameClock.Restart();
        }

        // ---------------------------------------------------------------- per-item cost, for the log

        /// <summary>When the item being built started (Stopwatch ticks), set by Mark just before each build call.</summary>
        private long _itemMark;

        /// <summary>The slowest item since the last report (build plus static batching, ms), its name, and how many took 50 ms or more.</summary>
        private double _slowestItemMs;
        private string _slowestItem = "";
        private int _slowItems;

        private void Mark()
        {
            _itemMark = Stopwatch.GetTimestamp();
        }

        /// <summary>"; slowest item 212 ms (prefab 002935..., 'prefab 7'), 3 of them 50 ms or more" - and the counters start again.</summary>
        private string TakeItemCost()
        {
            string text = "; slowest item " + _slowestItemMs.ToString("0") + " ms (" + _slowestItem + "), " + _slowItems + " took 50 ms or more";
            _slowestItemMs = 0;
            _slowestItem = "";
            _slowItems = 0;
            return text;
        }

        /// <summary>
        /// Remembers a built item so streaming neither rebuilds nor forgets it, removed again past its own unload radius
        /// (UnloadSquared). <paramref name="combine"/> false leaves the item out of static batching - a small detail (a sign,
        /// a lamp) whose meshes are shared with every other copy, where batching would only copy them per item.
        /// </summary>
        private void Register(EtsMapItem item, byte kind, GameObject built, float[] gamePosition, bool combine = true)
        {
            Register(item.Uid, built, gamePosition, combine, UnloadSquared(item, kind), false);
        }

        /// <summary>
        /// As above. <paramref name="deferCombine"/>: the static batching waits for a later frame of the stream with time
        /// left (_combineQueue), so a big item's build and its batching never land in the same frame.
        /// </summary>
        private void Register(ulong uid, GameObject built, float[] gamePosition, bool combine, float unloadSquared, bool deferCombine)
        {
            if (built == null || gamePosition == null)
            {
                // It failed once, so it will fail every time. Remember it, or streaming spends
                // every pass rebuilding the same missing models.
                _unbuildable.Add(uid);
                Cost(uid, null);
                return;
            }

            if (combine)
            {
                if (deferCombine)
                {
                    _combineQueue.Enqueue(built);
                }
                else
                {
                    CombineStatic(built);
                }
            }

            _built[uid] = new BuiltItem { Object = built, X = gamePosition[0], Z = gamePosition[2], UnloadSquared = unloadSquared };
            _uidByRoot[built.GetInstanceID()] = uid;
            Cost(uid, built);
        }

        /// <summary>The detail counts, when they changed since the last time: "[SomniumEuroTruck] Details: ...".</summary>
        private string _lastDetails = "";

        private void LogDetails()
        {
            string line = EtsSignBuilder.DetailsLine();

            if (line != _lastDetails)
            {
                _lastDetails = line;
                Debug.Log(line);
            }
        }

        /// <summary>
        /// What an item cost the frame: its build (since Mark) and its static batching. A load frame is _itemsPerFrame
        /// of these and a streaming frame _streamItemsPerFrame, so the slowest ones are what a player feels as a hitch.
        /// </summary>
        private void Cost(ulong uid, GameObject built)
        {
            double ms = (Stopwatch.GetTimestamp() - _itemMark) * 1000.0 / Stopwatch.Frequency;

            if (ms >= 50.0)
            {
                _slowItems++;
            }

            if (ms > _slowestItemMs && _itemMark != 0)
            {
                _slowestItemMs = ms;
                _slowestItem = uid.ToString("x16") + (built != null ? " '" + built.name + "'" : " (not built)");
            }
        }

        private Transform ParentFor(string name)
        {
            if (_parents.TryGetValue(name, out Transform cached) && cached != null)
            {
                return cached;
            }

            Transform parent = _root.transform.Find(name);

            if (parent == null)
            {
                parent = new GameObject(name).transform;
                parent.SetParent(_root.transform, false);
            }

            _parents[name] = parent;
            return parent;
        }

        // ---------------------------------------------------------------- streaming: smooth, ahead of the truck, out to the fog

        // Why this was rewritten (the final pass, 2026-09-23). The client log of that evening (VR) shows the stream building
        // each item on the main thread with every read its build made inside that frame: 76 items of one drive took 50 ms or
        // more, most of them 83 to 336 ms ("model 3000b" 263 ms, "road look6" 336 ms, "prefab 121" 302 ms), and the first
        // build's worst was 1179 ms ("prefab 11bus"). Harness Program5550 puts it on the textures: read + GDeflate + surface
        // layout 10.8 ms median, 75 ms p90, 224 ms worst (151 textures round the UK start, 4.0 s in all), against 0.3 ms median
        // and 22 ms worst for a model's read and parse. And everything stopped at 1200 m while the game's fog now ends at
        // 2183 m, so the edge of the world stood in plain view. Now:
        //  - every read an item needs (its models, their looks' material files and textures, a prefab's descriptor, the
        //    terrain brushes and the plants along it) is asked of the prefetch thread first (Prefetch), and the item is built
        //    only when they are in; a frame takes the finished reads (the texture uploads) and builds under a time budget;
        //  - the queue is ordered by distance to where the truck will be _leadSeconds from now, the road ahead first and the
        //    ground before the props on it;
        //  - large items (roads, terrain, prefabs, house rows, big models) are built out to _farRadius, into the fog, small ones
        //    to the stream radius as before; each is removed at its own radius plus a margin, not at one ring for all;
        //  - the scan of what is in range is spread over frames, and static batching waits for a frame with time left.
        // Measured round the two starts (harness Program5551, these rules): the tiered radii keep 3,144 items where 1,200 m for all
        // kept 2,767 round the UK start (+14 %: roads 101 -> 141, terrain items 42 -> 67, prefabs 32 -> 38, tall runs out to
        // 2.15 km, small runs and fences within 1.2 km), and 1,960 where it kept 1,606 round Szczecin (+22 %). The map's own view
        // distances keep the models almost unchanged (391 -> 394 instances, 151 -> 153 textures, +2 MB of 154 MB), so the growth
        // is ground, roads and houses.

        private const byte KindRoad = 0, KindPatch = 1, KindTerrain = 2, KindPrefab = 3, KindModel = 4, KindBuilding = 5, KindCurve = 6, KindSign = 7;

        /// <summary>How long the first build lets an item wait for its reads before building it anyway (reading on the frame, as before).</summary>
        private const float LoadWaitLimit = 8f;

        /// <summary>How long the stream lets an item wait for its reads before building it anyway.</summary>
        private const float StreamWaitLimit = 10f;

        /// <summary>The stream asks for more reads only while fewer than this many are outstanding, so the nearest items' reads never queue behind far ones.</summary>
        private const int StreamMaxInFlight = 48;

        /// <summary>Candidates looked at per frame from the first one not yet built: their reads asked, their readiness checked.</summary>
        private const int StreamWindow = 48;

        /// <summary>Handed back by a scan pass that must wait a frame (sectors still being read): the frame stops stepping it.</summary>
        private static readonly object ScanWait = new object();

        private Prefetch _pf;
        private readonly Dictionary<ulong, List<Prefetch.Need>> _loadNeeds = new Dictionary<ulong, List<Prefetch.Need>>();
        private int _loadWaited, _loadTimedOut;
        private Func<bool> _loadDrainMore, _streamDrainMore;
        private int _drainFrame = -1;

        private readonly Dictionary<string, Transform> _parents = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Queue<GameObject> _combineQueue = new Queue<GameObject>();

        /// <summary>One map item as the stream sees it: where its node is, how far out it is built and kept, the reads it waits for.</summary>
        private sealed class StreamEntry
        {
            public EtsMapItem Item;
            public byte Kind;
            public ulong Node;
            public float X = float.NaN, Z;
            public float BuildSquared, UnloadSquared;
            public float Key;
            public List<Prefetch.Need> Needs;
            public float AskedAt;
            public bool Waited;
        }

        private static readonly Comparison<StreamEntry> ByKey = (a, b) => a.Key.CompareTo(b.Key);

        private readonly Dictionary<EtsMapSector, List<StreamEntry>> _streamIndex = new Dictionary<EtsMapSector, List<StreamEntry>>();
        private List<StreamEntry> _candidates = new List<StreamEntry>();
        private List<StreamEntry> _nextCandidates = new List<StreamEntry>();
        private int _candHead;
        private readonly List<ulong> _unloadList = new List<ulong>();
        private int _unloadHead;
        private readonly List<EtsMapSector> _readSectors = new List<EtsMapSector>();
        private readonly List<EtsMapSector> _scanSectors = new List<EtsMapSector>();
        private readonly Stopwatch _streamClock = new Stopwatch();
        private float _streamFrameBudget;
        private int _framesWithoutBuild;

        // The followed point and the point ahead of it (game frame), and the heading (game frame, unit length) while moving.
        private float _gx, _gz, _ax, _az, _dirX, _dirZ;
        private bool _moving;
        private Vector3 _focusLast, _velocity;
        private float _focusTime = -1f;
        private int _inRange;
        private EtsTruckSeat _seat;
        private EtsTruckController _truck;
        private float _targetFoundAt = -10f;

        // Since the last "Streamed" line.
        private int _repAdded, _repRemoved, _repWaited, _repTimedOut, _repPostedAt, _repUploadsAt, _repCombined, _repFailures;
        private double _repWorstFrameMs, _repWorstCombineMs;
        private float _repSince;

        /// <summary>Small items: the stream radius, never less than the first build's radius.</summary>
        private float NearRadius => Mathf.Max(_streamRadius, _radius);

        /// <summary>Large items: the far radius, never less than the small items' one.</summary>
        private float FarRadius => Mathf.Max(_farRadius, NearRadius);

        /// <summary>How far past its own build radius an item is kept (hysteresis): the unload radius minus the far radius, at least 200 m.</summary>
        private float UnloadMargin => Mathf.Max(200f, _unloadRadius - FarRadius);

        /// <summary>
        /// Whether an item is built out to the far radius. Roads, terrain and prefabs always: they are the land. A model, run or
        /// sign by the size of its own bounding box in the sector (the k-DOP, whose first three axes are x, y and z): a model 8 m
        /// tall or 25 m across (buildings, halls, bridges, rock faces), a run 6 m tall (a row of houses, not a fence - a run's box
        /// is as long as the run, so only its height counts), a curve or sign 8 m tall (power lines, gantries). A box that is not
        /// plausible counts as small.
        /// </summary>
        private static bool IsLarge(EtsMapItem item, byte kind)
        {
            if (kind == KindRoad || kind == KindPatch || kind == KindTerrain || kind == KindPrefab)
            {
                return true;
            }

            if (item.KdopMin == null || item.KdopMax == null || item.KdopMin.Length < 3 || item.KdopMax.Length < 3)
            {
                return false;
            }

            float height = item.KdopMax[1] - item.KdopMin[1];
            float span = Mathf.Max(item.KdopMax[0] - item.KdopMin[0], item.KdopMax[2] - item.KdopMin[2]);

            if (!(height >= 0f && height < 2000f) || !(span >= 0f && span < 20000f))
            {
                return false;
            }

            switch (kind)
            {
                case KindModel: return height >= 8f || span >= 25f;
                case KindBuilding: return height >= 6f;
                default: return height >= 8f;
            }
        }

        /// <summary>How far out an item is built: far or near by its size, and a model or sign no further than the map's own view distance for it.</summary>
        private float BuildRadius(EtsMapItem item, byte kind)
        {
            float radius = IsLarge(item, kind) ? FarRadius : NearRadius;

            if ((kind == KindModel || kind == KindSign) && item.ViewDistance != 0)
            {
                radius = Mathf.Min(radius, item.ViewDistance * 10f);
            }

            return radius;
        }

        /// <summary>The distance, squared, past which a built item is removed: its build radius plus the margin.</summary>
        private float UnloadSquared(EtsMapItem item, byte kind)
        {
            float radius = BuildRadius(item, kind) + UnloadMargin;
            return radius * radius;
        }

        /// <summary>The models the first build places, nearest first, within the radius and their own view distance.</summary>
        private List<KeyValuePair<float, EtsMapModel>> NearestModels(List<EtsMapSector> sectors, float radiusSquared)
        {
            var candidates = new List<KeyValuePair<float, EtsMapModel>>();

            foreach (EtsMapSector sector in sectors)
            {
                foreach (EtsMapModel item in sector.Models)
                {
                    EtsMapNode node = _map.FindNode(item.NodeUid);

                    if (node == null)
                    {
                        continue;
                    }

                    float d = DistanceSquared(node.Position);

                    if (d <= radiusSquared && d <= ViewDistanceSquared(item))
                    {
                        candidates.Add(new KeyValuePair<float, EtsMapModel>(d, item));
                    }
                }
            }

            candidates.Sort((a, b) => a.Key.CompareTo(b.Key));
            return candidates;
        }

        // ---------------------------------------------------------------- the first build's reads

        private bool LoadFrameSpent()
        {
            return _loadFrameClock.Elapsed.TotalMilliseconds >= _loadFrameMs;
        }

        /// <summary>
        /// The first build's items, in the order LoadRoutine builds them and chosen by the same tests, each one's reads asked of
        /// the prefetch thread (AskLoad). Yields when the load frame's time is spent, so the loading screen keeps drawing.
        /// </summary>
        private IEnumerator AskForLoad(List<EtsMapSector> sectors, float radiusSquared, List<KeyValuePair<float, EtsMapModel>> models)
        {
            if (_roads)
            {
                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapRoad road in sector.Roads)
                    {
                        EtsMapNode start = _map.FindNode(road.StartNode);
                        if (start == null || !Within(start.Position, radiusSquared)) continue;
                        AskLoad(road, KindRoad);
                        if (LoadFrameSpent()) yield return null;
                    }
                }
            }

            if (_terrain)
            {
                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapBezierPatch patch in sector.BezierPatches)
                    {
                        EtsMapNode node = _map.FindNode(patch.NodeUid);
                        if (node == null || !Within(node.Position, radiusSquared)) continue;
                        AskLoad(patch, KindPatch);
                        if (LoadFrameSpent()) yield return null;
                    }

                    foreach (EtsMapTerrainItem item in sector.Terrains)
                    {
                        EtsMapNode start = _map.FindNode(item.StartNode);
                        if (start == null || !Within(start.Position, radiusSquared)) continue;
                        AskLoad(item, KindTerrain);
                        if (LoadFrameSpent()) yield return null;
                    }
                }
            }

            if (_prefabs)
            {
                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapPrefab prefab in sector.Prefabs)
                    {
                        EtsMapNode node = prefab.NodeUids.Length > 0 ? _map.FindNode(prefab.NodeUids[0]) : null;
                        if (node == null || !Within(node.Position, radiusSquared)) continue;
                        AskLoad(prefab, KindPrefab);
                        if (LoadFrameSpent()) yield return null;
                    }
                }
            }

            if (_models)
            {
                int placed = 0;

                foreach (KeyValuePair<float, EtsMapModel> candidate in models)
                {
                    if (placed++ >= _maxModels) break;
                    AskLoad(candidate.Value, KindModel);
                    if (LoadFrameSpent()) yield return null;
                }

                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapBuilding item in sector.Buildings)
                    {
                        EtsMapNode node = _map.FindNode(item.StartNode);
                        if (node == null || DistanceSquared(node.Position) > radiusSquared) continue;
                        AskLoad(item, KindBuilding);
                        if (LoadFrameSpent()) yield return null;
                    }

                    foreach (EtsMapCurve item in sector.Curves)
                    {
                        EtsMapNode node = _map.FindNode(item.NodeUid);
                        if (node == null || DistanceSquared(node.Position) > radiusSquared) continue;
                        AskLoad(item, KindCurve);
                        if (LoadFrameSpent()) yield return null;
                    }
                }
            }

            if (_details)
            {
                foreach (EtsMapSector sector in sectors)
                {
                    foreach (EtsMapSign item in sector.Signs)
                    {
                        EtsMapNode node = _map.FindNode(item.NodeUid);
                        if (node == null) continue;
                        float d = DistanceSquared(node.Position);
                        if (d > radiusSquared || d > ViewDistanceSquared(item)) continue;
                        AskLoad(item, KindSign);
                        if (LoadFrameSpent()) yield return null;
                    }
                }
            }
        }

        private void AskLoad(EtsMapItem item, byte kind)
        {
            var needs = new List<Prefetch.Need>();

            try
            {
                _pf.Require(item, kind, BuildOptions.RibbonMaterial, needs);
            }
            catch (Exception e)
            {
                _pf.NoteFailure(e);
                needs.Clear();
            }

            if (needs.Count > 0)
            {
                _loadNeeds[item.Uid] = needs;
            }
        }

        /// <summary>
        /// The first build, before an item: takes the prefetch thread's finished reads (while the load frame has time), and says
        /// whether the item must still wait for its own - true: yield a frame and ask again. Gives up after LoadWaitLimit (the
        /// build then reads what is missing on the frame, as it always did). Always false with the prefetch off.
        /// </summary>
        private bool LoadWaits(ulong uid, ref float since)
        {
            if (_pf == null || !_prefetch)
            {
                return false;
            }

            DrainPrefetch(false);

            if (!_loadNeeds.TryGetValue(uid, out List<Prefetch.Need> needs))
            {
                return false;
            }

            if (Prefetch.AllReady(needs))
            {
                _loadNeeds.Remove(uid);
                return false;
            }

            float now = Time.realtimeSinceStartup;

            if (since < 0f)
            {
                since = now;
                _loadWaited++;
                return true;
            }

            if (now - since > LoadWaitLimit)
            {
                _loadNeeds.Remove(uid);
                _loadTimedOut++;
                return false;
            }

            return true;
        }

        /// <summary>The prefetch thread's finished reads onto the frame: one per frame at least, more while the load's or the stream's frame has time.</summary>
        private void DrainPrefetch(bool stream)
        {
            if (_pf == null)
            {
                return;
            }

            if (_loadDrainMore == null)
            {
                _loadDrainMore = () => FirstThisFrame() || _loadFrameClock.Elapsed.TotalMilliseconds < _loadFrameMs;
                _streamDrainMore = () => FirstThisFrame() || _streamClock.Elapsed.TotalMilliseconds < _streamFrameBudget;
            }

            _pf.Drain(stream ? _streamDrainMore : _loadDrainMore);
        }

        private bool FirstThisFrame()
        {
            if (_drainFrame == Time.frameCount)
            {
                return false;
            }

            _drainFrame = Time.frameCount;
            return true;
        }

        // ---------------------------------------------------------------- the stream

        /// <summary>Forgets the stream's queue and state (a new centre, a new stream): the index of sectors is kept - it is in game coordinates.</summary>
        private void ResetStream()
        {
            _candidates.Clear();
            _nextCandidates.Clear();
            _candHead = 0;
            _unloadList.Clear();
            _unloadHead = 0;
            _combineQueue.Clear();
            _parents.Clear();
            _focusTime = -1f;
            _velocity = Vector3.zero;
            _moving = false;
        }

        /// <summary>
        /// What the stream follows: the session's arrival point while it is Loading (PLAN U19), the scene's follow target, else
        /// the truck while somebody here drives it or the player while they walk (EtsTruckSeat.StreamFocus), else the truck.
        /// The scene is searched once a second, not every frame.
        /// </summary>
        private Transform StreamTarget()
        {
            if (StreamFocusOverride != null)
            {
                return StreamFocusOverride;
            }

            if (_follow != null)
            {
                return _follow;
            }

            float now = Time.realtimeSinceStartup;

            if (now - _targetFoundAt >= 1f)
            {
                _targetFoundAt = now;
                _seat = FindFirstObjectByType<EtsTruckSeat>();
                _truck = FindFirstObjectByType<EtsTruckController>();
            }

            Transform target = _seat != null ? _seat.StreamFocus : null;

            if (target == null && _truck != null)
            {
                target = _truck.transform;
            }

            return target;
        }

        /// <summary>
        /// The followed point in the game's frame (Unity z is the negated game z), its velocity (sampled every 0.2 s, smoothed,
        /// and forgotten on a jump of 300 m - a teleport or a new centre, not a drive), and the point ahead: speed (at most
        /// 50 m/s) x _leadSeconds along the heading.
        /// </summary>
        private void Follow(Transform target, float now)
        {
            Vector3 p = target.position;

            if (_focusTime < 0f || (p - _focusLast).sqrMagnitude > 300f * 300f)
            {
                _focusLast = p;
                _focusTime = now;
                _velocity = Vector3.zero;
            }
            else if (now - _focusTime >= 0.2f)
            {
                Vector3 v = (p - _focusLast) / (now - _focusTime);
                v.y = 0f;
                _velocity = Vector3.Lerp(_velocity, v, 0.5f);
                _focusLast = p;
                _focusTime = now;
            }

            float speed = _velocity.magnitude;
            _moving = speed > 2f;
            _gx = p.x + _centerGame.x;
            _gz = _centerGame.z - p.z;

            if (_moving)
            {
                _dirX = _velocity.x / speed;
                _dirZ = -_velocity.z / speed;
            }
            else
            {
                _dirX = _dirZ = 0f;
            }

            float lead = Mathf.Min(speed, 50f) * _leadSeconds;
            _ax = _gx + _dirX * lead;
            _az = _gz + _dirZ * lead;
        }

        private double StreamTime()
        {
            return _streamClock.Elapsed.TotalMilliseconds;
        }

        /// <summary>Whether ground close to the truck is still to be built: among the next few candidates, a road, terrain or prefab within 600 m of it or of the point ahead.</summary>
        private bool Urgent()
        {
            for (int i = _candHead; i < _candidates.Count && i < _candHead + 8; i++)
            {
                StreamEntry e = _candidates[i];

                if (e.Kind > KindPrefab || float.IsNaN(e.X) || Known(e.Item.Uid))
                {
                    continue;
                }

                float dx = e.X - _gx, dz = e.Z - _gz, ax = e.X - _ax, az = e.Z - _az;

                if (Mathf.Min(dx * dx + dz * dz, ax * ax + az * az) < 600f * 600f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Keeps the map built round the followed point, every frame a little: the prefetch thread's finished reads taken
        /// (texture uploads), a slice of the scan of what is in range, builds from the queue nearest to the point ahead first
        /// (each only once its reads are in), one deferred static batching and the removals - all under _streamBudgetMs (the
        /// frame ms while ground near the truck is missing). Sectors are read on the map's reader thread as the truck nears them.
        /// </summary>
        private IEnumerator StreamRoutine()
        {
            var stats = new EtsMapBuilder.Stats();
            ResetStream();

            if (_prefetch && _pf == null)
            {
                _pf = new Prefetch(_map) { WarmVegetation = _vegetation };
            }

            _repSince = Time.realtimeSinceStartup;
            _repPostedAt = _pf != null ? _pf.Posted : 0;
            _repUploadsAt = _pf != null ? _pf.Uploads : 0;

            Debug.Log("[SomniumEuroTruck] Stream: large items (roads, terrain, junctions and yards, house rows, models 8 m tall or 25 m across) to " + FarRadius.ToString("0") +
                      " m, small ones to " + NearRadius.ToString("0") + " m (a model or sign no further than the map's own view distance for it); each removed " +
                      UnloadMargin.ToString("0") + " m past its own radius; " + _streamBudgetMs.ToString("0.0") + " ms a frame (" + Mathf.Max(_streamBudgetMs, _streamFrameMs).ToString("0.0") +
                      " while ground near the truck is missing), nearest to the point " + _leadSeconds.ToString("0.#") + " s ahead first; reads " +
                      (_pf == null ? "on the frame (prefetch off)" : _pf.Threaded ? "on a worker thread" : "on the frame (NO worker thread: " + _pf.ThreadFailure + ")") + ".");

            IEnumerator scan = null;
            float nextScan = 0f;

            while (_stream && _root != null)
            {
                Transform target = StreamTarget();

                if (target == null)
                {
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                _streamClock.Restart();
                float now = Time.realtimeSinceStartup;

                // Nothing in here may end the coroutine: a fault is reported (five times at most) and the stream goes on.
                try
                {
                    Follow(target, now);
                    _streamFrameBudget = Urgent() ? Mathf.Max(_streamBudgetMs, _streamFrameMs) : _streamBudgetMs;

                    // 1. The prefetch thread's finished reads: texture uploads and models into the cache.
                    DrainPrefetch(true);

                    // 2. What is in range, a slice a frame; a new pass every half second while moving, every two when parked.
                    if (scan == null && now >= nextScan)
                    {
                        scan = ScanPass();
                    }

                    if (scan != null)
                    {
                        bool running;

                        do
                        {
                            running = scan.MoveNext();
                        }
                        while (running && scan.Current != ScanWait && StreamTime() < _streamFrameBudget * 0.5);

                        if (!running)
                        {
                            scan = null;
                            nextScan = now + (_moving ? 0.5f : 2f);
                        }
                    }

                    // 3. Builds, 4. one deferred static batching, 5. removals - while the frame has time.
                    BuildSome(stats);
                    CombineSome();
                    UnloadSome();

                    double spent = StreamTime();

                    if (spent > _repWorstFrameMs)
                    {
                        _repWorstFrameMs = spent;
                    }

                    ReportStream(now);
                }
                catch (Exception e)
                {
                    if (_repFailures++ < 5)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Stream: a frame of streaming failed (" + e.GetType().Name + ": " + e.Message + "); scanning again in 2 s.\n" + e.StackTrace);
                    }

                    scan = null;
                    nextScan = now + 2f;
                }

                yield return null;
            }
        }

        /// <summary>
        /// One pass over what is in range: the sectors round the followed point read (reader thread; the pass waits a frame at
        /// a time until they are), each indexed once (IndexSector), every entry within its build radius of the followed point or
        /// of the point ahead and not yet built queued by its priority (PriorityKey), and the built items both points have left
        /// behind listed for removal. Yields every 256 entries so the frame can stop it.
        /// </summary>
        private IEnumerator ScanPass()
        {
            float far = FarRadius;

            // Out to the far radius + 500 m read (a road or strip that starts inside must find its far node - an item built
            // without one is refused for good, _unbuildable), the far radius and the lead scanned.
            while (!_map.TryReadAround(_gx, _gz, far + 500f, _readSectors, out _, out _))
            {
                yield return ScanWait;
            }

            if (!_map.TryReadAround(_gx, _gz, far + Mathf.Min(50f * _leadSeconds, 450f), _scanSectors, out _, out _))
            {
                yield break;
            }

            var sectors = new List<EtsMapSector>(_scanSectors);
            _nextCandidates.Clear();
            int inRange = 0, seen = 0;

            foreach (EtsMapSector sector in sectors)
            {
                if (!_streamIndex.TryGetValue(sector, out List<StreamEntry> entries))
                {
                    entries = new List<StreamEntry>();
                    IEnumerator indexing = IndexSector(sector, entries);

                    while (indexing.MoveNext())
                    {
                        yield return null;
                    }

                    _streamIndex[sector] = entries;
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    if ((++seen & 255) == 0)
                    {
                        yield return null;
                    }

                    StreamEntry e = entries[i];

                    if (float.IsNaN(e.X))
                    {
                        EtsMapNode node = _map.FindNode(e.Node);

                        if (node == null)
                        {
                            continue;
                        }

                        e.X = node.Position[0];
                        e.Z = node.Position[2];
                    }

                    float fx = e.X - _gx, fz = e.Z - _gz, dF = fx * fx + fz * fz;
                    float ax = e.X - _ax, az = e.Z - _az, dA = ax * ax + az * az;

                    if (Mathf.Min(dF, dA) > e.BuildSquared)
                    {
                        continue;
                    }

                    inRange++;

                    if (Known(e.Item.Uid))
                    {
                        continue;
                    }

                    e.Key = PriorityKey(e, dA, fx, fz, dF);
                    _nextCandidates.Add(e);
                }
            }

            _inRange = inRange;
            _nextCandidates.Sort(ByKey);
            List<StreamEntry> swap = _candidates;
            _candidates = _nextCandidates;
            _nextCandidates = swap;
            _nextCandidates.Clear();
            _candHead = 0;

            // Removals: whatever both points have left beyond its own unload radius (an item the first build placed without one
            // known keeps the old single ring, the far radius plus the margin).
            float fallback = FarRadius + UnloadMargin;
            fallback *= fallback;
            _unloadList.Clear();
            _unloadHead = 0;

            foreach (KeyValuePair<ulong, BuiltItem> pair in _built)
            {
                BuiltItem b = pair.Value;
                float fx = b.X - _gx, fz = b.Z - _gz, ax = b.X - _ax, az = b.Z - _az;

                if (Mathf.Min(fx * fx + fz * fz, ax * ax + az * az) > (b.UnloadSquared > 0f ? b.UnloadSquared : fallback))
                {
                    _unloadList.Add(pair.Key);
                }
            }
        }

        /// <summary>
        /// The build order: distance to the point ahead, shortened by up to 45 % for what lies ahead of the heading while moving
        /// (the road you are driving into before the one behind), and by 20 % for the ground (roads, terrain, prefabs) so it
        /// comes before the props that stand on it.
        /// </summary>
        private float PriorityKey(StreamEntry e, float distanceAheadSquared, float fx, float fz, float distanceSquared)
        {
            float d = Mathf.Sqrt(distanceAheadSquared);

            if (_moving && distanceSquared > 1f)
            {
                float cos = (fx * _dirX + fz * _dirZ) / Mathf.Sqrt(distanceSquared);

                if (cos > 0f)
                {
                    d *= 1f - 0.45f * cos;
                }
            }

            if (e.Kind <= KindPrefab)
            {
                d *= 0.8f;
            }

            return d;
        }

        /// <summary>A sector's items as stream entries, each with its build and unload radius (the switches of the What header respected). Yields every 512.</summary>
        private IEnumerator IndexSector(EtsMapSector sector, List<StreamEntry> into)
        {
            int n = 0;

            if (_roads)
            {
                foreach (EtsMapRoad road in sector.Roads)
                {
                    Index(into, road, KindRoad, road.StartNode);
                    if ((++n & 511) == 0) yield return null;
                }
            }

            if (_terrain)
            {
                foreach (EtsMapBezierPatch patch in sector.BezierPatches)
                {
                    Index(into, patch, KindPatch, patch.NodeUid);
                    if ((++n & 511) == 0) yield return null;
                }

                foreach (EtsMapTerrainItem item in sector.Terrains)
                {
                    Index(into, item, KindTerrain, item.StartNode);
                    if ((++n & 511) == 0) yield return null;
                }
            }

            if (_prefabs)
            {
                foreach (EtsMapPrefab prefab in sector.Prefabs)
                {
                    if (prefab.NodeUids.Length == 0) continue;
                    Index(into, prefab, KindPrefab, prefab.NodeUids[0]);
                    if ((++n & 511) == 0) yield return null;
                }
            }

            if (_models)
            {
                foreach (EtsMapModel item in sector.Models)
                {
                    Index(into, item, KindModel, item.NodeUid);
                    if ((++n & 511) == 0) yield return null;
                }

                foreach (EtsMapBuilding item in sector.Buildings)
                {
                    Index(into, item, KindBuilding, item.StartNode);
                    if ((++n & 511) == 0) yield return null;
                }

                foreach (EtsMapCurve item in sector.Curves)
                {
                    Index(into, item, KindCurve, item.NodeUid);
                    if ((++n & 511) == 0) yield return null;
                }
            }

            if (_details)
            {
                foreach (EtsMapSign item in sector.Signs)
                {
                    Index(into, item, KindSign, item.NodeUid);
                    if ((++n & 511) == 0) yield return null;
                }
            }
        }

        private void Index(List<StreamEntry> into, EtsMapItem item, byte kind, ulong node)
        {
            float build = BuildRadius(item, kind);
            float unload = build + UnloadMargin;
            into.Add(new StreamEntry { Item = item, Kind = kind, Node = node, BuildSquared = build * build, UnloadSquared = unload * unload });
        }

        /// <summary>
        /// Builds from the front of the queue while the frame has time (and _streamItemsPerFrame at most): an item whose reads
        /// are not in yet is passed over for the next one (its reads asked when fewer than StreamMaxInFlight are outstanding),
        /// looking StreamWindow items deep. When the frame's time went on uploads, a build still goes in every third frame, so
        /// the queue never stalls behind a run of textures.
        /// </summary>
        private void BuildSome(EtsMapBuilder.Stats stats)
        {
            int built = 0, looked = 0;

            for (int i = _candHead; i < _candidates.Count && looked < StreamWindow; i++)
            {
                StreamEntry e = _candidates[i];

                if (Known(e.Item.Uid))
                {
                    if (i == _candHead) _candHead++;
                    continue;
                }

                looked++;

                if (!StreamReady(e))
                {
                    continue;
                }

                if (built >= Mathf.Max(1, _streamItemsPerFrame) || (StreamTime() >= _streamFrameBudget && (built > 0 || _framesWithoutBuild < 2)))
                {
                    break;
                }

                BuildEntry(e, stats);
                built++;

                if (i == _candHead && Known(e.Item.Uid)) _candHead++;
            }

            _framesWithoutBuild = built > 0 ? 0 : _framesWithoutBuild + 1;
        }

        /// <summary>Whether an entry's reads are in (asking for them the first time it is looked at); true once it has waited StreamWaitLimit.</summary>
        private bool StreamReady(StreamEntry e)
        {
            if (_pf == null || !_prefetch)
            {
                return true;
            }

            float now = Time.realtimeSinceStartup;

            if (e.Needs == null)
            {
                if (_pf.InFlight >= StreamMaxInFlight)
                {
                    return false;
                }

                e.Needs = new List<Prefetch.Need>();

                try
                {
                    _pf.Require(e.Item, e.Kind, BuildOptions.RibbonMaterial, e.Needs);
                }
                catch (Exception ex)
                {
                    _pf.NoteFailure(ex);
                    e.Needs.Clear();
                }

                e.AskedAt = now;
                e.Waited = e.Needs.Count > 0;
            }

            if (Prefetch.AllReady(e.Needs))
            {
                return true;
            }

            if (now - e.AskedAt > StreamWaitLimit)
            {
                e.Needs.Clear();
                _repTimedOut++;
                return true;
            }

            return false;
        }

        private void BuildEntry(StreamEntry e, EtsMapBuilder.Stats stats)
        {
            EtsMapNode node = _map.FindNode(e.Node);

            if (node == null)
            {
                return;
            }

            if (e.Waited)
            {
                _repWaited++;
                e.Waited = false;
            }

            Mark();
            GameObject built = null;

            try
            {
                switch (e.Kind)
                {
                    case KindRoad: built = EtsMapBuilder.BuildRoad(_map, (EtsMapRoad)e.Item, BuildOptions, ParentFor("roads"), stats); break;
                    case KindPatch: built = EtsTerrainBuilder.BuildBezierPatch(_map, (EtsMapBezierPatch)e.Item, BuildOptions, ParentFor("terrain items"), stats); break;
                    case KindTerrain: built = EtsTerrainBuilder.BuildTerrainItem(_map, (EtsMapTerrainItem)e.Item, BuildOptions, ParentFor("terrain items"), stats); break;
                    case KindPrefab: built = EtsMapBuilder.BuildPrefab(_map, (EtsMapPrefab)e.Item, BuildOptions, ParentFor("prefabs"), stats); break;
                    case KindModel: built = EtsMapBuilder.BuildModel(_map, (EtsMapModel)e.Item, BuildOptions, ParentFor("models"), stats); break;
                    case KindBuilding: built = EtsRunBuilder.BuildBuilding(_map, (EtsMapBuilding)e.Item, BuildOptions, ParentFor("runs"), stats); break;
                    case KindCurve: built = EtsRunBuilder.BuildCurve(_map, (EtsMapCurve)e.Item, BuildOptions, ParentFor("runs"), stats); break;
                    case KindSign: built = EtsSignBuilder.BuildSign(_map, (EtsMapSign)e.Item, BuildOptions, ParentFor("signs")); break;
                }
            }
            catch (Exception ex)
            {
                // One item's fault costs that item, never the stream: an exception out of here used to end the stream's
                // coroutine, and the world stopped loading for the rest of the visit.
                if (_repFailures++ < 5)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Stream: building map item " + e.Item.Uid.ToString("x16") + " failed (" + ex.GetType().Name + ": " + ex.Message + "); left out.");
                }

                built = null;
            }

            Register(e.Item.Uid, built, node.Position, e.Kind != KindSign, e.UnloadSquared, true);
            _repAdded++;
        }

        /// <summary>One deferred static batching (Register, deferCombine) while the frame has time; one regardless when more than 200 wait.</summary>
        private void CombineSome()
        {
            while (_combineQueue.Count > 0 && (StreamTime() < _streamFrameBudget || _combineQueue.Count > 200))
            {
                GameObject item = _combineQueue.Dequeue();

                if (item == null)
                {
                    continue;
                }

                long start = Stopwatch.GetTimestamp();

                try
                {
                    CombineStatic(item);
                }
                catch (Exception e)
                {
                    if (_repFailures++ < 5)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Stream: static batching of '" + item.name + "' failed (" + e.GetType().Name + ": " + e.Message + "); it draws unbatched.");
                    }
                }

                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

                if (ms > _repWorstCombineMs)
                {
                    _repWorstCombineMs = ms;
                }

                _repCombined++;
                break;
            }
        }

        /// <summary>The removals the last pass listed, while the frame has time (one at least): each checked again, as the truck may have turned round since.</summary>
        private void UnloadSome()
        {
            int removed = 0;

            while (_unloadHead < _unloadList.Count)
            {
                if (removed > 0 && StreamTime() >= _streamFrameBudget)
                {
                    break;
                }

                ulong uid = _unloadList[_unloadHead++];

                if (!_built.TryGetValue(uid, out BuiltItem item))
                {
                    continue;
                }

                float fx = item.X - _gx, fz = item.Z - _gz, ax = item.X - _ax, az = item.Z - _az;
                float limit = item.UnloadSquared > 0f ? item.UnloadSquared : (FarRadius + UnloadMargin) * (FarRadius + UnloadMargin);

                if (Mathf.Min(fx * fx + fz * fz, ax * ax + az * az) <= limit)
                {
                    continue;
                }

                if (item.Object != null)
                {
                    // Meshes made at runtime are not freed with their objects. Only the item's own (road slices, terrain,
                    // batched copies): a model's pieces are shared with every other instance still in view
                    // (EtsModelBuilder.IsShared), and so are the traffic lights' (EtsSemaphores.IsSharedMesh).
                    foreach (MeshFilter filter in item.Object.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (filter.sharedMesh != null && !EtsModelBuilder.IsShared(filter.sharedMesh) && !EtsSemaphores.IsSharedMesh(filter.sharedMesh)) Destroy(filter.sharedMesh);
                    }

                    _uidByRoot.Remove(item.Object.GetInstanceID());
                    Destroy(item.Object);
                }

                _built.Remove(uid);
                removed++;
                _repRemoved++;
            }
        }

        /// <summary>
        /// "[SomniumEuroTruck] Streamed ..." at most every 5 s, when anything came or went: counts, the worst frame the stream
        /// spent on the main thread (drain + scan + builds + batching + removals) against its budget, how many items waited for
        /// the prefetch thread and what it read, the queue, the slowest single item, and the managed heap.
        /// </summary>
        private void ReportStream(float now)
        {
            if (now - _repSince < 5f)
            {
                return;
            }

            if (_repAdded > 0 || _repRemoved > 0)
            {
                int posted = _pf != null ? _pf.Posted - _repPostedAt : 0;
                int uploads = _pf != null ? _pf.Uploads - _repUploadsAt : 0;
                long native = 0;

                try
                {
                    native = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                }
                catch (Exception)
                {
                    native = 0;
                }

                Debug.Log("[SomniumEuroTruck] Streamed " + _repAdded + " items in, " + _repRemoved + " out over " + (now - _repSince).ToString("0.0") + " s; " + _built.Count + " built, " + _inRange +
                          " in range (large to " + FarRadius.ToString("0") + " m, small to " + NearRadius.ToString("0") + " m, " + (_moving ? "leading " + (_velocity.magnitude * _leadSeconds).ToString("0") + " m ahead" : "parked") +
                          "); worst build frame " + _repWorstFrameMs.ToString("0.0") + " ms (budget " + _streamBudgetMs.ToString("0.0") + " ms); " + _repWaited + " of " + _repAdded +
                          " items waited for the worker (" + posted + " reads asked, " + uploads + " textures uploaded, " + (_pf != null ? _pf.InFlight : 0) + " in flight, " + _repTimedOut +
                          " built without them" + (_pf != null ? _pf.TakeFailures() : "") + "); " + Mathf.Max(0, _candidates.Count - _candHead) + " queued, " + _repCombined + " batched (worst " +
                          _repWorstCombineMs.ToString("0.0") + " ms, " + _combineQueue.Count + " waiting)" + (_repAdded > 0 ? TakeItemCost() : "") + "; heap " + (GC.GetTotalMemory(false) >> 20) + " MB" +
                          (native > 0 ? ", Unity " + (native >> 20) + " MB" : "") + ".");

                if (_details && _repAdded > 0)
                {
                    LogDetails();
                }
            }

            _repSince = now;
            _repAdded = _repRemoved = _repWaited = _repTimedOut = _repCombined = 0;
            _repWorstFrameMs = _repWorstCombineMs = 0;
            _repPostedAt = _pf != null ? _pf.Posted : 0;
            _repUploadsAt = _pf != null ? _pf.Uploads : 0;
        }

        // ---------------------------------------------------------------- the prefetch thread

        /// <summary>
        /// Everything a map item's build reads from the archives, read and parsed on a worker thread of its own (EtsWorker, "Ets
        /// map prefetch") before the item is built: its models (EtsEngine.ReadModel -> AdoptModel), the material files of the
        /// look each is built with and their textures (EtsTextureCache.Prepare -> Adopt, the upload; decal shadows through
        /// PrepareAlphaFromRed), a prefab's descriptor (EtsMap.ReadDescriptor), the terrain brushes and the plants along roads
        /// and terrain. Which textures a material loads follows EtsMaterialCache.Build (the rule the traffic and the truck
        /// factory use, AskTextures). Anything it misses the build still reads on the frame, as before - a miss costs time, never
        /// correctness. The traffic keeps its own worker; the archive read is thread safe (a lock round the one stream read).
        /// </summary>
        private sealed class Prefetch
        {
            /// <summary>One read (or a model's look: its material files, then their textures as Children). Ready once it and its children have landed.</summary>
            public sealed class Need
            {
                public bool Landed;
                public List<Need> Children;
                public List<Action> Then;

                public bool Ready
                {
                    get
                    {
                        if (!Landed)
                        {
                            return false;
                        }

                        if (Children != null)
                        {
                            for (int i = 0; i < Children.Count; i++)
                            {
                                if (!Children[i].Landed) return false;
                            }
                        }

                        return true;
                    }
                }
            }

            private readonly EtsMap _map;
            private readonly EtsWorker _worker;
            private readonly Dictionary<string, Need> _needs = new Dictionary<string, Need>(StringComparer.Ordinal);
            private readonly Dictionary<ulong, Need[]> _plantSets = new Dictionary<ulong, Need[]>();
            private readonly Dictionary<ulong, Need> _brushes = new Dictionary<ulong, Need>();

            /// <summary>Material files parsed on the worker, by normalised path (only the worker's jobs touch it; locked all the same).</summary>
            private readonly Dictionary<string, EtsMaterialDef> _definitions = new Dictionary<string, EtsMaterialDef>(StringComparer.Ordinal);

            private bool _closed;
            private int _failures;
            private string _lastFailure;

            public int InFlight { get; private set; }
            public int Posted { get; private set; }
            public int Uploads { get; private set; }
            public bool Threaded => _worker.Threaded;
            public string ThreadFailure => _worker.Failure;

            public Prefetch(EtsMap map)
            {
                _map = map;
                _worker = new EtsWorker("Ets map prefetch");
            }

            public void Close()
            {
                _closed = true;
                _worker.Dispose();
            }

            public int Drain(Func<bool> more)
            {
                return _closed ? 0 : _worker.Drain(more);
            }

            public void NoteFailure(Exception e)
            {
                _failures++;
                _lastFailure = e.GetType().Name + ": " + e.Message;
            }

            /// <summary>", 2 failed (last: ...)" when any read or hand-over failed since the last call, else "".</summary>
            public string TakeFailures()
            {
                if (_failures == 0)
                {
                    return "";
                }

                string text = ", " + _failures + " failed (last: " + _lastFailure + ")";
                _failures = 0;
                return text;
            }

            /// <summary>True when every need has landed; the landed ones are taken out of the list as it goes.</summary>
            public static bool AllReady(List<Need> needs)
            {
                for (int i = needs.Count - 1; i >= 0; i--)
                {
                    if (!needs[i].Ready)
                    {
                        return false;
                    }

                    needs.RemoveAt(i);
                }

                return true;
            }

            private static void Add(List<Need> into, Need need)
            {
                if (need != null && !need.Ready)
                {
                    into.Add(need);
                }
            }

            private static void Child(Need parent, Need child)
            {
                if (child != null && !child.Landed)
                {
                    (parent.Children ?? (parent.Children = new List<Need>())).Add(child);
                }
            }

            private void Post(Need need, Func<object> work, Action<object> land)
            {
                InFlight++;
                Posted++;
                _worker.Post(work, (result, error) =>
                {
                    InFlight--;

                    if (_closed)
                    {
                        return;
                    }

                    if (error != null)
                    {
                        NoteFailure(error);
                    }
                    else
                    {
                        try
                        {
                            land(result);
                        }
                        catch (Exception e)
                        {
                            NoteFailure(e);
                        }
                    }

                    Finish(need);
                });
            }

            private void Finish(Need need)
            {
                need.Landed = true;
                List<Action> then = need.Then;
                need.Then = null;

                if (then == null)
                {
                    return;
                }

                foreach (Action action in then)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        NoteFailure(e);
                    }
                }
            }

            // ------------------------------------------------------------ what an item reads

            /// <summary>The reads a map item's build makes, as needs not yet landed, into <paramref name="into"/> (asked of the worker now).</summary>
            public void Require(EtsMapItem item, byte kind, string ribbonMaterial, List<Need> into)
            {
                if (EtsEngine.Files == null || EtsEngine.Textures == null || _closed)
                {
                    return;
                }

                switch (kind)
                {
                    case KindRoad: Road((EtsMapRoad)item, ribbonMaterial, into); break;
                    case KindPatch: Patch((EtsMapBezierPatch)item, into); break;
                    case KindTerrain: TerrainItem((EtsMapTerrainItem)item, into); break;
                    case KindPrefab: PrefabItem((EtsMapPrefab)item, into); break;
                    case KindModel: ModelItem((EtsMapModel)item, into); break;
                    case KindBuilding: BuildingItem((EtsMapBuilding)item, into); break;
                    case KindCurve: CurveItem((EtsMapCurve)item, into); break;
                    case KindSign: SignItem((EtsMapSign)item, into); break;
                }
            }

            /// <summary>EtsMapBuilder.BuildRoad: the template models and the edge models in the road's look, or a legacy road's ribbon, sidewalk and kerb materials; the terrain beside it and its plants.</summary>
            private void Road(EtsMapRoad road, string ribbonMaterial, List<Need> into)
            {
                EtsSiiUnit look = _map.RoadLook(road.LookToken);
                string lookName = EtsToken.Decode(road.MaterialToken);

                if (look != null && look.Has("template_right"))
                {
                    Add(into, Look(look.GetString("template_right"), lookName));

                    if (look.Has("template_left"))
                    {
                        Add(into, Look(look.GetString("template_left"), lookName));
                    }

                    Edge(road.RightEdgeToken, lookName, into);
                    Edge(road.LeftEdgeToken, lookName, into);
                    Edge(road.RightInnerEdgeToken, lookName, into);
                    Edge(road.LeftInnerEdgeToken, lookName, into);
                    Edge(road.LeftOuterEdgeToken, lookName, into);
                }
                else
                {
                    Add(into, Material(ribbonMaterial));
                    Add(into, Material("/material/road/small_block_pavement.mat"));
                    Add(into, Material("/material/road/kerb_tile.mat"));
                    Sidewalk(road.Right, into);
                    Sidewalk(road.Left, into);
                }

                // EtsMapBuilder's legacy asphalt atlas and lines, and every road's railings and side models (lamps, posts).
                _roadMaterials.Clear();
                _roadModels.Clear();
                EtsMapBuilder.RoadReads(_map, road, _roadMaterials, _roadModels);
                foreach (string path in _roadMaterials) Add(into, Material(path));
                foreach (KeyValuePair<string, string> model in _roadModels) Add(into, Look(model.Key, model.Value));

                Side(road.Right, into);
                Side(road.Left, into);
            }

            private readonly List<string> _roadMaterials = new List<string>();
            private readonly List<KeyValuePair<string, string>> _roadModels = new List<KeyValuePair<string, string>>();

            private void Edge(ulong token, string lookName, List<Need> into)
            {
                if (token == 0) return;
                EtsSiiUnit edge = _map.RoadEdge(token);
                if (edge != null && edge.Has("model")) Add(into, Look(edge.GetString("model"), lookName));
            }

            private void Sidewalk(EtsTerrainSide side, List<Need> into)
            {
                if (side == null || side.SidewalkMaterial == 0) return;
                EtsSiiUnit unit = _map.Sidewalk(side.SidewalkMaterial);
                string path = unit != null ? unit.GetString("path") : null;
                if (!string.IsNullOrEmpty(path)) Add(into, Material(path));
            }

            /// <summary>A terrain side (of a road, a terrain item, a prefab corner): its brushes and the plants of its vegetation bands.</summary>
            private void Side(EtsTerrainSide side, List<Need> into)
            {
                if (side == null)
                {
                    return;
                }

                Brushes(side.Quads, into);

                if (side.Vegetation != null)
                {
                    foreach (EtsVegetationRecord band in side.Vegetation)
                    {
                        if (band != null && band.Token != 0 && band.Density > 0f) WarmPlants(band.Token);
                    }
                }
            }

            private void Brushes(EtsTerrainQuadData quads, List<Need> into)
            {
                Add(into, Material(EtsTerrainBuilder.FallbackMaterial));

                if (quads == null || quads.MaterialTokens == null)
                {
                    return;
                }

                foreach (ulong token in quads.MaterialTokens)
                {
                    Add(into, Brush(token));
                }
            }

            /// <summary>A terrain brush token's material (terrain_material.sii path), as EtsTerrainBuilder.TerrainMaterial loads it.</summary>
            private Need Brush(ulong token)
            {
                if (token == 0)
                {
                    return null;
                }

                if (_brushes.TryGetValue(token, out Need known))
                {
                    return known;
                }

                EtsSiiUnit unit = _map.TerrainMaterial(token);
                Need need = unit != null && unit.Has("path") ? Material(unit.GetString("path")) : null;
                _brushes[token] = need;
                return need;
            }

            /// <summary>Whether the plants along roads and terrain are read ahead (the map viewer's _vegetation).</summary>
            public bool WarmVegetation = true;

            private readonly List<Need> _warm = new List<Need>();

            /// <summary>
            /// A side's plants read ahead WITHOUT the item waiting for them: the sides' trees are planted later and a little a
            /// frame by EtsVegetationStream (EtsVegetationBuilder.Pump), not in the road's or terrain's build, so the build need not
            /// wait - but when the planting comes, the models and textures are in the caches and nothing is read on its frame.
            /// </summary>
            private void WarmPlants(ulong token)
            {
                if (!WarmVegetation)
                {
                    return;
                }

                _warm.Clear();
                Plants(token, _warm);
                _warm.Clear();
            }

            /// <summary>A vegetation set's plants (veg.* model[] -> vegetmodel.*), as EtsVegetationBuilder scatters them.</summary>
            private void Plants(ulong token, List<Need> into)
            {
                if (!_plantSets.TryGetValue(token, out Need[] needs))
                {
                    var list = new List<Need>();
                    EtsSiiUnit set = _map.Vegetation(token);

                    if (set != null)
                    {
                        List<string> units = set.GetArray("models");

                        if (units.Count == 0)
                        {
                            units = set.GetArray("model");
                        }

                        foreach (string unit in units)
                        {
                            Plant(unit.Trim(), list);
                        }
                    }

                    needs = list.ToArray();
                    _plantSets[token] = needs;
                }

                foreach (Need need in needs)
                {
                    Add(into, need);
                }
            }

            /// <summary>A vegetmodel unit: the model EtsVegetationBuilder.Model builds (medium, else high, else sprite) and the sprite.</summary>
            private void Plant(string unitName, List<Need> into)
            {
                EtsSiiUnit unit = _map.VegetationModel(unitName);

                if (unit == null)
                {
                    return;
                }

                string main = null;

                foreach (string key in new[] { "med_poly_model", "hi_poly_model", "sprite_model" })
                {
                    main = unit.GetString(key, null);
                    if (!string.IsNullOrEmpty(main)) break;
                }

                Add(into, Look(main, "default"));
                string sprite = unit.GetString("sprite_model", null);

                if (!string.IsNullOrEmpty(sprite) && sprite != main)
                {
                    Add(into, Look(sprite, "default"));
                }
            }

            private void Patch(EtsMapBezierPatch patch, List<Need> into)
            {
                Brushes(patch.Quads, into);

                if (patch.VegetationTokens != null)
                {
                    foreach (ulong token in patch.VegetationTokens)
                    {
                        if (token != 0) WarmPlants(token);
                    }
                }
            }

            private void TerrainItem(EtsMapTerrainItem item, List<Need> into)
            {
                Side(item.Right, into);
                Side(item.Left, into);
            }

            /// <summary>EtsMapBuilder.BuildPrefab: the prefab model in its default look, the descriptor, the corner terrain.</summary>
            private void PrefabItem(EtsMapPrefab prefab, List<Need> into)
            {
                EtsSiiUnit def = _map.PrefabDefinition(prefab.PrefabToken);

                if (def == null)
                {
                    return;
                }

                Add(into, Look(def.GetString("model_desc"), "default"));
                Add(into, Descriptor(def.GetString("prefab_desc")));

                if (prefab.Corners != null)
                {
                    foreach (EtsTerrainSide corner in prefab.Corners)
                    {
                        Side(corner, into);
                    }
                }
            }

            /// <summary>EtsMapBuilder.BuildModel: the model in the item's look, or a tree's vegetation model.</summary>
            private void ModelItem(EtsMapModel item, List<Need> into)
            {
                EtsSiiUnit def = _map.ModelDefinition(item.ModelToken);

                if (def == null)
                {
                    return;
                }

                if (def.Has("vegetation_model"))
                {
                    Plant(def.GetString("vegetation_model"), into);
                }
                else
                {
                    Add(into, Look(def.GetString("model_desc"), EtsToken.Decode(item.LookToken)));
                }
            }

            /// <summary>EtsRunBuilder.BuildBuilding: the scheme's first building model (its default look).</summary>
            private void BuildingItem(EtsMapBuilding item, List<Need> into)
            {
                EtsSiiUnit scheme = _map.BuildingScheme(item.SchemeToken);
                List<string> models = scheme != null ? scheme.GetArray("models") : null;

                if (models == null || models.Count == 0)
                {
                    return;
                }

                EtsSiiUnit definition = _map.BuildingModel(models[0].Trim());
                Add(into, Look(definition != null ? definition.GetString("model_desc") : null, "default"));
            }

            private void CurveItem(EtsMapCurve item, List<Need> into)
            {
                EtsSiiUnit definition = _map.CurveModel(item.ModelToken);
                Add(into, Look(definition != null ? definition.GetString("model_desc") : null, "default"));
            }

            /// <summary>EtsSignBuilder: the sign model in the item's look, else the unit's look_name.</summary>
            private void SignItem(EtsMapSign item, List<Need> into)
            {
                EtsSiiUnit unit = _map.SignModel(item.ModelToken);

                if (unit == null)
                {
                    return;
                }

                string look = item.LookToken != 0 ? EtsToken.Decode(item.LookToken) : unit.GetString("look_name", "default");
                Add(into, Look(unit.GetString("model_desc", null), look));
            }

            // ------------------------------------------------------------ the reads

            /// <summary>A model read and parsed on the worker and adopted into EtsEngine's cache. Null when it is cached already.</summary>
            private Need ModelData(string path)
            {
                string key = "m" + EtsEngine.ModelKey(path);

                if (_needs.TryGetValue(key, out Need known))
                {
                    return known;
                }

                if (EtsEngine.IsModelLoaded(path))
                {
                    return null;
                }

                var need = new Need();
                _needs[key] = need;
                EtsFileSystem files = EtsEngine.Files;
                Post(need, () =>
                {
                    EtsModelData read = EtsEngine.ReadModel(files, path, out string why);
                    return (object)read ?? why ?? "not read";
                }, result =>
                {
                    // A model that cannot be read is left to LoadModel on the frame, which fails the same way and says why.
                    if (result is EtsModelData model && ReferenceEquals(files, EtsEngine.Files))
                    {
                        EtsEngine.AdoptModel(path, model);
                    }
                });

                return need;
            }

            /// <summary>
            /// A model in one look: the model first (ModelData), then the look's material files read on the worker, then their
            /// textures (Children). Ready when all have landed. EtsModelBuilder falls back to look 0 for an unknown name, as here.
            /// </summary>
            private Need Look(string path, string look)
            {
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                string key = "l" + EtsEngine.ModelKey(path) + "|" + (look ?? "");

                if (_needs.TryGetValue(key, out Need known))
                {
                    return known;
                }

                var need = new Need();
                _needs[key] = need;
                Need model = ModelData(path);

                if (model != null && !model.Landed)
                {
                    (model.Then ?? (model.Then = new List<Action>())).Add(() => PostLook(need, path, look));
                }
                else
                {
                    PostLook(need, path, look);
                }

                return need;
            }

            private void PostLook(Need need, string path, string look)
            {
                if (!EtsEngine.TryGetModel(path, out EtsModelData model) || model == null || model.LookMaterials == null || model.LookMaterials.Length == 0)
                {
                    Finish(need);
                    return;
                }

                int index = Mathf.Max(0, model.LookIndex(look));

                if (index >= model.LookMaterials.Length)
                {
                    index = 0;
                }

                PostMaterials(need, model.LookMaterials[index]);
            }

            /// <summary>A material file on its own (terrain brushes, sidewalks, kerbs): read on the worker, then its textures.</summary>
            private Need Material(string path)
            {
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                string key = "M" + path;

                if (_needs.TryGetValue(key, out Need known))
                {
                    return known;
                }

                var need = new Need();
                _needs[key] = need;
                PostMaterials(need, new[] { path });
                return need;
            }

            private void PostMaterials(Need need, string[] paths)
            {
                if (paths == null || paths.Length == 0)
                {
                    Finish(need);
                    return;
                }

                EtsFileSystem files = EtsEngine.Files;
                var list = (string[])paths.Clone();
                Post(need, () => ReadDefinitions(files, list), result =>
                {
                    if (result is EtsMaterialDef[] defs)
                    {
                        foreach (EtsMaterialDef def in defs)
                        {
                            if (def != null) AskTextures(def, need);
                        }
                    }
                });
            }

            /// <summary>Worker: the material files, parsed (null where missing or unreadable), each once per visit.</summary>
            private EtsMaterialDef[] ReadDefinitions(EtsFileSystem files, string[] paths)
            {
                var defs = new EtsMaterialDef[paths.Length];

                for (int i = 0; i < paths.Length; i++)
                {
                    if (string.IsNullOrEmpty(paths[i]))
                    {
                        continue;
                    }

                    string key = "/" + EtsHashFs.Normalize(paths[i]);
                    EtsMaterialDef def;

                    lock (_definitions)
                    {
                        if (_definitions.TryGetValue(key, out def))
                        {
                            defs[i] = def;
                            continue;
                        }
                    }

                    try
                    {
                        string text = files.ReadText(key);
                        def = text == null ? null : EtsMaterialDef.Parse(text, key);
                    }
                    catch (Exception)
                    {
                        def = null;
                    }

                    lock (_definitions)
                    {
                        _definitions[key] = def;
                    }

                    defs[i] = def;
                }

                return defs;
            }

            /// <summary>
            /// Main thread: the textures EtsMaterialCache.Build loads for this material - base (with its reflection-mask plane
            /// where the material is masked), second layer, amod mask, normal map; a leaves material's base and mask; a decal
            /// shadow's red plane. Materials Build skips ask for nothing. The rule is the truck factory's and the traffic's.
            /// </summary>
            private void AskTextures(EtsMaterialDef def, Need parent)
            {
                string effect = def.Effect ?? string.Empty;

                if (effect.Length == 0 || def.EffectHas("shadowonly") || effect == "eut2.none" || effect.StartsWith("eut2.none", StringComparison.Ordinal) ||
                    def.EffectHas("shadowmap") || def.EffectHas("mlaaweight") || def.EffectHas(".night") || def.EffectHas("fakeshadow"))
                {
                    return;
                }

                if (def.EffectHas("decalshadow"))
                {
                    Child(parent, Shadow(def.BaseTexture));
                    return;
                }

                if (EtsModelBuilder.IsLeaves(def) && EtsMaterialCache.LeavesShader != null)
                {
                    Child(parent, Texture(def.BaseTexture, false, false));

                    if (def.Textures.TryGetValue("texture_mask", out string leafMask) && !string.IsNullOrEmpty(leafMask))
                    {
                        Child(parent, Texture(leafMask, true, false));
                    }

                    return;
                }

                if (def.EffectHas("water"))
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

                Child(parent, Texture(def.BaseTexture, false, masked));
                string second = EtsMaterialCache.SecondLayer(def, out _);

                if (second != null && !def.EffectHas("decal.over"))
                {
                    Child(parent, Texture(second, false, false));
                }

                if (second != null && def.EffectHas("amod") && !def.EffectHas("iamod") && def.Textures.TryGetValue("texture_mask", out string amodMask))
                {
                    Child(parent, Texture(amodMask, false, false));
                }

                if (def.NormalTexture != null && !def.EffectHas("tsnmapuv"))
                {
                    Child(parent, Texture(def.NormalTexture, true, false));
                }
            }

            /// <summary>A texture read and laid out on the worker (EtsTextureCache.Prepare) and uploaded on landing (Adopt). Null when loaded already (with its mask plane, when asked).</summary>
            private Need Texture(string tobjPath, bool linear, bool withMask)
            {
                EtsTextureCache cache = EtsEngine.Textures;

                if (string.IsNullOrEmpty(tobjPath) || cache == null)
                {
                    return null;
                }

                if (cache.IsLoaded(tobjPath, linear) && (!withMask || cache.HasMaskPlane(tobjPath)))
                {
                    return null;
                }

                string key = (linear ? "T" : "t") + (withMask ? "+" : "-") + tobjPath;

                if (_needs.TryGetValue(key, out Need known))
                {
                    return known;
                }

                var need = new Need();
                _needs[key] = need;
                EtsFileSystem files = EtsEngine.Files;
                Post(need, () => EtsTextureCache.Prepare(files, tobjPath, linear, withMask), result =>
                {
                    if (result is EtsTextureCache.Prepared prepared && ReferenceEquals(cache, EtsEngine.Textures))
                    {
                        cache.Adopt(prepared);
                        Uploads++;
                    }
                });

                return need;
            }

            /// <summary>A decal shadow's red plane (EtsTextureCache.PrepareAlphaFromRed), uploaded on landing.</summary>
            private Need Shadow(string tobjPath)
            {
                EtsTextureCache cache = EtsEngine.Textures;

                if (string.IsNullOrEmpty(tobjPath) || cache == null || cache.IsAlphaFromRedLoaded(tobjPath))
                {
                    return null;
                }

                string key = "s" + tobjPath;

                if (_needs.TryGetValue(key, out Need known))
                {
                    return known;
                }

                var need = new Need();
                _needs[key] = need;
                EtsFileSystem files = EtsEngine.Files;
                Post(need, () => EtsTextureCache.PrepareAlphaFromRed(files, tobjPath), result =>
                {
                    if (result is EtsTextureCache.PreparedShadow prepared && ReferenceEquals(cache, EtsEngine.Textures))
                    {
                        cache.AdoptAlphaFromRed(prepared);
                        Uploads++;
                    }
                });

                return need;
            }

            /// <summary>A prefab descriptor read and parsed on the worker (EtsMap.ReadDescriptor) and cached on landing. Null when cached already.</summary>
            private Need Descriptor(string path)
            {
                if (string.IsNullOrEmpty(path) || _map.HasDescriptor(path))
                {
                    return null;
                }

                string key = "d" + path;

                if (_needs.TryGetValue(key, out Need known))
                {
                    return known;
                }

                var need = new Need();
                _needs[key] = need;
                EtsFileSystem files = EtsEngine.Files;
                Post(need, () => EtsMap.ReadDescriptor(files, path), result => _map.AdoptDescriptor(path, result as EtsMap.DescriptorRead));
                return need;
            }
        }

        private string Progress(string stage, EtsMapBuilder.Stats stats)
        {
            return string.Join("\n", _log.ToArray()) + "\nBuilding " + stage + " ... " + stats;
        }

        /// <summary>When the last progress line went to the log (real time).</summary>
        private float _progressLoggedAt = float.MinValue;

        /// <summary>
        /// A build step's progress: the status text gets the whole report as always, the log one line at most once a
        /// second. It used to log the whole report - some 12 lines - every 25 items: the client's Player.log of
        /// 2026-09-23 00:53 holds 59 such progress reports and 62 copies of the same opening lines from one load.
        /// </summary>
        private void ReportProgress(string stage, EtsMapBuilder.Stats stats)
        {
            if (_status != null)
            {
                _status.text = Progress(stage, stats);
            }

            if (Time.realtimeSinceStartup - _progressLoggedAt >= 1f)
            {
                _progressLoggedAt = Time.realtimeSinceStartup;
                Debug.Log("[SomniumEuroTruck] Map: building " + stage + " ... " + stats);
            }
        }

        /// <summary>" 1 sector could not be read: ..." when the reader thread failed on any (they count as absent), else "".</summary>
        private string ReadFailureNote()
        {
            if (_map == null || _map.ReadFailures.Count == 0)
            {
                return "";
            }

            return " " + _map.ReadFailures.Count + " sector(s) could not be read and count as absent: " + string.Join("; ", _map.ReadFailures.ToArray());
        }

        private bool Within(float[] gamePosition, float radiusSquared)
        {
            return DistanceSquared(gamePosition) <= radiusSquared;
        }

        private float DistanceSquared(float[] gamePosition)
        {
            float dx = gamePosition[0] - _centerGame.x;
            float dz = gamePosition[2] - _centerGame.z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// True when this scene has no camera of its own, so the editor needs one to see anything.
        ///
        /// The test counts every camera that draws to the screen, INCLUDING a disabled one, and
        /// deliberately not Camera.main. Two things made the tag test wrong. The truck's camera is
        /// never tagged MainCamera, and - the one that actually bit - the front end PARKS the world
        /// camera while the garage and the launchpad are up (EtsFrontEnd sets its enabled to false),
        /// so Camera.main is null for that whole stretch. Once the map grew big enough that loading
        /// finished after the launchpad appeared, this fired and dropped a second camera into the
        /// scene at (0, 40, -60). The front end then re-enabled the real one, leaving two cameras
        /// drawing to the screen, and the stray one - created later, same depth - drew last. The
        /// truck was being followed the whole time; the view was simply the wrong camera's.
        ///
        /// The mirrors' six cameras render into textures and never count.
        /// </summary>
        /// <summary>
        /// How far the map says an item may be seen from, squared, from the view-distance byte in
        /// its own header. The unit is METRES OVER TEN - a far model of a mountain range carries 40
        /// for 400 m - and the world was ignoring it entirely, drawing 1,262 model items round the
        /// bay where the game draws 242. Most of the surplus is the demo's own backdrop families,
        /// which are built to be looked at through half a kilometre of haze and not from the
        /// pavement. A zero byte means the item has no limit of its own.
        /// </summary>
        private static float ViewDistanceSquared(EtsMapItem item)
        {
            if (item.ViewDistance == 0)
            {
                return float.MaxValue;
            }

            float metres = item.ViewDistance * 10f;
            return metres * metres;
        }

        private static bool NoScreenCamera()
        {
            foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (camera.targetTexture == null)
                {
                    return false;
                }
            }

            return true;
        }

        private void CreateFlyCamera()
        {
            var cameraObject = new GameObject("Ets Map Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 6000f;
            cameraObject.transform.position = new Vector3(0f, 40f, -60f);
            cameraObject.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
            cameraObject.AddComponent<EtsFlyCamera>();

            if (FindAnyObjectByType<Light>() == null)
            {
                var lightObject = new GameObject("Ets Map Light");
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.Soft;
                lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
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

        /// <summary>
        /// A grass terrain material from the archives for the flat stand-in ground. The German
        /// main grass is the first choice; older or newer versions are searched by name.
        /// </summary>
        private static string GroundMaterialPath()
        {
            string[] candidates = { "/material/terrain/grass_ger_main.mat", "/material/terrain/grass_main.mat", "/material/terrain/grass_green_main.mat" };

            foreach (string candidate in candidates)
            {
                if (EtsEngine.Files.Exists(candidate)) return candidate;
            }

            if (EtsEngine.Files.TryList("/material/terrain", out List<string> files, out List<string> _))
            {
                foreach (string file in files)
                {
                    if (file.StartsWith("grass") && file.EndsWith(".mat")) return "/material/terrain/" + file;
                }
            }

            return null;
        }
    }

    /// <summary>Editor-only free camera for looking over a built map. Somnium's rig replaces it.</summary>
    public class EtsFlyCamera : MonoBehaviour
    {
        public float Speed = 30f;
        public float FastMultiplier = 5f;
        public float LookSensitivity = 3f;

        private float _yaw;
        private float _pitch;

        private void Start()
        {
            Vector3 euler = transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x;
        }

        private void Update()
        {
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * LookSensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * LookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            float speed = Speed * (Input.GetKey(KeyCode.LeftShift) ? FastMultiplier : 1f) * Time.deltaTime;
            Vector3 move = Vector3.zero;

            if (Input.GetKey(KeyCode.W)) move += transform.forward;
            if (Input.GetKey(KeyCode.S)) move -= transform.forward;
            if (Input.GetKey(KeyCode.A)) move -= transform.right;
            if (Input.GetKey(KeyCode.D)) move += transform.right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;

            transform.position += move * speed;
        }
    }
}
