using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The garage behind the game's menus, and the dark studio behind Truck Preference.
    ///
    /// The launchpad and the desktop do not sit on a photograph; /ui/desktop_background.sii puts a
    /// ui_truck_scene_base behind them whose config, /def/desktop/config.sii, names a model
    /// (/model/ui/garage/desktop_garage.pmd), the truck's place in it (the origin), a camera
    /// (position 0,3,0 aimed at 0,3,1, 45 degrees) and, for the launchpad's slow drift, a
    /// cinematic path in /def/camera/ui/desktop_garage_cam.sii: five orbit cameras, each with a
    /// list of positions a quarter of a second apart. The lights are given too - straight down,
    /// ambient 0.3, diffuse 3.0.
    ///
    /// All of that is rebuilt here, far below the map so nothing else can wander into shot: the
    /// garage model stood up as it is, a copy of the built truck parked at its origin, and a camera
    /// that follows the first of the file's paths and then drifts.
    ///
    /// The same unit type configures Truck Preference's studio (owner D, Phase 2; [gameui §3.2]):
    /// /ui/truck_preference_bcg.sii is a ui_truck_scene_base with config_path /def/empty_dark_scene.sii -
    /// /model/ui/empty_dark_scene.pmd (look default, variant Default), the camera at (-28, 2, 3) aimed at
    /// (0, 2.5, -0.7) with a 30-degree view and the cinematic path /def/camera/ui/camera_empty_dark_scene.sii,
    /// light direction (-1.5, -7, -1.5), ambient 0.35, diffuse 2.0, floor_reflection true. Build(config, home,
    /// truck, target) reads either. Given a RenderTexture (the studio), the camera draws into it and never to
    /// the screen or a headset, and the light is NOT a directional one: a directional light lights the whole
    /// world, and the player is standing in it at the yard while the tablet shows the studio. A spot light
    /// stands 10 m back along the file's light direction instead, its range (40 m) far short of the 2.5 km
    /// between the studio and anything else, with half the file's diffuse as intensity x distance squared (URP's
    /// punctual lights fall off with the square of the distance; at the full value the floor drew near white). The world's own sun and ambient still reach
    /// the studio (global in URP) - the game's lighting of this scene is therefore approximated, not copied.
    /// The floor reflection and the vehicle_reflection cube map are not reproduced; a fill light riding on the camera
    /// stands in for what they show of a dark truck (_fill). The camera follows the file's path: 63 positions, a
    /// quarter of a second apart, once round the truck from its front (probes 2044, 2045 - rendered with URP 17.3).
    /// </summary>
    public class EtsGarageScene : MonoBehaviour
    {
        public const string ConfigPath = "/def/desktop/config.sii";

        /// <summary>Truck Preference's dark studio (/ui/truck_preference_bcg.sii's config_path).</summary>
        public const string StudioConfigPath = "/def/empty_dark_scene.sii";

        /// <summary>Where the garage is built: far beneath the world.</summary>
        public static readonly Vector3 Home = new Vector3(0f, -2500f, 0f);

        /// <summary>Where the studio is built: as far beneath, 400 m aside from the garage (they never exist together).</summary>
        public static readonly Vector3 StudioHome = new Vector3(0f, -2500f, 400f);

        public Camera Camera { get; private set; }
        public bool Built { get; private set; }

        /// <summary>Where the config stands the truck (truck_position): a studio's truck is built under it.</summary>
        public Transform TruckPlace { get; private set; }

        private GameObject _root;
        private Light _light;
        private Light _fill;

        /// <summary>
        /// The studio's fill light, riding on its camera: the preview DAF is painted near black (daf_xf_euro6.sui base_color
        /// 0.0077) and the game shows its shape by the vehicle_reflection cube map and the floor reflection, neither of which
        /// is reproduced - without a light from the camera's side the truck read as a black cut-out (probe 2045, URP). Its
        /// intensity follows the camera's distance squared so the truck is lit alike all round the file's orbit (14 to
        /// 28 m); 1.0 at the truck is the file's specular (light_specular 1.0), a reading, not the game's formula.
        /// </summary>
        private const float FillAtTruck = 1.0f;
        private readonly List<Vector3> _path = new List<Vector3>();
        private Vector3 _aim = new Vector3(0f, 3f, 1f);
        private Vector3 _cameraPosition = new Vector3(0f, 3f, 0f);
        private float _fov = 45f;
        private float _time;

        /// <summary>Builds the garage and parks a copy of the truck in it. Returns what was done.</summary>
        public string Build(GameObject truckToCopy)
        {
            return Build(ConfigPath, Home, truckToCopy, null);
        }

        /// <summary>
        /// Builds the scene a ui_truck_scene_base config describes at home, with a copy of truckToCopy (may be null) at
        /// its truck_position. target null: the garage's camera, drawing to the screen when Show turns it on. A target:
        /// the studio's camera, drawing into it (class notes). Returns what was done.
        /// </summary>
        public string Build(string configPath, Vector3 home, GameObject truckToCopy, RenderTexture target)
        {
            if (Built)
            {
                return "already built";
            }

            if (EtsEngine.Files == null)
            {
                return "archives not mounted";
            }

            bool studio = target != null;
            _root = new GameObject(studio ? "Ets Truck Studio Scene" : "Ets Garage");
            _root.transform.SetParent(transform, false);
            _root.transform.position = home;

            EtsSiiFile config = EtsEngine.Files.ReadSii(configPath);
            EtsSiiUnit unit = config != null && config.Units.Count > 0 ? config.Units[0] : null;
            string modelPath = unit != null ? unit.GetString("scene_model_path", null) : null;
            string report = "";

            if (unit != null)
            {
                float[] cam = unit.GetFloats("camera_position");
                float[] aim = unit.GetFloats("camera_aim");
                if (cam != null && cam.Length >= 3) _cameraPosition = EtsModelBuilder.ToUnity(cam[0], cam[1], cam[2]);
                if (aim != null && aim.Length >= 3) _aim = EtsModelBuilder.ToUnity(aim[0], aim[1], aim[2]);
                _fov = unit.GetFloat("camera_fov", 45f);
            }

            if (!string.IsNullOrEmpty(modelPath))
            {
                EtsModelData model = EtsEngine.LoadModel(modelPath, out string failure);

                if (model != null)
                {
                    EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
                    {
                        Look = unit.GetString("scene_model_look", "default") ?? "default",
                        Variants = new[] { unit.GetString("scene_model_variant", "default") ?? "default", "Default", "default" },
                        CreateLocators = false,
                        AddColliders = false,
                        IncludeCollision = false
                    }, _root.transform, EtsEngine.Materials);

                    float[] at = unit.GetFloats("scene_model_position");
                    built.Root.transform.localPosition = at != null && at.Length >= 3 ? EtsModelBuilder.ToUnity(at[0], at[1], at[2]) : Vector3.zero;
                    built.Root.name = studio ? "studio" : "garage";
                    report = (studio ? "studio " : "garage ") + built.PiecesDrawn + " pieces";
                }
                else
                {
                    report = (studio ? "studio" : "garage") + " model: " + failure;
                }
            }
            else
            {
                report = "no scene_model_path in " + configPath;
            }

            float[] truckAt = unit != null ? unit.GetFloats("truck_position") : null;
            TruckPlace = new GameObject("truck place").transform;
            TruckPlace.SetParent(_root.transform, false);
            TruckPlace.localPosition = truckAt != null && truckAt.Length >= 3 ? EtsModelBuilder.ToUnity(truckAt[0], truckAt[1], truckAt[2]) : Vector3.zero;

            if (truckToCopy != null)
            {
                GameObject copy = Instantiate(truckToCopy, TruckPlace);
                copy.name = "truck in garage";
                Strip(copy);
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Vector3.one;
                report += ", truck copied";
            }

            float[] direction = unit != null ? unit.GetFloats("light_direction") : null;
            Vector3 dir = direction != null && direction.Length >= 3 ? EtsModelBuilder.ToUnity(direction[0], direction[1], direction[2]) : Vector3.down;
            dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.down;

            var lightObject = new GameObject(studio ? "studio light" : "garage light");
            lightObject.transform.SetParent(_root.transform, false);
            _light = lightObject.AddComponent<Light>();
            _light.color = Color.white;
            _light.cullingMask = ~0;

            if (studio)
            {
                // A spot light 10 m back along the file's direction, aimed at the truck's middle (class notes), at half the file's
                // diffuse: at the full 2.0 the floor under it drew nearly white in URP beside the world's sun and ambient
                // (probe 2045), where the game's "empty dark scene" floor is dark. A reading, not the game's formula.
                float[] diffuse = unit != null ? unit.GetFloats("light_diffuse") : null;
                float strength = diffuse != null && diffuse.Length >= 1 ? diffuse[0] : 2f;
                const float back = 10f;
                Vector3 middle = TruckPlace.localPosition + new Vector3(0f, 1.8f, 0f);
                _light.type = LightType.Spot;
                _light.range = 40f;
                _light.spotAngle = 110f;
                _light.intensity = 0.5f * strength * back * back;
                _light.shadows = LightShadows.None;
                lightObject.transform.localPosition = middle - dir * back;
                lightObject.transform.localRotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);
                report += ", spot light " + _light.intensity.ToString("0") + " at 10 m";
            }
            else
            {
                // The lights the config asks for: straight down, bright, with a soft ambient.
                _light.type = LightType.Directional;
                _light.intensity = 1.6f;
                _light.shadows = LightShadows.Soft;
                lightObject.transform.rotation = Quaternion.LookRotation(dir + new Vector3(0.25f, 0f, 0.15f), Vector3.up);
            }

            // The camera, on the config's spot, following the cinematic path when there is one.
            var cameraObject = new GameObject(studio ? "studio camera" : "garage camera");
            cameraObject.transform.SetParent(_root.transform, false);
            Camera = cameraObject.AddComponent<Camera>();
            Camera.fieldOfView = _fov;
            Camera.nearClipPlane = 0.1f;
            Camera.farClipPlane = studio ? 120f : 400f;
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = studio ? Color.black : new Color(0.06f, 0.06f, 0.07f, 1f);
            Camera.depth = studio ? -20 : 5;
            Camera.enabled = false;

            // A monitor's backdrop: never a headset's eyes. The studio's camera draws into a texture, which URP never draws
            // in stereo, and under URP setting stereoTargetEye only logs "You can use Camera.stereoTargetEye only with the
            // built-in renderer" (probe 2045), so it is left alone there.
            if (!studio)
            {
                Camera.stereoTargetEye = StereoTargetEyeMask.None;
            }

            if (studio)
            {
                Camera.targetTexture = target;
                Camera.aspect = (float)target.width / Mathf.Max(1, target.height);
                Camera.allowHDR = false;
                Camera.allowMSAA = false;

                var fillObject = new GameObject("studio fill light");
                fillObject.transform.SetParent(cameraObject.transform, false);
                fillObject.transform.localPosition = new Vector3(0f, 1.5f, 0f);
                _fill = fillObject.AddComponent<Light>();
                _fill.type = LightType.Point;
                _fill.range = 60f;
                _fill.color = Color.white;
                _fill.shadows = LightShadows.None;
                _fill.cullingMask = ~0;
            }

            cameraObject.transform.localPosition = _cameraPosition;
            cameraObject.transform.LookAt(_root.transform.TransformPoint(_aim), Vector3.up);

            ReadPath(unit != null ? unit.GetString("scene_cinematic_camera", null) : null);
            Built = true;
            return report + (_path.Count > 0 ? ", camera path " + _path.Count + " points" : ", fixed camera");
        }

        /// <summary>
        /// A copy of the driven truck with everything but its looks removed - at once, before any
        /// of the copied behaviours can run a Start and set up wheels, mirrors or sound of their own.
        /// </summary>
        private static void Strip(GameObject copy)
        {
            foreach (MonoBehaviour behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(behaviour);
            foreach (Joint joint in copy.GetComponentsInChildren<Joint>(true)) DestroyImmediate(joint);
            foreach (Rigidbody body in copy.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(body);
            foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true)) DestroyImmediate(collider);
            foreach (Camera camera in copy.GetComponentsInChildren<Camera>(true)) DestroyImmediate(camera);
            foreach (AudioSource audio in copy.GetComponentsInChildren<AudioSource>(true)) DestroyImmediate(audio);
            foreach (Light light in copy.GetComponentsInChildren<Light>(true)) DestroyImmediate(light);
            foreach (ReflectionProbe probe in copy.GetComponentsInChildren<ReflectionProbe>(true)) DestroyImmediate(probe);
        }

        /// <summary>The first camera's positions from the cinematic file, a quarter of a second apart.</summary>
        private void ReadPath(string cinematicPath)
        {
            _path.Clear();

            if (string.IsNullOrEmpty(cinematicPath))
            {
                return;
            }

            EtsSiiFile file = EtsEngine.Files.ReadSii(cinematicPath);

            if (file == null)
            {
                return;
            }

            foreach (EtsSiiUnit unit in file.Units)
            {
                if (unit.Type != "camera_animation_info")
                {
                    continue;
                }

                // "positions[12]: ( x, y, z )" lines are gathered under "positions" in order.
                List<string> entries = unit.GetArray("positions");

                if (entries == null)
                {
                    continue;
                }

                foreach (string entry in entries)
                {
                    float[] p = EtsSiiValue.ParseFloats(entry);

                    if (p != null && p.Length >= 3)
                    {
                        _path.Add(EtsModelBuilder.ToUnity(p[0], p[1], p[2]));
                    }
                }

                if (_path.Count > 1)
                {
                    break;
                }
            }
        }

        /// <summary>Turns the camera on (the only camera that draws, for the garage; into its texture, for the studio) and the scene with it.</summary>
        public void Show(bool on)
        {
            if (Camera != null)
            {
                Camera.enabled = on;
            }

            if (_root != null)
            {
                _root.SetActive(on);
            }

            _time = 0f;
        }

        /// <summary>
        /// The studio's switch: the scene live (a truck may be building in it) and the camera drawing, separately. An idle,
        /// asleep studio is inactive, so the world's cameras - the desktop's reaches 10 km (CameraSystem.prefab) - never
        /// spend a draw on it.
        /// </summary>
        public void SetLive(bool sceneActive, bool cameraOn)
        {
            if (_root != null && _root.activeSelf != sceneActive)
            {
                _root.SetActive(sceneActive);
            }

            if (Camera != null && Camera.enabled != cameraOn)
            {
                Camera.enabled = cameraOn;
            }
        }

        private void LateUpdate()
        {
            if (Camera == null || !Camera.enabled)
            {
                return;
            }

            _time += Time.deltaTime;
            Vector3 local = _cameraPosition;

            if (_path.Count > 1)
            {
                // Along the file's path at its own pace, then back and forth so it never ends.
                float t = Mathf.PingPong(_time / 0.25f, _path.Count - 1);
                int i = Mathf.Clamp((int)t, 0, _path.Count - 2);
                local = Vector3.Lerp(_path[i], _path[i + 1], t - i);
            }
            else
            {
                local += new Vector3(Mathf.Sin(_time * 0.15f) * 0.6f, 0f, 0f);
            }

            Camera.transform.localPosition = local;
            Camera.transform.LookAt(_root.transform.TransformPoint(_aim), Vector3.up);

            if (_fill != null)
            {
                float d = Vector3.Distance(Camera.transform.position, _root.transform.TransformPoint(_aim));
                _fill.intensity = FillAtTruck * d * d;
            }
        }

        // The world's sun and ambient reach the garage and the studio (class notes), and their cameras draw with no tone
        // curve (no post-processing; the studio's is LDR). Since 2026-09-25 the climate's exposure is the game's own, 2.4 x
        // the one these scenes were tuned under, so round this scene's camera the lights go back to the old level
        // (EtsClimate.LegacyScale) and to the phase's own after it. Two delegate calls a camera a frame; nothing allocated.
        private System.Action<UnityEngine.Rendering.ScriptableRenderContext, Camera> _beginCamera;
        private System.Action<UnityEngine.Rendering.ScriptableRenderContext, Camera> _endCamera;
        private bool _lightsScaled;

        private void OnEnable()
        {
            if (_beginCamera == null)
            {
                _beginCamera = BeginCamera;
                _endCamera = EndCamera;
            }

            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += _beginCamera;
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += _endCamera;
        }

        private void OnDisable()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= _beginCamera;
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= _endCamera;

            if (_lightsScaled)
            {
                _lightsScaled = false;
                EtsClimate.ScaleLights(EtsClimate.PhaseScale);
            }
        }

        private void BeginCamera(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
        {
            if (camera != null && camera == Camera)
            {
                _lightsScaled = true;
                EtsClimate.ScaleLights(EtsClimate.LegacyScale);
            }
        }

        private void EndCamera(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
        {
            if (_lightsScaled && camera == Camera)
            {
                _lightsScaled = false;
                EtsClimate.ScaleLights(EtsClimate.PhaseScale);
            }
        }

        private void OnDestroy()
        {
            // The studio's texture is its owner's (EtsTruckStudio); the scene only lets go of it.
            if (Camera != null)
            {
                Camera.targetTexture = null;
            }
        }
    }

    /// <summary>
    /// Truck Preference's studio (owner D, Phase 2; [gameui §3.2], PLAN.md §1 row 12): the dark scene
    /// (EtsGarageScene with /def/empty_dark_scene.sii, 2.5 km under the map) and one preview truck standing in it,
    /// built by C's EtsTruckFactory.BuildDisplay (the preview save's own chassis, cabin, paint and wheels; no
    /// controller, mirrors, probe or door; no interior - the camera is outside), drawn into a 1280 x 800 texture that
    /// the page shows behind the game's layout.
    ///
    /// Only one truck at a time, and one build at a time: asking for another while one is building makes the next
    /// build start when this one ends, and a truck no longer wanted when its build ends is thrown away unseen. The camera
    /// draws only while the page keeps it awake (KeepAwake from its 4 Hz Tick, 0.6 s at a time), so a tablet closed
    /// on the page costs nothing a few frames later. Meshes and materials are the factory's caches (EtsModelBuilder
    /// MeshCache, EtsMaterialCache): a truck built again costs its slices, not new memory.
    /// </summary>
    public sealed class EtsTruckStudio : MonoBehaviour
    {
        public const int TextureWidth = 1280, TextureHeight = 800;

        public static EtsTruckStudio Current { get; private set; }

        /// <summary>What the page shows behind the layout.</summary>
        public RenderTexture Texture { get; private set; }

        /// <summary>The preview index standing in the studio now (-1: none yet).</summary>
        public int Shown => _shownPreview;

        /// <summary>A build is running.</summary>
        public bool Building => _building;

        private EtsGarageScene _scene;
        private Transform _holder;
        private int _shownPreview = -1, _wanted = -1;
        private bool _building, _awake;
        private float _awakeUntil;
        private string _report = "";

        /// <summary>The studio, built on first use. Null when the archives are not mounted.</summary>
        public static EtsTruckStudio Get()
        {
            if (Current != null)
            {
                return Current;
            }

            if (EtsEngine.Files == null)
            {
                return null;
            }

            var host = new GameObject("Ets Truck Studio");
            var studio = host.AddComponent<EtsTruckStudio>();
            Current = studio;
            studio.Build();
            return studio;
        }

        private void Build()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Texture = new RenderTexture(TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32) { name = "Ets truck studio" };
            Texture.Create();
            _scene = gameObject.AddComponent<EtsGarageScene>();
            _report = _scene.Build(EtsGarageScene.StudioConfigPath, EtsGarageScene.StudioHome, null, Texture);
            _scene.SetLive(false, false);
            Debug.Log("[SomniumEuroTruck] Truck studio: " + _report + " (built in " + watch.ElapsedMilliseconds + " ms).");
        }

        /// <summary>The preview truck to stand in the studio (EtsTruckCatalog index).</summary>
        public void Want(int previewIndex)
        {
            _wanted = previewIndex;

            if (!_building && _wanted != _shownPreview)
            {
                StartCoroutine(BuildWanted());
            }
        }

        /// <summary>Draws for the next <paramref name="seconds"/>; the page calls it at 4 Hz while it is on screen.</summary>
        public void KeepAwake(float seconds = 0.6f)
        {
            _awakeUntil = Mathf.Max(_awakeUntil, Time.unscaledTime + seconds);

            if (!_awake)
            {
                _awake = true;
                Live();
            }
        }

        /// <summary>Stops drawing now (the page hidden).</summary>
        public void Sleep()
        {
            _awakeUntil = 0f;

            if (_awake)
            {
                _awake = false;
                Live();
            }
        }

        private void Update()
        {
            if (_awake && Time.unscaledTime > _awakeUntil)
            {
                Sleep();
            }
        }

        /// <summary>The scene live while it draws or a truck is building in it (a live hierarchy for the factory); the camera only while awake.</summary>
        private void Live()
        {
            _scene?.SetLive(_awake || _building, _awake);
        }

        private IEnumerator BuildWanted()
        {
            _building = true;
            Live();

            while (_wanted != _shownPreview)
            {
                int want = _wanted;
                EtsTruckCatalog catalog = EtsTruckCatalog.Current != null ? EtsTruckCatalog.Current : EtsTruckCatalog.Load(EtsEngine.Files);

                if (catalog == null || want < 0 || want >= catalog.Trucks.Count || _scene == null || _scene.TruckPlace == null)
                {
                    break;
                }

                EtsPreviewTruck truck = catalog.Trucks[want];
                var holder = new GameObject("truck " + truck.Stem).transform;
                holder.SetParent(_scene.TruckPlace, false);
                EtsTruckRig built = null;
                string failure = null;
                float began = Time.realtimeSinceStartup;
                IEnumerator build = EtsTruckFactory.BuildDisplay(catalog.Identity(truck.Stem, EtsTruckIdentity.SavePaint), holder, true, rig => built = rig, why => failure = why);

                while (build.MoveNext())
                {
                    yield return build.Current;
                }

                if (built == null || want != _wanted)
                {
                    // Failed, or no longer wanted: thrown away unseen (a failure keeps the one shown before).
                    Destroy(holder.gameObject);

                    if (built == null)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Truck studio: " + truck.Stem + " could not be built - " + failure);
                        _shownPreview = want;
                    }

                    continue;
                }

                if (_holder != null)
                {
                    Destroy(_holder.gameObject);
                }

                _holder = holder;
                _shownPreview = want;
                Debug.Log("[SomniumEuroTruck] Truck studio: " + truck.Stem + " (" + truck.TruckId + ") standing in the studio, built in " + (Time.realtimeSinceStartup - began).ToString("0.0") + " s.");
            }

            _building = false;
            Live();
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }

            if (Texture != null)
            {
                Texture.Release();
                Destroy(Texture);
                Texture = null;
            }
        }
    }
}
