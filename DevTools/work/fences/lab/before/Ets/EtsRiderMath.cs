using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The riders' pure math: players standing or sitting on a moving truck, trailer or AI car, kept on it
    /// the way the Main Build keeps players on its elevators (owner B; [elev §1, §7.3], PLAN.md §3.3, §5.4,
    /// §7.B). Everything is a Pose (position and rotation, no scale): every vehicle root this world builds
    /// is unscaled (nothing under W writes localScale on a truck, trailer or car root), so a Pose is exactly
    /// what Transform.TransformPoint does for them, and the same formula is used to measure an offset and
    /// to put it back, on every client.
    ///
    /// Engine-free on purpose: probe 1840 (S/phase1/B/probe/out1840.txt) ran the UnityEngine value types
    /// under mono with no player. Quaternion.Euler, Inverse, AngleAxis, eulerAngles, Slerp, LookRotation
    /// and Matrix4x4.TRS are engine calls there (MissingMethodException: "cant resolve internal call");
    /// Quaternion * Quaternion, Quaternion * Vector3, Quaternion.Angle/Dot/Normalize, Mathf and Vector3
    /// are plain C#. So the yaw rotation, its inverse and the heading are written out here, and the offline
    /// tests (probe 1842) run the very code the world runs.
    ///
    /// The four operations, and where each is used:
    ///  - Measure / Target: a rider's root as an offset in the vehicle's frame, and back. The rider's own
    ///    client measures against the vehicle as IT draws it; every other client puts the body back on the
    ///    vehicle as THAT client draws it, so the vehicle's network delay cancels ([net §2.7]). Measured in
    ///    this world's two-player run, the client's own avatar stream lands 0.5 m (parked) to 4.75 m
    ///    (60 km/h) off the drawn seat ([elev §4], Player.log 2026-09-22): only vehicle-relative data works.
    ///  - Correct: the rigid correction C(p, q) = (pT + R(dYaw)(p - rawRoot), R(dYaw) q) that moves a remote
    ///    body, head and hands together onto the target, so head and hands keep the pose the same Fusion
    ///    snapshot gave them relative to the body (PLAN C26: no head or hand offsets on the wire).
    ///  - Carry / CarryRoot: the local standing rider moved by the vehicle's motion since last frame, the
    ///    client's MobilePlatformMover rebuilt (motionDelta = deltaRotation*rel - rel + deltaPosition,
    ///    MB/DynamicObjects/Additional/MobilePlatformMover.cs:100-144). Yaw is carried only for VR (Q-B1):
    ///    on a monitor the client's camera and movement own the facing (NonXRMovementProvider.cs:105-116).
    ///  - InsideBox / Stays: whether a walker is still on the vehicle ([elev §7.3.1 step 4]).
    /// </summary>
    public static class EtsRiderMath
    {
        /// <summary>
        /// A vehicle frame that moves further than this between two frames has been set down somewhere new
        /// (a job's depot, a respawn): a standing rider is left where they stood rather than thrown across the
        /// map (PLAN §3.5, [elev R10]). The same 50 m EtsNetTimeline uses for a truck jump (EtsNetWire.cs:234);
        /// the fastest vehicle here, 130 km/h at 20 Hz, moves 1.8 m in one frame.
        /// </summary>
        public const float JumpMetres = 50f;

        /// <summary>A walker stays on the vehicle's box grown by this much across and along it ([elev §7.3.1 step 4]).</summary>
        public const float SideMargin = 0.3f;

        /// <summary>...and by this much upward, so a jump does not count as leaving.</summary>
        public const float UpMargin = 3f;

        /// <summary>An airborne walker counts as still riding for this long, whatever the box says.</summary>
        public const float AirborneGrace = 1.5f;

        /// <summary>
        /// Above this vehicle speed a rider's IK legs are switched off (SetVRIKLegMovementDisabled): the
        /// substitute for the client's VRIKAddPlatformMotion, which world code cannot reach ([elev §3]).
        /// Walking speed is about 1.4 m/s; below 0.5 m/s the legs may step as the rider shuffles.
        /// </summary>
        public const float LegsOffSpeed = 0.5f;

        /// <summary>A rotation of degrees about +Y: the same quaternion as Quaternion.Euler(0, degrees, 0), in plain C#.</summary>
        public static Quaternion Yaw(float degrees)
        {
            float half = degrees * 0.5f * Mathf.Deg2Rad;
            return new Quaternion(0f, Mathf.Sin(half), 0f, Mathf.Cos(half));
        }

        /// <summary>
        /// The heading of a rotation in degrees, (-180, 180]: the angle of its forward axis on the ground.
        /// Equal to eulerAngles.y (mod 360) for any pitch under 90 degrees, since Unity applies yaw last
        /// (Z, X, Y order) and pitch only shortens the forward axis' ground projection.
        /// </summary>
        public static float YawOf(Quaternion q)
        {
            Vector3 forward = q * Vector3.forward;
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        /// <summary>The inverse of a unit rotation (Quaternion.Inverse is an engine call, probe 1840).</summary>
        public static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        /// <summary>A world point in the frame's local space: frame.InverseTransformPoint for an unscaled frame.</summary>
        public static Vector3 ToLocal(Pose frame, Vector3 world)
        {
            return Conjugate(frame.rotation) * (world - frame.position);
        }

        /// <summary>A local point in world space: frame.TransformPoint for an unscaled frame.</summary>
        public static Vector3 ToWorld(Pose frame, Vector3 local)
        {
            return frame.position + frame.rotation * local;
        }

        /// <summary>
        /// A rider's body root as the rider record carries it: its position in the vehicle's frame and its
        /// heading relative to the vehicle's (EtsRider.Root, EtsRider.Yaw). Measured by the rider's own client
        /// against the vehicle as that client draws it.
        /// </summary>
        public static void Measure(Pose frame, Vector3 root, Quaternion rootRotation, out Vector3 offset, out float yawLocal)
        {
            offset = ToLocal(frame, root);
            yawLocal = Mathf.DeltaAngle(YawOf(frame.rotation), YawOf(rootRotation));
        }

        /// <summary>Where a rider record puts the body root on a vehicle as this client draws it: Frame.TransformPoint(o), frame heading + yawLocal.</summary>
        public static void Target(Pose frame, Vector3 offset, float yawLocal, out Vector3 root, out float yaw)
        {
            root = ToWorld(frame, offset);
            yaw = YawOf(frame.rotation) + yawLocal;
        }

        /// <summary>
        /// One pose of a remote body (its root, head or a hand) moved rigidly so that the body's root lands on
        /// targetRoot with heading targetYaw: C(p, q) = (pT + R(dYaw)(p - rawRoot), R(dYaw) q), where
        /// dYaw = targetYaw - rawRootYaw [elev §7.3.1 step 5]. Applied with the same rawRoot to every part of one
        /// body, the parts keep their distances and their rotations relative to the root exactly (probe 1842).
        /// </summary>
        public static Pose Correct(Pose raw, Vector3 rawRoot, float rawRootYaw, Vector3 targetRoot, float targetYaw)
        {
            Quaternion turn = Yaw(Mathf.DeltaAngle(rawRootYaw, targetYaw));
            return new Pose(targetRoot + turn * (raw.position - rawRoot), Normalised(turn * raw.rotation));
        }

        /// <summary>A world point moved with the frame from its last pose to its current one (the rigid motion of the frame).</summary>
        public static Vector3 Carry(Pose frameBefore, Pose frameNow, Vector3 point)
        {
            return ToWorld(frameNow, ToLocal(frameBefore, point));
        }

        /// <summary>How far the frame turned about the vertical since last frame, degrees.</summary>
        public static float YawDelta(Pose frameBefore, Pose frameNow)
        {
            return Mathf.DeltaAngle(YawOf(frameBefore.rotation), YawOf(frameNow.rotation));
        }

        /// <summary>
        /// The local standing rider's body root carried by the frame: its position rigidly, and (VR only) its
        /// heading turned by the frame's yaw about the new position. The root stays upright - a person on a
        /// truck climbing a hill does not lean with it. When the frame's turn is pure yaw, turning the root
        /// about its own position carries every point of the rig (the head, the hands) rigidly too, so a VR
        /// rider's head, offset from the root in tracking space, rides the truck without swinging (probe 1842).
        /// </summary>
        public static Pose CarryRoot(Pose frameBefore, Pose frameNow, Pose root, bool carryYaw)
        {
            Vector3 position = Carry(frameBefore, frameNow, root.position);
            Quaternion rotation = carryYaw ? Normalised(Yaw(YawDelta(frameBefore, frameNow)) * root.rotation) : root.rotation;
            return new Pose(position, rotation);
        }

        /// <summary>Whether the frame was set down somewhere new between two frames rather than moved (JumpMetres).</summary>
        public static bool Jumped(Pose frameBefore, Pose frameNow)
        {
            return (frameNow.position - frameBefore.position).sqrMagnitude > JumpMetres * JumpMetres;
        }

        /// <summary>
        /// Whether a point in the frame's local space is on the box (centre, size): inside it grown by sideMargin
        /// across and along and by upMargin upward. Not grown downward: a player under a trailer is not on it.
        /// </summary>
        public static bool InsideBox(Vector3 localPoint, Vector3 centre, Vector3 size, float sideMargin, float upMargin)
        {
            Vector3 d = localPoint - centre;
            Vector3 half = size * 0.5f;
            return Mathf.Abs(d.x) <= half.x + sideMargin && Mathf.Abs(d.z) <= half.z + sideMargin &&
                   d.y >= -half.y && d.y <= half.y + upMargin;
        }

        /// <summary>Whether a walker stays on the vehicle: on the box, or airborne for no longer than AirborneGrace.</summary>
        public static bool Stays(bool inside, bool airborne, float airborneSeconds)
        {
            return inside || (airborne && airborneSeconds <= AirborneGrace);
        }

        /// <summary>
        /// The fastest a player moves themselves, m/s: the client's desktop sprint, SprintSpeed 6 times at most
        /// MaxSprintSpeedMultiplier 1.2 (MB/Player/Configs/Additional/NonXR.Movement.Settings.asset:15-25; walking
        /// is HorizontalSpeed 1.6). A headset's stick moves at 3 (Player.XR.Controls.Movement.prefab:520
        /// m_MoveSpeed). CarryLost cannot tell a drag-back from the player's own step backward below twice this.
        /// </summary>
        public const float MaxOwnSpeed = 7.2f;

        /// <summary>
        /// Whether the carry was lost this frame: the root, read again in LateUpdate, is more than half the
        /// frame's travel BEHIND where the carry put it, along the travel. That is the character controller
        /// dragging the body back to where it thought it was ([elev R4, §7.6] "carry lost N times").
        /// A dragged-back body lands near carried - travel plus its own step, a body walking backward near
        /// carried plus its own step, and the own step is at most ownStepMax (MaxOwnSpeed x the frame time).
        /// The midpoint tells the two apart only while the travel exceeds twice that, so shorter travel is not
        /// counted. At 90 Hz that is 16 cm a frame, 52 km/h, at any frame rate the same speed; a fixed per-frame
        /// floor instead (checker probe 1850 X6: 5 cm) counted a walker running back on a truck at 20 km/h
        /// as a lost carry every frame.
        /// </summary>
        public static bool CarryLost(Vector3 carried, Vector3 actual, Vector3 travel, float ownStepMax)
        {
            float length = travel.magnitude;

            if (length <= 2f * ownStepMax || length <= 0f)
            {
                return false;
            }

            return Vector3.Dot(actual - carried, travel / length) < -0.5f * length;
        }

        /// <summary>
        /// A rider record as a sample for EtsNetTimeline, so remote riders are drawn on the rider's own clock with
        /// the tested stamp clock, slewed lag, Hermite and epoch snap (EtsNetWire.cs:216-481), as [elev §7.2] and
        /// [net §2.7] ask: Position = the root offset, Velocity = its rate, Rotation = the relative heading.
        /// The world's receiver is A's EtsRiderTrack (in EtsPlayerWire.cs), which builds the same sample
        /// itself (Seq = the rider's stamp, velocity only within one target key); this adapter and FromTimeline
        /// are the reference probe 1842 tests the timeline with, and checker probe 1851 runs the real chain.
        /// </summary>
        public static EtsNetWire.State ToTimeline(Vector3 offset, float yawLocal, Vector3 offsetVelocity, int epoch, int seq, int stampMs)
        {
            EtsNetWire.State state = default;
            state.Seq = seq;
            state.StampMs = stampMs;
            state.Epoch = epoch;
            state.Driver = -1;
            state.Position = offset;
            state.Velocity = offsetVelocity;
            state.Rotation = Yaw(yawLocal);
            state.TrailerRotation = Quaternion.identity;
            return state;
        }

        /// <summary>The offset and relative heading of a sampled timeline state (ToTimeline's inverse).</summary>
        public static void FromTimeline(in EtsNetWire.State state, out Vector3 offset, out float yawLocal)
        {
            offset = state.Position;
            yawLocal = YawOf(state.Rotation);
        }

        /// <summary>
        /// The rate of a rider's offset between two records, on the writer's clock. The wire carries no velocity
        /// (PLAN §5.3: ints 34-35 and 43), and a Hermite with zero tangents would ease in and out of every 20 Hz
        /// sample - a walker drawn in 50 ms pulses; probe 1842 measures both. Zero across a gap over one second
        /// or a stamp that did not advance. The two records must share one target, seat and epoch: across a
        /// change the "rate" is the jump between two vehicles, and the first sample of the new epoch flings the
        /// drawn rider 1.5 m off both paths (checker probe 1850 X1). A new epoch starts from rest (zero), which
        /// is what EtsRiderTrack does by comparing its key.
        /// </summary>
        public static Vector3 OffsetVelocity(Vector3 before, int beforeStampMs, Vector3 now, int nowStampMs)
        {
            int ms = unchecked(nowStampMs - beforeStampMs);

            if (ms <= 0 || ms > 1000)
            {
                return Vector3.zero;
            }

            return (now - before) * (1000f / ms);
        }

        /// <summary>A rider record is rewritten when the root moved more than this since the last write, metres ([elev §7.2]).</summary>
        public const float WriteMoveMetres = 0.01f;

        /// <summary>...or turned more than this, degrees.</summary>
        public const float WriteTurnDegrees = 1f;

        /// <summary>
        /// Writes of an unchanged record after the rider stops. The receiver takes a sample's velocity from the
        /// last two records (OffsetVelocity), and EtsNetTimeline carries the newest sample on along its velocity
        /// for up to MaxExtrapolation (0.25 s) and then HOLDS there (EtsNetWire.cs:418-425). Without these a
        /// walker who stops is drawn up to 1.4 m/s x 0.25 s = 35 cm past the spot until they move again (probe
        /// 1842 T3b measures it); two identical records give a velocity of exactly zero.
        /// </summary>
        public const int SettleWrites = 3;

        /// <summary>
        /// A root closer than this to the last write (and a heading within SettleTurnDegrees) counts as at rest for
        /// the settle writes: the wire's grid (EtsPlayerWire ints 34-35, S16 millimetres). Anything more is still
        /// moving, if slower than WriteMoveMetres per interval, and starts the settle writes again: counted down
        /// regardless, a 3 cm lean at 0.15 m/s spent all three on the way (7-8 mm a write), the last record kept a
        /// velocity, and the drawn rider was held 9-14 mm past where they stopped until they moved again (checker
        /// probe 1851 Y5). The worst left over is this much a write: 1 mm x 0.25 s / 0.05 s = 5 mm.
        /// </summary>
        public const float SettleMetres = 0.001f;

        /// <summary>The heading's rest band for the settle writes, degrees (Angle16 steps are 0.0055 degrees).</summary>
        public const float SettleTurnDegrees = 0.1f;

        /// <summary>The state of one rider's write rule (EtsRiders keeps one for the local player).</summary>
        public struct WriteGate
        {
            public bool Written;
            public Vector3 Root;
            public float Yaw;
            public double NextAt;
            public int Settle;
        }

        /// <summary>WriteDue with no discrete field changed but the target (see the full overload).</summary>
        public static bool WriteDue(ref WriteGate gate, Vector3 root, float yaw, bool newTarget, double now, float interval)
        {
            return WriteDue(ref gate, root, yaw, newTarget, false, now, interval);
        }

        /// <summary>
        /// Whether to write the rider record now: at once for a new target or seat; otherwise at most once per
        /// interval, while the root moves more than WriteMoveMetres or turns more than WriteTurnDegrees since the
        /// last write, or a field the receivers do not snap on changed (changed: the airborne, XR and yaw-carried
        /// flags); then, while it still creeps (more than SettleMetres a write), and SettleWrites more times once it
        /// is at rest. A rider at rest writes nothing, so a seated passenger costs no bandwidth ([net §2.10]).
        /// Updates the gate when it says yes.
        /// A changed flag waits for the interval like a move: passed as a new target instead, an airborne flag
        /// that flips every frame (CharacterController.isGrounded on a moving deck) wrote 90 records a second at
        /// 90 Hz against MaxWriteHz 20 (checker probe 1850 X2; 18 a second this way).
        /// </summary>
        public static bool WriteDue(ref WriteGate gate, Vector3 root, float yaw, bool newTarget, bool changed, double now, float interval)
        {
            if (!newTarget)
            {
                if (now < gate.NextAt)
                {
                    return false;
                }

                float moveSq = (root - gate.Root).sqrMagnitude;
                float turn = Mathf.Abs(Mathf.DeltaAngle(yaw, gate.Yaw));
                bool moved = changed || !gate.Written || moveSq > WriteMoveMetres * WriteMoveMetres || turn > WriteTurnDegrees;
                bool creeping = moveSq > SettleMetres * SettleMetres || turn > SettleTurnDegrees;

                if (moved || (gate.Settle > 0 && creeping))
                {
                    gate.Settle = SettleWrites;
                }
                else if (gate.Settle > 0)
                {
                    gate.Settle--;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                gate.Settle = SettleWrites;
            }

            gate.Written = true;
            gate.Root = root;
            gate.Yaw = yaw;
            gate.NextAt = now + interval;
            return true;
        }

        private static Quaternion Normalised(Quaternion q)
        {
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return length > 1e-6f ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
        }
    }
}
