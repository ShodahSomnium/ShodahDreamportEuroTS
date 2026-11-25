using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Getting in: click the truck (desktop) or point at it and pull the trigger (VR).
    ///
    /// What the client's pointers can hit decides the shape of this, and all of it is from the
    /// client source (E:\Somnium Space\Main Build):
    ///
    ///   - Desktop has no XR interactor at all. A click on a world object arrives as a UI pointer
    ///     event: the EventSystem's XRUIInputModule (mouse on) and a PhysicsRaycaster on the
    ///     desktop camera, whose event mask with the camera's culling mask leaves layers
    ///     0, 1, 4, 5, 14, 16. That is how the client's own SDKSitInteraction sits you down.
    ///   - VR points with XRI's NearFarInteractor: far-ray mask 0, 5, 6, 10, 14, 16, 23, 5 m reach,
    ///     triggers ignored, interaction layers 144 (4 XRInteractable, 7 XRRay), and hovered
    ///     activate on - so the trigger fires `activated` on whatever the line is on. The client's
    ///     XRInteractableButton answers both, as this does.
    ///
    /// Neither sees the truck's own layer (28), and 0 and 14 are the ground the wheels stand on,
    /// so the handle is its own child box on layer 16 (BuildingItem - the SDK sample SDK_Cone's
    /// layer and the sit prefab's). It must be solid, not a trigger: the VR ray and XRI's collider
    /// collection both skip triggers. It is kept out of every contact (excludeLayers = all), the
    /// way the client's tablet MuteButton is a solid collider that touches nothing and can still
    /// be pressed - row 16 would otherwise meet the road, the avatar and the truck itself. It only
    /// exists while the player is on foot and the truck is parked (kinematic), and the truck's
    /// inertia is put back after it is added, so the handling is untouched.
    ///
    /// Only while driving is allowed (the front end has handed over) and the menu is shut: the
    /// game's order is menu, job, then truck.
    ///
    /// The integrated plan (PLAN.md §5.5, C10, C11; owner B) puts one handle on every truck rig -
    /// the player's own and every other player's puppet - through Create(rig), which the truck
    /// factory calls. Its rules, only on a rig and only with EtsFeatures.NewFlow:
    ///   - own truck: the handle exists only while it is parked (kinematic), for the inertia reason
    ///     in Build below; a click is the driver's seat at once, no claim (C10: only the owner drives);
    ///   - another player's truck: the handle always exists (a puppet is always kinematic); a click
    ///     asks its owner for the passenger seat (EtsNet.RequestSeat) and sits down when granted;
    ///   - neither while the player is seated, the menu is open or the tablet app is showing (a click
    ///     on the tablet must not reach a truck behind it, [tablet §3 row 16]).
    /// Today's one truck keeps the four-argument Create and exactly today's rule; Create(rig) builds
    /// nothing while NewFlow is false, so that truck never gets a second handle.
    /// </summary>
    public sealed class EtsTruckDoor : MonoBehaviour
    {
        public EtsTruckController Truck;
        public EtsTruckSeat Seat;
        public EtsMenu Menu;

        /// <summary>The rig this handle is on (Create(rig)); null on the old flow's one truck.</summary>
        public EtsTruckRig Rig;

        /// <summary>The layer both client pointers can hit that is not ground or UI.</summary>
        public const int HandleLayer = 16;

        /// <summary>XRI interaction layer 4, 'XRInteractable', which the client's Near-Far interactor carries.</summary>
        private const int InteractionLayer = 1 << 4;

        private GameObject _handle;
        private BoxCollider _box;
        private bool _reported;

        /// <summary>Builds the handle on the truck. Call after EtsTruckController.Configure.</summary>
        public static EtsTruckDoor Create(GameObject host, EtsTruckController truck, EtsTruckSeat seat, EtsMenu menu)
        {
            EtsTruckDoor door = host.GetComponent<EtsTruckDoor>();

            if (door == null)
            {
                door = host.AddComponent<EtsTruckDoor>();
            }

            door.Truck = truck;
            door.Seat = seat;
            door.Menu = menu;
            door.Build();
            return door;
        }

        /// <summary>
        /// The handle on any rig, own or puppet, created by the truck factory (PLAN.md §7.B; the usable rules are
        /// PLAN C11). The local player's seat and menu are the per-player ones on the bootstrap object (EtsNet's,
        /// PLAN §3.2), found now or on the first frame they exist. Returns null, building nothing, while
        /// EtsFeatures.NewFlow is false: today's one truck gets its handle from the overload above
        /// (EtsModelViewer.SetUpDriving), and a second one on the same truck would take the same clicks.
        /// </summary>
        public static EtsTruckDoor Create(EtsTruckRig rig)
        {
            if (!EtsFeatures.NewFlow || rig == null || rig.Controller == null)
            {
                return null;
            }

            EtsTruckDoor door = rig.GetComponent<EtsTruckDoor>();

            if (door == null)
            {
                door = rig.gameObject.AddComponent<EtsTruckDoor>();
            }

            door.Rig = rig;
            door.Truck = rig.Controller;
            door.FindLocal();
            door.Build();
            return door;
        }

        private float _nextFind;

        /// <summary>The local player's seat and menu on the bootstrap object, once a second until both are there.</summary>
        private void FindLocal()
        {
            if ((Seat != null && Menu != null) || Time.unscaledTime < _nextFind)
            {
                return;
            }

            _nextFind = Time.unscaledTime + 1f;
            EtsNet net = EtsNet.Instance;

            if (net != null)
            {
                if (Seat == null) Seat = net.GetComponent<EtsTruckSeat>();
                if (Menu == null) Menu = net.GetComponent<EtsMenu>();
            }
        }

        private void Build()
        {
            if (Truck == null || Truck.BodyBox == null || _handle != null)
            {
                return;
            }

            // The truck's inertia is left entirely to Unity. This used to read it here and set it
            // back after adding the box, and that locked in a nonsense value: Unity works mass
            // properties out lazily, the body's own box had been added in this same frame, so the
            // read got the placeholder of a body with no colliders. Set explicitly, it turned off
            // the automatic calculation for good, and a 7 t truck with the rotational inertia of a
            // small box spun out of control the moment it became dynamic (client run 2026-09-21,
            // "as soon as I enter the truck it spins": thrown 34 m up, fell through twice). The
            // handle is only ever active while the truck is parked and kinematic, and Unity
            // recomputes the automatic tensor from the colliders present when it goes dynamic.

            // Built inactive, so the interactable does not register until there is someone to use it.
            _handle = new GameObject("Ets get in (click the truck)");
            _handle.SetActive(false);
            _handle.layer = HandleLayer;
            _handle.transform.SetParent(Truck.transform, false);

            // The body box, reaching down to the steps so the door is easy to point at.
            BoxCollider bodyBox = Truck.BodyBox;
            float top = bodyBox.center.y + bodyBox.size.y * 0.5f;
            float bottom = Mathf.Min(bodyBox.center.y - bodyBox.size.y * 0.5f, 0.3f);
            _box = _handle.AddComponent<BoxCollider>();
            _box.center = new Vector3(bodyBox.center.x, (top + bottom) * 0.5f, bodyBox.center.z);
            _box.size = new Vector3(bodyBox.size.x, top - bottom, bodyBox.size.z);
            _box.isTrigger = false;
            _box.excludeLayers = ~0;
            _box.providesContacts = false;

            // VR: after the collider, because the interactable collects its colliders when it wakes.
            var interactable = _handle.AddComponent<XRSimpleInteractable>();
            interactable.interactionLayers = InteractionLayer;
            interactable.activated.AddListener(OnActivated);
            interactable.selectEntered.AddListener(OnSelected);

            // Desktop: the client's EventSystem delivers the click to this.
            _handle.AddComponent<EtsTruckDoorHandle>().Door = this;
        }

        private void OnActivated(ActivateEventArgs args)
        {
            Use("trigger on the truck");
        }

        private void OnSelected(SelectEnterEventArgs args)
        {
            Use("grip on the truck");
        }

        /// <summary>Whether getting in is on offer right now.</summary>
        public bool Usable => Rig != null ? UsableOnRig : UsableToday;

        /// <summary>Today's rule, for the old flow's one truck (unchanged).</summary>
        private bool UsableToday =>
            Truck != null && Seat != null && Menu != null && Truck.Placed &&
            Seat.HasPlayer && !Seat.Seated && Menu.AllowEscape && !Menu.IsOpen;

        /// <summary>
        /// PLAN C11 for a rig: an own truck only while parked - the handle is a collider on the truck's body and
        /// must never be part of it while it is dynamic (Build) - and a puppet always, since it is kinematic.
        /// Menu.AllowEscape (the old front end's hand-over) is not asked: the new flow has no such front end, and
        /// there are no trucks near the player before arrival.
        /// </summary>
        private bool UsableOnRig =>
            EtsFeatures.NewFlow && Truck != null && Seat != null && Menu != null && Truck.Placed &&
            Seat.HasPlayer && !Seat.Seated && !Menu.IsOpen && !EtsTabletApp.IsVisible &&
            (Rig.IsPuppet || !Truck.Driven);

        /// <summary>Gets in, if getting in is on offer. Every way of pressing the truck ends here.</summary>
        public void Use(string how)
        {
            if (!Usable)
            {
                return;
            }

            if (Rig != null)
            {
                UseRig(how);
                return;
            }

            EtsGameUi.Note(how + ": getting in");

            // In a room the seat may be somebody else's: ask the network first (EtsNet.Claim).
            // Alone or offline the answer is immediate and this is what it always did.
            EtsNet net = EtsNet.Instance;

            if (net != null)
            {
                if (_claiming)
                {
                    return;
                }

                _claiming = true;
                net.Claim(GetIn, reason =>
                {
                    _claiming = false;
                    EtsGameUi.Note("get in refused: " + reason);
                    Menu.Jobs?.Tell(reason);
                });
                return;
            }

            GetIn();
        }

        private bool _claiming;

        private void GetIn()
        {
            _claiming = false;

            // Gone before the truck goes dynamic, so the body it computes its mass from is the
            // truck's own box alone.
            _handle.SetActive(false);
            Menu.EnterTruck();
        }

        /// <summary>
        /// A click on a rig (PLAN §5.5, C10): the own truck is the driver's seat at once; anyone else's asks its
        /// owner for the passenger seat and waits for the answer (EtsNet.RequestSeat: granted within 0.25 s plus the
        /// network delay, refused, or "did not answer" after 3 s, [net §2.7]). EtsRiders stops publishing meanwhile,
        /// because EtsNet writes the requesting rider record itself.
        /// </summary>
        private void UseRig(string how)
        {
            EtsTruckRig rig = Rig;

            if (!rig.IsPuppet)
            {
                EtsGameUi.Note(how + ": getting in (own truck)");
                _handle.SetActive(false);
                Menu.EnterTruck(rig, 0);
                return;
            }

            EtsNet net = EtsNet.Instance;

            if (net == null || _claiming)
            {
                return;
            }

            EtsGameUi.Note(how + ": asking " + EtsNet.NameOf(rig.OwnerPlayerId) + " for the passenger seat");
            _claiming = true;
            EtsRiders riders = EtsRiders.Instance;
            int token = ++_askToken;

            net.RequestSeat(rig.OwnerSlot, 1, () =>
            {
                _claiming = false;
                EndHold(riders);

                // Still on foot, and the truck still here: sit down.
                if (rig != null && Seat != null && !Seat.Seated && Menu != null)
                {
                    Menu.EnterTruck(rig, 1);
                }

                // Granted but not seated (the truck vanished while the answer travelled, the Bridge gave no body): drop the
                // granted record now, or EtsNet keeps it for GrantedHandOver (5 s) and the owner's table keeps the seat that
                // long plus its own 2 s validation ([net §2.7] step 4); cleared now, the seat is free about 2 s later. Not
                // while seated anywhere (the own driver's seat taken meanwhile): the owner's validation frees it within 2 s,
                // and LeaveSeat must not touch the record of the seat in use.
                if (Seat == null || !Seat.Seated)
                {
                    Debug.Log("[SomniumEuroTruck] Riders: the passenger seat was granted but nobody sat down; handed back.");
                    net.LeaveSeat();
                }
            }, reason =>
            {
                _claiming = false;
                EndHold(riders);
                EtsGameUi.Note("passenger seat refused: " + reason);
                Tell(reason);
            });

            // EtsNet writes the requesting rider record itself, so EtsRiders holds its own until the answer ([net §2.7]
            // step 1). Held AFTER the request and only while it is still out, as the tablet's Ride along does
            // (EtsTabletApp.RideAlong, E's checker probe 2275): EtsRiders' hold is one flag, and RequestSeat answers a request
            // it replaces (a Ride along still out: "Another request took its place") inside this call, whose handler lifts the
            // hold - a hold taken before the call would be lifted under this request, and a player standing on a vehicle
            // would then publish over the requesting record (the owner never sees it: "did not answer"). A request refused
            // at once never holds, so it cannot lift the tablet's hold either. Same frame, before EtsRiders' next EarlyPass.
            if (_claiming && token == _askToken && riders != null)
            {
                riders.HoldPublishing(true);
                _holding = true;
            }
        }

        private int _askToken;
        private bool _holding; // this door holds EtsRiders' publishing; only then does an answer lift it

        private void EndHold(EtsRiders riders)
        {
            if (!_holding)
            {
                return; // never held (answered inside RequestSeat): the hold, if any, is the tablet's
            }

            _holding = false;

            if (riders != null)
            {
                riders.HoldPublishing(false);
            }
        }

        private void Tell(string text)
        {
            EtsTabletApp app = EtsTabletApp.Current;

            if (app != null)
            {
                app.Toast(text);
            }
            else if (Menu != null && Menu.Jobs != null)
            {
                Menu.Jobs.Tell(text);
            }
        }

        private float _nextBuild;

        private void Update()
        {
            if (_handle == null)
            {
                // A rig's handle is built from the truck's body box (Build), which EtsTruckController.Configure makes.
                // The factory creates the door after Configure (EtsTruckFactory.Build), so this is only a guard: a door
                // created earlier gets its handle once the box exists, instead of never.
                if (Rig == null || Time.unscaledTime < _nextBuild)
                {
                    return;
                }

                _nextBuild = Time.unscaledTime + 1f;
                Truck = Rig.Controller;
                Build();

                if (_handle == null)
                {
                    return;
                }
            }

            if (Rig != null)
            {
                FindLocal();
            }

            bool usable = Usable;

            if (_handle.activeSelf != usable)
            {
                _handle.SetActive(usable);

                if (usable && !_reported)
                {
                    _reported = true;
                    Debug.Log("[SomniumEuroTruck] The truck can be got into: " + (EtsVr.Present ? "point at it and pull the trigger." : "click it."));
                }
            }

            // Desktop, belt and braces: if the client's pointer event never arrives, a plain click
            // on the handle still counts. Use() does nothing once seated, so both firing is harmless.
            // Not when the pointer is over anything the EventSystem sees (the client's own screen UI,
            // or the handle itself - then the pointer event does the job), and not through a
            // building or the ground standing between the camera and the truck.
            if (usable && !EtsVr.Present && Input.GetMouseButtonDown(0) &&
                (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
            {
                Camera camera = Camera.main;
                int seen = (1 << HandleLayer) | EtsLayers.GroundMask | (1 << EtsLayers.Environment);

                if (camera != null && Physics.Raycast(camera.ScreenPointToRay(Input.mousePosition), out RaycastHit hit, 60f, seen, QueryTriggerInteraction.Ignore) && hit.collider == _box)
                {
                    Use("click on the truck");
                }
            }
        }
    }

    /// <summary>Takes the desktop click on the handle (see EtsTruckDoor) and hands it on.</summary>
    public sealed class EtsTruckDoorHandle : MonoBehaviour, IPointerClickHandler
    {
        public EtsTruckDoor Door;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (Door != null && eventData.button == PointerEventData.InputButton.Left)
            {
                Door.Use("click on the truck");
            }
        }
    }
}
