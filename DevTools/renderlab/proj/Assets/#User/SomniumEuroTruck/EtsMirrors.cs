using System;
using System.Collections.Generic;
using System.Globalization;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Mirrors the way the game does them. Every mirror glass in the models carries a placeholder
    /// texture named for its mirror - close_mirror_reflection, close_s_mirror_reflection, far_,
    /// far_s_, side_, front_ - and game_data.sii gives each of the six its field of view (55, 90,
    /// 55, 90, 60, 100 degrees). For each mirror found, a camera sits at the glass and looks where
    /// the driver's gaze reflects off it, renders into a small texture, and the glass shows that
    /// texture flipped left to right. Both the interior and the exterior glass of a mirror share
    /// one camera.
    ///
    /// Smoothness (the jitter a headset showed while driving), four things:
    ///
    /// Every mirror the driver can see renders EVERY frame. They used to take turns, one mirror in
    /// three per frame, so each picture was 30 Hz on a 90 Hz display: the road behind stepped
    /// every third frame, and the head-driven view stepped with it.
    ///
    /// Each camera is placed in RenderPipelineManager.beginCameraRendering, not LateUpdate. By
    /// then every LateUpdate has run - EtsTruckSeat has put the rig (and so the head) in the cab
    /// for this frame's truck pose - and the headset's TrackedPoseDriver has written its
    /// just-before-render pose (Application.onBeforeRender, order -30000). Read in LateUpdate, the
    /// head could be where the truck was last frame, 0.28 m behind at 90 km/h and 90 Hz, because
    /// the order of two LateUpdates is not fixed (EtsSteeringGrab had the same problem).
    ///
    /// The eye point is kept in the truck's own frame and eased (EyeSmoothing), so tracking noise
    /// and small head tremor no longer swing the reflection, while a deliberate lean still does.
    ///
    /// The camera's up is the cab's up, not the world's: a mirror bolted to the cab shows the world
    /// level when the cab rolls, which a world-up picture pasted on a rolling glass does not.
    ///
    /// The picture is rendered at about twice the texels the glass covers and read through its own
    /// mip chain with trilinear, anisotropic filtering, so thin lines stop crawling (the client
    /// runs with MSAA off, and URP ignores a render texture's own MSAA then).
    /// </summary>
    public class EtsMirrors : MonoBehaviour
    {
        public EtsTruckController Truck;
        public Transform Root;

        /// <summary>The driver's camera. Mirrors only render while it looks out from the seat.</summary>
        public EtsTruckCamera View;

        /// <summary>Texels per side of the 55 degree mirrors (was 256).</summary>
        public int Resolution = 512;

        /// <summary>Texels per side of the 90 degree small mirrors (was 192).</summary>
        public int SmallResolution = 384;

        /// <summary>
        /// How far the mirrors see. The game's own r_mirror_view_distance is 80 (the demo's
        /// game.log.txt: uset r_mirror_view_distance "80"); this was 180.
        /// </summary>
        public float FarClip = 80f;

        /// <summary>Seconds the eye point takes to follow the head (headset only). 0 follows it raw.</summary>
        public float EyeSmoothing = 0.1f;

        /// <summary>How much of the head's lean the mirrors follow: 1 all of it, 0 none (fixed, as the game's are).</summary>
        public float HeadFollow = 1f;

        /// <summary>Largest lean from the driver's eye the mirrors follow, in metres.</summary>
        public float MaxLean = 0.6f;

        /// <summary>
        /// Half-angle, degrees, round where the viewer looks inside which a mirror is rendered. A
        /// glass behind the viewer's shoulder is not updated until it comes into view (the same
        /// frame it does). 180 renders every mirror always. 70 leaves 15 degrees over half the widest
        /// Meta headset field (Quest 3, about 110 degrees across by Meta's specification - the headset
        /// model is not in the log); looking straight ahead, the
        /// passenger-side glass (about 70 to 75 degrees to the right) is then skipped.
        /// </summary>
        public float ViewCone = 70f;

        /// <summary>
        /// The wide-angle small mirrors render every this many frames (1 = every frame). A fallback
        /// if the frame-time line shows the headset cannot afford all four: 2 halves their cost and
        /// leaves them at half the display rate, still steadier than the old one-in-three.
        /// </summary>
        public int SmallMirrorInterval = 1;

        private sealed class Mirror
        {
            public string Type;
            public float Fov;
            public RenderTexture Texture;
            public Material Material;
            public Camera Camera;
            public Renderer Anchor;
            public Vector3 LocalCentre;
            public Vector3 LocalNormal;
            public readonly List<Renderer> Glass = new List<Renderer>();

            /// <summary>The frame its camera last rendered, for the picture samples (-1 never).</summary>
            public int LastRender = -1;
        }

        private static readonly string[] Types = { "close_mirror", "close_s_mirror", "far_mirror", "far_s_mirror", "side_mirror", "front_mirror" };
        private static readonly float[] DefaultFov = { 55f, 90f, 55f, 90f, 60f, 100f };
        private readonly List<Mirror> _mirrors = new List<Mirror>();
        private int _interiorFrames;

        // The picture samples: one about 4 s into the cab, one a minute later (still in the cab), so
        // a single sample taken at a loading bay cannot mislead (review MIR-1: the first run in the
        // headset logged 0.01-0.06 where the old code had logged 0.29-0.55).
        private const float SecondSampleDelay = 60f;
        private float _firstSampleTime = -1f;
        private bool _secondSampleTaken;
        private bool _built;
        private bool _subscribed;
        private CommandBuffer _mips;

        // The eye in Root's frame, eased; worked out once a frame at the first mirror camera.
        private Vector3 _eyeLocal;
        private bool _eyeKnown;
        private int _eyeFrame = -1;

        // For the log: renders and frame times since the last report.
        private int _renders;
        private int _framesCounted;
        private readonly float[] _frameTimes = new float[1024];
        private int _frameTimeCount;
        private float _nextReport;

        public int Count => _mirrors.Count;

        private void OnEnable()
        {
            if (!_subscribed)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCamera;
                RenderPipelineManager.endCameraRendering += OnEndCamera;
                _subscribed = true;
            }
        }

        private void OnDisable()
        {
            if (_subscribed)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
                RenderPipelineManager.endCameraRendering -= OnEndCamera;
                _subscribed = false;
            }

            foreach (Mirror mirror in _mirrors)
            {
                if (mirror.Camera != null) mirror.Camera.enabled = false;
            }
        }

        private void Start()
        {
            Build();
        }

        // What the scans have looked at: every renderer already judged (glass or not), and how many renderers the
        // truck had at the last scan, so a later look only works when something was added (the interior of a truck
        // someone boards, add-ons hung after the mirrors).
        private readonly HashSet<Renderer> _seen = new HashSet<Renderer>();
        private readonly List<Renderer> _scan = new List<Renderer>();
        private readonly List<Material> _slots = new List<Material>();
        private int _lastRendererCount = -1;
        private float[] _fov;
        private int _glassCount;
        private int _rescansLeft;
        private float _nextRescan;

        /// <summary>
        /// Finds the mirror glass under Root and gives each mirror its camera. Safe to call again: a later call only
        /// takes on glass that was not there before (the interior of a truck built after the mirrors, e.g. a puppet's
        /// cab, built when someone boards it), and logs the set again when it changed. Glass is recognised by any of
        /// its material's textures being a *_mirror_reflection picture; the material is looked up by its path, with
        /// the vertex-colour variant's " vcol" suffix taken off (EtsMaterialCache.WithVertexColour: a glass piece with
        /// a non-neutral mean vertex colour gets a material named "path vcol", which the plain path lookup did not
        /// know - the glass was skipped and the truck logged "Mirrors: 0 ()").
        /// </summary>
        public void Build()
        {
            if (Root == null)
            {
                return;
            }

            bool first = !_built;

            if (first)
            {
                _built = true;
                _fov = ReadFov();

                // A few more looks over the next 20 s, in case parts of the cab are hung after this. Cheap: nothing
                // is done unless the truck has a different number of renderers than at the last look.
                _rescansLeft = 10;
                _nextRescan = Time.unscaledTime + 1f;
            }

            // The first look always logs (as before); a later one (the component's Start after the factory's call)
            // logs only if it found more glass.
            Scan(first);
        }

        /// <summary>Looks again for glass added since the last look (the rig calls it when a lazy interior is done).</summary>
        public void Rescan()
        {
            if (!_built)
            {
                Build();
                return;
            }

            if (Root != null)
            {
                Scan(false);
            }
        }

        private static float[] ReadFov()
        {
            float[] fov = (float[])DefaultFov.Clone();
            EtsSiiFile gameData = null;

            try
            {
                gameData = EtsEngine.Files != null ? EtsEngine.Files.ReadSii("/def/game_data.sii") : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Mirrors: game_data.sii not read (" + e.Message + "); the default fields of view.");
            }

            if (gameData != null)
            {
                foreach (EtsSiiUnit unit in gameData.Units)
                {
                    if (!unit.Has("mirror_fov")) continue;
                    List<string> values = unit.GetArray("mirror_fov");

                    for (int i = 0; i < values.Count && i < fov.Length; i++)
                    {
                        string s = values[i].Trim().TrimEnd('f', 'F');

                        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && parsed > 5f)
                        {
                            fov[i] = parsed;
                        }
                    }
                }
            }

            return fov;
        }

        private void Scan(bool always)
        {
            _scan.Clear();
            Root.GetComponentsInChildren(true, _scan);

            if (!always && _scan.Count == _lastRendererCount)
            {
                return;
            }

            _lastRendererCount = _scan.Count;
            int mirrorsBefore = _mirrors.Count;
            int glassBefore = _glassCount;
            int tinted = 0;

            foreach (Renderer renderer in _scan)
            {
                if (renderer == null || !_seen.Add(renderer)) continue;

                // Every material slot, not only the first: a piece is one renderer with one material today, but the
                // glass must not be missed if that ever changes.
                _slots.Clear();
                renderer.GetSharedMaterials(_slots);
                bool changed = false;

                for (int slot = 0; slot < _slots.Count; slot++)
                {
                    Material material = _slots[slot];
                    int index = GlassType(material);
                    if (index < 0) continue;

                    if (material.name.EndsWith(" vcol", StringComparison.Ordinal)) tinted++;
                    string type = Types[index];
                    Mirror mirror = _mirrors.Find(m => m.Type == type);

                    if (mirror == null)
                    {
                        mirror = Create(type, _fov[index], type.Contains("_s_"), material);
                        _mirrors.Add(mirror);
                    }

                    mirror.Glass.Add(renderer);
                    _glassCount++;
                    _slots[slot] = mirror.Material;
                    changed = true;

                    var filter = renderer.GetComponent<MeshFilter>();

                    if (mirror.Anchor == null && filter != null && filter.sharedMesh != null)
                    {
                        Mesh mesh = filter.sharedMesh;
                        mirror.Anchor = renderer;
                        mirror.LocalCentre = mesh.subMeshCount > 1 && slot < mesh.subMeshCount ? mesh.GetSubMesh(slot).bounds.center : mesh.bounds.center;
                        Vector3 normal = Vector3.zero;

                        foreach (Vector3 n in mesh.normals)
                        {
                            normal += n;
                        }

                        mirror.LocalNormal = normal.sqrMagnitude > 1e-6f ? normal.normalized : Vector3.back;
                    }
                }

                if (changed)
                {
                    if (_slots.Count == 1) renderer.sharedMaterial = _slots[0];
                    else renderer.sharedMaterials = _slots.ToArray();
                }
            }

            if (always || _mirrors.Count != mirrorsBefore || _glassCount != glassBefore)
            {
                Debug.Log("[SomniumEuroTruck] Mirrors: " + _mirrors.Count + " (" + string.Join(", ", _mirrors.ConvertAll(m => m.Type + " " + m.Fov + " deg " + m.Texture.width + " px").ToArray()) +
                          "), every frame while in view, out to " + FarClip.ToString("0") + " m, eye eased over " + EyeSmoothing.ToString("0.00") + " s; " +
                          _glassCount + " glass pieces (" + tinted + " new with a vertex-colour material) among " + _scan.Count + " renderers under '" + Root.name + "'" +
                          (always ? "" : ", looked again") + ".");
            }
        }

        /// <summary>Which of <see cref="Types"/> a material is the glass of, or -1.</summary>
        private static int GlassType(Material material)
        {
            if (material == null) return -1;
            string name = material.name;
            if (string.IsNullOrEmpty(name) || name.StartsWith("mirror ", StringComparison.Ordinal)) return -1;

            EtsMaterialDef def = Definition(name);

            if (def == null && name.EndsWith(" vcol", StringComparison.Ordinal))
            {
                def = Definition(name.Substring(0, name.Length - 5));
            }

            if (def == null && name.EndsWith(" (Instance)", StringComparison.Ordinal))
            {
                def = Definition(name.Substring(0, name.Length - 11));
            }

            if (def == null) return -1;

            // texture_base first (as before), then any other texture slot.
            int found = TypeOfTexture(def.BaseTexture);
            if (found >= 0) return found;

            foreach (KeyValuePair<string, string> texture in def.Textures)
            {
                found = TypeOfTexture(texture.Value);
                if (found >= 0) return found;
            }

            return -1;
        }

        private static EtsMaterialDef Definition(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/') return null;

            try
            {
                return EtsEngine.Materials != null ? EtsEngine.Materials.GetDefinition(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>close_mirror_reflection.tobj (and any longer name starting so) is close_mirror; close_s_ does not start "close_mirror_".</summary>
        private static int TypeOfTexture(string texture)
        {
            if (string.IsNullOrEmpty(texture) || texture.IndexOf("mirror_reflection", StringComparison.Ordinal) < 0) return -1;
            string file = texture.Substring(texture.LastIndexOf('/') + 1);

            for (int i = 0; i < Types.Length; i++)
            {
                if (file.StartsWith(Types[i] + "_reflection", StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        private Mirror Create(string type, float fov, bool small, Material glass)
        {
            int size = small ? SmallResolution : Resolution;

            // Mip-mapped and read trilinear and anisotropic: the glass covers fewer screen pixels
            // than the picture has texels across, so it is read from a pre-filtered level instead of
            // skipping texels (the sparkle). The mips are made after each render (OnEndCamera).
            // 24-bit depth: at 16 bits two surfaces 3 cm apart - thousands of such cells in the
            // ground census - cannot be told apart beyond about 25 m, if URP draws straight into it.
            var texture = new RenderTexture(size, size, 24)
            {
                name = "mirror " + type,
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.Create();

            // The glass keeps its shader and shows the camera's picture, lit as an emissive so
            // the cab's shade does not darken it, and flipped left to right as a mirror flips.
            // Only left to right: the glass is mapped with v = 1 at its top (the game's mirror
            // pictures are stored bottom row first, unlike its DDS files), which is a Unity render
            // texture's own order, so the picture stands the right way up by itself.
            var material = new Material(glass) { name = "mirror " + type };
            if (material.HasProperty("_BaseMap")) { material.SetTexture("_BaseMap", texture); material.SetTextureScale("_BaseMap", new Vector2(-1f, 1f)); material.SetTextureOffset("_BaseMap", new Vector2(1f, 0f)); }
            if (material.HasProperty("_MainTex")) { material.SetTexture("_MainTex", texture); material.SetTextureScale("_MainTex", new Vector2(-1f, 1f)); material.SetTextureOffset("_MainTex", new Vector2(1f, 0f)); }
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_EmissionMap")) { material.SetTexture("_EmissionMap", texture); material.SetColor("_EmissionColor", Color.white); material.EnableKeyword("_EMISSION"); }
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);

            var cameraObject = new GameObject("mirror camera " + type);
            cameraObject.transform.SetParent(Root, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.targetTexture = texture;
            camera.fieldOfView = fov;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = FarClip;
            camera.depth = -20f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.enabled = false;

            // No shadow maps, depth or colour copies, or post-processing for a 512 px picture.
            EtsMirrorsUrp.MakeCheap(camera, false);

            return new Mirror { Type = type, Fov = fov, Texture = texture, Material = material, Camera = camera };
        }

        /// <summary>
        /// Which mirrors render this frame. Only the choice is made here: a camera switched on
        /// inside the render callbacks would not be drawn until the next frame. Where each camera
        /// looks is decided in <see cref="OnBeginCamera"/>.
        /// </summary>
        private void LateUpdate()
        {
            // The later looks (Build): glass hung after the first look, e.g. cab parts built a few frames later.
            if (_built && _rescansLeft > 0 && Time.unscaledTime >= _nextRescan)
            {
                _rescansLeft--;
                _nextRescan = Time.unscaledTime + 2f;
                Rescan();
            }

            if (_mirrors.Count == 0)
            {
                return;
            }

            // From outside the cab the glass is a few pixels; the pictures freeze until the
            // driver is back in the seat.
            bool wanted = View == null || (View.Active && View.View == EtsTruckCamera.Mode.Interior);
            Camera viewer = EtsVr.Head();

            // In a headset the mode is always the cab's (EtsModelViewer), on foot too; a head more
            // than a lean away from the driver's eye is not in the seat.
            if (wanted && EtsVr.Present && viewer != null && Truck != null && Root != null &&
                (viewer.transform.position - Root.TransformPoint(Truck.DriverEyeLocal)).sqrMagnitude > 1.5f * 1.5f)
            {
                wanted = false;
            }

            if (wanted && ++_interiorFrames == 240)
            {
                _firstSampleTime = Time.unscaledTime;
                Inspect("sample 1, about 4 s in the cab");
            }
            else if (wanted && !_secondSampleTaken && _firstSampleTime >= 0f && Time.unscaledTime - _firstSampleTime >= SecondSampleDelay)
            {
                _secondSampleTaken = true;
                Inspect("sample 2, " + (Time.unscaledTime - _firstSampleTime).ToString("0") + " s after the first, in the cab");
            }

            float cone = Mathf.Cos(Mathf.Clamp(ViewCone, 0f, 180f) * Mathf.Deg2Rad);
            int on = 0;
            int small = 0;

            for (int i = 0; i < _mirrors.Count; i++)
            {
                Mirror mirror = _mirrors[i];

                if (mirror.Anchor == null)
                {
                    continue;
                }

                bool render = wanted;

                // Staggered, so two small mirrors at interval 2 do not both land on the same frame.
                if (render && SmallMirrorInterval > 1 && mirror.Type.Contains("_s_") && (Time.frameCount + small++) % SmallMirrorInterval != 0)
                {
                    render = false;
                }

                if (render && viewer != null && ViewCone < 180f)
                {
                    // From the glass's transform, not Renderer.bounds: the anchor may be the outside glass, whose
                    // object is switched off while the driver is in the cab (EtsTruckRig.ShowFromInside), and an
                    // inactive renderer's bounds are empty at the world origin.
                    Vector3 toGlass = mirror.Anchor.transform.TransformPoint(mirror.LocalCentre) - viewer.transform.position;
                    render = toGlass.sqrMagnitude < 1e-4f || Vector3.Dot(viewer.transform.forward, toGlass.normalized) >= cone;
                }

                mirror.Camera.enabled = render;

                if (render)
                {
                    on++;
                }
            }

            Report(wanted, on);
        }

        /// <summary>
        /// Places a mirror camera just before it renders, from this frame's final truck and head
        /// poses. URP calls this before culling the camera (UniversalRenderPipeline.RenderCameraStack:
        /// "Handle the case where camera position is modified in BeginCameraRendering"), and the
        /// mirrors (depth -20) render before the headset's camera (depth 0).
        /// </summary>
        private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            Mirror mirror = Find(camera);

            if (mirror == null || mirror.Anchor == null || Root == null)
            {
                return;
            }

            Vector3 eye = Root.TransformPoint(EyeLocal());
            Transform glass = mirror.Anchor.transform;
            Vector3 centre = glass.TransformPoint(mirror.LocalCentre);
            Vector3 normal = glass.TransformDirection(mirror.LocalNormal).normalized;

            if (Vector3.Dot(normal, eye - centre) < 0f)
            {
                normal = -normal;
            }

            Vector3 gaze = (centre - eye).normalized;
            Vector3 reflected = Vector3.Reflect(gaze, normal);

            // The cab's up: the picture turns with the glass, so the world in it stays level when
            // the cab rolls, as it does in a mirror bolted to a cab.
            mirror.Camera.transform.SetPositionAndRotation(centre + normal * 0.06f, Quaternion.LookRotation(reflected, Root.up));
            mirror.LastRender = Time.frameCount;
            _renders++;
        }

        /// <summary>The picture is finished (URP has submitted the camera): build its mip chain.</summary>
        private void OnEndCamera(ScriptableRenderContext context, Camera camera)
        {
            Mirror mirror = Find(camera);

            if (mirror == null || mirror.Texture == null || !mirror.Texture.useMipMap)
            {
                return;
            }

            if (_mips == null)
            {
                _mips = new CommandBuffer { name = "mirror mips" };
            }

            _mips.Clear();
            _mips.GenerateMips(mirror.Texture);
            context.ExecuteCommandBuffer(_mips);
            context.Submit();
        }

        private Mirror Find(Camera camera)
        {
            for (int i = 0; i < _mirrors.Count; i++)
            {
                if (_mirrors[i].Camera == camera)
                {
                    return _mirrors[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The eye the mirrors reflect from, in Root's frame. On a monitor, the driver's eye - the
        /// point EtsTruckCamera puts the interior camera at (DriverEyeLocal, head_offset included;
        /// the bare EyeLocal was 0.11 m off it on the DAF XF, about 6 degrees of the close mirror's
        /// view). In a headset, the driver's eye plus the head's lean from it (HeadFollow of it, at
        /// most MaxLean), eased over EyeSmoothing. Worked out once a frame, at the first mirror
        /// camera - after the seat has placed the rig and the headset has its final pose.
        /// </summary>
        private Vector3 EyeLocal()
        {
            if (_eyeFrame == Time.frameCount)
            {
                return _eyeLocal;
            }

            _eyeFrame = Time.frameCount;
            Vector3 rest = Truck == null ? Vector3.zero : Truck.DriverEyeLocal;
            Vector3 target = rest;

            if (Truck == null)
            {
                Camera main = Camera.main;
                target = main != null ? Root.InverseTransformPoint(main.transform.position) : Vector3.zero;
            }
            else if (EtsVr.Present && HeadFollow > 0f)
            {
                Camera head = EtsVr.Head();

                if (head != null)
                {
                    Vector3 lean = Root.InverseTransformPoint(head.transform.position) - rest;
                    target = rest + Vector3.ClampMagnitude(lean * HeadFollow, MaxLean);
                }
            }

            if (!_eyeKnown || EyeSmoothing <= 0f)
            {
                _eyeLocal = target;
                _eyeKnown = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / EyeSmoothing);
                _eyeLocal = Vector3.Lerp(_eyeLocal, target, k);
            }

            return _eyeLocal;
        }

        /// <summary>
        /// Every ten seconds while the mirrors are wanted: how many rendered per frame and the frame
        /// times, so the headset cost of rendering every frame is a measurement, not a guess.
        /// </summary>
        private void Report(bool wanted, int on)
        {
            _framesCounted++;
            _frameTimes[_frameTimeCount % _frameTimes.Length] = Time.unscaledDeltaTime;
            _frameTimeCount++;

            if (Time.unscaledTime < _nextReport)
            {
                return;
            }

            bool first = _nextReport == 0f;
            _nextReport = Time.unscaledTime + 10f;

            if (first || !wanted || _framesCounted == 0)
            {
                _renders = 0;
                _framesCounted = 0;
                _frameTimeCount = 0;
                return;
            }

            int n = Mathf.Min(_frameTimeCount, _frameTimes.Length);
            float[] sorted = new float[n];
            Array.Copy(_frameTimes, sorted, n);
            Array.Sort(sorted);
            float median = n > 0 ? sorted[n / 2] * 1000f : 0f;
            float p95 = n > 0 ? sorted[Mathf.Min(n - 1, (int)(n * 0.95f))] * 1000f : 0f;
            float refresh = DisplayRefreshRate();

            Debug.Log("[SomniumEuroTruck] Mirrors: " + ((float)_renders / _framesCounted).ToString("0.00") + " renders a frame (" + on + " now); frame " +
                      median.ToString("0.0") + " ms median, " + p95.ToString("0.0") + " ms 95th" + (refresh > 0f ? " at " + refresh.ToString("0") + " Hz" : "") +
                      "; eye lean " + (Truck != null ? (_eyeLocal - Truck.DriverEyeLocal).ToString("F3") : "-") + " m.");
            _renders = 0;
            _framesCounted = 0;
            _frameTimeCount = 0;
        }

        private static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();

        /// <summary>The headset's refresh rate, or 0 without one (XRDevice.refreshRate is in the deprecated VR module).</summary>
        private static float DisplayRefreshRate()
        {
            if (!XRSettings.isDeviceActive)
            {
                return 0f;
            }

            Displays.Clear();
            SubsystemManager.GetSubsystems(Displays);

            foreach (XRDisplaySubsystem display in Displays)
            {
                if (display.running && display.TryGetDisplayRefreshRate(out float rate) && rate > 0f)
                {
                    return rate;
                }
            }

            return 0f;
        }

        /// <summary>
        /// Twice, in the cab (4 s in, then a minute later): the average colour of every mirror's
        /// picture and where its camera looks, so a dark or empty glass can be told apart from a
        /// wrong view in the log. Two averages per mirror (diagnostic only, review MIR-1):
        ///   - mip 0: every texel of the picture the camera drew, copied out on its own
        ///     (Graphics.CopyTexture), whatever the mip chain holds;
        ///   - chain: an 8x8 blit of the mip-mapped texture, as before. Hardware LOD makes that
        ///     read about mip 6, i.e. what the glass shows from a distance, so it goes dark if the
        ///     chain is not built (offline probe 1585: 0.00 before GenerateMips, the true average
        ///     after) or holds an older picture.
        /// With the mip count and how many frames ago the camera last drew.
        /// </summary>
        private void Inspect(string when)
        {
            var parts = new List<string>();
            var pixels = new Texture2D(8, 8, TextureFormat.RGB24, false);
            Texture2D whole = null;
            bool canCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None;
            RenderTexture previous = RenderTexture.active;

            foreach (Mirror mirror in _mirrors)
            {
                if (mirror.Texture == null || !mirror.Texture.IsCreated())
                {
                    parts.Add(mirror.Type + ": no picture");
                    continue;
                }

                RenderTexture small = RenderTexture.GetTemporary(8, 8, 0);
                Graphics.Blit(mirror.Texture, small);
                Color chain = Average(small, pixels);
                RenderTexture.ReleaseTemporary(small);

                string mip0 = "n/a (no CopyTexture)";

                if (canCopy)
                {
                    RenderTexture level0 = RenderTexture.GetTemporary(mirror.Texture.width, mirror.Texture.height, 0, mirror.Texture.graphicsFormat);
                    Graphics.CopyTexture(mirror.Texture, 0, 0, level0, 0, 0);

                    if (whole == null || whole.width != level0.width || whole.height != level0.height)
                    {
                        if (whole != null) Destroy(whole);
                        whole = new Texture2D(level0.width, level0.height, TextureFormat.RGB24, false);
                    }

                    mip0 = Rgb(Average(level0, whole));
                    RenderTexture.ReleaseTemporary(level0);
                }

                string drawn = mirror.LastRender < 0 ? "never drawn" : "drawn " + (Time.frameCount - mirror.LastRender) + " frames ago";
                parts.Add(mirror.Type + " mip0 " + mip0 + ", chain " + Rgb(chain) + " (" + mirror.Texture.mipmapCount + " mips, " + drawn + ") looking " +
                          Root.InverseTransformDirection(mirror.Camera.transform.forward).ToString("0.00") + " (truck frame) from " + Root.InverseTransformPoint(mirror.Camera.transform.position).ToString("0.00"));
            }

            RenderTexture.active = previous;
            Destroy(pixels);
            if (whole != null) Destroy(whole);
            Debug.Log("[SomniumEuroTruck] Mirror pictures, " + when + " (average colour r/g/b, 0 black to 1 white: mip0 = every texel the camera drew, chain = an 8x8 read through the mips as the glass sees it from afar; camera direction and position on the truck): " +
                      string.Join("; ", parts.ToArray()) + ".");
        }

        /// <summary>The mean colour of a render texture, read back through a CPU texture of the same size.</summary>
        private static Color Average(RenderTexture source, Texture2D into)
        {
            RenderTexture.active = source;
            into.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
            into.Apply(false);
            Color32[] read = into.GetPixels32();
            long r = 0, g = 0, b = 0;

            foreach (Color32 c in read)
            {
                r += c.r;
                g += c.g;
                b += c.b;
            }

            float n = Mathf.Max(1, read.Length) * 255f;
            return new Color(r / n, g / n, b / n);
        }

        private static string Rgb(Color c)
        {
            return c.r.ToString("0.00") + "/" + c.g.ToString("0.00") + "/" + c.b.ToString("0.00");
        }

        private void OnDestroy()
        {
            foreach (Mirror mirror in _mirrors)
            {
                if (mirror.Texture != null) mirror.Texture.Release();
            }

            if (_mips != null)
            {
                _mips.Release();
                _mips = null;
            }
        }
    }
}
