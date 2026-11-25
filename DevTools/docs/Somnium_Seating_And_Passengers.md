# Seating a Somnium player in a moving vehicle, and passengers

How the ETS2 Dreamport makes a Somnium Space player sit in a truck seat, keeps them exactly in place while the
truck moves (desktop and VR), and lets other players ride along as passengers who stay in sync on every screen.

Written for another Dreamport developer and their AI assistant. Every technique names the file and function it
comes from. All world code paths below are relative to
`Assets/#User/SomniumEuroTruck/` in the ETS2 project. Client source references are to
`E:/Somnium Space/Main Build/Assets/_SomniumSpace/`. Cite those by class and method. The line numbers in the
client source change between builds.

---

## 1. The problem: why simple parenting fails in Somnium

The obvious approach is to parent the player to the seat transform, or to set `Body.Root.position` once and let
the vehicle carry it. In Somnium neither works:

- **You do not own the player hierarchy.** The local player is a client prefab (`XR.Body` / `NonXR.Body`) that the
  client moves itself every frame (locomotion, gravity, IK, its own pose system). Re-parenting it breaks the
  client, and anything you write once is overwritten on the next frame.
- **The avatar capsule is solid, even with "collision disabled".** The body carries a `CharacterController` on
  layer 2 and a kinematic Rigidbody. If you write it into a dynamic vehicle's collider, PhysX pushes the vehicle
  out at up to 10 m/s. On 2026-09-21 the ETS truck was pushed through the road and fell 1.6 km.
- **There are three different "positions".** These are the body root, the camera (VR: the headset owns it), and
  the drawn avatar mesh (the client's `MainRoot` / `NonXRMainRoot`, placed in the client's own `Update`). If any of
  them is placed before the vehicle moves this frame, it trails the seat by one frame of travel. At 90 km/h that
  is about 40 cm.
- **The network sends world positions, and they are useless on a moving vehicle.** Each client draws the vehicle
  with its own network delay. Measured on 2026-09-22, a player's own avatar stream landed 0.5 m (parked) to
  4.75 m (60 km/h) away from the seat as other clients drew it. Only vehicle-relative data works.

The answer has four parts:

1. Switch off the client's own motion through the Bridge.
2. Write the body root (and in VR the rig) **every `LateUpdate`, after the vehicle has moved**.
3. Re-place the drawn avatar after that, the way the client does on its elevators.
4. Network the rider's pose **relative to the vehicle**, so every client puts the body back on the vehicle as it
   draws it.

---

## 2. Client facts you must know

These were measured from the client source (`Main Build`) on 2026-09-21, and from client runs.

| Fact | Where | Consequence |
|---|---|---|
| The avatar `CharacterController` is on layer **2 (Ignore Raycast)** on `XR.Body` / `NonXR.Body`, with a **kinematic Rigidbody**. The VR body also has two dynamic 1 kg physical hand boxes on layer 7. | `Player.XR.Body.prefab`, `Player.NonXR.Body.prefab` | The avatar collides with whatever layer 2 collides with, including vehicles (layer 28). |
| `SetCollisionDisableState(true)` only ORs layer 2 into the controller's `excludeLayers`. | `Player/Scripts/Components/Features/Common/MotionFeature.cs` `SetCollisionDisableState` | Players stop bumping into each other, and that is all. The capsule stays solid against everything else. |
| The client's own vehicle mode switches the `CharacterController` off. The Bridge does not offer this. | `NonXRMotionFeature` | You have to make the vehicle ignore the avatar (or the avatar ignore the vehicle) yourself. **Never park an avatar inside a dynamic body.** |
| Collision matrix: row 2 hits 0,1,2,3,7,11-14,16,21-24,28,29,31. Row 28 (Vehicle) hits 0,2,3,7,11,14,15,16,23,24,26,28-31. The client and SDK matrices are byte-identical. | `ProjectSettings/DynamicsManager.asset` | Summarised in `Ets/EtsLayers.cs`. |
| **Desktop clicks** reach world objects only as UI pointer events (XRUIInputModule plus a PhysicsRaycaster on the desktop camera). The effective layers are 0,1,4,5,14,16. | client EventSystem | Use `IPointerClickHandler` on a collider on layer 16. |
| **VR pointing** is XRI's `NearFarInteractor`, not an XRRayInteractor. Far mask 0,5,6,10,14,16,23, 5 m reach, **triggers ignored**, interaction layers 144 (bits 4 and 7), hovered-activate on. | `Player.XR.Body.prefab` | Use a **non-trigger** collider on layer 16 with an `XRSimpleInteractable` on interaction layer `1 << 4`. |
| On a monitor the client **shows** your own avatar's head (`NonXRMainRoot` calls `SetHeadVisibilityState(true)`). In a headset it hides it (`MainRoot.SetHeadVisibilityState(false)`). | `AvatarSystem/Scripts/Roots/MainRoot.cs`, `NonXRMainRoot.cs` | A seated desktop camera at the driver's eye sits inside your own head. Hide it yourself. |
| On elevators, the client places a monitor avatar's main root in **LateUpdate** instead of Update. | `AvatarSystem/Scripts/RootHandlers/NonXR/NonXRRootsMovementHandler.cs` (`if (_isInElevator) RootMovementProcess` in `LateUpdate`), switched by `DynamicObjects/Additional/MobilePlatformPlayerHandler.cs` (`SetAvatarToElevatorState(true)`) | This is not reachable from a world. Rebuild it (section 5.3). |
| Elevator networking: each rider sends their offset in the platform frame, and receivers put it back on their own drawn platform. Both the network transform and the driven transform are written. | `DynamicObjects/Additional/MobilePlatformMover.cs` (`SetLocalOffsetToDriver`, `UseLocalOffsetFromDriver`, `MoveMotionFeature`) | This is the model for passengers (section 6). |
| Poses: `SetPose` re-applied while the pose holds only re-applies the height. The client drops a pose on its cancel action (left X in VR, probably Escape on desktop), on avatar reload, and on scale change. | `PosesFeature.CheckQuitInput`, `PosesFeature.OnSetupModelComplete` | Re-assert the pose **every seated frame**. |
| Entering a pose in VR turns the rig toward where the head looks, counter-turns `HeadCustomPoseOffset`, and slides the rig under the head. | `XRPosesFeature.SetPlayerToPose` | Undo the turn, or "forward" becomes wherever the player happened to look when they sat down. |
| The server compiles worlds against an **older Fusion**. On 2026-09-23 an upload failed on `GetMasterClient`. | server compile | Avoid newer Fusion APIs such as `runner.GetAllBehaviours<NetworkTransform>`. Use a scene search for `NetworkBehaviour`. |
| The client log is at `%USERPROFILE%/AppData/LocalLow/Somnium Space Ltd/Somnium Space VR/Player.log`. | | Tag every world log line (for example `[SomniumEuroTruck]`) and read the log after every run. |

---

## 3. Getting in (interaction)

**File:** `EtsTruckDoor.cs` (`Build`, `Use`, `UseRig`, `Update`, class `EtsTruckDoorHandle`).

Neither client pointer can see the vehicle's own layer (28), and layers 0 and 14 are the ground. So the "door" is
a separate child box on **layer 16**:

```csharp
// EtsTruckDoor.Build
_handle = new GameObject("Ets get in (click the truck)");
_handle.SetActive(false);                       // inactive until usable: the interactable registers on wake
_handle.layer = HandleLayer;                    // 16
_handle.transform.SetParent(Truck.transform, false);
_box = _handle.AddComponent<BoxCollider>();
_box.isTrigger = false;                         // the VR NearFar ray and XRI's collider collection skip triggers
_box.excludeLayers = ~0;                        // touches nothing (like the client's tablet MuteButton)
_box.providesContacts = false;

var interactable = _handle.AddComponent<XRSimpleInteractable>();   // after the collider
interactable.interactionLayers = InteractionLayer;                 // 1 << 4 "XRInteractable"
interactable.activated.AddListener(OnActivated);                   // VR trigger
interactable.selectEntered.AddListener(OnSelected);                // VR grip
_handle.AddComponent<EtsTruckDoorHandle>().Door = this;            // desktop IPointerClickHandler
```

Rules:

- **Desktop backup:** `Update` also raycasts on `Input.GetMouseButtonDown(0)` against layer 16, ground and
  environment, but only when `EventSystem.current.IsPointerOverGameObject()` is false. `Use()` does nothing once
  the player is seated, so if both paths fire, nothing bad happens.
- **Only show the handle when getting in is possible** (`Usable`, `UsableOnRig`): the player is on foot, menus
  and the tablet are closed, and, for the player's own truck, **only while it is parked (kinematic)**. A collider
  added to a dynamic body changes its mass and inertia (see pitfall 2026-09-21b).
- **Own truck:** `UseRig` turns the handle off and calls `EtsMenu.EnterTruck(rig, 0)`, which calls
  `EtsTruckSeat.GetIn(rig, 0)`.
- **Someone else's truck:** `UseRig` calls `EtsNet.RequestSeat(rig.OwnerSlot, 1, granted, refused)` and sits down
  in the `granted` callback with `Menu.EnterTruck(rig, 1)`. See section 6.

---

## 4. Seating the local player

**File:** `EtsTruckSeat.cs`. There is one per local player, on the bootstrap object (`EtsTruckSeat.Ensure`).

### 4.1 Taking the player over: `TrySeat`

```csharp
var player = SomniumBridge.PlayersContainer?.LocalPlayer;
_root   = player?.References?.Body?.Root;      // the body root (XR.Body / NonXR.Body)
_motion = player?.Features?.Motion;
_animation = player?.Features?.Animation;
_poses  = player?.Features?.Poses;
_avatar = player?.Features?.Avatar;
_head   = player?.References?.Body?.Head;

if (Truck != null && _seatIndex == 0) Truck.SetDriven(true);   // vehicle first: its box already ignores the avatar
if (_seatIndex != 0) ExcludeVehicles(true);                     // passenger: own capsule ignores vehicles

_motion.SetCollisionDisableState(true);
_motion.SetFlyModeDisableState(true);
_motion.SetTeleportationDisableState(true);
_motion.SetMovementSpeed(0f, 0f, 0f);
_motion.SetGravity(0f, 0f);
_motion.SetJump(0f, 0f);
_animation?.SetFallingAnimationDisabled(true);
_avatar?.SetVRIKLegMovementDisabled(true);     // no IK stepping while carried at road speed
```

- The Bridge may not have a body yet (editor, or still loading). `GetIn` then starts `FindPlayer`, which retries
  every 0.5 s for up to 15 s and logs once if it gives up.
- **Order matters:** make the vehicle ignore the avatar *before* the avatar is first written into it.

### 4.2 Every frame: `LateUpdate`

`EtsTruckSeat` runs at the default order (0) in `LateUpdate`. By then the truck's Rigidbody has moved (physics
step), its interpolated pose has been applied to `transform`, and every `Update` has run. The seat writes the
player **last**, onto the pose the truck will be drawn at this frame:

```csharp
// EtsTruckSeat.LateUpdate
bool entered = ForceSitting && KeepSitting();       // 1. the pose first: entering it moves and turns the rig

_root.position = truck.TransformPoint(SeatLocalNow); // 2. hip point on the seat locator
_root.rotation = SeatRotation(truck);                //    heading only (section 8)
Vector3 eye = truck.TransformPoint(EyeLocalNow);

if (EtsVr.Present)
{
    if (entered) _restKnown = false;                 // pose height change is not a lean
    PlaceHead(eye);                                  // 3a. VR: move the rig so the headset lands on the eye
    if (Wheel != null && _seatIndex == 0) Wheel.AfterRigPlaced();   // hands only now (section 4.6)
}
else if (_head != null && PoseLowered)
{
    _root.position += eye - _head.position;          // 3b. desktop: lower the root so the posed head is on the eye
}
```

`SeatLocalNow` is `Truck.SeatLocalFor(seat)` and `EyeLocalNow` is `Truck.EyeLocalFor(seat)` (in
`EtsTruckController`). Seat 0 is the driver's locator. Any other index is the passenger seat. The passenger eye
is the driver eye shifted by the difference between the two seat locators.

### 4.3 The sitting pose: `KeepSitting`, `UndoPoseRecentre`

The pose is set **every seated frame**, because the client can drop it at any time:

```csharp
// EtsTruckSeat.KeepSitting
_poses.SetPose(SeatPose);           // PoseType.SittingStraight (head at 0.7267 of avatar height)
if (EtsVr.Present && eyes != null && _poseOffset != null)
    UndoPoseRecentre(eyes);
```

- The pose is networked by the client itself, so everyone else sees the player sitting with no world code.
- `SetPose` is wrapped in try/catch. If it throws, `ForceSitting` is turned off for the session and the throw is
  logged.
- **VR re-centre undo:** `FindPoseOffset` walks up from the camera to the client's `HeadCustomPoseOffset` (and
  `LeftHandCustomPoseOffset` / `RightHandCustomPoseOffset` above the hand anchors). `UndoPoseRecentre` sets their
  `localRotation` back to identity and puts the camera straight above the rig origin, keeping the height the pose
  set:

```csharp
if (_poseOffset.localRotation != Quaternion.identity)
{
    _poseOffset.localRotation = Quaternion.identity;
    Vector3 inRig = _root.InverseTransformPoint(eyes.position);
    _poseOffset.position -= _root.TransformVector(new Vector3(inRig.x, 0f, inRig.z));
}
UndoHandRecentre(_leftHandOffset);   // the client turns the hand offsets by the same angle;
UndoHandRecentre(_rightHandOffset);  // left turned, a 40 degree entry puts the hands ~0.33 m off
```

- **Desktop:** `PoseLowered` is true when `HeadCustomPoseOffset.localPosition.y < -0.05`, which means the pose has
  lowered the head anchor. The root is then shifted so the head anchor lands on the eye, which brings the hips
  down onto the seat.

### 4.4 Head placement in VR: `PlaceHead`

You cannot write the headset camera. Move the **rig** by the difference between where the eyes are and where the
eye point is. Keep the player's own head movement (leaning) on top, but clamp it:

```csharp
// EtsTruckSeat.PlaceHead
Camera head = EtsVr.Head();
if (_rig == null)
{
    bool carried = head.transform.IsChildOf(_root);
    _rig = carried ? _root : head.transform.root;   // normally the body root ("XR.Body")
}
Vector3 inRig = _rig.InverseTransformPoint(head.transform.position);
if (!_restKnown || (inRig - _headRest).sqrMagnitude > 0.7f * 0.7f) { _headRest = inRig; _restKnown = true; }
Vector3 lean   = Vector3.ClampMagnitude(inRig - _headRest, LeanLimit);   // 0.45 m
Vector3 wanted = eye + _rig.TransformVector(lean);
_rig.position += wanted - head.transform.position;
```

- The "rest" head position is re-taken when the head is more than 0.7 m from it: tracking came up late, or the
  player stood up or re-centred.
- On desktop the camera is not the player's. `EtsTruckCamera.LateUpdate` places it at the eye and returns
  immediately when `EtsVr.Present`, because in a headset writing the camera would tear the view off the head.

### 4.5 Desktop and VR differences

| | Desktop | VR |
|---|---|---|
| Camera | World-placed by `EtsTruckCamera` at `EyeLocalFor(seat)` | Headset. `PlaceHead` moves the rig. |
| Body root | Seat locator, then lowered so the posed head is on the eye | Seat locator, then the rig is shifted by `PlaceHead` |
| Pose re-centre | No re-centre to undo | `UndoPoseRecentre` on head and hand offsets |
| Drawn avatar | Placed by the client in `Update`, so it must be re-placed in `LateUpdate` (5.3) | VRIK places it at the end of `LateUpdate` from the moved targets. Leave it alone. |
| Own head | Shown by the client, so hide it (5.4) | Already hidden by the client |
| Facing on a standing platform | The client owns it. Do not turn the player. | Turned with the vehicle (`EtsRiders.CarryYawInVr`) |

### 4.6 Hands on the wheel: `EtsSteeringGrab`

**File:** `EtsSteeringGrab.cs` (`AfterRigPlaced`, `LetGo`).

- The hands are the Bridge's `Body.LeftHand` / `Body.RightHand`. They hang under the rig that the seat moves, so
  they are **only in the cab after `PlaceHead`**. Read earlier, a hand is where the truck was last frame (0.28 m
  behind at 90 km/h and 90 Hz). That is why the seat calls `Wheel.AfterRigPlaced()` at the end of its own
  `LateUpdate`, instead of relying on script order between two components.
- No XRI and no collider on the wheel. A collider would become part of the truck's compound body. The grip value
  is read from UnityEngine.XR. Turning is measured from how the hand moved **inside the rig** (tracking space), so
  the cab moving under a level rig does not turn the wheel.
- Only the driver gets this (`Seat.IsDriver`). A passenger has no wheel.

---

## 5. Staying perfect while moving

### 5.1 Rigidbody interpolation and who reads what

**File:** `EtsTruckController.cs` (`Configure`, `SetPuppet`, `SetDriven`).

- A **driven** truck is a dynamic Rigidbody with `RigidbodyInterpolation.Interpolate`. Unity writes the
  interpolated pose to `transform` before `Update`, so `truck.transform` read in `LateUpdate` is exactly where the
  truck is drawn. The seat places the player on that pose. Never place a seated player from `FixedUpdate`, or from
  the Rigidbody's `position`.
- A **puppet** (another player's truck drawn from the network) is kinematic with
  `RigidbodyInterpolation.None`. The comment reads "Placed by transform every frame; Rigidbody interpolation would
  draw it a step behind". It is posed in `EtsNet.Update` (`DrawPuppets`) before anything reads it.
- A **parked** truck is kinematic and solid. Nobody can walk through it or shove it.

### 5.2 Frame order

`[DefaultExecutionOrder]` is honoured for components added at run time. The client's log checked this, and
`EtsOrderProbe` tests it.

| Order | Phase | What |
|---|---|---|
| (physics) | FixedUpdate, then interpolation | Driven truck moves |
| -100 `EtsNet` | Update | Read slots, draw puppets (`DrawPuppets`), then `EtsRiders.EarlyPass()`, then write own slot |
| 0 (client) | Update | The client's receive drivers and `NonXRRootsMovementHandler` place avatars |
| 9990 `EtsAvatarSync` | Update | Remember the main root's pose relative to the body root |
| -100 `EtsNet` | LateUpdate | `EtsRiders.LatePass()`: re-place remote riders after Fusion's Render |
| 0 `EtsTruckSeat` | LateUpdate | Place the local body root and rig on the truck, then hands |
| 0 `EtsTruckCamera` | LateUpdate | Desktop camera at the eye |
| 9990 `EtsAvatarSync` | LateUpdate | Put the main root back on this frame's body (elevator technique), hide own head |
| 9998 (client) | LateUpdate | VRIK |

### 5.3 The elevator technique, rebuilt: `EtsAvatarSync`

**File:** `EtsAvatarSync.cs` (`Update`, `LateUpdate`, `Remember`, `Carry`, `FindMain`). The class has
`[DefaultExecutionOrder(9990)]`.

The client places a monitor avatar's drawn root (`NonXRMainRoot`) in `Update`, which is *before* the seat moves the
body root with the truck. A 2D avatar has no IK to fix this later, so the avatar trailed the camera by one frame
of travel. The client solves this on its elevators by placing the root in `LateUpdate`. A world cannot switch that
on, so the world copies it:

```csharp
// Update (order 9990): after the client placed the main root, remember it in the body-root frame
Quaternion inv = Quaternion.Inverse(body.BodyRoot.rotation);
body.OffPos = inv * (body.MainRoot.position - body.BodyRoot.position);
body.OffRot = inv * body.MainRoot.rotation;

// LateUpdate (order 9990): after the seat / LatePass moved the body root, put it back by the same pose
Quaternion rot = body.BodyRoot.rotation;
body.MainRoot.SetPositionAndRotation(body.BodyRoot.position + rot * body.OffPos, rot * body.OffRot);
```

- This is done for your own avatar (desktop only, while seated) and for every **remote monitor rider** that
  `EtsRiders` placed this frame (`EtsRiders.PlacedDesktopRemotes`).
- Headset avatars are skipped. VRIK places them later from the moved targets.
- **Finding the main copy** (`FindMain`, `MainRootOf`): look for an active humanoid `Animator` whose head bone is
  within 0.6 m of `Body.Head` and which has a component whose type name is `MainRoot` or `NonXRMainRoot` on itself
  or up to 3 parents above. The search runs at most once a second while nothing has been found.

### 5.4 Hiding your own head: `EtsAvatarSync.HeadHide`

This is the Black Ops 2 Dreamport's method:

- Scale the main copy's head bone to `1e-4`. Hair, hat and glasses hang under the bone and go with it.
- Do it only while seated with the camera within 45 cm of the head. It is released past 60 cm, so the chase view
  shows the whole avatar.
- Re-apply it **every frame**, because the client resets the head scale when it sets the model up again.
- Restore it on getting out, on `OnDisable` / `OnDestroy`, and when the avatar changes.
- If the head is already scaled under 0.001, the client has hidden it (headset), so leave it alone.
- Others still see the whole head. The local main copy is drawn only on this client and casts no shadow, and bone
  scale is not networked.
- Known side effect (untested): the truck's mirror cameras may show the driver without a head.

### 5.5 Collisions while moving

**Files:** `EtsTruckController.SetDriven`, `Ets/EtsLayers.cs`.

- **Driver** (`SetDriven(true)`): the truck goes dynamic, and its box (and the trailer's) gets
  `excludeLayers = EtsLayers.SeatedBodyExcludes = GroundMask | (1<<2) | (1<<7) | (1<<24)`. It ignores the drive
  surface, the avatar capsule, the VR physical hands and the Player layer.
- **On getting out** (`SetDriven(false)`): velocities are zeroed, the truck goes kinematic, and the box **stays
  see-through for 1 more second** (`_solidAgainAt`) while the Bridge lifts the capsule clear. After that it is solid
  again (`EtsLayers.BodyExcludes`). If the truck was tipped or airborne, it is set down upright
  (`PlaceOnGround`).
- **Passenger:** the puppet they sit in stays kinematic and solid, so other walkers can stand on it. Instead, the
  passenger's own capsule ignores vehicles (`EtsTruckSeat.ExcludeVehicles`):

```csharp
_capsule = _root.GetComponent<CharacterController>();
if (_capsule != null && (_capsule.excludeLayers.value & EtsLayers.SeatedCapsuleExcludes) == 0)
{
    _capsule.excludeLayers = _capsule.excludeLayers.value | EtsLayers.SeatedCapsuleExcludes;  // 1 << 28
    _capsuleExcluded = true;            // only clear a bit this code set
}
```

The client only ORs and clears bit 2 of that mask, so bit 28 survives until the seat clears it in `Release`.

---

## 6. Passengers

### 6.1 Claiming a seat (state-based, no RPC)

**Files:** `EtsTruckDoor.UseRig`, `EtsNet.RequestSeat`, `EtsNet.AnswerSeatRequest`, and `EtsSeatTable` /
`EtsSeatRequest` in `EtsPlayerWire.cs`.

Each player owns one networked slot ("Ets Net Player k", a `NetworkBridgeData64`) and writes only that slot. The
seat claim is done entirely with state:

1. **The passenger asks.** `EtsNet.RequestSeat(ownerSlot, seat, granted, refused)` writes the passenger's own
   rider record as *requesting*. The record already contains the seat's offset, so a grant puts the body straight
   into the seat:

   ```csharp
   _localRider = new EtsRider {
       Kind = EtsTargetKind.Tractor, Index = ownerSlot, Serial = serial, Seat = seat,
       Requesting = true, Epoch = (_localRider.Epoch + 1) & 0xFF, Xr = EtsVr.Present,
       Root = cab != null ? cab.SeatLocalFor(seat) : Vector3.zero, Yaw = 0f };
   ```

   Some requests are refused at once (`SeatRefusal`): playing alone, not joined yet, the seat is out of range, the
   truck is gone, or the seat is taken.
2. **The owner decides.** Every 0.25 s (`EtsSeatTable.ServeEvery`), the owner's client offers every slot's rider
   record to its `EtsSeatTable.Offer`. A *requesting* record that names this owner's slot, truck serial and a free
   seat gets the seat. A player can hold only one seat. The owner writes the table into ints 26-27 of **its own**
   slot (`EtsPlayerWire.PackSeats`: four 16-bit PlayerIds+1).
3. **The passenger reads the answer.** `EtsSeatRequest.Step` reads the owner's table. If it names this player, the
   answer is *Granted*. If it names someone else, the answer is *Taken*. With no answer in 3 s
   (`EtsSeatRequest.Timeout`) it is *NoAnswer*, and if the truck vanished it is *Gone*. On a grant, `Requesting` is
   cleared and `granted` runs, and `EtsTruckDoor` then calls `Menu.EnterTruck(rig, 1)`, which calls
   `EtsTruckSeat.GetIn(rig, 1)`.
4. **Keeping and freeing the seat.** The owner keeps the seat while the passenger's record keeps naming it. If the
   record stops naming the seat for `EtsSeatTable.StaleAfter` (2 s), or the player leaves the room, or the truck is
   gone, the seat is freed (`EtsSeatTable.End`). A late joiner reads who sits where straight from ints 26-27.

Hand-over details:

- While a request is out, `EtsRiders.HoldPublishing(true)` stops the rider pass from overwriting the requesting
  record. `EtsTruckDoor` takes this hold *after* calling `RequestSeat`, so it cannot be lifted by the request it
  replaces.
- If a seat is granted but nobody sits down, `EtsTruckDoor` calls `net.LeaveSeat()`. `EtsNet.ReleaseStaleRequestRecord`
  also drops it after `GrantedHandOver` (5 s).

Only the owner drives (`EtsTruckSeat.GetIn(rig, seat)` refuses seat 0 of a puppet). A passenger **never** calls
`SetDriven`, so getting in or out never wakes or parks someone else's truck.

### 6.2 What goes on the wire

**File:** `EtsPlayerWire.cs` (`EtsRider`, `PackRider`, `UnpackRider`).

```csharp
public struct EtsRider {
    public EtsTargetKind Kind; public int Index; public int Serial;  // which vehicle: Tractor/Trailer/TrafficCar, owner slot, serial
    public int Seat;                 // 0 driver, 1..4 passenger, 255 standing
    public bool Requesting;          // a seat request is pending
    public int Epoch;                // bumped on target or mode change; receivers snap
    public bool Airborne, Xr, YawCarried;
    public Vector3 Root; public float Yaw;  // body root in the VEHICLE's frame (m), yaw relative to the vehicle (deg)
}
```

- The record is packed into ints 33-35 and 43 of the rider's own slot. Root is stored in millimetres as 16-bit
  values, and yaw as a 16-bit angle.
- Head and hand offsets are **not** sent. The client's own avatar stream still carries them. Only the root is
  corrected (see 6.3).
- **Publishing** (`EtsRiders.Publish`, from `LocalEarly`): the root is measured against the vehicle **as this
  client draws it**:

  ```csharp
  EtsRiderMath.Measure(frame, _root.position, _root.rotation, out rider.Root, out rider.Yaw);
  // offset = Conjugate(frame.rotation) * (root - frame.position);  yawLocal = DeltaAngle(yaw(frame), yaw(root))
  net.SetLocalRider(in rider);
  ```

  At most 20 Hz (`MaxWriteHz`), only when something changes. After the rider stops, a couple of settling records
  go out (`EtsRiderMath.WriteDue`). A rider at rest repeats the record every 3 s (`KeepAliveEvery`). A seated
  passenger therefore costs almost no bandwidth.

### 6.3 Placing remote players relative to the vehicle

**File:** `EtsRiders.cs` (`RemotesEarly`, `RemotesLate`, `Bind`, `CacheRaw`, `Apply`, `Place`, `Limbs`, `Restore`).
The math is in `Ets/EtsRiderMath.cs`.

Every client puts each remote rider onto the vehicle **as that client draws it**, so the vehicle's network delay
cancels out. Offline, a passenger at 90 km/h lands within 0.82 mm of the drawn seat. Without the correction the
error was 5.35 m.

1. `EtsNet.RemoteRiders` gives each remote record, sampled on the rider's own timeline. Records with
   `Requesting` set are skipped: they are not a seat yet.
2. `EtsNet.TryGetVehicleFrame(kind, index, serial, out frame)` returns this client's drawn transform for that
   vehicle.
3. Target: `EtsRiderMath.Target(frame, offset, yawLocal, out root, out yaw)`, which is
   `frame.TransformPoint(offset)` with heading `frame heading + yawLocal`.
4. **Rigid correction** of the whole body (root, head, both hands) with the same transform, so the head and hands
   keep the pose that Fusion's snapshot gave them relative to the body:

   ```csharp
   // EtsRiderMath.Correct
   Quaternion turn = Yaw(Mathf.DeltaAngle(rawRootYaw, targetYaw));
   return new Pose(targetRoot + turn * (raw.position - rawRoot), Normalised(turn * raw.rotation));
   ```

5. **Write both copies**, exactly like `MobilePlatformMover.UseLocalOffsetFromDriver`. That means the Fusion
   network transforms (`Body/Root`, `Head`, `LeftHand`, `RightHand`, found by `Bind`) *and* the Bridge transforms
   they drive (`Player.References.Body.Root/Head/LeftHand/RightHand`). If only one is written, the trackers lag a
   frame and jitter against the head.

   ```csharp
   // EtsRiders.Apply
   remote.NtRoot.SetPositionAndRotation(root.position, root.rotation);
   bodyRoot.SetPositionAndRotation(root.position, root.rotation);
   Place(remote.NtHead, body?.Head, remote.RawHead, rawRoot, rawYaw, remote);   // same for both hands
   ```

6. This runs twice a frame:
   - In `EarlyPass` (EtsNet.Update), *before* the client's order-0 receive drivers copy the transforms.
   - In `LatePass` (EtsNet.LateUpdate), after Fusion's Render has interpolated again. This also keeps things that
     hang under the body, such as the voice speaker under Body/Head, on the truck.

   After that, `EtsAvatarSync` carries remote **monitor** avatars' main roots (5.3).
7. `Limbs` turns off IK leg stepping (`SetVRIKLegMovementDisabled(true)`) and the falling animation for a seated
   rider, or for one on a vehicle faster than 0.5 m/s. These switches are local to each client. When the rider
   leaves, `Restore` gives them back. That is the substitute for the client's `VRIKAddPlatformMotion`, which a
   world cannot reach.
8. `Bind` finds the network transforms with a scene search for `NetworkBehaviour` whose
   `Object.StateAuthority.PlayerId` is the rider's and whose parent is named `Body`. The search runs at most once a
   second. It does not use `runner.GetAllBehaviours<NetworkTransform>`, because the server's older Fusion rejects
   it.

Standing riders use the same records with `Seat = 255`. The local walker is carried by the vehicle's motion since
last frame (`EtsRiderMath.CarryRoot`, then `Physics.SyncTransforms()`, in `LocalEarly`). This is the client's
`MobilePlatformMover.MoveMotionFeature` rebuilt. Yaw is carried in VR only.

### 6.4 Who may get out when

**Files:** `EtsTruckSeat.CanGetOut`, `EtsTruckSeat.GetOut`.

- The **driver** may always get out, and the truck is parked where it stands.
- A **passenger** may get out only below `EtsRiders.PassengerExitMaxKmh` (10 km/h). Otherwise the tablet or menu
  shows "Wait until the truck slows down to get out."
- If the **owner's truck vanishes** (owner left, despawned, or replaced), `EtsTruckSeat.Update` calls `Vanished`.
  That releases the seat, then `LandBeside` waits up to 10 s for ground at the last drawn door position. If none
  appears, it sends the player to the session's arrival point (`TryArrival`).
- If the driver's own truck stops being theirs (replaced in the tablet), `OnOwnChanged` calls `LetOutOfFormerOwn`
  *immediately*, while the rig still exists.

---

## 7. Getting out safely

**Files:** `EtsTruckSeat.Release`, `EtsTruckSeat.StandBeside`, `EtsVr.Teleport`.

The order in `Release` matters:

```csharp
Wheel?.LetGo();
LevelRig();                               // upright about the eyes (a no-op now, kept as a guard)
_poses.SetPose(PoseType.None);            // leave the pose BEFORE moving the player (VR: puts rig back under head)
_avatar?.SetVRIKLegMovementDisabled(false);
_motion.SetCollisionDisableState(false);
_motion.SetFlyModeDisableState(false);
_motion.SetTeleportationDisableState(false);
_motion.SetMovementSpeed(1f, 1f, 1f);
_motion.SetGravity(1f, 1f);
_motion.SetJump(1f, 1f);
_animation?.SetFallingAnimationDisabled(false);
ExcludeVehicles(false);                   // passenger: clear only the bit we set
if (Truck != null && _seatIndex == 0) Truck.SetDriven(false);   // driver only: park
if (_onRig && _seatIndex != 0) EtsNet.Instance?.LeaveSeat();    // passenger: drop the rider record
if (_onRig) EtsLocalTruck.SetOccupied(null);
```

Then `StandBeside` puts the player on the ground 1.6 m outside their door, facing the truck. The side comes from
the seat locator's x: the driver is on the left and the passenger on the right.

- **Use `DoTeleportToPoint`, not a transform write.** It moves the CharacterController and the body together.
  Setting the transform does not.

  ```csharp
  // EtsVr.Teleport
  motion.DoTeleportToPoint(position, new Vector3(0f, yawDegrees, 0f), null);
  ```

- The ground is found by a ray from 3 m above the door down 30 m against `GroundMask | Environment`, and the
  player is placed 5 cm above the hit.
- The driven truck's box stays see-through for one more second (5.5) so the capsule is never inside a solid
  vehicle.
- `OnDisable` also calls `Release`, so disabling or destroying the seat always gives the player back.

---

## 8. Comfort rules

- **No pitch or roll on the player. Heading only.**

  ```csharp
  // EtsTruckSeat.SeatRotation
  return Quaternion.Euler(0f, truck.rotation.eulerAngles.y, 0f);
  ```

  A cab-tilt follow (pitch and roll with 0.08 s smoothing, tablet toggle "Seat: tilt with the cab") was added on
  2026-09-23 and **removed on 2026-09-25**. The user found the view rolling and pitching with the truck "VERY
  disorientating" in the headset. The cab can pitch and roll *around* a level player. The steering wheel measures
  hand motion in rig space for this reason (4.6).
- Keep the player's own head movement in VR (the lean, clamped to 0.45 m). Never snap the headset to a fixed point.
- Undo the client's pose re-centre (4.3), or the view turns to wherever the player looked when they sat down.
- When standing riders are carried, turn them with the vehicle only in VR. On a monitor the client owns the facing.
- In VR the trigger that pointed at the truck is also the accelerator. The pedals stay disarmed until both
  triggers are released (`EtsTruckController.DisarmVrPedals`). A passenger is always disarmed.

---

## 9. Pitfalls we hit

| Date | What happened | Cause | Fix |
|---|---|---|---|
| 2026-09-21 | With nobody driving, the truck accelerated 6, 11, 297, then 434 km/h and fell 1.6 km through the world. | The seat wrote the avatar's solid capsule (layer 2, kinematic Rigidbody) into the dynamic truck box every frame, and PhysX depenetrated the truck at up to 10 m/s. `SetCollisionDisableState` does not stop this. | Kinematic and solid when parked. Driven: box `excludeLayers` gets layers 2, 7 and 24. Solid again 1 s after getting out. Wheel rays start above the hub. |
| 2026-09-21 | "As soon as I enter the truck it spins": the truck was thrown 34 m up and fell through twice. | The door handle's code read the Rigidbody inertia in the same frame the body box was added (a placeholder value) and set it back explicitly. That turned automatic inertia off for good. | Leave inertia to Unity. Only add the handle collider while the truck is kinematic. |
| 2026-09-22 | Remote avatars were drawn 0.5-4.75 m off the seat (and 2.5-8.7 m at 90 km/h). | Each client draws the truck with its own delay, and the avatar stream is in world space. | Vehicle-relative rider records, placed on each client's own drawn vehicle (section 6). |
| 2026-09-23 | Upload rejected by the server compile. | `GetMasterClient` does not exist in the server's older Fusion. | Use only proven Fusion calls. Find network bodies by a scene search for `NetworkBehaviour`. |
| 2026-09-23, then 2026-09-25 | Cab-tilt follow made the user sick in VR. | The player pitched and rolled with the truck. | Removed. Heading only (section 8). |
| (seat code) | In VR the truck's "forward" was wherever the player had looked when they sat down, and the hands were about 0.33 m off. | `XRPosesFeature.SetPlayerToPose` turns `HeadCustomPoseOffset` and the hand offsets. | `UndoPoseRecentre` / `UndoHandRecentre` every seated frame. |
| (wheel code) | Hands were 0.28 m behind the wheel at 90 km/h. | The hands were read before the seat moved the rig (script order). | The seat calls `Wheel.AfterRigPlaced()` itself at the end of its `LateUpdate`. |
| (seat code) | The player stood up in the seat. | The client drops the pose on its cancel button (left X in VR, which this world also used for its menu), on avatar reload and on scale change. | `SetPose(SittingStraight)` every seated frame. |
| 2026-10-14 | On a monitor the camera was inside the player's own head, and the avatar trailed the seat by about 40 cm at 90 km/h. | The client shows the head on desktop, and places the 2D avatar in `Update` before the truck moves the body. | `EtsAvatarSync`: hide the head bone, and re-place the main root in `LateUpdate` (elevator technique). |
| (checker probe 2240) | A granted seat stayed taken although nobody sat in it. A desktop body slid 86 mm from the seat locator instead of snapping. | The granted request record lingered. The first real record shared its epoch with the request. | `LeaveSeat` when nobody sat down, `GrantedHandOver` 5 s, and the epoch is stepped when publishing resumes. |
| (seat code) | A destroyed rig could not be cleared from `EtsLocalTruck.Occupied`, so listeners never heard of it. | Unity's `==` reports a destroyed object as null. | Let the driver out inside `OnOwnChanged`, before the rig is destroyed. Defer nested `OccupiedChanged` handling to the next `Update`. |

The editor has no avatar, so **none of the physics pitfalls show up in Play mode**. Test in the client and read
`Player.log`.

---

## 10. Checklist to reuse in another Dreamport

1. **Interaction:** add a child box on layer 16 with `isTrigger = false`, `excludeLayers = ~0`, an
   `XRSimpleInteractable` (`interactionLayers = 1 << 4`, `activated` + `selectEntered`) and an
   `IPointerClickHandler`. Keep it inactive unless getting in is possible. Never add it to a dynamic body.
2. **Vehicle physics:** kinematic and solid when empty. When driven, the box `excludeLayers` gets
   `(1<<2)|(1<<7)|(1<<24)`. Restore it 1 s after getting out. Puppets are kinematic with interpolation off. The
   driven body uses `Interpolate`.
3. **Take the player:** use `SomniumBridge.PlayersContainer.LocalPlayer`. On `Features.Motion` call
   `SetCollisionDisableState(true)`, `SetFlyModeDisableState(true)`, `SetTeleportationDisableState(true)`,
   `SetMovementSpeed(0,0,0)`, `SetGravity(0,0)` and `SetJump(0,0)`. Also call
   `Features.Animation.SetFallingAnimationDisabled(true)` and `Features.Avatar.SetVRIKLegMovementDisabled(true)`.
   A passenger in a solid vehicle also ORs the vehicle layer into its own `CharacterController.excludeLayers`.
4. **Every `LateUpdate` (default order):**
   - `Features.Poses.SetPose(PoseType.SittingStraight)`, then undo the VR re-centre on `HeadCustomPoseOffset`
     and the hand pose offsets.
   - Set `Body.Root` position to the seat locator and its rotation to the vehicle's **yaw only**.
   - VR: move the rig so the headset lands on the eye point, keeping a clamped lean. Desktop: place your own camera
     at the eye, and lower the root so the posed head is on the eye.
   - Then do anything that reads the hands.
5. **Order 9990:** in `Update` remember the `MainRoot`/`NonXRMainRoot` pose relative to `Body.Root`. In
   `LateUpdate` re-apply it (desktop avatars only). While seated in first person, scale your own head bone to
   1e-4, every frame, and restore it on exit.
6. **Network (if passengers are wanted):**
   - Each player publishes `{vehicle id, seat, root offset and yaw in the vehicle's frame, epoch}` measured against
     their own drawn vehicle. Limit it to 20 Hz and send a keep-alive when idle.
   - The vehicle's owner keeps a seat table in their own state, granted from "requesting" records and freed after
     2 s without a matching record.
   - Receivers place each body with one rigid correction on their own drawn vehicle. Write both the Fusion
     transforms and the Bridge transforms, in an early pass (before order 0) and again after Fusion's Render.
     Turn off IK legs and the falling animation for remote seated riders.
7. **Get out:** leave the pose first with `SetPose(PoseType.None)`, restore every motion switch, clear only the
   capsule bits you set, then `Motion.DoTeleportToPoint(groundPoint, new Vector3(0, yaw, 0), null)` beside the door.
   Passengers only below a speed limit. Handle "vehicle vanished" by landing the player where it was last drawn.
8. **Never** park an avatar inside a dynamic body. **Never** pitch or roll the player. **Always** test in the
   client and read `Player.log`.
