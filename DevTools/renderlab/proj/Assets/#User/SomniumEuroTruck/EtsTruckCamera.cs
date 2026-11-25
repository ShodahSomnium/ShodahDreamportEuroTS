using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The driver's view. Two modes: inside the cab at the driver's eye, looking round within the
    /// limits the game's interior camera definition gives this truck (its field of view, head
    /// offset and default downward tilt come from the same file); and a chase view behind the
    /// truck. Like the Quake world's camera, this does not own a camera: each frame after
    /// everything else has moved it places whichever camera is rendering, so it works with the
    /// editor's camera here and with the Somnium client's camera in the world. Right mouse looks
    /// round, C switches inside and outside, F hands the camera back to the free-flight camera.
    /// </summary>
    public class EtsTruckCamera : MonoBehaviour
    {
        public enum Mode { Interior, Chase }

        public EtsTruckController Truck;
        public Mode View = Mode.Chase;
        public bool Active = true;

        [Tooltip("Metres behind the truck's origin in the chase view.")]
        public float ChaseDistance = 14f;

        public float ChaseHeight = 5f;
        public float ChaseFov = 60f;
        public float MouseSensitivity = 3f;

        private Camera _camera;
        private float _yaw;
        private float _pitch;
        private bool _pitchInitialised;
        private EtsFlyCamera _flyCamera;
        private float _restoreFov = -1f;

        private void Update()
        {
            if (Truck == null)
            {
                return;
            }

            // The game's camera keys: 1 the cab, 2 behind the truck (C is the game's cruise control, EtsTruckController).
            Mode wanted = Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1) ? Mode.Interior
                : Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2) ? Mode.Chase : View;

            if (wanted != View)
            {
                View = wanted;
                _yaw = 0f;
                _pitchInitialised = false;
            }

            // The free camera exists only in the editor. In the client F is Somnium's own Interact
            // key (2D Input Actions), and taking it here handed the truck the keys of a player on foot.
            if (Application.isEditor && Input.GetKeyDown(KeyCode.F))
            {
                Active = !Active;

                if (_flyCamera == null)
                {
                    _flyCamera = FindFirstObjectByType<EtsFlyCamera>();
                }

                if (_flyCamera != null)
                {
                    _flyCamera.enabled = !Active;
                }

                Truck.InputEnabled = Active;

                if (!Active && _camera != null && _restoreFov > 0f)
                {
                    _camera.fieldOfView = _restoreFov;
                }
            }
        }

        /// <summary>
        /// Takes the view or hands it back. Handing it back restores the field of view this
        /// changed, so the client's own camera (following the avatar on foot) is as it was.
        /// </summary>
        public void SetActive(bool on)
        {
            Active = on;

            if (!on && _camera != null && _restoreFov > 0f)
            {
                _camera.fieldOfView = _restoreFov;
                _restoreFov = -1f;
            }

            if (!on)
            {
                GiveBrainBack();
            }
        }

        private EtsTruckRig _boundRig;
        private int _boundSeat;
        private bool _subscribed;
        private EtsTruckSeat _seat;

        /// <summary>
        /// Follow a rig from a seat; the eye is rig.Controller.EyeLocalFor(seat), so a passenger looks out from the
        /// right-hand seat (PLAN.md §5.5 step 3, §7.B). The look-round starts again from straight ahead, and a
        /// headset is always in the cab, as SetUpDriving sets it for today's truck. Called with the new flow's
        /// EtsLocalTruck.OccupiedChanged (Rebind) and by EtsMenu.EnterTruck; nothing binds on the old flow, where
        /// Truck is set by EtsModelViewer and the eye stays DriverEyeLocal. Binding the rig and seat already bound
        /// changes nothing, so the menu's call right after the seat's announcement keeps the view as it is.
        /// </summary>
        public void Bind(EtsTruckRig rig, int seat)
        {
            int index = seat == 0 ? 0 : 1; // C's rule: 0 the driver's, any other the passenger's (EyeLocalFor)

            if (rig != null && rig == _boundRig && index == _boundSeat && rig.Controller == Truck)
            {
                // Same rig and seat (the player on foot already watched their own truck from its driver's seat): the
                // desktop look-round stays. A headset is still always in the cab: the mirrors (EtsMirrors), the cab sound
                // (EtsTruckAudio) and the cab/outside add-ons (EtsTruckRig.Update) follow this mode, and a switch to the
                // chase view on the tablet's Drive page would otherwise outlive getting out and back in, which every
                // Bind undid before this early return existed.
                if (EtsVr.Present)
                {
                    View = Mode.Interior;
                }

                return;
            }

            _boundRig = rig;
            _boundSeat = index;

            if (rig != null && rig.Controller != null)
            {
                Truck = rig.Controller;
            }

            _yaw = 0f;
            _pitchInitialised = false;

            if (rig != null && EtsVr.Present)
            {
                View = Mode.Interior;
            }
        }

        /// <summary>
        /// The new flow's binding (PLAN §3.1 "the driver kit rebinds", B2): the truck the local player sits in
        /// (EtsLocalTruck.Occupied, from the seat they sat in), else their own truck from its driver's seat, else none -
        /// the player arrives on foot and a truck exists only once they choose one in the tablet. On foot the own truck
        /// is followed but not drawn (Draws), so the C key and the tablet's camera switch still have a truck to set the
        /// mode for; with no Somnium player at all (the editor) it is drawn, the old flow's editor behaviour.
        /// </summary>
        private void Rebind()
        {
            EtsTruckRig occupied = EtsLocalTruck.Occupied;

            if (occupied != null)
            {
                Bind(occupied, Seat != null && Seat.Seated ? Seat.SeatIndex : 0);
                return;
            }

            EtsTruckRig own = EtsLocalTruck.Own;

            if (own != null)
            {
                Bind(own, 0);
                return;
            }

            // No truck: nothing to follow. Truck goes too - it would otherwise keep a destroyed rig's controller, which
            // Unity reports as null anyway, or a puppet the player has just left.
            _boundRig = null;
            _boundSeat = 0;
            Truck = null;
            HandBack();
        }

        /// <summary>The local seat on this object (the bootstrap puts the seat and this camera on 'Ets Net'), looked up once.</summary>
        private EtsTruckSeat Seat
        {
            get
            {
                if (_seat == null)
                {
                    _seat = GetComponent<EtsTruckSeat>();
                }

                return _seat;
            }
        }

        /// <summary>
        /// New flow: whether this camera may take the view this frame - the local player sits in the truck it follows, or
        /// there is no Somnium player to follow instead (the editor). EtsMenu switches Active the same way every frame
        /// (EtsMenu.Update: Active = seated); this also covers the frames before it has, and a truck that is Own but
        /// not sat in, so the client's own camera is never taken from a player on foot.
        /// </summary>
        private bool Draws => _boundRig != null && (EtsLocalTruck.Occupied == _boundRig || Seat == null || !Seat.HasPlayer);

        /// <summary>The eye in the truck's frame: the bound seat's, or the driver's on the old flow.</summary>
        private Vector3 EyeLocal => _boundRig != null && _boundRig.Controller == Truck ? Truck.EyeLocalFor(_boundSeat) : Truck.DriverEyeLocal;

        /// <summary>Gets or adds the local player's one truck camera on the bootstrap object (PLAN §3.2; EtsNet.Awake calls it only with EtsFeatures.NewFlow).</summary>
        public static EtsTruckCamera Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsTruckCamera camera = host.GetComponent<EtsTruckCamera>();
            return camera != null ? camera : host.AddComponent<EtsTruckCamera>();
        }

        private void OnEnable()
        {
            if (EtsFeatures.NewFlow && !_subscribed)
            {
                _subscribed = true;
                EtsLocalTruck.OccupiedChanged += OnLocalTruckChanged;
                EtsLocalTruck.OwnChanged += OnLocalTruckChanged;
                Rebind();
            }
        }

        /// <summary>
        /// The truck the local player sits in, or their own truck, changed (EtsTruckSeat raises Occupied after choosing
        /// the seat, so its index is already the new one; A raises Own on a spawn or despawn). Both go through Rebind:
        /// on foot (Occupied null) the view is also handed back by the menu (EtsMenu.GetOut -> SetActive(false)).
        /// </summary>
        private void OnLocalTruckChanged(EtsTruckRig rig)
        {
            Rebind();
        }

        /// <summary>Gives the client its camera back as it was (the Cinemachine brain and the field of view) if this has it.</summary>
        private void HandBack()
        {
            if (_camera != null && _restoreFov > 0f)
            {
                _camera.fieldOfView = _restoreFov;
                _restoreFov = -1f;
            }

            GiveBrainBack();
        }

        /// <summary>
        /// The client's desktop camera ('Camera' in CameraSystem.prefab) carries a CinemachineBrain
        /// that runs in LateUpdate after world scripts (execution order 100) and writes the camera's
        /// pose, clip planes and field of view every frame - so a seated desktop player only ever
        /// saw the client's own orbit round the avatar. It is paused while this has the camera and
        /// handed back after. Found by name, so nothing here needs the Cinemachine assembly, which
        /// the server does not compile worlds against.
        /// </summary>
        private Behaviour _brain;

        private void HoldBrain()
        {
            if (_brain == null || _brain.gameObject != _camera.gameObject)
            {
                GiveBrainBack();
                _brain = _camera.GetComponent("CinemachineBrain") as Behaviour;
            }

            if (_brain != null && _brain.enabled)
            {
                _brain.enabled = false;
            }
        }

        private void GiveBrainBack()
        {
            if (_brain != null && !_brain.enabled)
            {
                _brain.enabled = true;
            }

            _brain = null;
        }

        private void OnDisable()
        {
            GiveBrainBack();

            if (_subscribed)
            {
                _subscribed = false;
                EtsLocalTruck.OccupiedChanged -= OnLocalTruckChanged;
                EtsLocalTruck.OwnChanged -= OnLocalTruckChanged;
            }
        }

        private void LateUpdate()
        {
            // In a headset the camera is the headset's: its pose comes from tracking every frame,
            // and writing it here would tear the view away from the player's head. EtsTruckSeat
            // moves the rig instead, so the eyes arrive at the driver's eye.
            if (!Active || Truck == null || !Truck.Placed || EtsVr.Present)
            {
                return;
            }

            // New flow: only the truck the player sits in (or, in the editor, their own). A frame this camera held the
            // view in and may not now gives it straight back, so the client's camera is never left frozen.
            if (EtsFeatures.NewFlow && !Draws)
            {
                if (_brain != null || _restoreFov > 0f)
                {
                    HandBack();
                }

                return;
            }

            if (_camera == null || !_camera.isActiveAndEnabled)
            {
                FindCamera();

                if (_camera == null)
                {
                    return;
                }
            }

            HoldBrain();

            if (_flyCamera == null)
            {
                _flyCamera = FindFirstObjectByType<EtsFlyCamera>();

                if (_flyCamera != null)
                {
                    _flyCamera.enabled = false;
                }
            }

            EtsVehicleSpec spec = Truck.Spec;

            if (!_pitchInitialised)
            {
                _pitch = View == Mode.Interior ? -(spec != null ? spec.MouseUpDownDefault : -8f) : 12f;
                _pitchInitialised = true;
            }

            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxisRaw("Mouse X") * MouseSensitivity;
                _pitch -= Input.GetAxisRaw("Mouse Y") * MouseSensitivity;
            }

            Transform truck = Truck.transform;

            if (View == Mode.Interior)
            {
                // The game's limits are given as left positive, right negative, up positive.
                float left = spec != null ? spec.MouseLeftLimit : 210f;
                float right = spec != null ? spec.MouseRightLimit : -170f;
                _yaw = Mathf.Clamp(_yaw, -left, -right);
                _pitch = Mathf.Clamp(_pitch, -(spec != null ? spec.MouseUpLimit : 73f), -(spec != null ? spec.MouseDownLimit : -65f));

                _camera.transform.position = truck.TransformPoint(EyeLocal);
                _camera.transform.rotation = truck.rotation * Quaternion.Euler(_pitch, _yaw, 0f);
                SetFov(spec != null ? spec.InteriorFov : 75f);
            }
            else
            {
                _pitch = Mathf.Clamp(_pitch, -20f, 60f);
                Quaternion orbit = Quaternion.Euler(0f, truck.eulerAngles.y + _yaw, 0f) * Quaternion.Euler(_pitch, 0f, 0f);
                Vector3 target = truck.position + Vector3.up * 2.5f;
                Vector3 position = target - orbit * Vector3.forward * ChaseDistance + Vector3.up * (ChaseHeight - 2.5f);

                // Keep the chase camera above the ground it may swing behind.
                if (Physics.Raycast(position + Vector3.up * 20f, Vector3.down, out RaycastHit hit, 40f, ~(1 << 2), QueryTriggerInteraction.Ignore) && hit.point.y + 1f > position.y)
                {
                    position.y = hit.point.y + 1f;
                }

                _camera.transform.position = position;
                _camera.transform.rotation = Quaternion.LookRotation(target - position, Vector3.up);
                SetFov(ChaseFov);
            }
        }

        private void SetFov(float fov)
        {
            if (_restoreFov < 0f)
            {
                _restoreFov = _camera.fieldOfView;
            }

            _camera.fieldOfView = fov;

            // See as far as the weather allows. A camera clipping at Unity's default 1000 m cut the
            // ground off well before the fog had hidden it, and the bare sky behind read as a hole.
            if (_camera.farClipPlane < EtsClimate.Visibility)
            {
                _camera.farClipPlane = EtsClimate.Visibility;
            }
        }

        private void FindCamera()
        {
            _camera = Camera.main;

            if (_camera != null && _camera.isActiveAndEnabled)
            {
                return;
            }

            foreach (Camera candidate in FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (candidate.isActiveAndEnabled && candidate.targetTexture == null)
                {
                    _camera = candidate;
                    return;
                }
            }

            _camera = null;
        }
    }
}
