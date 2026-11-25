using System.Collections.Generic;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The drawn avatars of players in a truck, kept on it (2026-10-14, the user: "I see through my head when I am in truck
    /// driver mode, please use the elevator technique to keep me in sync with other players and myself and also hide my head
    /// for me, like you did in black ops 2").
    ///
    /// 1. The own head hidden on this screen. The Black Ops 2 Dreamport's way (BO2 Game/Combat/Bo2Handling.DeskArms.cs
    ///    Bo2HeadHide, called from Game/Net/Bo2DeskBody.cs OwnAfter): the head bone of the own avatar's MAIN copy (the
    ///    humanoid Animator whose head is at the player's head, found as Game/Net/Bo2AvatarFind.cs ByHead does) is shrunk
    ///    to 1/10000 - hair, horns, hat and glasses hang under it and go with it. The client does the same to the local
    ///    headset avatar's main copy for good (MB AvatarSystem/Scripts/Roots/MainRoot.cs:99-112, SetHeadVisibilityState),
    ///    and shows it on a monitor (NonXRMainRoot.cs:14), which is why the cab camera sat inside the head. Only the main
    ///    copy is touched: it is drawn on this client only (the local main copy casts no shadow, MainRoot.cs:69-70; the
    ///    shadow, the mirrors' and the tablet camera's copies are other roots), and nothing about a bone's scale is
    ///    networked, so everyone else still sees the whole avatar. Kept shrunk while seated with the camera at the head
    ///    (BO2's first-person test: within 45 cm, out at 60 cm, so the chase view shows the avatar whole), given back the
    ///    moment that ends. A head the client has already hidden (a headset) is left alone.
    ///
    /// 2. The elevator technique. On its elevators the client places a monitor avatar's main root in LateUpdate instead
    ///    of Update (MB AvatarSystem/Scripts/RootHandlers/NonXR/NonXRRootsMovementHandler.cs: "if (_isInElevator)
    ///    RootMovementProcess" in LateUpdate, switched by MobilePlatformPlayerHandler.cs:82 SetAvatarToElevatorState for
    ///    every player on the platform), because the platform moves the body after Update: a monitor avatar has no IK to
    ///    place it again later (NonXRAvatarSeatHandler.cs: "the 2D avatar carries no IK solver at all"). None of that is
    ///    reachable from a world, so it is rebuilt: after the client placed the main root in Update (this runs at order
    ///    9990), its pose in the player's body root frame is taken; in LateUpdate, after the seat (EtsTruckSeat, order 0)
    ///    and the riders' LatePass (EtsNet, order -100) moved the body root with the truck, the main root is put back on
    ///    the body root by that same pose - the client's own placement, only on this frame's body. Before it the avatar
    ///    trailed the seat by one frame of the truck's travel (about 40 cm at 90 km/h and 60 fps) while the camera did not.
    ///    The same for every remote monitor rider EtsRiders put on a vehicle (EtsRiders.PlacedDesktopRemotes), whose body
    ///    root LatePass re-places after Fusion's Render. Headset avatars are placed by VRIK at the end of LateUpdate from
    ///    the moved targets already, so they are not touched. The network side of the elevator method - each rider sends
    ///    the pose in the vehicle's frame, every client puts them on its own drawn vehicle (MobilePlatformMover.cs:167-189)
    ///    - is EtsRiders' already.
    ///
    /// Per frame: no allocation; a search (FindObjectsByType) at most once a second per body not found yet.
    /// </summary>
    [DefaultExecutionOrder(9990)]
    public sealed class EtsAvatarSync : MonoBehaviour
    {
        /// <summary>Hide the own avatar's head from the own camera while seated.</summary>
        public bool HideOwnHead = true;

        /// <summary>Place seated monitor avatars (own and remote riders) on this frame's body root in LateUpdate.</summary>
        public bool CarryAvatars = true;

        /// <summary>BO2's first-person test (Bo2DeskBody.OwnBody): the camera within 45 cm of the head bone, out again past 60 cm.</summary>
        private const float FirstPersonIn = 0.45f;
        private const float FirstPersonOut = 0.6f;

        /// <summary>BO2's Bo2HeadHide factor.</summary>
        private const float HideFactor = 0.0001f;

        /// <summary>A head bone found within this of the player's head (Bo2AvatarFind.ByHead).</summary>
        private const float FindRadius = 0.6f;

        private const string MainRootType = "MainRoot";
        private const string NonXrMainRootType = "NonXRMainRoot";

        private sealed class Body
        {
            public ISomniumPlayer Player;
            public Transform BodyRoot;
            public Animator Anim;
            public Transform MainRoot;
            public Transform Head;
            public float NextSearch;
            public Vector3 OffPos;
            public Quaternion OffRot;
            public int OffFrame = -1;
            public int Seen;
            public bool Said;
            public bool CarrySaid;
            public float Worst;
        }

        private static readonly List<MonoBehaviour> Buf = new List<MonoBehaviour>(16);

        private readonly Body _own = new Body();
        private readonly List<Body> _remotes = new List<Body>(8);
        private readonly List<ISomniumPlayer> _placed = new List<ISomniumPlayer>(8);
        private EtsTruckSeat _seat;
        private float _nextSeatLookup;

        // The hidden head and the scale it is given back.
        private Transform _hidden;
        private Vector3 _hiddenScale;
        private bool _firstPerson;
        private bool _saidHide, _saidShow, _saidClient, _saidNoAvatar;

        public static EtsAvatarSync Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsAvatarSync sync = host.GetComponent<EtsAvatarSync>();
            return sync != null ? sync : host.AddComponent<EtsAvatarSync>();
        }

        private bool Seated
        {
            get
            {
                if (_seat == null && Time.unscaledTime >= _nextSeatLookup)
                {
                    _nextSeatLookup = Time.unscaledTime + 1f;
                    _seat = GetComponent<EtsTruckSeat>();
                }

                return _seat != null && _seat.Seated && _seat.Truck != null;
            }
        }

        private void Update()
        {
            int frame = Time.frameCount;
            bool seated = Seated;
            bool vr = EtsVr.Present;

            // The own avatar: needed seated (head hiding in both modes, the carry on a monitor).
            if (seated)
            {
                ISomniumPlayer local = null;

                try { local = SomniumBridge.PlayersContainer?.LocalPlayer; }
                catch (System.Exception) { local = null; }

                if (Ensure(_own, local, "my own avatar") && CarryAvatars && !vr)
                {
                    Remember(_own, frame);
                }
            }

            // Remote monitor riders on a vehicle drawn here.
            _placed.Clear();
            EtsRiders riders = CarryAvatars ? EtsRiders.Instance : null;

            if (riders != null)
            {
                riders.PlacedDesktopRemotes(_placed);
            }

            for (int i = 0; i < _placed.Count; i++)
            {
                Body body = RemoteOf(_placed[i]);
                body.Seen = frame;

                if (Ensure(body, body.Player, "a remote rider's avatar"))
                {
                    Remember(body, frame);
                }
            }

            for (int i = _remotes.Count - 1; i >= 0; i--)
            {
                if (_remotes[i].Seen != frame)
                {
                    _remotes.RemoveAt(i);
                }
            }
        }

        private void LateUpdate()
        {
            int frame = Time.frameCount;
            bool seated = Seated;

            if (seated && CarryAvatars && !EtsVr.Present)
            {
                Carry(_own, frame, "my own avatar");
            }

            for (int i = 0; i < _remotes.Count; i++)
            {
                Carry(_remotes[i], frame, "a remote rider's avatar");
            }

            HeadHide(seated);
        }

        private void OnDisable()
        {
            ShowHead();
        }

        private void OnDestroy()
        {
            ShowHead();
        }

        // ---------------------------------------------------------------- the own head

        private void HeadHide(bool seated)
        {
            Transform head = _own.Head;
            bool alive = head != null && _own.Anim != null && _own.Anim.gameObject.activeInHierarchy;

            if (_hidden != null && (_hidden != head || !alive))
            {
                ShowHead();
            }

            if (!HideOwnHead || !seated || !alive)
            {
                _firstPerson = false;
                ShowHead();
                return;
            }

            Camera camera = Camera.main;

            if (camera == null || !camera.isActiveAndEnabled)
            {
                camera = EtsVr.Head();
            }

            if (camera == null)
            {
                return;
            }

            // The head bone's own position, before it is shrunk: the shrink leaves the bone where it is.
            float d = (head.position - camera.transform.position).magnitude;
            _firstPerson = EtsVr.Present || d < (_firstPerson ? FirstPersonOut : FirstPersonIn);

            if (!_firstPerson)
            {
                ShowHead();
                return;
            }

            if (_hidden == null)
            {
                Vector3 scale = head.localScale;

                if (Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))) < 0.001f)
                {
                    // The client hides the local headset avatar's head itself (MainRoot.SetHeadVisibilityState).
                    if (!_saidClient)
                    {
                        _saidClient = true;
                        Debug.Log("[SomniumEuroTruck] Avatar sync: my avatar's head '" + head.name + "' is already hidden by the client (scale " + scale.x.ToString("0.0e0") + "); left as it is.");
                    }

                    return;
                }

                _hidden = head;
                _hiddenScale = scale;
            }

            // Every frame: the client sets the head scale itself when it sets the model up again (NonXRMainRoot.SetupHead).
            head.localScale = _hiddenScale * HideFactor;

            if (!_saidHide)
            {
                _saidHide = true;
                Debug.Log("[SomniumEuroTruck] Avatar sync: seated - my avatar's head '" + head.name + "' (" + KindOf(_own.Anim.transform) + " copy '" + _own.Anim.name + "') is hidden on this screen only, hair and hat with it; " +
                          "camera " + (d * 100f).ToString("0") + " cm from it (" + (EtsVr.Present ? "headset" : "monitor") + "); others still see it whole.");
            }
        }

        private void ShowHead()
        {
            if (_hidden == null)
            {
                return;
            }

            try
            {
                _hidden.localScale = _hiddenScale;
            }
            catch (System.Exception)
            {
                // The avatar went away (BO2's Bo2HeadHide.Restore).
            }

            _hidden = null;

            if (!_saidShow)
            {
                _saidShow = true;
                Debug.Log("[SomniumEuroTruck] Avatar sync: my avatar's head is shown again (got out, the chase view, or a new avatar).");
            }
        }

        // ---------------------------------------------------------------- the carry

        /// <summary>After the client placed the main root in Update: its pose in the body root's frame.</summary>
        private static void Remember(Body body, int frame)
        {
            if (body.MainRoot == null || body.BodyRoot == null)
            {
                return;
            }

            Quaternion inv = Quaternion.Inverse(body.BodyRoot.rotation);
            body.OffPos = inv * (body.MainRoot.position - body.BodyRoot.position);
            body.OffRot = inv * body.MainRoot.rotation;
            body.OffFrame = frame;
        }

        /// <summary>In LateUpdate, once the body root is on this frame's truck: the main root put back on it by the same pose.</summary>
        private static void Carry(Body body, int frame, string who)
        {
            if (body.OffFrame != frame || body.MainRoot == null || body.BodyRoot == null)
            {
                return;
            }

            Quaternion rot = body.BodyRoot.rotation;
            Vector3 position = body.BodyRoot.position + rot * body.OffPos;
            float moved = (position - body.MainRoot.position).magnitude;
            body.MainRoot.SetPositionAndRotation(position, rot * body.OffRot);

            if (moved > body.Worst)
            {
                body.Worst = moved;
            }

            if (!body.CarrySaid && moved > 0.01f)
            {
                body.CarrySaid = true;
                Debug.Log("[SomniumEuroTruck] Avatar sync: " + who + " '" + body.MainRoot.name + "' carried " + (moved * 100f).ToString("0.0") +
                          " cm onto this frame's seat in LateUpdate (the client placed it in Update, before the truck moved the body; its elevators do the same).");
            }
        }

        // ---------------------------------------------------------------- finding the avatar

        private Body RemoteOf(ISomniumPlayer player)
        {
            for (int i = 0; i < _remotes.Count; i++)
            {
                if (_remotes[i].Player == player)
                {
                    return _remotes[i];
                }
            }

            var body = new Body { Player = player };
            _remotes.Add(body);
            return body;
        }

        /// <summary>The player's avatar main copy, kept while it lives; searched at most once a second while missing.</summary>
        private bool Ensure(Body body, ISomniumPlayer player, string who)
        {
            Transform bodyRoot = null;
            Transform head = null;

            try
            {
                bodyRoot = player?.References?.Body?.Root;
                head = player?.References?.Body?.Head;
            }
            catch (System.Exception)
            {
                bodyRoot = null;
            }

            if (player != body.Player || bodyRoot != body.BodyRoot)
            {
                if (body == _own)
                {
                    ShowHead();
                }

                body.Player = player;
                body.BodyRoot = bodyRoot;
                body.Anim = null;
                body.MainRoot = null;
                body.Head = null;
                body.NextSearch = 0f;
            }

            if (bodyRoot == null)
            {
                return false;
            }

            if (body.Anim != null && body.MainRoot != null && body.Head != null && body.Anim.gameObject.activeInHierarchy)
            {
                return true;
            }

            if (Time.unscaledTime < body.NextSearch)
            {
                return false;
            }

            body.NextSearch = Time.unscaledTime + 1f;
            Vector3 at = head != null ? head.position : bodyRoot.position + Vector3.up * 1.2f;
            Animator anim = FindMain(at, out Transform mainRoot);

            if (anim == null)
            {
                if (body == _own && !_saidNoAvatar)
                {
                    _saidNoAvatar = true;
                    Debug.Log("[SomniumEuroTruck] Avatar sync: no avatar main copy (MainRoot / NonXRMainRoot with a humanoid Animator) has its head within " + FindRadius.ToString("0.0") + " m of my head yet; looking again every second.");
                }

                return false;
            }

            body.Anim = anim;
            body.MainRoot = mainRoot;
            body.Head = anim.GetBoneTransform(HumanBodyBones.Head);

            if (!body.Said)
            {
                body.Said = true;
                Debug.Log("[SomniumEuroTruck] Avatar sync: " + who + " is '" + anim.name + "' under " + KindOf(anim.transform) + " '" + mainRoot.name + "', head bone '" + body.Head.name + "' " +
                          ((body.Head.position - at).magnitude * 100f).ToString("0") + " cm from the player's head" + (mainRoot.IsChildOf(bodyRoot) ? " (inside the body root)" : " (outside the body root)") + ".");
            }

            return true;
        }

        /// <summary>
        /// The main copy whose head bone is nearest at (within FindRadius): an active humanoid Animator under a client
        /// MainRoot / NonXRMainRoot, the root the main camera draws (Bo2AvatarFind.ByHead's rule, main copies only).
        /// Allocates: only when called.
        /// </summary>
        internal static Animator FindMain(Vector3 at, out Transform mainRoot)
        {
            mainRoot = null;
            Animator best = null;
            float bestD = FindRadius;
            Animator[] all = FindObjectsByType<Animator>(FindObjectsSortMode.None);

            for (int i = 0; i < all.Length; i++)
            {
                Animator a = all[i];

                if (a == null || !a.gameObject.activeInHierarchy || !a.isHuman || a.avatar == null)
                {
                    continue;
                }

                Transform h = a.GetBoneTransform(HumanBodyBones.Head);

                if (h == null)
                {
                    continue;
                }

                float d = (h.position - at).magnitude;

                if (d >= bestD)
                {
                    continue;
                }

                Transform root = MainRootOf(a.transform);

                if (root == null)
                {
                    continue;
                }

                bestD = d;
                best = a;
                mainRoot = root;
            }

            return best;
        }

        /// <summary>The transform carrying the client's MainRoot / NonXRMainRoot on the Animator's object or up to 3 parents above it.</summary>
        private static Transform MainRootOf(Transform t)
        {
            for (int up = 0; t != null && up <= 3; up++, t = t.parent)
            {
                Buf.Clear();
                t.GetComponents(Buf);

                for (int i = 0; i < Buf.Count; i++)
                {
                    if (Buf[i] == null)
                    {
                        continue;
                    }

                    string n = Buf[i].GetType().Name;

                    if (n == MainRootType || n == NonXrMainRootType)
                    {
                        Buf.Clear();
                        return t;
                    }
                }
            }

            Buf.Clear();
            return null;
        }

        private static string KindOf(Transform t)
        {
            Transform root = MainRootOf(t);

            if (root == null)
            {
                return "no client root";
            }

            Buf.Clear();
            root.GetComponents(Buf);
            string kind = MainRootType;

            for (int i = 0; i < Buf.Count; i++)
            {
                if (Buf[i] != null && Buf[i].GetType().Name == NonXrMainRootType)
                {
                    kind = NonXrMainRootType;
                }
            }

            Buf.Clear();
            return kind;
        }
    }
}
