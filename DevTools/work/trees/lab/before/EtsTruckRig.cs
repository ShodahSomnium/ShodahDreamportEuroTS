using System;
using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// One built truck: the local player's own, or a remote player's puppet (owner C; PLAN.md §3.1, §5.1,
    /// §7.C). EtsTruckFactory puts it on every truck root it builds - the scene's truck included, which
    /// EtsModelViewer has the factory build since Phase 1 - and fills it in before `done` is called.
    ///
    /// It holds what belongs to one built truck and to no other: the controller, what the preview save
    /// made of it (Assembly, shared data), the add-on models this truck hung (Exterior/InteriorAddons,
    /// which ShowFromInside switches), the interior model's root, the combined exterior locators, and
    /// the reflection probe it refreshes. The per-frame work is the probe's refresh coroutine (one
    /// time-sliced RenderProbe every ReflectionInterval seconds) and, on the new flow, one enum compare
    /// while this is the local player's own or occupied truck (the cab/outside add-on switch, Update).
    ///
    /// The local truck's view (Phase 2, PLAN §3.1 "consumers rebind on EtsLocalTruck.OwnChanged"): when
    /// EtsLocalTruck makes a rig Own or Occupied it calls <see cref="LocalRoleChanged"/>, which points the
    /// rig's mirrors at the local driver kit's truck camera (the one EtsNet.Awake ensured on 'Ets Net') -
    /// EtsMirrors renders only while that camera is in the cab, and with no camera at all it renders every
    /// frame - and starts watching that camera's mode for ShowFromInside, which the viewer's Update does for
    /// the old flow's one truck.
    ///
    /// A remote player's truck (Phase 3, C3; built by EtsTruckFactory with forPuppet): kinematic and placed by A from the
    /// network (EtsTruckController.ApplyPuppet); its cab built only when the local view comes within InteriorRadius or the
    /// local player sits in it (UpdateInterior, SetInteriorVisible); its own engine sound (EtsTruckAudio.ForPuppet); its
    /// door handle (EtsTruckDoor.Create, B); and its boxes solid or see-through to the own truck by Q-A3's 10 km/h rule
    /// (SetVehicleContacts / UpdateVehicleContacts, A calls). Per frame a puppet costs one reference compare, a distance
    /// four times a second, and its sound's distance: 3 puppets and a trailer drawn, 0.02 ms a frame and no allocation
    /// (probe 2250).
    /// </summary>
    public sealed class EtsTruckRig : MonoBehaviour
    {
        public EtsTruckController Controller { get; private set; }
        public EtsTruckIdentity Identity { get; private set; }
        public bool IsPuppet { get; private set; }

        /// <summary>-1 = the local player's.</summary>
        public int OwnerPlayerId { get; private set; } = -1;

        public int OwnerSlot { get; private set; } = -1;

        /// <summary>
        /// Set by EtsJobs (own truck) or EtsNetVehicle (puppet). A puppet's trailer takes the truck's current contact rule
        /// with the own truck (SetVehicleContacts), so a trailer that appears mid-overtake is as see-through as its tractor.
        /// </summary>
        public EtsTrailer Trailer
        {
            get => _trailer;
            set
            {
                _trailer = value;

                if (IsPuppet && value != null)
                {
                    value.SetBoxExcludes(ContactExcludes(VehicleContacts));
                }
            }
        }

        private EtsTrailer _trailer;

        /// <summary>The preview stem ("daf_xf_euro6"), or null for a truck built from the scene's own configuration.</summary>
        public string Stem { get; private set; }

        /// <summary>The save the truck was assembled from; null when the scene's configuration was used without one.</summary>
        public EtsTruckAssembly Assembly { get; private set; }

        /// <summary>The chassis model (the shared truck.pmd, or the chassis of a truck with a cabin of its own); parsed data, shared through EtsEngine's cache.</summary>
        public EtsModelData Model { get; private set; }

        /// <summary>
        /// Every locator the exterior shows (chassis, then a separate cabin's), by name, first one wins -
        /// what add-ons, wheels, the interior and the fifth wheel were hung from.
        /// </summary>
        public Dictionary<string, Transform> Locators { get; private set; } = new Dictionary<string, Transform>(StringComparer.Ordinal);

        /// <summary>The interior model's root (dashboard and interior add-ons hang under it too), or null.</summary>
        public GameObject Interior { get; private set; }

        /// <summary>How the seats were found (EtsTruckSeats.Find), for the log.</summary>
        public string SeatsFrom { get; private set; }

        /// <summary>
        /// The paint its truckpaint materials were made with (the identity's colour, or the save's when the identity says
        /// SavePaint). A puppet trailer is built with its tractor's (EtsTrailer.CreatePuppet's paint overload): the job
        /// trailer's one truckpaint material (1 of the curtainsider's 10, probe 2250) takes the owner's truck colour on
        /// the owner's client, because EtsJobs builds it while the global paint is the own truck's.
        /// </summary>
        public Color Paint { get; private set; } = Color.white;

        internal void SetPaint(Color paint)
        {
            Paint = paint;
        }

        /// <summary>The own truck's mirrors and reflection probe; null on puppets (PLAN §3.1).</summary>
        public EtsMirrors Mirrors { get; private set; }
        public ReflectionProbe Probe { get; private set; }

        /// <summary>An add-on model this truck hung, and whether its accessory also has a model for the other view.</summary>
        public sealed class Mounted
        {
            public string Slot;
            public GameObject Root;
            public bool HasBoth;
        }

        public readonly List<Mounted> ExteriorAddons = new List<Mounted>();
        public readonly List<Mounted> InteriorAddons = new List<Mounted>();

        private float _reflectionInterval = 2f;

        /// <summary>The factory's hand-over; nothing else writes these.</summary>
        internal void Init(EtsTruckController controller, EtsTruckIdentity identity, bool puppet, string stem, EtsTruckAssembly assembly,
                           EtsModelData model, Dictionary<string, Transform> locators, GameObject interior, string seatsFrom)
        {
            Controller = controller;
            Model = model;
            Identity = identity;
            IsPuppet = puppet;
            Stem = stem;
            Assembly = assembly;
            Locators = locators ?? Locators;
            Interior = interior;
            SeatsFrom = seatsFrom;
        }

        internal void SetMirrors(EtsMirrors mirrors)
        {
            Mirrors = mirrors;
        }

        /// <summary>
        /// Mirrors for another player's truck the local player rides in (a puppet has none of its own, PLAN §3.1): made
        /// the first time it is ridden in, on only while it is (LocalRoleChanged). Its cab may still be building (lazy
        /// interior); the mirrors take its glass on when it is done (UpdateInterior → Rescan).
        /// </summary>
        private void RideMirrors()
        {
            if (Controller == null)
            {
                return;
            }

            EtsMirrors mirrors = GetComponent<EtsMirrors>();
            if (mirrors == null) mirrors = gameObject.AddComponent<EtsMirrors>();
            mirrors.Truck = Controller;
            mirrors.Root = transform;
            if (mirrors.View == null) mirrors.View = LocalCamera();
            Mirrors = mirrors;
            mirrors.Build();
        }

        /// <summary>A, for puppets: whose truck this is.</summary>
        public void SetOwner(int playerId, int slot)
        {
            OwnerPlayerId = playerId;
            OwnerSlot = slot;
        }

        /// <summary>
        /// Shows or hides the cab's interior (PLAN §3.1, §7.C). An own truck's interior was built with it, and this switches
        /// it as it always has. A puppet's is built lazily (C3, EtsTruckFactory.Recipe.LazyInterior): the first `true`
        /// starts building it over the next frames (sliced, nothing read from the archives), and it shows once whole.
        ///
        /// Who decides, on a puppet: by default the rig itself ([net §2.6]): shown while the local player sits in it
        /// (EtsLocalTruck.Occupied) or while the local view is within <see cref="InteriorRadius"/> m of it, hidden again
        /// beyond <see cref="InteriorRadiusOut"/> m (hysteresis, so standing at the edge does not flip it). A call here
        /// takes over from the distance rule for that rig (A may want its own rule); sitting in it always shows it.
        /// </summary>
        public void SetInteriorVisible(bool on)
        {
            if (_lazy == null)
            {
                ShowInterior(on);
                return;
            }

            _explicitInterior = on ? 1 : 0;
        }

        /// <summary>Whether this truck has its interior objects (always, except a puppet whose cab nobody has come near yet).</summary>
        public bool InteriorBuilt => _lazy == null || _lazy.Done;

        /// <summary>The local view nearer than this (m) shows a puppet's interior ([net §2.6] InteriorRadius).</summary>
        public const float InteriorRadius = 60f;

        /// <summary>...and further than this hides it again.</summary>
        public const float InteriorRadiusOut = 75f;

        private void ShowInterior(bool on)
        {
            if (Interior != null && Interior.activeSelf != on)
            {
                Interior.SetActive(on);
            }

            foreach (GameObject part in _interiorParts)
            {
                if (part != null && part.activeSelf != on) part.SetActive(on);
            }
        }

        private EtsTruckFactory.LazyInterior _lazy;
        private int _explicitInterior = -1;
        private bool _near;
        private int _interiorShown = -1;
        private int _nextNearCheck;

        /// <summary>The factory's hand-over for a puppet (Recipe.LazyInterior): the cab to build when it is wanted.</summary>
        internal void SetLazyInterior(EtsTruckFactory.LazyInterior lazy)
        {
            _lazy = lazy;
        }

        /// <summary>For probes: the lazy build's frames and worst frame once done ("" before).</summary>
        public string InteriorPace => _lazy == null || !_lazy.Done ? "" : _lazy.Frames + " frames, worst " + _lazy.WorstFrameMs.ToString("0.0") + " ms (" + _lazy.WorstFrameWhat + ")";

        /// <summary>
        /// A puppet's cab, each frame (Update): the distance rule four times a second, then one frame's share of the build
        /// while it is wanted and not yet whole, then showing or hiding it. No allocation.
        /// </summary>
        private void UpdateInterior()
        {
            if (_explicitInterior < 0 && Time.frameCount >= _nextNearCheck)
            {
                _nextNearCheck = Time.frameCount + 15;
                Transform eye = LocalView();

                if (eye != null)
                {
                    float metres = (eye.position - transform.position).magnitude;
                    _near = _near ? metres < InteriorRadiusOut : metres < InteriorRadius;
                }
            }

            bool wanted = ReferenceEquals(EtsLocalTruck.Occupied, this) || (_explicitInterior >= 0 ? _explicitInterior == 1 : _near);

            if (!_lazy.Done)
            {
                if (!wanted || !_lazy.Step())
                {
                    return;
                }

                // Just built: the rig's own now, on the truck's layer (EtsLayers.SetRecursively ran in Configure, before
                // these objects existed), the steering wheel turning with the network's steering, and the cab/outside
                // pairs as the view says.
                Interior = _lazy.Root;
                if (Interior != null) EtsLayers.SetRecursively(Interior.transform, EtsLayers.Vehicle);

                foreach (GameObject part in _interiorParts)
                {
                    if (part != null) EtsLayers.SetRecursively(part.transform, EtsLayers.Vehicle);
                }

                if (Controller != null && _lazy.SteeringWheel != null)
                {
                    Controller.SteeringWheel = _lazy.SteeringWheel;
                }

                // The cab's mirror glass exists only now: a truck ridden in (RideMirrors) takes it on.
                if (Mirrors != null)
                {
                    Mirrors.Rescan();
                }

                _interiorShown = -1;

                if (_watching)
                {
                    _shownInside = -1;
                }
                else
                {
                    ShowFromInside(false);
                }
            }

            int show = wanted ? 1 : 0;

            if (show != _interiorShown)
            {
                _interiorShown = show;
                ShowInterior(wanted);
            }
        }

        // ------------------------------------------------------------------ puppets against the own truck (Q-A3)

        /// <summary>
        /// Q-A3 (the user, 2026-09-22): player trucks collide below 10 km/h of relative speed and pass through above it.
        /// A puppet is drawn about 135 ms behind its driver and is kinematic, an immovable wall to the own truck ([net §8.3]);
        /// at walking pace a bump is harmless and expected (parking side by side), at speed it would be a wreck that never
        /// happened on the other client.
        /// </summary>
        public const float ContactKmh = 10f;

        /// <summary>Solid again only below ContactKmh minus this, so a speed wobbling round 10 km/h does not flip the box every frame.</summary>
        public const float ContactHysteresisKmh = 1f;

        /// <summary>Whether this puppet's boxes (truck and trailer) are solid to the own truck and trailer. True as built.</summary>
        public bool VehicleContacts { get; private set; } = true;

        private static int ContactExcludes(bool solid)
        {
            // Walkers (the avatar's layers) keep meeting it either way: they stand on it and cannot walk through it.
            return solid ? EtsLayers.BodyExcludes : EtsLayers.BodyExcludes | (1 << EtsLayers.Vehicle);
        }

        /// <summary>
        /// The mechanism (A decides when): a puppet's truck and trailer boxes ignore every Vehicle-layer body (false) or meet
        /// them (true). Unity drops a pair when EITHER collider excludes the other's layer, so this one side is enough for
        /// the own truck and its trailer (probe 2250 checks it). Puppets only; an own truck's box follows its driver
        /// (EtsTruckController.SetDriven).
        /// </summary>
        public void SetVehicleContacts(bool solid)
        {
            if (!IsPuppet || VehicleContacts == solid)
            {
                return;
            }

            VehicleContacts = solid;
            int layers = ContactExcludes(solid);

            if (Controller != null && Controller.BodyBox != null)
            {
                Controller.BodyBox.excludeLayers = layers;
            }

            if (_trailer != null)
            {
                _trailer.SetBoxExcludes(layers);
            }
        }

        /// <summary>
        /// The Q-A3 rule, ready for A to call once a frame per puppet after ApplyPuppet: the puppet's drawn velocity
        /// (EtsTruckController.PuppetVelocity) against the own truck's physics velocity; see-through at ContactKmh and
        /// above, solid again below ContactKmh - ContactHysteresisKmh, but never while the two overlap (turning solid inside
        /// another box would shove the own truck out at the depenetration speed, as EtsTrailer.Detach explains). With no
        /// own truck, the own speed is 0. Returns the state it left. No allocation.
        /// </summary>
        public bool UpdateVehicleContacts(EtsTruckRig own)
        {
            if (!IsPuppet || Controller == null)
            {
                return VehicleContacts;
            }

            EtsTruckController mine = own != null && !ReferenceEquals(own, this) ? own.Controller : null;
            Vector3 ownVelocity = mine != null && mine.Body != null && !mine.Body.isKinematic ? mine.Body.linearVelocity : Vector3.zero;
            float relativeKmh = (Controller.PuppetVelocity - ownVelocity).magnitude * 3.6f;
            bool solid = VehicleContacts
                ? relativeKmh < ContactKmh
                : relativeKmh < ContactKmh - ContactHysteresisKmh && !Overlaps(own);
            SetVehicleContacts(solid);
            return solid;
        }

        /// <summary>Whether this puppet's truck or trailer box overlaps the own truck's or trailer's (world boxes, 0.25 m apart counted as touching).</summary>
        private bool Overlaps(EtsTruckRig own)
        {
            if (own == null || own.Controller == null)
            {
                return false;
            }

            Collider a1 = Controller.BodyBox, a2 = _trailer != null ? _trailer.BodyBox : null;
            Collider b1 = own.Controller.BodyBox, b2 = own.Controller.Trailer != null ? own.Controller.Trailer.BodyBox : null;
            return Touch(a1, b1) || Touch(a1, b2) || Touch(a2, b1) || Touch(a2, b2);
        }

        private static bool Touch(Collider a, Collider b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            Bounds x = a.bounds;
            x.Expand(0.5f);
            return x.Intersects(b.bounds);
        }

        /// <summary>The animated dashboard: built beside the interior on the same locator, not under it, so it is switched with it.</summary>
        internal void AddInteriorPart(GameObject part)
        {
            if (part != null) _interiorParts.Add(part);
        }

        private readonly List<GameObject> _interiorParts = new List<GameObject>();

        /// <summary>
        /// The game shows an accessory's interior_model instead of its exterior_model while the camera
        /// is in the cab - not both. Mirrors, sun visors and the like have both, and hanging both at once
        /// put two mirror housings on every arm from the driver's seat. This switches the pairs; add-ons
        /// with only one model stay as they are.
        /// </summary>
        public void ShowFromInside(bool inside)
        {
            foreach (Mounted mounted in ExteriorAddons)
            {
                if (mounted.HasBoth && mounted.Root != null) mounted.Root.SetActive(!inside);
            }

            foreach (Mounted mounted in InteriorAddons)
            {
                if (mounted.HasBoth && mounted.Root != null) mounted.Root.SetActive(inside);
            }
        }

        // ------------------------------------------------------------------ the local truck's view (new flow)

        /// <summary>The camera whose mode switches the add-on pairs and shows the mirrors; set while this is the local Own or Occupied truck.</summary>
        private EtsTruckCamera _view;
        private bool _watching;
        private int _shownInside = -1;

        /// <summary>
        /// EtsLocalTruck, after this rig became or stopped being the local player's Own or Occupied truck (new flow
        /// only; the old flow never calls SetOwn or SetOccupied, and its viewer wires the scene truck itself).
        /// Local: the mirrors look through the local truck camera, and the cab/outside pairs follow its mode.
        /// Not local (a puppet nobody here sits in, or the own truck after a despawn): the outside models, and the
        /// mirrors keep their last camera (only own trucks have mirrors, PLAN §3.1).
        /// </summary>
        internal void LocalRoleChanged()
        {
            // The own truck's mirrors rest while the local player rides in another truck (C3): EtsMirrors renders whenever the
            // truck camera is in a cab (EtsMirrors.LateUpdate, "wanted"), whichever cab, so a passenger on a monitor paid for
            // three mirror cameras on a truck they had left behind. They come back when the player is out or in this one.
            bool ridden = this != null && ReferenceEquals(EtsLocalTruck.Occupied, this);

            if (ridden && IsPuppet && Mirrors == null)
            {
                RideMirrors();
            }

            if (Mirrors != null)
            {
                // A ridden puppet's mirrors (RideMirrors) only while the local player sits in it.
                bool mine = IsPuppet ? ridden : EtsLocalTruck.Occupied == null || ridden;

                if (Mirrors.enabled != mine)
                {
                    Mirrors.enabled = mine;
                }

                if (mine)
                {
                    Mirrors.Rescan();
                }
            }

            bool local = this != null && (EtsLocalTruck.Own == this || EtsLocalTruck.Occupied == this);

            if (!local)
            {
                _watching = false;
                _view = null;

                if (_shownInside != 0)
                {
                    _shownInside = 0;
                    ShowFromInside(false);
                }

                return;
            }

            _view = LocalCamera();
            _watching = true;
            _shownInside = -1;

            if (Mirrors != null && _view != null)
            {
                Mirrors.View = _view;
            }
        }

        private static int _viewFrame = -1;
        private static Transform _view0;

        /// <summary>
        /// Where the local player looks from (the headset's camera, else the main camera), looked up once a frame and shared
        /// by every truck's distance rules (EtsTruckAudio's fade and the puppet sound's 300 m cut, UpdateInterior). Each
        /// lookup is EtsVr.Present - XRSettings.loadedDeviceName, a native string made anew on every read while a headset
        /// is active - and, when Camera.main is missing or off, EtsVr.Head's FindObjectsByType, a scene search that returns
        /// a new array. Per truck that was two lookups a frame for each of up to 16 remote trucks (C3 checker); now it is
        /// one a frame. The Transform is cached, not its position, so a view that moves within the frame is read live.
        /// </summary>
        internal static Transform LocalView()
        {
            int frame = Time.frameCount;

            // Again within the frame only when the camera found was destroyed since (a frame that found none keeps none).
            if (frame != _viewFrame || (!ReferenceEquals(_view0, null) && _view0 == null))
            {
                _viewFrame = frame;
                Camera camera = EtsVr.Present ? EtsVr.Head() : Camera.main;
                _view0 = camera != null ? camera.transform : null;
            }

            return _view0;
        }

        /// <summary>The local driver kit's truck camera: on 'Ets Net' (EtsNet.Awake → EtsTruckCamera.Ensure, PLAN §3.2), else any in the scene.</summary>
        internal static EtsTruckCamera LocalCamera()
        {
            EtsNet net = EtsNet.Instance;
            EtsTruckCamera camera = net != null ? net.GetComponent<EtsTruckCamera>() : null;
            return camera != null ? camera : FindFirstObjectByType<EtsTruckCamera>();
        }

        private void Update()
        {
            if (_lazy != null)
            {
                UpdateInterior();
            }

            if (!_watching)
            {
                return;
            }

            if (_view == null)
            {
                // The kit may be added after the rig became local (the editor, a late bootstrap): once a second.
                if (Time.frameCount % 90 != 0) return;
                _view = LocalCamera();
                if (_view == null) return;
                if (Mirrors != null) Mirrors.View = _view;
            }

            // Inside only while this truck is the one the camera shows from its cab. A headset is always in the cab
            // (EtsTruckCamera.Bind sets Interior), and so the pairs follow the camera there too.
            int inside = _view.Active && _view.Truck == Controller && _view.View == EtsTruckCamera.Mode.Interior ? 1 : 0;

            if (inside != _shownInside)
            {
                _shownInside = inside;
                ShowFromInside(inside == 1);
            }
        }

        /// <summary>
        /// Stands the truck on the ground at a spot (Unity position and yaw) once the ground is there, the way
        /// EtsModelViewer.PlaceWhenGroundExists always has (for A's SpawnOwnTruck, PLAN §5.5 step 8, and the editor
        /// truck): wait up to `mapWait` seconds for the map's first build (EtsMapViewer.AnyCompleted), then one
        /// FixedUpdate, then a ray from 200 m above the spot every 0.25 s, up to 120 times (30 s), and only when it
        /// finds ground, EtsTruckController.PlaceOnGround. Parked (SetDriven(false)) first when `parked`: a player on
        /// foot gets in by clicking it. `done` gets whether the truck was placed. Nothing is placed without ground
        /// under it - setting a truck down anyway is what once dropped it through the map.
        /// </summary>
        public IEnumerator PlaceWhenGroundExists(Vector3 position, float yaw, bool parked, float mapWait, Action<bool> done)
        {
            float deadline = Time.time + mapWait;

            while (!EtsMapViewer.AnyCompleted && Time.time < deadline)
            {
                yield return null;
            }

            yield return new WaitForFixedUpdate();
            EtsTruckController truck = Controller;

            if (truck == null)
            {
                done?.Invoke(false);
                yield break;
            }

            if (parked)
            {
                truck.SetDriven(false);
            }

            bool grounded = false;

            for (int attempt = 0; attempt < 120 && !grounded && truck != null; attempt++)
            {
                if (Physics.Raycast(position + Vector3.up * 200f, Vector3.down, 600f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    grounded = truck.PlaceOnGround(position, yaw);
                }

                if (!grounded)
                {
                    yield return new WaitForSeconds(0.25f);
                }
            }

            done?.Invoke(grounded);
        }

        /// <summary>
        /// The game renders an environment map around the truck for its chrome, glass and paint to
        /// reflect. A realtime probe above the cab does the same here: one cube face per frame, the
        /// whole cube refreshed every interval, at a fraction of what one mirror costs. The near
        /// plane keeps the roof out of it. Own trucks only (PLAN §3.1).
        ///
        /// "One face per frame" is ReflectionProbeTimeSlicingMode.IndividualFaces (Phase 2): the probe was
        /// created with the default, AllFacesAtOnce, which renders all six faces of the world within 220 m in
        /// the frame RenderProbe is called - a hitch every interval that VR at 90 Hz cannot hide. Time-sliced,
        /// a refresh is spread over nine frames (six faces, then the mips). Its cost in the client is not
        /// measured (probes render only with a graphics device; every offline check here runs without one).
        /// </summary>
        internal void SetUpReflections(float interval)
        {
            if (Probe != null)
            {
                return;
            }

            _reflectionInterval = interval;
            var holder = new GameObject("reflection probe");
            holder.transform.SetParent(transform, false);
            holder.transform.localPosition = new Vector3(0f, 4.5f, 0f);
            ReflectionProbe probe = holder.AddComponent<ReflectionProbe>();
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
            probe.resolution = 64;
            probe.hdr = false;
            probe.size = new Vector3(400f, 200f, 400f);
            probe.nearClipPlane = 2f;
            probe.farClipPlane = 220f;
            probe.shadowDistance = 0f;
            probe.importance = 2;
            probe.boxProjection = false;

            // Reflect the world, not the truck: the cab and trailer would otherwise fill the cube
            // and there would be nothing to see in the chrome. The truck is on the Vehicle layer.
            probe.cullingMask = ~(1 << EtsLayers.Vehicle);
            Probe = probe;
            StartCoroutine(RefreshReflections());
        }

        private IEnumerator RefreshReflections()
        {
            // A small, low-resolution cube refreshed slowly. Chrome and paint do not need a
            // per-frame mirror of the road, and a full realtime probe every frame is what makes a
            // weak machine stutter. Rendered once at the start, then on a relaxed timer (one wait object for
            // the truck's life: no allocation per refresh).
            var wait = new WaitForSeconds(Mathf.Max(0.5f, _reflectionInterval));

            while (Probe != null)
            {
                Probe.RenderProbe();
                yield return wait;
            }
        }
    }

    /// <summary>
    /// The local player's trucks: Own (the one they spawned) and Occupied (the one they sit in, own or a
    /// puppet), with change events the per-player driver kit rebinds on (owner C; PLAN.md §3.1, §7.C).
    /// The setters store, tell the rigs concerned (EtsTruckRig.LocalRoleChanged: the mirrors' camera and the
    /// cab/outside add-ons) and announce, and Reset clears. A calls SetOwn after a spawn or despawn, B
    /// SetOccupied on seat and release (Phase 2), and EtsModelViewer SetOwn for the editor's truck only
    /// (no Somnium client, so no tablet to spawn one from). EtsNet.OnDestroy calls Reset, because statics
    /// outlive a world visit (PLAN §3.2, persist §2.3) and a rig from the last visit would be a destroyed
    /// object; Reset also stops the factory's worker thread.
    /// </summary>
    public static class EtsLocalTruck
    {
        public static EtsTruckRig Own { get; private set; }
        public static EtsTruckRig Occupied { get; private set; }

        public static event Action<EtsTruckRig> OwnChanged;
        public static event Action<EtsTruckRig> OccupiedChanged;

        /// <summary>
        /// A, after a spawn or despawn. The same rig twice announces once. The test is by reference, not Unity's ==: a rig
        /// destroyed before SetOwn(null) (a despawn that destroys first) compares equal to null under Unity's rules, so
        /// SetOwn(null) would return without telling anyone the truck is gone (probe 2031 Local).
        /// </summary>
        public static void SetOwn(EtsTruckRig rig)
        {
            if (ReferenceEquals(Own, rig))
            {
                return;
            }

            EtsTruckRig was = Own;
            Own = rig;
            Tell(was);
            Tell(rig);
            OwnChanged?.Invoke(rig);
        }

        /// <summary>B, on seat and release. By reference, as SetOwn.</summary>
        public static void SetOccupied(EtsTruckRig rig)
        {
            if (ReferenceEquals(Occupied, rig))
            {
                return;
            }

            EtsTruckRig was = Occupied;
            Occupied = rig;
            Tell(was);
            Tell(rig);

            // The own truck too, when it is neither: its mirrors rest while the player rides elsewhere (LocalRoleChanged).
            if (!ReferenceEquals(Own, was) && !ReferenceEquals(Own, rig))
            {
                Tell(Own);
            }

            OccupiedChanged?.Invoke(rig);
        }

        /// <summary>Unity's null test: a despawned rig is a destroyed object that == null, and has nothing to switch.</summary>
        private static void Tell(EtsTruckRig rig)
        {
            if (rig != null)
            {
                rig.LocalRoleChanged();
            }
        }

        /// <summary>EtsNet.OnDestroy: forget the rigs and every subscriber of the visit that is ending, and stop the factory's worker.</summary>
        public static void Reset()
        {
            Own = null;
            Occupied = null;
            OwnChanged = null;
            OccupiedChanged = null;
            EtsTruckFactory.Shutdown();
        }
    }
}
