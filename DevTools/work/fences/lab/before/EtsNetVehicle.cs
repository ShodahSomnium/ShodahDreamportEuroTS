using System;
using System.Collections;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// One vehicle as EtsTraffic and the riders see it: the own truck or a puppet (owner A; PLAN.md §7.A,
    /// EtsNet.AllVehicles). Velocity is the drawn one for a puppet (its Rigidbody is kinematic and reads zero).
    /// Box is the tractor's body box (EtsTruckController.BodyBox), null for a stand-in.
    /// </summary>
    public struct EtsVehicleView
    {
        public Transform Tractor; public Vector3 Velocity; public EtsTrailer Trailer; public bool Remote;
        public BoxCollider Box;
    }

    /// <summary>
    /// Another player's truck (and trailer) drawn on this client: one per counted remote slot with "truck present"
    /// (owner A; [net §2.6, §2.11], PLAN.md §3.1, §5.1, §8 Phase 3). EtsNet creates it, feeds its timeline from the
    /// slot's ints every frame (EtsPlayerWire.ToTimeline) and draws it (Draw); it is destroyed with its holder, which
    /// also stops a build still running.
    ///
    ///  - The rig is C's EtsTruckFactory.Build(identity, forPuppet: true): sliced over frames (1.5 ms a frame, C's probe
    ///    2030/2031), kinematic, no mirrors or reflection probe; EtsNet builds one at a time. Until it is ready - or for
    ///    good when it cannot be built here (no install, a catalogue that differs) - a grey stand-in box is drawn, so
    ///    the owner's avatar is not seen gliding along on nothing (the old EtsNet.Placeholder).
    ///  - The drawn pose is the slot's timeline sampled now (EtsNetTimeline: a beat behind, Hermite, snapping on a new
    ///    placement, serial, owner or claim), applied through EtsTruckController.ApplyPuppet, which also turns and
    ///    hangs the wheels on this client's road.
    ///  - A coupled trailer is rebuilt from the articulation, as probe 1674 and NetTest2 (probe 1823) measured within
    ///    1 cm: trailerRot = tractorRot * articulation, trailerPos = tractorPos + tractorRot * fifthWheel -
    ///    trailerRot * kingpin (EtsPlayerWire.TrailerPose). A parked one stands at its own sent pose. The trailer is C's
    ///    sliced EtsTrailer.BuildPuppet (BuildTrailer), queued behind the truck builds in EtsNet's one build at a time.
    ///  - Frame is what riders are placed on (EtsNet.TryGetVehicleFrame): the rig's root, or before the rig exists an
    ///    empty transform at the same drawn pose, so a passenger's seat offset means the same on every client.
    /// </summary>
    public sealed class EtsNetVehicle : MonoBehaviour
    {
        public int Slot = -1;
        public int OwnerId = -1;
        public EtsTruckIdentity Identity;
        public int TruckSerial;
        public EtsTruckRig Rig;
        public EtsTrailer Trailer;

        /// <summary>A puppet's Rigidbody is kinematic, so its own velocity reads zero; this is the drawn one.</summary>
        public Vector3 DrawnVelocity { get; private set; }

        /// <summary>The truck's samples, fed from the slot's ints (EtsPlayerWire.ToTimeline; the articulation rides in TrailerRotation).</summary>
        public readonly EtsNetTimeline Timeline = new EtsNetTimeline();

        // [net §2.11] also put the slot's rider timeline here. It is not: a player rides whether or not they have
        // a truck of their own, so EtsNet keeps one EtsRiderTrack per slot (EtsPlayerWire.cs; tested by probe 1823).

        /// <summary>Sampled at least once: it has a drawn pose (AllVehicles and the rider frames count it from then on).</summary>
        public bool Drawn { get; private set; }

        public bool Building { get; private set; }
        public bool BuildFailed { get; private set; }
        public string BuildFailure { get; private set; }
        public float BuiltSeconds { get; private set; }

        /// <summary>Time.time it stopped counting (owner gone, another epoch), -1 while it counts: EtsNet's 2 s grace.</summary>
        public float MissingSince = -1f;

        /// <summary>Beyond EtsNet.TruckDrawRadius of the local focus, or not counting: not drawn, the timeline still fed.</summary>
        public bool Hidden { get; private set; }

        /// <summary>What riders are placed on: the rig's root, or the stand-in frame at the same drawn pose.</summary>
        public Transform Frame => Rig != null ? Rig.transform : _frame;

        /// <summary>The trailer's box (for traffic and truck contacts), null without a trailer.</summary>
        public BoxCollider TrailerBox { get; private set; }

        private Transform _frame;
        private GameObject _placeholder;
        private Vector3 _fifthWheelLocal = DefaultFifthWheel;
        private Vector3 _kingpinLocal = DefaultKingpin;
        private int _trailerSerial = -1;
        private EtsTrailerIdentity _trailerId;
        private EtsTrailerIdentity _wantedTrailerId;
        private int _wantedTrailerSerial;
        private bool _trailerFailed;

        /// <summary>
        /// Where a trailer hangs before the rig tells (the stand-in has no locators): the DAF XF's hook_t is about 1.05 m up
        /// and on the rear axle line; the curtain sider's hook 5.07 m ahead of its origin (EtsTrailer.Create's default).
        /// </summary>
        private static readonly Vector3 DefaultFifthWheel = new Vector3(0f, 1.05f, -0.6f);
        private static readonly Vector3 DefaultKingpin = new Vector3(0f, 1f, 5.07f);

        /// <summary>The stand-in, EtsNet's old Placeholder: a truck-sized box whose centre is 1.9 m above the rig's origin.</summary>
        private static readonly Vector3 PlaceholderSize = new Vector3(2.5f, 3.3f, 6.1f);

        public static EtsNetVehicle Create(int slot, int ownerId, EtsTruckIdentity identity, int truckSerial)
        {
            var holder = new GameObject("Ets Puppet (slot " + slot + ")");
            var vehicle = holder.AddComponent<EtsNetVehicle>();
            vehicle.Slot = slot;
            vehicle.OwnerId = ownerId;
            vehicle.Identity = identity;
            vehicle.TruckSerial = truckSerial & 0xFF;
            vehicle._frame = new GameObject("frame").transform;
            vehicle._frame.SetParent(holder.transform, false);
            return vehicle;
        }

        // ---------------------------------------------------------------- the build (one at a time; EtsNet starts it)

        /// <summary>
        /// C's factory, drained here so a throw inside it ends this build with a reason instead of an endless one (C# allows
        /// no yield inside a try with a catch, hence the split). finished runs once, built or not.
        /// </summary>
        public IEnumerator Build(Action<EtsNetVehicle> finished)
        {
            Building = true;
            float began = Time.realtimeSinceStartup;
            EtsTruckRig rig = null;
            string failure = null;
            int ownerId = OwnerId, slot = Slot;

            // SetOwner before anything else sees the rig: B's door on it asks EtsNet.RequestSeat(rig.OwnerSlot, 1).
            IEnumerator build = EtsTruckFactory.Build(Identity, transform, true, r =>
            {
                rig = r;

                if (r != null)
                {
                    r.SetOwner(ownerId, slot);
                }
            }, f => failure = f);

            while (true)
            {
                bool more;
                object current = null;

                try
                {
                    more = build.MoveNext();

                    if (more)
                    {
                        current = build.Current;
                    }
                }
                catch (Exception e)
                {
                    failure = "the truck factory threw " + e.GetType().Name + ": " + e.Message;
                    Debug.LogException(e);
                    more = false;
                }

                if (!more)
                {
                    break;
                }

                yield return current;
            }

            Building = false;
            BuiltSeconds = Time.realtimeSinceStartup - began;

            if (rig == null || rig.Controller == null)
            {
                if (rig != null)
                {
                    Destroy(rig.gameObject);
                }

                BuildFailed = true;
                BuildFailure = failure ?? "no reason given";
                finished?.Invoke(this);
                yield break;
            }

            Rig = rig;
            Transform root = rig.transform;
            Transform fifth = rig.Controller.FifthWheel;

            // The locator in the tractor's own frame (the rig is still at its holder's identity, unscaled).
            _fifthWheelLocal = fifth != null ? root.InverseTransformPoint(fifth.position) : DefaultFifthWheel;
            rig.SetInteriorVisible(false);

            if (Drawn)
            {
                root.SetPositionAndRotation(_frame.position, _frame.rotation);
            }

            if (Trailer != null)
            {
                // C's setter gives the trailer the truck's Q-A3 state from now on (Contacts: the rig's rule, not the ghost).
                Rig.Trailer = Trailer;
                _trailerGhost = false;
            }

            if (_placeholder != null)
            {
                Destroy(_placeholder);
                _placeholder = null;
            }

            if (Hidden)
            {
                rig.gameObject.SetActive(false);
            }

            finished?.Invoke(this);
        }

        // ---------------------------------------------------------------- drawing (EtsNet.Update, every frame)

        /// <summary>The trailer the slot says (flags and ints 19-20): rebuilt when the serial or the identity changes.</summary>
        public void WantTrailer(bool present, int serial, EtsTrailerIdentity id)
        {
            _wantedTrailerSerial = present ? serial & 0xFF : -1;
            _wantedTrailerId = id;
        }

        /// <summary>
        /// The drawn pose now: the timeline sampled at local time now, the tractor, the trailer, the stand-in. visible false
        /// (too far, not counting) poses the frame and the rig only, so a rider on it is still placed right.
        /// </summary>
        public void Draw(double now, float dt, bool visible)
        {
            SetHidden(!visible);

            if (!Timeline.Sample(now, out EtsNetWire.State s))
            {
                return;
            }

            Drawn = true;
            DrawnVelocity = s.Velocity;
            _frame.SetPositionAndRotation(s.Position, s.Rotation);

            if (Rig != null && Rig.Controller != null)
            {
                if (visible)
                {
                    Rig.Controller.ApplyPuppet(s.Position, s.Rotation, s.Velocity, s.Steer, s.Rpm, s.Throttle, s.Brake, s.Gear,
                                               s.Has(EtsPlayerWire.TruckParkingBrake), dt);
                    Rig.Controller.SetIndicator(s.Has(EtsPlayerWire.TruckHazard) ? 2 : s.Has(EtsPlayerWire.TruckBlinkLeft) ? -1 : s.Has(EtsPlayerWire.TruckBlinkRight) ? 1 : 0);

                    // A driven truck's engine as its driver's client has it (off until a headset driver takes the wheel);
                    // ApplyPuppet's reading of the revs agrees, whichever sees the start first. A parked truck keeps idling.
                    if (s.Has(EtsPlayerWire.TruckDriven)) Rig.Controller.EngineRunning = s.Has(EtsPlayerWire.TruckEngine);
                }
                else
                {
                    Rig.transform.SetPositionAndRotation(s.Position, s.Rotation);
                }
            }
            else if (visible)
            {
                // Being built, or not buildable here: the stand-in box.
                Placeholder(s.Position, s.Rotation);
            }

            DrawTrailer(s, visible, dt);
        }

        private void DrawTrailer(in EtsNetWire.State s, bool visible, float dt)
        {
            bool wanted = _wantedTrailerSerial >= 0 && s.Has(EtsPlayerWire.TruckTrailer);
            _trailerWanted = wanted;

            if (Trailer != null && (!wanted || _wantedTrailerSerial != _trailerSerial || !SameTrailer(_wantedTrailerId, _trailerId)))
            {
                DropTrailer();
            }

            // A failed build is not retried for the same trailer; another one (a new serial or identity) is tried afresh.
            if (_trailerFailed && (_wantedTrailerSerial != _failedSerial || !SameTrailer(_wantedTrailerId, _failedId)))
            {
                _trailerFailed = false;
            }

            if (Trailer == null)
            {
                // Built by BuildTrailer when EtsNet's one-at-a-time build queue reaches it (NeedsTrailer).
                return;
            }

            Vector3 position;
            Quaternion rotation;

            if (s.Has(EtsPlayerWire.TruckTrailerCoupled))
            {
                EtsPlayerWire.TrailerPose(s.Position, s.Rotation, s.TrailerRotation, _fifthWheelLocal, _kingpinLocal, out position, out rotation);
            }
            else
            {
                position = s.TrailerPosition;
                rotation = s.TrailerRotation;
            }

            if (Trailer.gameObject.activeSelf != visible)
            {
                Trailer.gameObject.SetActive(visible);
            }

            if (visible)
            {
                // C's puppet step (EtsTrailer.ApplyPuppet): placed, and its wheels hung on this client's road and turned by the
                // speed - a kinematic trailer's FixedUpdate never runs them (C's probe 2250: 0.017 ms a frame for 3 puppets and
                // a trailer). A parked trailer does not roll.
                Trailer.ShowLegs(!s.Has(EtsPlayerWire.TruckTrailerCoupled));
                float speed = s.Has(EtsPlayerWire.TruckTrailerCoupled) ? Vector3.Dot(s.Velocity, rotation * Vector3.forward) : 0f;
                Trailer.ApplyPuppet(position, rotation, speed, dt);
            }
            else
            {
                Trailer.transform.SetPositionAndRotation(position, rotation);
            }
        }

        /// <summary>A trailer is wanted and none is drawn or being built (EtsNet.StartNextBuild starts BuildTrailer then).</summary>
        public bool NeedsTrailer => _trailerWanted && Trailer == null && !TrailerBuilding && !_trailerFailed && EtsEngine.IsMounted;

        /// <summary>A trailer build is running (EtsNet counts it as the one build at a time).</summary>
        public bool TrailerBuilding { get; private set; }

        /// <summary>Why the last trailer build failed, for EtsNet's log line.</summary>
        public string TrailerFailure { get; private set; }

        /// <summary>The last trailer build's time, seconds (for EtsNet's log line).</summary>
        public float TrailerSeconds { get; private set; }

        private bool _trailerWanted;
        private int _failedSerial = -1;
        private EtsTrailerIdentity _failedId;
        private bool _trailerGhost;

        /// <summary>
        /// C's sliced puppet trailer (EtsTrailer.BuildPuppet, C3) by the slot's identity, in the tractor's paint (EtsTruckRig.Paint)
        /// as the owner's own trailer is painted. Not EtsTrailer.CreatePuppet: that one call reads and uploads the whole trailer on
        /// one frame when it is the visit's first (C's probe 2250: 13 ms of script cold, against 1.5 ms a frame sliced), which
        /// broke the ~2 ms a frame rule the first time a remote trailer appeared. EtsNet runs it as its one build at a time, after
        /// every waiting truck. A result that is out of date when it arrives (the slot changed trailer meanwhile) is thrown away.
        /// finished runs once, built or not.
        /// </summary>
        public IEnumerator BuildTrailer(Action<EtsNetVehicle> finished)
        {
            TrailerBuilding = true;
            TrailerFailure = null;
            float began = Time.realtimeSinceStartup;
            int serial = _wantedTrailerSerial;
            EtsTrailerIdentity id = _wantedTrailerId;
            EtsTrailer built = null;
            string failure = null;
            IEnumerator build = EtsTrailer.BuildPuppet(id, transform, Rig != null ? Rig.Paint : Color.white, t => built = t, f => failure = f);

            while (true)
            {
                bool more;
                object current = null;

                try
                {
                    more = build.MoveNext();

                    if (more)
                    {
                        current = build.Current;
                    }
                }
                catch (Exception e)
                {
                    failure = "the trailer build threw " + e.GetType().Name + ": " + e.Message;
                    Debug.LogException(e);
                    more = false;
                }

                if (!more)
                {
                    break;
                }

                yield return current;
            }

            TrailerBuilding = false;
            TrailerSeconds = Time.realtimeSinceStartup - began;

            if (built == null)
            {
                _trailerFailed = true;
                _failedSerial = serial;
                _failedId = id;
                TrailerFailure = failure ?? "no reason given";
                finished?.Invoke(this);
                yield break;
            }

            if (Trailer != null || serial != _wantedTrailerSerial || !SameTrailer(id, _wantedTrailerId))
            {
                // Out of date: the slot dropped or changed its trailer while this one was built.
                Destroy(built.gameObject);
                finished?.Invoke(this);
                yield break;
            }

            Trailer = built;
            TrailerBox = built.BodyBox;
            _trailerSerial = serial;
            _trailerId = id;
            _trailerGhost = false;
            _kingpinLocal = built.Hook != null ? built.transform.InverseTransformPoint(built.Hook.position) : DefaultKingpin;

            if (Rig != null)
            {
                // C's setter gives the trailer the truck's current Q-A3 state (EtsTruckRig.SetVehicleContacts).
                Rig.Trailer = built;
            }

            if (Hidden)
            {
                built.gameObject.SetActive(false);
            }

            finished?.Invoke(this);
        }

        private void DropTrailer()
        {
            if (Trailer != null)
            {
                Destroy(Trailer.gameObject);
            }

            if (Rig != null)
            {
                Rig.Trailer = null;
            }

            Trailer = null;
            TrailerBox = null;
            _trailerSerial = -1;
            _trailerGhost = false;
        }

        private static bool SameTrailer(EtsTrailerIdentity a, EtsTrailerIdentity b)
        {
            return a.Family == b.Family && a.Variant == b.Variant && a.Cargo == b.Cargo && a.CompanyHash == b.CompanyHash;
        }

        private void Placeholder(Vector3 position, Quaternion rotation)
        {
            if (_placeholder == null)
            {
                _placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _placeholder.name = BuildFailed ? "Ets truck (not buildable here)" : "Ets truck (being built)";
                Destroy(_placeholder.GetComponent<Collider>());
                _placeholder.transform.SetParent(transform, false);
                _placeholder.transform.localScale = PlaceholderSize;
            }

            _placeholder.transform.SetPositionAndRotation(position + rotation * new Vector3(0f, 1.9f, 0f), rotation);
        }

        private void SetHidden(bool hidden)
        {
            if (Hidden == hidden)
            {
                return;
            }

            Hidden = hidden;

            if (Rig != null && Rig.gameObject.activeSelf == hidden)
            {
                Rig.gameObject.SetActive(!hidden);
            }

            if (Trailer != null && Trailer.gameObject.activeSelf == hidden)
            {
                Trailer.gameObject.SetActive(!hidden);
            }

            if (_placeholder != null && _placeholder.activeSelf == hidden)
            {
                _placeholder.SetActive(!hidden);
            }

            if (_plate != null)
            {
                _plate.Hidden = hidden;
            }
        }

        // ---------------------------------------------------------------- truck against truck (Q-A3)

        /// <summary>This puppet and the own truck pass through each other now (Q-A3: above 10 km/h relative, or still overlapping).</summary>
        public bool PassingThrough => Rig != null ? !Rig.VehicleContacts : _trailerGhost;

        /// <summary>
        /// Q-A3 (settled 2026-09-22): player trucks collide below 10 km/h relative speed and pass through above it. The
        /// mechanism is C's, built for this call (EtsTruckRig.UpdateVehicleContacts, once a frame per puppet after the draw):
        /// the puppet's drawn velocity against the own truck's, see-through at 10 km/h and above, solid again below 9 km/h
        /// and never while the boxes overlap, set on the puppet's truck and trailer boxes as Collider.excludeLayers (the
        /// Vehicle layer). That replaced this class's own Physics.IgnoreCollision pairs (checker, Phase 3): Unity drops an
        /// ignored pair when a collider is deactivated, and a puppet beyond TruckDrawRadius is deactivated (SetHidden), so a
        /// truck that came back into view while passing stayed solid at speed; calls on the inactive colliders were logged as
        /// errors too. excludeLayers is a property of the collider and survives it (C's probe 2250 measured both states in
        /// Unity). A trailer drawn behind a stand-in (no rig: being built, or not buildable here; the stand-in box has no
        /// collider) passes through the own truck, as its stand-in does. No allocation.
        /// </summary>
        public void Contacts(EtsTruckRig own)
        {
            if (Rig != null)
            {
                Rig.UpdateVehicleContacts(own);
                return;
            }

            if (Trailer != null && !_trailerGhost)
            {
                Trailer.SetBoxExcludes(EtsLayers.BodyExcludes | (1 << EtsLayers.Vehicle));
                _trailerGhost = true;
            }
        }

        // ---------------------------------------------------------------- the nameplate ([net §2.6], PLAN §3.7 item 4)

        private EtsNameplate _plate;
        private ulong _shownCity = ulong.MaxValue;
        private string _detail;

        /// <summary>
        /// "{name}" with the game's orange star when they are the room master (their slot's int 4 bit 19), and "→ {destination
        /// city}" under it while they have a job: D's EtsNameplate (D3), one per drawn truck, following the drawn frame (the
        /// same pose as the rig, and there before the rig is built), turning to the viewer and scaling with the distance
        /// itself, hidden from a viewer seated in this truck. Set is free when nothing changed; the detail string is made only
        /// when the city changes. It goes with the holder (the plate destroys itself when what it follows is gone).
        /// </summary>
        public void SetPlate(string name, bool master, ulong destinationCity)
        {
            if (_plate == null)
            {
                _plate = EtsNameplate.Create(_frame);
            }

            if (destinationCity != _shownCity)
            {
                _shownCity = destinationCity;
                _detail = destinationCity != 0UL ? "\u2192 " + CityName(destinationCity) : null;
            }

            // It follows the drawn frame, not the rig, so its own "seated in that truck" test cannot see a passenger here.
            _plate.Hidden = Hidden || !Drawn || (Rig != null && EtsLocalTruck.Occupied == Rig);
            _plate.Set(name, master, _detail);
        }

        private void OnDestroy()
        {
            if (_plate != null)
            {
                Destroy(_plate.gameObject);
            }
        }

        /// <summary>A city's name from its token ("szczecin" -> "Szczecin"): the token is the game's own city id (EtsMapCity.Name).</summary>
        private static string CityName(ulong token)
        {
            string name = EtsToken.Decode(token);

            if (string.IsNullOrEmpty(name))
            {
                return "?";
            }

            return char.ToUpperInvariant(name[0]) + name.Substring(1).Replace('_', ' ');
        }
    }
}
