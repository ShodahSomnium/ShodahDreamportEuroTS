using System;
using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// A semi-trailer: the game's company curtainsider (two axles) built from its chassis
    /// definition and wheel definition, hung on a rigid body with raycast wheels like the truck's,
    /// and coupled to the truck's fifth wheel at its kingpin by a joint that swivels freely,
    /// pitches within the fifth wheel's limit and rolls within its play - both from physics.sii.
    /// The trailer brakes when the truck brakes and holds when parked.
    /// </summary>
    public class EtsTrailer : MonoBehaviour
    {
        public sealed class Wheel
        {
            public Transform Visual;
            public Quaternion VisualRest;
            public float SpinSign = 1f;
            public Vector3 LocalHub;
            public float Radius = 0.51f;
            public float Spring;
            public float Damper;
            public float RestLength;
            public float Travel = 0.2f;
            public bool Grounded;
            public float Compression;
            public float SpinDegrees;
            public RaycastHit Hit;
        }

        public const string DefaultChassisDefinition = "/def/vehicle/trailer/scs_box/curtain_sider/chassis.sii";
        public const string DefaultModel = "/vehicle/trailer_eu/scs_box/curtain_sider/ch_2_lod0.pmd";
        public const string DefaultWheelDefinition = "/def/vehicle/t_wheel/single_385_55_steel.sii";
        public const float Sag = 0.1f;
        public const float DampingRatio = 0.4f;
        public const float CorneringStiffness = 8f;
        public const float Grip = 0.9f;

        public EtsTruckController Truck;
        public Rigidbody Body;
        public Transform Hook;
        public float ChassisMass = 5860f;
        public float CargoMass;
        public float FifthWheelPitch = 20f;
        public float FifthWheelRoll = 3f;
        public string Description = "curtainsider";

        private ConfigurableJoint _joint;
        private BoxCollider _box;
        private readonly List<Wheel> _wheels = new List<Wheel>();
        private bool _ready;

        public bool Attached => _joint != null;

        // ------------------------------------------------------------------ hook / unhook by the driver (2026-09-23)
        //
        // ETS2's T key ("Trailer Attach/Detach", locale o_im_attach): a trailer uncoupled by its driver stays where it
        // stands - kinematic, solid, legs down - and can be coupled again by backing the fifth wheel under its kingpin.
        // The truck side (the key, the right stick, the tolerances, the messages) is EtsTruckController.ToggleTrailer.

        /// <summary>
        /// Uncoupled by its driver (Uncouple) and standing where it was left: it may be coupled again (Couple). Cleared by
        /// any other Detach (a delivery, a job cut) and by coupling. A job's trailer stays the job's meanwhile (EtsJobs keeps
        /// it; its delivery check reads the trailer's own position), so leaving it does not abandon the job.
        /// </summary>
        public bool Parked { get; private set; }

        /// <summary>Every trailer standing Parked in this client, for the truck's coupling search. Usually none or one.</summary>
        public static IReadOnlyList<EtsTrailer> ParkedTrailers => s_parked;

        private static readonly List<EtsTrailer> s_parked = new List<EtsTrailer>();

        // The truck it was uncoupled from: its box is let through the trailer's while it backs in under the nose (Update).
        private EtsTruckController _parkedBy;

        // The landing gear's feet as the model has them: brace_on (down, on the ground, parked) and brace_off (wound up,
        // coupled). The chassis model shows neither in any variant - the game switches them - so Create builds each alone.
        private GameObject _legsDown;
        private GameObject _legsUp;

        /// <summary>Metres from the fifth wheel to the kingpin inside which a truck backing in counts as coming under the nose.</summary>
        public const float ApproachReach = 8f;

        /// <summary>
        /// The landing gear down (parked) or wound up (coupled). Attach, Couple, Uncouple and Detach set it; a puppet's owner
        /// may too (EtsNetVehicle, from the wire's coupled flag). Nothing happens when the model has no brace parts.
        /// </summary>
        public void ShowLegs(bool down)
        {
            if (_legsDown != null && _legsDown.activeSelf != down) _legsDown.SetActive(down);
            if (_legsUp != null && _legsUp.activeSelf == down) _legsUp.SetActive(!down);
        }

        /// <summary>
        /// Where the kingpin (Hook) stands from a truck's fifth wheel: the distance across the ground, the height difference,
        /// and the angle between the two headings (0 = in line). False when either end is missing.
        /// </summary>
        public bool MeasureCoupling(EtsTruckController truck, out float across, out float height, out float angle)
        {
            across = height = angle = float.MaxValue;

            if (truck == null || Hook == null)
            {
                return false;
            }

            Transform fifthWheel = truck.FifthWheel != null ? truck.FifthWheel : truck.transform;
            Vector3 d = Hook.position - fifthWheel.position;
            across = new Vector2(d.x, d.z).magnitude;
            height = Mathf.Abs(d.y);
            angle = Vector3.Angle(Flat(truck.transform.forward), Flat(transform.forward));
            return true;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v : Vector3.forward;
        }

        /// <summary>
        /// The driver's uncoupling (T): the joint goes and the trailer stays where it stands, parked (kinematic, solid to
        /// walkers and traffic), its legs down. The job it carries goes on. False when it was not coupled.
        /// </summary>
        public bool Uncouple()
        {
            if (_joint == null || Truck == null)
            {
                return false;
            }

            EtsTruckController from = Truck;
            Detach();
            SetParked(true, from);
            return true;
        }

        /// <summary>
        /// The driver's coupling (T) of a parked trailer: the kingpin is moved onto the fifth wheel (the truck's caller has
        /// checked it is within the tolerance) keeping the trailer's own heading - the joint swivels freely, so a trailer
        /// coupled at an angle straightens as the truck pulls away - and joined there. Legs up.
        /// </summary>
        public void Couple(EtsTruckController truck, Transform fifthWheel)
        {
            Detach();
            Truck = truck;
            Transform t = transform;
            t.position += fifthWheel.position - Hook.position;
            Join(truck, fifthWheel);
        }

        private void SetParked(bool parked, EtsTruckController by)
        {
            Parked = parked;
            _parkedBy = parked ? by : null;

            if (parked)
            {
                if (!s_parked.Contains(this)) s_parked.Add(this);
            }
            else
            {
                s_parked.Remove(this);
            }
        }

        private void OnDestroy()
        {
            s_parked.Remove(this);
        }

        /// <summary>The own coupled trailer's hits (EtsDamage: trailer, cargo and, by truck_to_trailer_dmg, the truck).</summary>
        private void OnCollisionEnter(Collision collision)
        {
            EtsDamage.TrailerCollision(this, collision);
        }

        /// <summary>The trailer's body box (Create), for the Q-A3 overlap test (EtsTruckRig.UpdateVehicleContacts).</summary>
        public BoxCollider BodyBox => _box;

        /// <summary>A remote player's trailer drawn here (CreatePuppet): kinematic, never coupled, placed by ApplyPuppet.</summary>
        public bool IsPuppet { get; private set; }

        // ------------------------------------------------------------------ identity (PLAN §7.C; [net §2.4] ints 19-20)

        /// <summary>
        /// The trailer chassis definitions the world builds, by EtsTrailerIdentity.Family. The world builds one kind today:
        /// every job's trailer is the company curtainsider (EtsJobs: Create with no definition, so DefaultChassisDefinition),
        /// and a puppet must look like the trailer its owner pulls. So the list is fixed in code, not read from the archives:
        /// an index means the same trailer on every install and needs no catalogue hash, and a kind added later goes on the
        /// end. An index out of range (a newer client's kind) falls back to the curtainsider, as [net §2.11] asks.
        /// </summary>
        public static readonly string[] ChassisDefinitions = { DefaultChassisDefinition };

        /// <summary>The chassis definition an identity names (the curtainsider for any index this build does not know).</summary>
        public static string DefinitionFor(EtsTrailerIdentity id)
        {
            return id.Family >= 0 && id.Family < ChassisDefinitions.Length ? ChassisDefinitions[id.Family] : DefaultChassisDefinition;
        }

        /// <summary>The family index of a chassis definition (0, the curtainsider, for null or one not in the list).</summary>
        public static int FamilyOf(string chassisDefinition)
        {
            int index = string.IsNullOrEmpty(chassisDefinition) ? 0 : Array.IndexOf(ChassisDefinitions, chassisDefinition);
            return index < 0 ? 0 : index;
        }

        private EtsTrailerIdentity _identity;

        /// <summary>
        /// Which trailer this is, as it crosses the wire (PLAN §7.C; EtsPlayerWire.PackTrailer): Family from the chassis
        /// definition it was built from, Variant 0 (Create builds the "default" look and variant), and the load that
        /// SetLoadIdentity gave it (cargo index and company hash; 0 until then). A puppet carries the identity it was made from.
        /// </summary>
        public EtsTrailerIdentity Identity => _identity;

        /// <summary>
        /// G, when a job's trailer is loaded: the cargo (index in the sorted /def cargo list, [net §2.4] int 19 bits 16-31) and
        /// the company's token hash (int 20, for a logo later). Neither changes how the trailer is drawn today (the curtain
        /// hides the cargo, and no logo is drawn), so a trailer without them is drawn the same.
        /// </summary>
        public void SetLoadIdentity(int cargo, int companyHash)
        {
            _identity.Cargo = cargo;
            _identity.CompanyHash = companyHash;
        }

        /// <summary>
        /// The model files a trailer is built from: its chassis definition's "model" (DefaultModel when the definition is
        /// missing or names a file that is not there) and its wheel definition's "model" (null when none). Reads the two
        /// definitions through ReadSii (cached).
        /// </summary>
        public static void ResolveModels(string chassisDefinition, string wheelDefinition, out string modelPath, out string wheelPath)
        {
            modelPath = DefaultModel;
            wheelPath = null;
            EtsSiiFile chassis = EtsEngine.Files.ReadSii(chassisDefinition ?? DefaultChassisDefinition);

            if (chassis != null && chassis.Units.Count > 0)
            {
                string model = chassis.Units[0].GetString("model", null);
                if (!string.IsNullOrEmpty(model) && EtsEngine.Files.Exists(model)) modelPath = model;
            }

            EtsSiiFile wheelDef = EtsEngine.Files.ReadSii(wheelDefinition ?? DefaultWheelDefinition);

            if (wheelDef != null && wheelDef.Units.Count > 0)
            {
                string path = wheelDef.Units[0].GetString("model", null);
                if (!string.IsNullOrEmpty(path)) wheelPath = path;
            }
        }

        // ------------------------------------------------------------------ puppets (C3)

        /// <summary>
        /// A kinematic trailer drawn behind a remote player's truck (PLAN §7.C, [net §2.6] "Trailer"), built in one call:
        /// Create from the identity's definition, then no joint, no physics of its own (FixedUpdate never runs it), Rigidbody
        /// interpolation off (it is placed by transform every frame, as the truck puppet is), and the box solid to walkers so
        /// riders can stand on it (EtsLayers.BodyExcludes; the Q-A3 rule then follows its truck, EtsTruckRig.Trailer). The
        /// global paint is set for the call and handed back: white here, whatever the local truck's is; the overload takes
        /// the tractor's (EtsTruckRig.Paint), which is what the owner's own client paints its one truckpaint material with.
        /// Cold (first trailer of the visit) this reads and uploads everything on the frame; BuildPuppet is the sliced way.
        /// Warm it is objects only (about 1 ms, probe 2250). Null when the archives are not mounted or the model cannot be
        /// read (logged).
        /// </summary>
        public static EtsTrailer CreatePuppet(EtsTrailerIdentity id, Transform parent)
        {
            return CreatePuppet(id, parent, Color.white);
        }

        /// <summary>CreatePuppet with its tractor's paint (EtsTruckRig.Paint), so its one truckpaint material matches the owner's trailer.</summary>
        public static EtsTrailer CreatePuppet(EtsTrailerIdentity id, Transform parent, Color paint)
        {
            if (!EtsEngine.IsMounted)
            {
                return null;
            }

            var log = new List<string>();
            Color was = EtsMaterialCache.PaintColor;
            EtsMaterialCache.PaintColor = paint;
            EtsTrailer trailer;

            try
            {
                trailer = Create(parent, DefinitionFor(id), null, null, log);
            }
            finally
            {
                EtsMaterialCache.PaintColor = was;
            }

            if (trailer == null)
            {
                Debug.LogWarning("[SomniumEuroTruck] Trailer (puppet): " + string.Join(" ", log.ToArray()));
                return null;
            }

            trailer.gameObject.name = "Trailer (puppet)";
            trailer.IsPuppet = true;
            trailer._identity = id;

            // Drawn behind its tractor until told otherwise (ShowLegs, from the wire's coupled flag): legs wound up.
            trailer.ShowLegs(false);

            if (trailer.Body != null)
            {
                trailer.Body.isKinematic = true;
                trailer.Body.interpolation = RigidbodyInterpolation.None;
            }

            trailer.SetBoxExcludes(EtsLayers.BodyExcludes);
            return trailer;
        }

        /// <summary>
        /// CreatePuppet, sliced (C3): the definitions read in one step, then the trailer's files read on the truck factory's
        /// worker and its textures, materials and meshes made one a step within the factory's 1.5 ms budget
        /// (EtsTruckFactory.PrepareTrailer), then CreatePuppet, which now only makes objects. The first trailer of a visit is
        /// the expensive one; every later one finds all of it prepared. `failed` when the archives are not mounted, the parent
        /// is removed meanwhile, or the model cannot be built.
        /// </summary>
        public static IEnumerator BuildPuppet(EtsTrailerIdentity id, Transform parent, Action<EtsTrailer> done, Action<string> failed)
        {
            return BuildPuppet(id, parent, Color.white, done, failed);
        }

        /// <summary>BuildPuppet with its tractor's paint (EtsTruckRig.Paint), so its one truckpaint material matches the owner's trailer.</summary>
        public static IEnumerator BuildPuppet(EtsTrailerIdentity id, Transform parent, Color paint, Action<EtsTrailer> done, Action<string> failed)
        {
            if (!EtsEngine.IsMounted)
            {
                failed?.Invoke("The game's archives are not mounted.");
                yield break;
            }

            bool hadParent = parent != null;
            ResolveModels(DefinitionFor(id), null, out string modelPath, out string wheelPath);
            yield return null;
            var log = new List<string>();
            IEnumerator prepare = EtsTruckFactory.PrepareTrailer(modelPath, wheelPath, paint, log);

            while (prepare.MoveNext())
            {
                yield return prepare.Current;
            }

            if ((hadParent && parent == null) || !EtsEngine.IsMounted)
            {
                failed?.Invoke("The trailer's holder was removed during the build.");
                yield break;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            EtsTrailer trailer = CreatePuppet(id, parent, paint);

            if (trailer == null)
            {
                failed?.Invoke("The trailer " + modelPath + " could not be built.");
                yield break;
            }

            LastBuild = string.Join(" ", log.ToArray()) + " Objects in " + clock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms.";
            done?.Invoke(trailer);
        }

        /// <summary>The last BuildPuppet's summary (prepare frames, the object step's cost), for probes.</summary>
        public static string LastBuild { get; private set; } = "";

        /// <summary>
        /// One frame of a puppet trailer as the network draws it: placed by transform, its wheels hung at this client's ground
        /// under them and turned by the speed (as EtsTruckController.ApplyPuppet does for the tractor; FixedUpdate never runs on
        /// a kinematic trailer). `forwardSpeed` in m/s along the trailer (the tractor's drawn speed will do). No allocation.
        /// </summary>
        public void ApplyPuppet(Vector3 position, Quaternion rotation, float forwardSpeed, float dt)
        {
            if (!IsPuppet)
            {
                return;
            }

            transform.SetPositionAndRotation(position, rotation);
            Vector3 up = rotation * Vector3.up;

            foreach (Wheel wheel in _wheels)
            {
                wheel.SpinDegrees += forwardSpeed / wheel.Radius * Mathf.Rad2Deg * dt;
                wheel.Grounded = EtsTruckController.CastWheel(transform.TransformPoint(wheel.LocalHub), up, wheel.Radius, wheel.RestLength + wheel.Travel, out wheel.Hit);

                if (wheel.Visual == null)
                {
                    continue;
                }

                float drop = wheel.Grounded ? Mathf.Clamp(wheel.Hit.distance - wheel.Radius, -wheel.Travel, wheel.Travel) : wheel.Travel;
                wheel.Visual.localPosition = wheel.LocalHub - Vector3.up * drop;
                wheel.Visual.localRotation = wheel.VisualRest * Quaternion.Euler(wheel.SpinDegrees * wheel.SpinSign, 0f, 0f);
            }
        }

        /// <summary>Builds the trailer from the game's definitions under a parent. Kinematic until attached.</summary>
        public static EtsTrailer Create(Transform parent, string chassisDefinition, string wheelDefinition, EtsVehicleSpec truckSpec, List<string> log)
        {
            ResolveModels(chassisDefinition, wheelDefinition, out string modelPath, out string wheelPath);
            EtsModelData data = EtsEngine.LoadModel(modelPath, out string failure);

            if (data == null)
            {
                log.Add("Trailer: " + failure);
                return null;
            }

            var root = new GameObject("Trailer");
            root.transform.SetParent(parent, false);
            var trailer = root.AddComponent<EtsTrailer>();
            trailer._identity = new EtsTrailerIdentity { Family = FamilyOf(chassisDefinition) };

            EtsModelBuilder.Result built = EtsModelBuilder.Build(data, new EtsModelBuilder.Options
            {
                Look = "default",
                Variants = new[] { "default" },
                CreateLocators = true,
                CastShadows = true
            }, root.transform, EtsEngine.Materials);

            // The landing gear's feet, each part built alone (no variant shows them); down until it is coupled.
            trailer._legsDown = BuildPart(data, "brace_on", "Legs down", root.transform);
            trailer._legsUp = BuildPart(data, "brace_off", "Legs up", root.transform);
            trailer.ShowLegs(true);

            // Wheels from the wheel definition, left and right variants by the locator's side.
            EtsModelData wheelModel = null;

            if (!string.IsNullOrEmpty(wheelPath))
            {
                wheelModel = EtsEngine.LoadModel(wheelPath, out string wheelFailure);
            }

            float radius = 0.51f;

            if (wheelModel != null && wheelModel.BoundsMax[1] - wheelModel.BoundsMin[1] > 0.2f)
            {
                radius = (wheelModel.BoundsMax[1] - wheelModel.BoundsMin[1]) * 0.5f;
            }

            foreach (KeyValuePair<string, Transform> entry in built.Locators)
            {
                if (!entry.Key.StartsWith("wheel_", StringComparison.Ordinal)) continue;

                if (wheelModel != null)
                {
                    string side = entry.Value.localPosition.x < 0f ? "left" : "right";
                    EtsModelBuilder.Build(wheelModel, new EtsModelBuilder.Options { Look = "default", Variants = new[] { side, "default" }, CreateLocators = false }, entry.Value, EtsEngine.Materials);
                }

                var wheel = new Wheel
                {
                    Visual = entry.Value,
                    VisualRest = entry.Value.localRotation,
                    LocalHub = root.transform.InverseTransformPoint(entry.Value.position),
                    Radius = radius
                };
                wheel.SpinSign = Vector3.Dot(wheel.VisualRest * Vector3.right, Vector3.right) >= 0f ? 1f : -1f;
                trailer._wheels.Add(wheel);
            }

            trailer.Hook = EtsTruckFactory.FindLocator(built.Locators, "hook");

            if (trailer.Hook == null)
            {
                var hook = new GameObject("hook");
                hook.transform.SetParent(root.transform, false);
                hook.transform.localPosition = new Vector3(0f, 1f, 5.07f);
                trailer.Hook = hook.transform;
            }

            if (truckSpec != null)
            {
                trailer.FifthWheelPitch = truckSpec.FifthWheelPitch;
                trailer.FifthWheelRoll = truckSpec.FifthWheelRoll;
            }

            trailer.Body = root.AddComponent<Rigidbody>();
            trailer.Body.interpolation = RigidbodyInterpolation.Interpolate;
            trailer.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            trailer.Body.angularDamping = 0.8f;
            trailer.Body.isKinematic = true;

            var box = root.AddComponent<BoxCollider>();
            Vector3 min = EtsModelBuilder.ToUnity(data.BoundsMin[0], data.BoundsMin[1], data.BoundsMax[2]);
            Vector3 max = EtsModelBuilder.ToUnity(data.BoundsMax[0], data.BoundsMax[1], data.BoundsMin[2]);
            float bottom = Mathf.Max(min.y, 0.65f);
            box.center = new Vector3((min.x + max.x) * 0.5f, (bottom + max.y) * 0.5f, (min.z + max.z) * 0.5f);
            box.size = new Vector3(Mathf.Max(0.5f, max.x - min.x), Mathf.Max(0.5f, max.y - bottom), Mathf.Max(0.5f, max.z - min.z));

            EtsLayers.SetRecursively(root.transform, EtsLayers.Vehicle);
            box.excludeLayers = EtsLayers.BodyExcludes;
            trailer._box = box;
            trailer.SetCargoMass(0f);
            log.Add("Trailer " + modelPath + ": " + built.PiecesDrawn + " pieces, " + trailer._wheels.Count + " wheels of radius " + radius.ToString("0.00") + " m" +
                    (trailer._legsDown != null && trailer._legsUp != null ? ", landing gear feet (brace_on / brace_off)." : ", no landing gear parts in the model."));
            return trailer;
        }

        /// <summary>One named part of a model built alone under a holder called `label`, or null when the model has none.</summary>
        private static GameObject BuildPart(EtsModelData data, string part, string label, Transform parent)
        {
            bool has = false;

            foreach (EtsModelPart p in data.Parts)
            {
                has |= p.Name == part && p.PieceCount > 0;
            }

            if (!has)
            {
                return null;
            }

            try
            {
                EtsModelBuilder.Result result = EtsModelBuilder.Build(data, new EtsModelBuilder.Options
                {
                    Look = "default",
                    OnlyParts = new[] { part },
                    CreateLocators = false,
                    CastShadows = true
                }, parent, EtsEngine.Materials);

                if (result.Root == null)
                {
                    return null;
                }

                result.Root.name = label;

                if (result.PiecesDrawn == 0)
                {
                    Destroy(result.Root);
                    return null;
                }

                return result.Root;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Trailer part " + part + " could not be built: " + e.Message);
                return null;
            }
        }

        /// <summary>Sets the load and re-tunes the springs for it; a laden trailer sits on its axles the same as an empty one.</summary>
        public void SetCargoMass(float cargoMass)
        {
            CargoMass = cargoMass;
            float total = ChassisMass + CargoMass;

            if (Body != null)
            {
                Body.mass = Mathf.Max(1000f, total);
                Body.centerOfMass = new Vector3(0f, 1.6f, 0.4f);
            }

            // About seventy percent of a semi-trailer's weight rides on its own axles.
            float perWheel = _wheels.Count > 0 ? total * 0.7f / _wheels.Count : total;

            foreach (Wheel wheel in _wheels)
            {
                wheel.Spring = perWheel * 9.81f / Sag;
                wheel.Damper = 2f * Mathf.Sqrt(wheel.Spring * perWheel) * DampingRatio;
                wheel.RestLength = wheel.Radius + Sag;
            }
        }

        /// <summary>Couples the trailer to the truck: the kingpin is put on the fifth wheel and joined there.</summary>
        public void Attach(EtsTruckController truck, Transform fifthWheel)
        {
            Detach();
            Truck = truck;
            Transform t = transform;
            t.rotation = truck.transform.rotation;
            Vector3 hookLocal = t.InverseTransformPoint(Hook.position);
            t.position = fifthWheel.position - t.rotation * hookLocal;
            Join(truck, fifthWheel);
        }

        /// <summary>The joint on the kingpin, the trailer already placed with its Hook on the fifth wheel (Attach, Couple).</summary>
        private void Join(EtsTruckController truck, Transform fifthWheel)
        {
            Transform t = transform;
            Vector3 hookLocal = t.InverseTransformPoint(Hook.position);

            // Frozen with the truck when nobody is driving it (EtsTruckController.Driven); running,
            // and moving off at the truck's speed, when somebody is.
            Body.isKinematic = !truck.Driven;

            if (!Body.isKinematic)
            {
                Body.linearVelocity = truck.Body != null ? truck.Body.linearVelocity : Vector3.zero;
                Body.angularVelocity = Vector3.zero;
            }

            SetBoxExcludes(truck.Driven ? EtsLayers.SeatedBodyExcludes : EtsLayers.BodyExcludes);
            truck.Trailer = this;

            _joint = gameObject.AddComponent<ConfigurableJoint>();
            _joint.connectedBody = truck.Body;
            _joint.autoConfigureConnectedAnchor = false;
            _joint.anchor = hookLocal;
            _joint.connectedAnchor = truck.transform.InverseTransformPoint(fifthWheel.position);
            _joint.xMotion = ConfigurableJointMotion.Locked;
            _joint.yMotion = ConfigurableJointMotion.Locked;
            _joint.zMotion = ConfigurableJointMotion.Locked;
            _joint.angularXMotion = ConfigurableJointMotion.Limited;
            _joint.angularYMotion = ConfigurableJointMotion.Free;
            _joint.angularZMotion = ConfigurableJointMotion.Limited;
            _joint.lowAngularXLimit = new SoftJointLimit { limit = -FifthWheelPitch };
            _joint.highAngularXLimit = new SoftJointLimit { limit = FifthWheelPitch };
            _joint.angularZLimit = new SoftJointLimit { limit = FifthWheelRoll };
            _joint.enableCollision = false;
            _joint.projectionMode = JointProjectionMode.PositionAndRotation;
            _joint.projectionDistance = 0.05f;
            _joint.projectionAngle = 3f;
            _ready = true;
            ShowLegs(false);
        }

        private EtsIndicatorLamps _lamps;

        /// <summary>
        /// Lights or darkens the trailer's indicator lamps (the coupled truck's EtsTruckController drives them on every blink
        /// edge; a puppet's owner may too). Built on the first lit call: the trailer model's own lamp lenses, lit through
        /// their lamp masks the game's way (EtsVehicleLamps; 2026-10-04 - they were orange cubes). EtsVehicleLampDriver
        /// also lights its brake and reverse lamps with the coupled truck's.
        /// </summary>
        public void SetIndicatorLamps(bool left, bool right)
        {
            if (_lamps == null)
            {
                if (!left && !right)
                {
                    return;
                }

                _lamps = new EtsIndicatorLamps();

                try
                {
                    _lamps.BuildTrailer(this, _box);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Trailer indicator lamps could not be built: " + e.Message);
                }
            }

            _lamps.Set(left, right);
        }

        public void Detach()
        {
            // Any detach but the driver's own (Uncouple sets it again afterwards) ends the parked state: a delivered
            // trailer, or one whose job was cut, is not the driver's to couple again.
            SetParked(false, null);

            // Uncoupled, it no longer blinks with the truck.
            SetIndicatorLamps(false, false);

            if (_joint != null)
            {
                Destroy(_joint);
                _joint = null;
            }

            if (Truck != null && Truck.Trailer == this)
            {
                Truck.Trailer = null;
            }

            // The joint was the only thing keeping these two boxes out of each other, and the
            // trailer's front overhangs the tractor. Left solid and frozen on top of it, it would
            // shove the truck away at the depenetration speed - the push this whole change removed.
            // So the two ignore each other until they are clear (Update).
            if (Truck != null && Truck.BodyBox != null && _box != null)
            {
                if (_ignoring != null && _ignoring != Truck.BodyBox)
                {
                    Physics.IgnoreCollision(_box, _ignoring, false);
                }

                Physics.IgnoreCollision(_box, Truck.BodyBox, true);
                _ignoring = Truck.BodyBox;
            }

            Truck = null;

            // Standing on its own now: the landing gear down.
            ShowLegs(true);

            // Left behind (delivered, or about to be re-coupled): parked and solid, like an
            // unattended truck - nobody walking into it can shove it about.
            if (Body != null && !Body.isKinematic)
            {
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                Body.isKinematic = true;
            }

            SetBoxExcludes(EtsLayers.BodyExcludes);
        }

        /// <summary>Takes up the truck's parked or driven state (EtsTruckController.SetDriven).</summary>
        public void Follow(EtsTruckController truck)
        {
            if (Body == null || truck == null || Truck != truck)
            {
                return;
            }

            if (truck.Driven && Body.isKinematic && truck.Placed)
            {
                Body.isKinematic = false;
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
            }
            else if (!truck.Driven && !Body.isKinematic)
            {
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                Body.isKinematic = true;
            }
        }

        /// <summary>What the trailer's box passes through; see EtsLayers.SeatedBodyExcludes.</summary>
        public void SetBoxExcludes(int layers)
        {
            if (_box != null)
            {
                _box.excludeLayers = layers;
            }
        }

        /// <summary>
        /// Lines the trailer up on the truck's fifth wheel again after the truck has been set down
        /// somewhere else (Respawn, the fall recovery), keeping the same joint.
        /// </summary>
        public void Recouple()
        {
            if (Truck == null || _joint == null || Body == null)
            {
                return;
            }

            Transform fifthWheel = Truck.FifthWheel != null ? Truck.FifthWheel : Truck.transform;

            if (!Body.isKinematic)
            {
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
            }

            Body.isKinematic = true;
            Transform t = transform;
            t.rotation = Truck.transform.rotation;
            Vector3 hookLocal = t.InverseTransformPoint(Hook.position);
            t.position = fifthWheel.position - t.rotation * hookLocal;
            Body.isKinematic = !Truck.Driven;
        }

        private BoxCollider _ignoring;

        private void Update()
        {
            if (Truck != null || _box == null)
            {
                return;
            }

            // Parked by its driver: the truck it came off passes through the trailer's box while it is under the nose or
            // backing in to couple (the two boxes overlap there - the tractor's chassis runs under the trailer's front -
            // and would stop the truck a metre short of the kingpin). Anywhere else the two are solid to each other.
            BoxCollider truckBox = Parked && _parkedBy != null ? _parkedBy.BodyBox : null;

            if (truckBox != null && InApproach(_parkedBy))
            {
                if (_ignoring != truckBox)
                {
                    if (_ignoring != null)
                    {
                        Physics.IgnoreCollision(_box, _ignoring, false);
                    }

                    Physics.IgnoreCollision(_box, truckBox, true);
                    _ignoring = truckBox;
                }

                return;
            }

            // A trailer left behind stops ignoring the truck it came off once the two are apart.
            if (_ignoring != null && !_box.bounds.Intersects(_ignoring.bounds))
            {
                Physics.IgnoreCollision(_box, _ignoring, false);
                _ignoring = null;
            }
        }

        /// <summary>
        /// A truck in line with the trailer (within 60 degrees), its fifth wheel within ApproachReach of the kingpin with
        /// the kingpin behind it (or up to a metre ahead) and no more than 1.5 m to either side: backing in under the nose,
        /// or pulling away from it. No allocation.
        /// </summary>
        private bool InApproach(EtsTruckController truck)
        {
            if (Hook == null)
            {
                return false;
            }

            Transform fifthWheel = truck.FifthWheel != null ? truck.FifthWheel : truck.transform;
            Vector3 d = Hook.position - fifthWheel.position;
            d.y = 0f;

            if (d.sqrMagnitude > ApproachReach * ApproachReach || Vector3.Angle(Flat(truck.transform.forward), Flat(transform.forward)) > 60f)
            {
                return false;
            }

            Vector3 local = truck.transform.InverseTransformDirection(d);
            return local.z < 1f && Mathf.Abs(local.x) < 1.5f;
        }

        /// <summary>The trailer's own position in the world, for delivery checks.</summary>
        public Vector3 Position => transform.position;

        private void FixedUpdate()
        {
            if (!_ready || Body == null || Body.isKinematic)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;
            Vector3 up = transform.up;
            Vector3 forward = transform.forward;
            float brakePedal = Truck != null ? Mathf.Max(Truck.Brake, Truck.ParkingBrake ? 1f : 0f) : 1f;
            float brakePerWheel = Body.mass * 5f / Mathf.Max(1, _wheels.Count) * brakePedal;

            foreach (Wheel wheel in _wheels)
            {
                Vector3 hub = transform.TransformPoint(wheel.LocalHub);
                wheel.Grounded = EtsTruckController.CastWheel(hub, up, wheel.Radius, wheel.RestLength + wheel.Travel, out wheel.Hit);

                if (!wheel.Grounded)
                {
                    wheel.Compression = -wheel.Travel;
                    continue;
                }

                wheel.Compression = wheel.RestLength - wheel.Hit.distance;
                float suspensionVelocity = Vector3.Dot(Body.GetPointVelocity(hub), up);
                float force = wheel.Spring * wheel.Compression - wheel.Damper * suspensionVelocity;
                float bump = wheel.Compression - wheel.Travel;
                if (bump > 0f) force += wheel.Spring * 6f * bump;
                force = Mathf.Max(0f, force);
                Body.AddForceAtPosition(up * force, wheel.Hit.point);

                Vector3 rollDirection = Vector3.ProjectOnPlane(forward, wheel.Hit.normal).normalized;
                Vector3 sideDirection = Vector3.Cross(wheel.Hit.normal, rollDirection).normalized;
                Vector3 contactVelocity = Body.GetPointVelocity(wheel.Hit.point);
                float longitudinalSpeed = Vector3.Dot(contactVelocity, rollDirection);
                float lateralSpeed = Vector3.Dot(contactVelocity, sideDirection);
                float wheelMassShare = force / 9.81f;

                float stopForce = wheelMassShare * Mathf.Abs(longitudinalSpeed) / dt;
                float resist = brakePerWheel + force * 0.0075f;
                float longitudinal = -Mathf.Sign(longitudinalSpeed) * Mathf.Min(resist, stopForce);

                float slipAngle = Mathf.Atan2(lateralSpeed, Mathf.Max(Mathf.Abs(longitudinalSpeed), 0.5f));
                float lateral = -CorneringStiffness * slipAngle * force;
                float lateralStop = wheelMassShare * Mathf.Abs(lateralSpeed) / dt;
                lateral = Mathf.Sign(lateral) * Mathf.Min(Mathf.Abs(lateral), lateralStop);

                float maxFriction = Grip * force;
                float total = Mathf.Sqrt(longitudinal * longitudinal + lateral * lateral);

                if (total > maxFriction && total > 0f)
                {
                    longitudinal *= maxFriction / total;
                    lateral *= maxFriction / total;
                }

                Body.AddForceAtPosition(rollDirection * longitudinal + sideDirection * lateral, wheel.Hit.point);
                wheel.SpinDegrees += longitudinalSpeed / wheel.Radius * Mathf.Rad2Deg * dt;
            }

            // Anti-roll between the wheels of each axle: pairs by z.
            for (int i = 0; i < _wheels.Count; i++)
            {
                for (int j = i + 1; j < _wheels.Count; j++)
                {
                    Wheel a = _wheels[i], b = _wheels[j];
                    if (Mathf.Abs(a.LocalHub.z - b.LocalHub.z) > 0.3f || !a.Grounded || !b.Grounded) continue;
                    float bar = a.Spring * 0.5f * (a.Compression - b.Compression);
                    Body.AddForceAtPosition(up * bar, transform.TransformPoint(a.LocalHub));
                    Body.AddForceAtPosition(-up * bar, transform.TransformPoint(b.LocalHub));
                }
            }

            Vector3 velocity = Body.linearVelocity;
            Body.AddForce(-2.5f * velocity.magnitude * velocity);

            foreach (Wheel wheel in _wheels)
            {
                if (wheel.Visual == null) continue;
                float drop = wheel.Grounded ? Mathf.Clamp(wheel.Hit.distance - wheel.Radius, -wheel.Travel, wheel.Travel) : wheel.Travel;
                wheel.Visual.localPosition = wheel.LocalHub - Vector3.up * drop;
                wheel.Visual.localRotation = wheel.VisualRest * Quaternion.Euler(wheel.SpinDegrees * wheel.SpinSign, 0f, 0f);
            }
        }
    }
}
