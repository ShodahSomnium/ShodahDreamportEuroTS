using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// A truck's working cab instruments: the needles, warning lamps, stalks and switches of the game's animated
    /// dashboard model, driven from the truck's live state, and the cluster's digital display.
    ///
    /// The factory builds the animated dashboard (the interior definition's animated_model) as static meshes at their
    /// bind pose, where the DAF XF Euro 6's needles stand 16 degrees past zero and every warning lamp is half-way out of
    /// the cluster (instruments probe 4 / harness 348). This component, mounted on that dashboard root
    /// (<see cref="Mount"/>, one call from EtsTruckFactory.BuildInterior), turns it into what the game draws:
    ///  1. The kit (EtsInstrumentKit): the interior definition's animation keys, their .pma animations and the
    ///     dashboard's skin streams, read once per cab on a worker thread and shared by every truck with that cab.
    ///  2. The skeleton as Transforms under the dashboard, at the bind pose.
    ///  3. Every dashboard piece with a vertex on a moving bone drawn by a SkinnedMeshRenderer over those Transforms
    ///     (its static MeshRenderer switched off); pieces nothing moves stay static. Up to 1 ms of this a frame.
    ///  4. The display: the interior model's piece whose texture is the game's dashboard render target
    ///     (/vehicle/truck/share/dashboard.tobj, which is not a file) darkened and given the gear, speed, clock and fuel.
    /// Then every frame (LateUpdate, no allocation): EtsCabState steps from the controller (speed, rpm, pedals,
    /// engine, indicators, parking brake, gear, plus modelled fuel, air, temperatures), and each binding whose 0..1
    /// changed samples its animation into its bones' local transforms. Lamps with a few vertices and no child bones
    /// are hidden by a zero scale while off (the DAF XF parks its lamps 0.85 m behind the driver's eye).
    ///
    /// Safety: nothing here can break the truck. Every phase is inside try/catch; a failure logs once per kind and puts
    /// the dashboard back as the factory built it (static renderers on, skinned copies removed).
    /// Cost: a cab out of the local view's 40 m is stepped but not posed; a puppet's hidden cab (EtsTruckRig) does not
    /// run at all, being inactive.
    ///
    /// Log: "[SomniumEuroTruck] Instruments: &lt;truck&gt; - N needles, M lamps, K displays (bound: speed, rpm, ...)".
    ///
    /// For a headset driver (EtsCabControls, 2026-09-25) it also says where the blinker stalk, the parking brake lever and
    /// the hazard switch are as drawn (TryGetControl), lights the one a hand is on (SetControlHighlight), lets the stalk
    /// follow the hand (HoldStalk) and plays a press-and-return switch through when it is pressed (PlayPress). Nothing of
    /// that is built until the first ask. Log: "Cab controls: &lt;truck&gt; - stalk tip at ..., parking brake ..., hazard switch ...".
    /// </summary>
    public sealed class EtsInstruments : MonoBehaviour
    {
        /// <summary>Beyond this distance from the local view the needles are not posed (state still steps).</summary>
        public const float AnimateRadius = 40f;

        /// <summary>Build work per frame, milliseconds.</summary>
        public const float BuildBudgetMs = 1.0f;

        /// <summary>Headlights for the lamps and the light switch: 0 off, 1 parking lights, 2 dipped. Nothing sets them yet.</summary>
        public int Lights;

        /// <summary>Main beam, for its lamp and stalk.</summary>
        public bool HighBeam;

        /// <summary>The cab's live values (fuel, air, temperatures...), for other readers (a tablet page, the HUD).</summary>
        public EtsCabState State => _state;

        /// <summary>True once the needles are live.</summary>
        public bool Running => _phase == PhaseRunning;

        // ------------------------------------------------------------------ the kits, shared per cab

        private sealed class KitEntry
        {
            public EtsFileSystem Files;
            public EtsInstrumentKit Kit;
            public string Failure;
            public bool Pending;
            public Matrix4x4[] Bindposes;
            public readonly Dictionary<Mesh, Mesh> Skinned = new Dictionary<Mesh, Mesh>();
        }

        private static readonly Dictionary<string, KitEntry> Kits = new Dictionary<string, KitEntry>(StringComparer.Ordinal);
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Func<bool> Always = () => true;
        private static EtsWorker _worker;
        private static int _alive;

        // ------------------------------------------------------------------ one truck

        private const int PhaseFailed = -1, PhaseKit = 0, PhaseSkeleton = 1, PhasePieces = 2, PhaseDisplay = 3, PhaseRunning = 4;

        private EtsTruckRig _rig;
        private EtsTruckController _truck;
        private EtsModelData _model;
        private string _interiorPath, _chassisPath, _interiorModelPath, _interiorLook, _name;
        private KitEntry _entry;
        private string _kitKey;
        private int _phase = PhaseKit;
        private int _pieceCursor;
        private Dictionary<int, GameObject> _pieceObjects;
        private Transform _skeleton;
        private Transform[] _bones;
        private readonly List<Renderer> _switchedOff = new List<Renderer>();
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly List<Material> _madeMaterials = new List<Material>();
        private readonly List<Mesh> _madeMeshes = new List<Mesh>();
        private EtsCabState _state;
        private float[] _lastT;
        private Transform[][] _bindBones;
        private bool[][] _hideOff;
        private readonly float[] _rot = new float[4];
        private readonly float[] _pos = new float[3];
        private int _skinned;
        private int _displays;
        private float _buildStarted;
        private int _buildFrames;
        private bool _near = true;
        private int _nextNearCheck;
        private int _nextTruckSearch;
        private bool _counted;

        // The driver's controls (VR cab, EtsCabControls): found on the first ask, only for the truck a headset driver sits in.
        private ControlHandle[] _controls;
        private bool _controlsTried;
        private bool[] _momentary;
        private float[] _pressStart;
        private float _stalkHold;
        private int _stalkHoldFrame = -10;
        private int _ringsShown;

        // The display.
        private TextMeshPro _displayText;
        private float _displayRetryAt = -1f;
        private int _displayTries;
        private float _nextDisplay;
        private readonly char[] _chars = new char[64];

        /// <summary>
        /// Puts working instruments on a truck's animated dashboard: `dashboard` is the root EtsModelBuilder made of the
        /// interior's animated_model (EtsTruckFactory.BuildInterior's dash.Root), `animated` that model. Cheap: the work
        /// starts in the dashboard's own Update, off the factory's frame, and only while the dashboard is active. Returns
        /// null (and changes nothing) when it cannot apply. Never throws.
        /// </summary>
        public static EtsInstruments Mount(EtsTruckRig rig, GameObject dashboard, EtsModelData animated, EtsTruckAssembly assembly)
        {
            try
            {
                if (dashboard == null || animated == null || assembly == null || animated.Bones == null || animated.Bones.Length == 0)
                {
                    return null;
                }

                EtsInstruments existing = dashboard.GetComponent<EtsInstruments>();

                if (existing != null)
                {
                    return existing;
                }

                assembly.Parts.TryGetValue("interior", out string interior);
                assembly.Parts.TryGetValue("chassis", out string chassis);

                if (string.IsNullOrEmpty(interior))
                {
                    return null;
                }

                var instruments = dashboard.AddComponent<EtsInstruments>();
                instruments._rig = rig;
                instruments._model = animated;
                instruments._interiorPath = interior;
                instruments._chassisPath = chassis;
                instruments._interiorModelPath = assembly.InteriorModel;
                instruments._interiorLook = assembly.InteriorLook;
                instruments._name = !string.IsNullOrEmpty(assembly.TruckId) ? assembly.TruckId : rig != null && !string.IsNullOrEmpty(rig.Stem) ? rig.Stem : animated.Path;
                return instruments;
            }
            catch (Exception e)
            {
                WarnOnce("mount", "could not mount on " + (dashboard != null ? dashboard.name : "nothing") + ": " + e.Message);
                return null;
            }
        }

        private void Awake()
        {
            _alive++;
            _counted = true;
        }

        private void OnDestroy()
        {
            if (_counted)
            {
                _counted = false;
                _alive--;
            }

            foreach (Material material in _madeMaterials)
            {
                if (material != null) Destroy(material);
            }

            _madeMaterials.Clear();

            foreach (Mesh mesh in _madeMeshes)
            {
                if (mesh != null) Destroy(mesh);
            }

            _madeMeshes.Clear();

            if (_alive <= 0 && _worker != null)
            {
                // Nothing left to read for: stop the thread, and forget any kit it was still reading so the next truck asks again.
                _worker.Dispose();
                _worker = null;
                var pending = new List<string>();

                foreach (KeyValuePair<string, KitEntry> pair in Kits)
                {
                    if (pair.Value.Pending) pending.Add(pair.Key);
                }

                foreach (string key in pending) Kits.Remove(key);
            }
        }

        private void Update()
        {
            if (_phase == PhaseRunning || _phase == PhaseFailed)
            {
                return;
            }

            try
            {
                Build();
            }
            catch (Exception e)
            {
                Fail("build", e.GetType().Name + ": " + e.Message, e);
            }
        }

        private void LateUpdate()
        {
            if (_phase != PhaseRunning)
            {
                return;
            }

            try
            {
                Animate();
            }
            catch (Exception e)
            {
                // The pose stays where it last was; the truck drives on.
                WarnOnce("animate", _name + ": the instruments stopped: " + e);
                enabled = false;
            }
        }

        // ------------------------------------------------------------------ building, a phase at a time

        private void Build()
        {
            if (_phase == PhaseKit)
            {
                if (!KitReady())
                {
                    return;
                }

                _buildStarted = Time.realtimeSinceStartup;
                _phase = PhaseSkeleton;
            }

            _buildFrames++;
            float until = Time.realtimeSinceStartup + BuildBudgetMs * 0.001f;

            if (_phase == PhaseSkeleton)
            {
                MakeSkeleton();
                _phase = PhasePieces;
                return;
            }

            if (_phase == PhasePieces)
            {
                EtsInstrumentKit kit = _entry.Kit;

                while (_pieceCursor < kit.PieceMoves.Length)
                {
                    int p = _pieceCursor++;

                    if (kit.PieceMoves[p])
                    {
                        SkinPiece(p);

                        if (Time.realtimeSinceStartup > until)
                        {
                            return;
                        }
                    }
                }

                if (_skinned == 0)
                {
                    Fail("pieces", "no dashboard piece could be drawn skinned", null);
                    return;
                }

                _phase = PhaseDisplay;
                return;
            }

            if (_phase == PhaseDisplay)
            {
                try
                {
                    MakeDisplay();
                }
                catch (Exception e)
                {
                    // The display is a nicety: the needles run without it.
                    WarnOnce("display", _name + ": no cluster display: " + e.Message);
                }

                _phase = PhaseRunning;
                EtsInstrumentKit kit = _entry.Kit;
                Debug.Log("[SomniumEuroTruck] Instruments: " + _name + " - " + kit.Needles + " needles, " + kit.Lamps + " lamps, " + _displays +
                          " displays (bound: " + kit.BoundSummary + "); " + kit.Controls + " controls, " + kit.Rests + " held at rest, " + _skinned +
                          " skinned pieces, " + _bones.Length + " bones; built in " + _buildFrames + " frames, " +
                          ((Time.realtimeSinceStartup - _buildStarted) * 1000f).ToString("0") + " ms" +
                          (kit.Skipped.Count > 0 ? "; not driven: " + string.Join(", ", kit.Skipped.ToArray()) : "") +
                          (kit.Notes.Count > 0 ? "; " + string.Join("; ", kit.Notes.ToArray()) : "") + ".");
            }
        }

        /// <summary>The cab's kit: asked of the worker once per cab, then shared. False while it is being read.</summary>
        private bool KitReady()
        {
            EtsFileSystem files = EtsEngine.Files;

            if (files == null)
            {
                Fail("files", "the game's archives are not mounted", null);
                return false;
            }

            string key = _kitKey ?? (_kitKey = _interiorPath + "|" + _model.Path + "|" + _chassisPath);

            if (!Kits.TryGetValue(key, out KitEntry entry) || entry.Files != files || (!entry.Pending && entry.Kit == null && entry.Failure == null))
            {
                entry = new KitEntry { Files = files, Pending = true };
                Kits[key] = entry;
                Ask(entry, _model, _interiorPath, _chassisPath, _name);
            }

            if (entry.Pending && _worker != null)
            {
                _worker.Drain(Always);
            }

            if (entry.Pending)
            {
                return false;
            }

            if (entry.Kit == null || entry.Kit.Failure != null)
            {
                string why = entry.Kit != null ? entry.Kit.Failure : entry.Failure ?? "the kit could not be read";
                _phase = PhaseFailed;
                WarnOnce("kit " + key, "Instruments: " + _name + " - cannot be decoded, the dashboard stays still: " + why +
                                        (entry.Kit != null && entry.Kit.Skipped.Count > 0 ? " (skipped: " + string.Join(", ", entry.Kit.Skipped.ToArray()) + ")" : ""));
                return false;
            }

            _entry = entry;
            return true;
        }

        private static void Ask(KitEntry entry, EtsModelData model, string interior, string chassis, string name)
        {
            if (_worker == null)
            {
                _worker = new EtsWorker("Ets instruments reader");
            }

            EtsFileSystem files = entry.Files;
            Func<string, byte[]> read = files.Read;
            Func<string, EtsSiiFile> readSii = EtsTruckAssembly.UncachedReader(files);
            _worker.Post(() => EtsInstrumentKit.Build(read, readSii, model, interior, chassis, name), (result, error) =>
            {
                entry.Kit = result as EtsInstrumentKit;
                entry.Failure = error != null ? error.GetType().Name + ": " + error.Message : null;
                entry.Pending = false;
            });
        }

        /// <summary>The dashboard's "piece N" objects (EtsModelBuilder names them so), by piece index.</summary>
        private Dictionary<int, GameObject> MapPieces()
        {
            var map = new Dictionary<int, GameObject>();

            foreach (Transform part in transform)
            {
                foreach (Transform piece in part)
                {
                    string name = piece.name;

                    if (name.StartsWith("piece ", StringComparison.Ordinal) && int.TryParse(name.Substring(6), out int p) && !map.ContainsKey(p))
                    {
                        map[p] = piece.gameObject;
                    }
                }
            }

            return map;
        }

        /// <summary>The skeleton as Transforms at the bind pose (game frame mirrored on z, as EtsModelBuilder does), and the bindings' bones.</summary>
        private void MakeSkeleton()
        {
            _pieceObjects = MapPieces();
            EtsModelBone[] bones = _model.Bones;
            int count = bones.Length;
            int layer = gameObject.layer;
            _skeleton = new GameObject("instrument skeleton").transform;
            _skeleton.gameObject.layer = layer;
            _skeleton.SetParent(transform, false);
            _made.Add(_skeleton.gameObject);
            _bones = new Transform[count];

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject(string.IsNullOrEmpty(bones[i].Name) ? "bone " + i : bones[i].Name);
                go.layer = layer;
                _bones[i] = go.transform;
            }

            for (int i = 0; i < count; i++)
            {
                int parent = bones[i].Parent;
                bool valid = parent >= 0 && parent < count && parent != i && !Loops(bones, i);
                _bones[i].SetParent(valid ? _bones[parent] : _skeleton, false);
            }

            for (int i = 0; i < count; i++)
            {
                _bones[i].localPosition = EtsModelBuilder.ToUnity(bones[i].Translation);
                _bones[i].localRotation = EtsModelBuilder.ToUnityRotation(bones[i].Rotation);
                _bones[i].localScale = Vector3.one;
            }

            // Bind poses relative to the dashboard root, where the skinned pieces sit (identity under it, as the pieces do).
            if (_entry.Bindposes == null || _entry.Bindposes.Length != count)
            {
                var bindposes = new Matrix4x4[count];
                Matrix4x4 rootToWorld = transform.localToWorldMatrix;

                for (int i = 0; i < count; i++)
                {
                    bindposes[i] = _bones[i].worldToLocalMatrix * rootToWorld;
                }

                _entry.Bindposes = bindposes;
            }

            EtsInstrumentKit kit = _entry.Kit;
            int n = kit.Bindings.Count;
            _lastT = new float[n];
            _bindBones = new Transform[n][];
            _hideOff = new bool[n][];

            for (int b = 0; b < n; b++)
            {
                EtsInstrumentBinding binding = kit.Bindings[b];
                EtsAnimation anim = binding.Animation;
                _lastT[b] = float.NaN;
                _bindBones[b] = new Transform[anim.BoneCount];
                _hideOff[b] = new bool[anim.BoneCount];

                for (int k = 0; k < anim.BoneCount; k++)
                {
                    int bone = anim.BoneIndices[k];

                    if (bone >= 0 && bone < count)
                    {
                        _bindBones[b][k] = _bones[bone];
                        _hideOff[b][k] = binding.Kind == EtsInstrumentKind.Lamp && bone < kit.BoneVertices.Length && kit.BoneVertices[bone] <= 12 &&
                                         !(bone < kit.BoneHasChildren.Length && kit.BoneHasChildren[bone]);
                    }
                }
            }

            // A plausible tank per truck (no fuel model in this world): 60-90 % full, the same for the same truck.
            float share = 0.6f + 0.3f * Mathf.Repeat(Mathf.Abs(Mathf.Sin(GetInstanceID() * 12.9898f)) * 43758.5453f, 1f);
            _state = new EtsCabState(kit.TankLitres, kit.AdBlueLitres, share);
        }

        private static bool Loops(EtsModelBone[] bones, int start)
        {
            int current = bones[start].Parent;

            for (int guard = 0; guard <= bones.Length; guard++)
            {
                if (current < 0 || current >= bones.Length) return false;
                if (current == start) return true;
                current = bones[current].Parent;
            }

            return true;
        }

        /// <summary>One moving dashboard piece drawn skinned: a copy of its mesh with the file's weights, over the skeleton.</summary>
        private void SkinPiece(int p)
        {
            if (!_pieceObjects.TryGetValue(p, out GameObject piece) || piece == null)
            {
                return; // not shown in this variant
            }

            var filter = piece.GetComponent<MeshFilter>();
            var renderer = piece.GetComponent<MeshRenderer>();

            if (filter == null || renderer == null || filter.sharedMesh == null || !renderer.enabled)
            {
                return;
            }

            EtsInstrumentKit kit = _entry.Kit;
            Mesh source = filter.sharedMesh;

            if (kit.SkinIndices[p] == null || source.vertexCount != _model.Pieces[p].VertexCount || !source.isReadable)
            {
                WarnOnce("piece " + kit.ModelPath + " " + p, "Instruments: " + _name + " - piece " + p + " left still (" +
                         (kit.SkinIndices[p] == null ? "no skin streams" : !source.isReadable ? "mesh not readable" : "vertex count " + source.vertexCount + " vs " + _model.Pieces[p].VertexCount) + ").");
                return;
            }

            if (!_entry.Skinned.TryGetValue(source, out Mesh mesh) || mesh == null)
            {
                mesh = SkinnedCopy(source, kit.SkinIndices[p], kit.SkinWeights[p], _entry.Bindposes);
                _entry.Skinned[source] = mesh;
            }

            var holder = new GameObject("skinned " + piece.name);
            holder.layer = piece.layer;
            holder.transform.SetParent(piece.transform, false);
            _made.Add(holder);

            var skinned = holder.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = mesh;
            skinned.bones = _bones;
            skinned.rootBone = _skeleton;
            skinned.sharedMaterials = renderer.sharedMaterials;
            skinned.shadowCastingMode = renderer.shadowCastingMode;
            skinned.receiveShadows = renderer.receiveShadows;
            skinned.lightProbeUsage = renderer.lightProbeUsage;
            skinned.reflectionProbeUsage = renderer.reflectionProbeUsage;
            skinned.probeAnchor = renderer.probeAnchor;
            skinned.renderingLayerMask = renderer.renderingLayerMask;
            skinned.updateWhenOffscreen = false;
            skinned.skinnedMotionVectors = false;
            skinned.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            skinned.quality = SkinQuality.Auto;
            Bounds bounds = source.bounds;
            bounds.Expand(0.4f);
            skinned.localBounds = bounds;

            renderer.enabled = false;
            _switchedOff.Add(renderer);
            _skinned++;
        }

        private static Mesh SkinnedCopy(Mesh source, byte[] indices, byte[] weights, Matrix4x4[] bindposes)
        {
            Mesh mesh = Instantiate(source);
            mesh.name = source.name + " (skinned)";
            int count = source.vertexCount;
            var boneWeights = new BoneWeight[count];
            var idx = new int[4];
            var wgt = new int[4];

            for (int v = 0; v < count; v++)
            {
                int sum = 0;

                for (int k = 0; k < 4; k++)
                {
                    idx[k] = indices[v * 4 + k];
                    wgt[k] = weights[v * 4 + k];
                    sum += wgt[k];
                }

                // Heaviest first, as Unity expects.
                for (int a = 1; a < 4; a++)
                {
                    for (int c = a; c > 0 && wgt[c] > wgt[c - 1]; c--)
                    {
                        int ti = idx[c]; idx[c] = idx[c - 1]; idx[c - 1] = ti;
                        int tw = wgt[c]; wgt[c] = wgt[c - 1]; wgt[c - 1] = tw;
                    }
                }

                float scale = sum > 0 ? 1f / sum : 0f;
                boneWeights[v] = sum > 0
                    ? new BoneWeight
                    {
                        boneIndex0 = idx[0], weight0 = wgt[0] * scale,
                        boneIndex1 = idx[1], weight1 = wgt[1] * scale,
                        boneIndex2 = idx[2], weight2 = wgt[2] * scale,
                        boneIndex3 = idx[3], weight3 = wgt[3] * scale
                    }
                    : new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }

            mesh.boneWeights = boneWeights;
            mesh.bindposes = bindposes;
            return mesh;
        }

        /// <summary>Everything this component made is removed and the factory's static dashboard shown again.</summary>
        private void Fail(string kind, string why, Exception e)
        {
            _phase = PhaseFailed;
            WarnOnce("fail " + kind, "Instruments: " + _name + " - " + kind + " failed, the dashboard stays still: " + why + (e != null ? "\n" + e : ""));

            try
            {
                foreach (Renderer renderer in _switchedOff)
                {
                    if (renderer != null) renderer.enabled = true;
                }

                _switchedOff.Clear();

                foreach (GameObject made in _made)
                {
                    if (made != null) Destroy(made);
                }

                _made.Clear();
            }
            catch (Exception again)
            {
                WarnOnce("restore", "Instruments: restoring the dashboard: " + again.Message);
            }

            enabled = false;
        }

        // ------------------------------------------------------------------ every frame

        private void Animate()
        {
            if (_truck == null && Time.frameCount >= _nextTruckSearch)
            {
                _nextTruckSearch = Time.frameCount + 30;
                _truck = _rig != null ? _rig.Controller : null;

                if (_truck == null)
                {
                    _truck = GetComponentInParent<EtsTruckController>();
                }
            }

            EtsTruckController truck = _truck;

            if (truck != null)
            {
                bool electricity = truck.EngineRunning || (truck.Driven && !truck.Puppet) || (_rig != null && ReferenceEquals(EtsLocalTruck.Occupied, _rig));
                // 2026-10-14: the truck's own light switch, main beam and wipers (EtsTruckController, EtsHeadlights); the old fields still win when set.
                _state.Lights = Lights > 0 ? Lights : truck.Lights;
                _state.HighBeam = HighBeam || truck.HighBeam;
                _state.Wiper = truck.WiperPhase;
                _state.Step(Time.deltaTime, truck.SpeedKmh, truck.Rpm, truck.Throttle, truck.Brake, truck.EngineRunning, electricity,
                            truck.ParkingBrake, truck.Indicator, truck.IndicatorLit, truck.Gear);
            }
            else
            {
                _state.Step(Time.deltaTime, 0f, 0f, 0f, 0f, false, false, true, 0, false, 0);
            }

            // A hand on the stalk (EtsCabControls, this frame or the last): the stalk follows it until the indicator switches.
            if (_stalkHoldFrame >= Time.frameCount - 1)
            {
                _state.Values[(int)EtsCabSignal.BlinkerStalk] = _stalkHold;
            }

            if (Time.frameCount >= _nextNearCheck)
            {
                _nextNearCheck = Time.frameCount + 15;
                Transform eye = EtsTruckRig.LocalView();
                _near = eye == null || (eye.position - transform.position).sqrMagnitude < AnimateRadius * AnimateRadius ||
                        (_rig != null && (ReferenceEquals(EtsLocalTruck.Occupied, _rig) || ReferenceEquals(EtsLocalTruck.Own, _rig) && truck != null && truck.Driven));
            }

            if (!_near)
            {
                return;
            }

            List<EtsInstrumentBinding> bindings = _entry.Kit.Bindings;
            float[] values = _state.Values;

            for (int i = 0; i < bindings.Count; i++)
            {
                EtsInstrumentBinding binding = bindings[i];
                float t = binding.T(values[(int)binding.Signal]);

                // A press-and-return switch pressed by hand (PlayPress): played through once, toward its new state, so it
                // passes its pressed pose in the middle (a 0-or-1 state alone never shows it: both ends are the rest).
                if (_pressStart != null && _pressStart[i] >= 0f)
                {
                    float e = (Time.time - _pressStart[i]) / PressSeconds;

                    if (e >= 1f || e < 0f)
                    {
                        _pressStart[i] = -1f;
                    }
                    else
                    {
                        t = t >= 0.5f ? e : 1f - e;
                    }
                }

                if (Math.Abs(t - _lastT[i]) < 0.0004f)
                {
                    continue;
                }

                _lastT[i] = t;
                Pose(i, binding, t);
            }

            if (_ringsShown > 0)
            {
                FaceRings();
            }

            if (_displayRetryAt > 0f && Time.time >= _displayRetryAt)
            {
                _displayRetryAt = -1f;

                try
                {
                    MakeDisplay();

                    if (_displayText != null)
                    {
                        Debug.Log("[SomniumEuroTruck] Instruments: " + _name + " - cluster display on (font ready after " + _displayTries + " tries).");
                    }
                }
                catch (Exception e)
                {
                    WarnOnce("display", _name + ": no cluster display: " + e.Message);
                }
            }

            if (_displayText != null && Time.time >= _nextDisplay)
            {
                _nextDisplay = Time.time + 0.25f;
                UpdateDisplay(truck);
            }
        }

        private void Pose(int i, EtsInstrumentBinding binding, float t)
        {
            EtsAnimation anim = binding.Animation;
            Transform[] bones = _bindBones[i];
            bool[] hide = _hideOff[i];

            for (int k = 0; k < bones.Length; k++)
            {
                Transform bone = bones[k];

                if (bone == null)
                {
                    continue;
                }

                anim.Sample(k, t, _rot, _pos);
                bone.localPosition = new Vector3(_pos[0], _pos[1], -_pos[2]);
                bone.localRotation = new Quaternion(-_rot[0], -_rot[1], _rot[2], _rot[3]);

                if (hide[k])
                {
                    Vector3 scale = t > 0.5f ? Vector3.one : Vector3.zero;

                    if (bone.localScale != scale)
                    {
                        bone.localScale = scale;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ the driver's controls (VR cab, 2026-09-25)
        //
        // Where the blinker stalk, the parking brake lever and the hazard switch are, for EtsCabControls: found once, on the
        // first ask (so puppets and parked trucks never pay for it), from the game's own files (EtsInstrumentKit.FindControls,
        // checked against the VR cab scout's measurements of all 18 demo trucks - vrcab/probe/vc1_out.txt). A control's
        // points ride on its bones (bone.TransformPoint of the bind-pose point carried into the bone by its bind pose), so
        // they follow the pose this component draws: the lever down when the parking brake is on, the stalk up or down.
        // A cab with no animated hazard switch takes the 4-way lamp inside its switch, else the hazard decal the scout
        // found on the static interior (EtsInstrumentKit.HazardDecal), else none.

        public const int StalkControl = 0, ParkingControl = 1, HazardControl = 2, ControlCount = 3;

        /// <summary>How long a press-and-return switch takes to go in and come back when pressed by hand (s).</summary>
        public const float PressSeconds = 0.3f;

        private sealed class ControlHandle
        {
            public EtsCabControl Data;
            public Transform TipBone, MainBone, CentreBone;
            public Transform Fixed;
            public Vector3 TipLocal, CentreLocal;
            public string Source;
            public GameObject Highlight;
            public Renderer HighlightRenderer;
            public bool Ring;
            public bool HighlightTried;
            public int Level;
        }

        private static Material _glowHover, _glowHeld;
        private static Mesh _ringMesh;

        /// <summary>
        /// A control's points in the world, as drawn this frame: the pivot (the main bone's origin), the tip, the centre;
        /// how thick it is round the pivot-to-tip line and how far it reaches from its centre. `point` is a fixed spot with
        /// no geometry of its own (the hazard decal): pivot, tip and centre are the same there. False when this cab has no
        /// such control (or the instruments are not running).
        /// </summary>
        public bool TryGetControl(int which, out Vector3 pivot, out Vector3 tip, out Vector3 centre, out float thickness, out float extent, out bool point)
        {
            pivot = tip = centre = default;
            thickness = extent = 0f;
            point = false;

            if (which < 0 || which >= ControlCount || !EnsureControls())
            {
                return false;
            }

            ControlHandle handle = _controls[which];

            if (handle == null)
            {
                return false;
            }

            if (handle.Fixed != null)
            {
                pivot = tip = centre = handle.Fixed.TransformPoint(handle.CentreLocal);
                point = true;
                return true;
            }

            if (handle.TipBone == null || handle.MainBone == null || handle.CentreBone == null)
            {
                return false;
            }

            pivot = handle.MainBone.position;
            tip = handle.TipBone.TransformPoint(handle.TipLocal);
            centre = handle.CentreBone.TransformPoint(handle.CentreLocal);
            thickness = handle.Data.Thickness;
            extent = handle.Data.Extent;
            return true;
        }

        /// <summary>
        /// Lights a control for the hand on it: 0 off, 1 a hand is on it (the trigger will act on it), 2 it is being worked.
        /// The control's own vertices drawn again over it in a warm tint (the world's decal shader: stereo-correct, shipped
        /// with the world), or a small ring round a point with no geometry (the hazard decal, a lamp). Built on first use.
        /// </summary>
        public void SetControlHighlight(int which, int level)
        {
            if (_controls == null || which < 0 || which >= ControlCount)
            {
                return;
            }

            ControlHandle handle = _controls[which];

            if (handle == null || handle.Level == level)
            {
                return;
            }

            handle.Level = level;

            try
            {
                if (level > 0 && !handle.HighlightTried)
                {
                    handle.HighlightTried = true;
                    BuildHighlight(handle, which);
                }

                if (handle.Highlight == null)
                {
                    return;
                }

                bool on = level > 0;

                if (handle.Highlight.activeSelf != on)
                {
                    handle.Highlight.SetActive(on);
                    if (handle.Ring) _ringsShown += on ? 1 : -1;
                }

                if (on)
                {
                    handle.HighlightRenderer.sharedMaterial = level >= 2 ? _glowHeld : _glowHover;
                }

                if (on && handle.Ring)
                {
                    FaceRings();
                }
            }
            catch (Exception e)
            {
                WarnOnce("highlight", "Cab controls: " + _name + " - no highlight: " + e.Message);
                handle.Highlight = null;
            }
        }

        /// <summary>The stalk follows a hand this frame (0 left, 0.5 rest, 1 right), until the hand lets it be.</summary>
        public void HoldStalk(float t)
        {
            _stalkHold = Mathf.Clamp01(t);
            _stalkHoldFrame = Time.frameCount;
        }

        /// <summary>A press by hand: every press-and-return switch on that signal plays through once (see PressSeconds).</summary>
        public void PlayPress(EtsCabSignal signal)
        {
            if (_pressStart == null || _momentary == null || _entry == null || _entry.Kit == null)
            {
                return;
            }

            List<EtsInstrumentBinding> bindings = _entry.Kit.Bindings;

            for (int i = 0; i < bindings.Count && i < _pressStart.Length; i++)
            {
                if (bindings[i].Signal == signal && _momentary[i])
                {
                    _pressStart[i] = Time.time;
                }
            }
        }

        private bool EnsureControls()
        {
            if (_controlsTried)
            {
                return _controls != null;
            }

            if (_phase != PhaseRunning)
            {
                return false;
            }

            _controlsTried = true;

            try
            {
                EtsInstrumentKit kit = _entry.Kit;
                var shown = new bool[_model.Pieces.Length];

                foreach (KeyValuePair<int, GameObject> pair in _pieceObjects)
                {
                    if (pair.Key >= 0 && pair.Key < shown.Length && pair.Value != null)
                    {
                        shown[pair.Key] = true;
                    }
                }

                var handles = new ControlHandle[ControlCount];

                foreach (EtsCabControl control in kit.FindControls(shown))
                {
                    int slot = control.Signal == EtsCabSignal.BlinkerStalk ? StalkControl : control.Signal == EtsCabSignal.ParkingStick ? ParkingControl : HazardControl;

                    if (handles[slot] != null || !ValidBone(control.TipBone) || !ValidBone(control.MainBone) || !ValidBone(control.CentreBone))
                    {
                        continue;
                    }

                    handles[slot] = new ControlHandle
                    {
                        Data = control,
                        TipBone = _bones[control.TipBone],
                        MainBone = _bones[control.MainBone],
                        CentreBone = _bones[control.CentreBone],
                        TipLocal = _entry.Bindposes[control.TipBone].MultiplyPoint3x4(new Vector3(control.Tip[0], control.Tip[1], control.Tip[2])),
                        CentreLocal = _entry.Bindposes[control.CentreBone].MultiplyPoint3x4(new Vector3(control.Centre[0], control.Centre[1], control.Centre[2])),
                        Source = (control.FromLamp ? "the 4-way lamp in the switch, bone " : "bone ") + _model.Bones[control.MainBone].Name + ", " + control.Vertices + " vertices" +
                                 (control.Momentary ? ", press-and-return" : "")
                    };
                }

                if (handles[HazardControl] == null && EtsInstrumentKit.HazardDecal(_interiorModelPath, out float x, out float y, out float z))
                {
                    Transform interior = FindInteriorRoot();
                    handles[HazardControl] = new ControlHandle
                    {
                        Fixed = interior != null ? interior : transform,
                        CentreLocal = new Vector3(x, y, z),
                        TipLocal = new Vector3(x, y, z),
                        Source = "the hazard-triangle decal on the dashboard (static; the cab scout's table)"
                    };
                }

                _momentary = new bool[kit.Bindings.Count];
                _pressStart = new float[kit.Bindings.Count];

                for (int i = 0; i < kit.Bindings.Count; i++)
                {
                    _momentary[i] = EtsInstrumentKit.IsMomentary(kit.Bindings[i].Animation);
                    _pressStart[i] = -1f;
                }

                _controls = handles;
                LogControls();
            }
            catch (Exception e)
            {
                _controls = null;
                WarnOnce("controls " + _name, "Cab controls: " + _name + " - could not be found: " + e.GetType().Name + ": " + e.Message);
            }

            return _controls != null;
        }

        private bool ValidBone(int bone)
        {
            return bone >= 0 && bone < _bones.Length && _bones[bone] != null && bone < _entry.Bindposes.Length;
        }

        /// <summary>"Cab controls: DAF XF Euro 6 - stalk at (x, y, z) m in the truck ... ." Once per truck, on the first ask.</summary>
        private void LogControls()
        {
            EtsTruckController truck = _truck != null ? _truck : GetComponentInParent<EtsTruckController>();
            Transform frame = truck != null ? truck.transform : null;
            var line = new System.Text.StringBuilder("[SomniumEuroTruck] Cab controls: ").Append(_name).Append(" -");
            string[] names = { " stalk", " parking brake", " hazard switch" };

            for (int c = 0; c < ControlCount; c++)
            {
                line.Append(c > 0 ? ";" : "").Append(names[c]);

                if (_controls[c] == null || !TryGetControl(c, out Vector3 pivot, out Vector3 tip, out Vector3 centre, out float thick, out float extent, out bool point))
                {
                    line.Append(c == ParkingControl ? " none in the files (no parking_stick_anim; right A still works)" : " none in the files");
                    continue;
                }

                Vector3 at = c == HazardControl ? centre : tip;
                Vector3 local = frame != null ? frame.InverseTransformPoint(at) : at;
                line.Append(c == HazardControl ? " at " : " tip at ").Append(local.ToString("F3")).Append(frame != null ? " in the truck" : " in the world");

                if (!point)
                {
                    line.Append(", ").Append(_controls[c].Data.Length.ToString("0.000")).Append(" m from its pivot, ").Append(thick.ToString("0.000")).Append(" m thick");
                }

                line.Append(" (").Append(_controls[c].Source).Append(')');
            }

            Debug.Log(line.Append('.').ToString());
        }

        private void BuildHighlight(ControlHandle handle, int which)
        {
            if (!Glow())
            {
                WarnOnce("glow", "Cab controls: no highlight (the world's decal shader is not loaded).");
                return;
            }

            Mesh mesh = handle.Data != null && !handle.Data.FromLamp ? ControlMesh(handle.Data) : null;
            GameObject holder;

            if (mesh != null)
            {
                _madeMeshes.Add(mesh);
                holder = new GameObject("cab control highlight " + which);
                holder.layer = gameObject.layer;
                holder.transform.SetParent(transform, false);
                var skinned = holder.AddComponent<SkinnedMeshRenderer>();
                skinned.sharedMesh = mesh;
                skinned.bones = _bones;
                skinned.rootBone = _skeleton;
                Bounds bounds = mesh.bounds;
                bounds.Expand(0.4f);
                skinned.localBounds = bounds;
                skinned.updateWhenOffscreen = false;
                skinned.skinnedMotionVectors = false;
                skinned.quality = SkinQuality.Auto;
                handle.HighlightRenderer = skinned;
            }
            else
            {
                holder = new GameObject("cab control ring " + which);
                holder.layer = gameObject.layer;
                holder.transform.SetParent(transform, false);
                holder.AddComponent<MeshFilter>().sharedMesh = Ring();
                handle.HighlightRenderer = holder.AddComponent<MeshRenderer>();
                handle.Ring = true;
            }

            Renderer renderer = handle.HighlightRenderer;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.sharedMaterial = _glowHover;
            holder.SetActive(false);
            handle.Highlight = holder;
            _made.Add(holder);
        }

        /// <summary>
        /// The control's own triangles (all three corners on the control), skinned to the dashboard's skeleton with the
        /// file's weights, 1 mm out along the normals so it lies over the control. Null when it has too few to draw.
        /// </summary>
        private Mesh ControlMesh(EtsCabControl control)
        {
            EtsInstrumentKit kit = _entry.Kit;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var weights = new List<BoneWeight>();
            var triangles = new List<int>();
            var idx = new int[4];
            var wgt = new int[4];

            for (int p = 0; p < control.PieceVertices.Length && p < _model.Pieces.Length; p++)
            {
                int[] verts = control.PieceVertices[p];
                EtsModelPiece piece = _model.Pieces[p];

                if (verts == null || piece.Indices == null || kit.SkinIndices[p] == null)
                {
                    continue;
                }

                var map = new int[piece.VertexCount];
                for (int v = 0; v < map.Length; v++) map[v] = -1;
                byte[] si = kit.SkinIndices[p], sw = kit.SkinWeights[p];

                foreach (int v in verts)
                {
                    map[v] = positions.Count;
                    var normal = piece.Normals != null ? new Vector3(piece.Normals[v * 3], piece.Normals[v * 3 + 1], -piece.Normals[v * 3 + 2]) : Vector3.zero;
                    positions.Add(new Vector3(piece.Positions[v * 3], piece.Positions[v * 3 + 1], -piece.Positions[v * 3 + 2]) + normal * 0.001f);
                    normals.Add(normal);
                    int sum = 0;

                    for (int k = 0; k < 4; k++)
                    {
                        idx[k] = si[v * 4 + k];
                        wgt[k] = sw[v * 4 + k];
                        sum += wgt[k];
                    }

                    for (int a = 1; a < 4; a++)
                    {
                        for (int c = a; c > 0 && wgt[c] > wgt[c - 1]; c--)
                        {
                            int ti = idx[c]; idx[c] = idx[c - 1]; idx[c - 1] = ti;
                            int tw = wgt[c]; wgt[c] = wgt[c - 1]; wgt[c - 1] = tw;
                        }
                    }

                    float scale = sum > 0 ? 1f / sum : 0f;
                    weights.Add(sum > 0
                        ? new BoneWeight
                        {
                            boneIndex0 = idx[0], weight0 = wgt[0] * scale,
                            boneIndex1 = idx[1], weight1 = wgt[1] * scale,
                            boneIndex2 = idx[2], weight2 = wgt[2] * scale,
                            boneIndex3 = idx[3], weight3 = wgt[3] * scale
                        }
                        : new BoneWeight { boneIndex0 = control.MainBone, weight0 = 1f });
                }

                int[] tri = piece.Indices;

                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    int a = tri[t], b = tri[t + 1], c = tri[t + 2];

                    if (a < map.Length && b < map.Length && c < map.Length && map[a] >= 0 && map[b] >= 0 && map[c] >= 0)
                    {
                        triangles.Add(map[a]);
                        triangles.Add(map[c]);
                        triangles.Add(map[b]);
                    }
                }
            }

            if (triangles.Count < 12 * 3)
            {
                return null;
            }

            var mesh = new Mesh { name = "cab control highlight" };

            if (positions.Count > 65000)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            var colours = new Color32[positions.Count];
            for (int i = 0; i < colours.Length; i++) colours[i] = new Color32(128, 128, 128, 128);
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.colors32 = colours;
            mesh.uv = new Vector2[positions.Count]; // the decal shader reads a uv (its white texture)
            mesh.SetTriangles(triangles, 0);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = _entry.Bindposes;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The two tints, on the world's decal shader (vertex colour 128 = one; lit like the cab, so the colour is bright).</summary>
        private static bool Glow()
        {
            if (_glowHover != null && _glowHeld != null)
            {
                return true;
            }

            Shader shader = EtsMaterialCache.DecalShader;

            if (shader == null)
            {
                return false;
            }

            _glowHover = Tint(shader, "cab control hover", new Color(1.9f, 1.25f, 0.35f, 0.32f));
            _glowHeld = Tint(shader, "cab control held", new Color(2.2f, 1.5f, 0.45f, 0.55f));
            return true;
        }

        private static Material Tint(Shader shader, string name, Color colour)
        {
            var material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            if (material.HasProperty("_VertexAlphaScale")) material.SetFloat("_VertexAlphaScale", 2f);
            return material;
        }

        /// <summary>A flat ring, 22 to 30 mm, in the xy plane facing +z (turned to the eye while shown).</summary>
        private static Mesh Ring()
        {
            if (_ringMesh != null)
            {
                return _ringMesh;
            }

            const int segments = 32;
            const float inner = 0.022f, outer = 0.030f;
            var vertices = new Vector3[segments * 2];
            var normals = new Vector3[segments * 2];
            var colours = new Color32[segments * 2];
            var triangles = new int[segments * 6];

            for (int s = 0; s < segments; s++)
            {
                float angle = s * Mathf.PI * 2f / segments;
                var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                vertices[s * 2] = direction * inner;
                vertices[s * 2 + 1] = direction * outer;
                normals[s * 2] = normals[s * 2 + 1] = Vector3.forward;
                colours[s * 2] = colours[s * 2 + 1] = new Color32(128, 128, 128, 128);
                int next = (s + 1) % segments;
                triangles[s * 6] = s * 2;
                triangles[s * 6 + 1] = next * 2;
                triangles[s * 6 + 2] = s * 2 + 1;
                triangles[s * 6 + 3] = s * 2 + 1;
                triangles[s * 6 + 4] = next * 2;
                triangles[s * 6 + 5] = next * 2 + 1;
            }

            _ringMesh = new Mesh { name = "cab control ring", vertices = vertices, normals = normals, colors32 = colours, uv = new Vector2[segments * 2], triangles = triangles };
            _ringMesh.RecalculateBounds();
            return _ringMesh;
        }

        /// <summary>Every shown ring on its point, turned to the local view (a few transforms; nothing allocated).</summary>
        private void FaceRings()
        {
            Transform eye = EtsTruckRig.LocalView();

            for (int c = 0; c < ControlCount; c++)
            {
                ControlHandle handle = _controls != null ? _controls[c] : null;

                if (handle == null || !handle.Ring || handle.Highlight == null || !handle.Highlight.activeSelf)
                {
                    continue;
                }

                if (!TryGetControl(c, out _, out _, out Vector3 centre, out _, out _, out _))
                {
                    continue;
                }

                Vector3 toEye = eye != null ? eye.position - centre : -transform.forward;
                Transform ring = handle.Highlight.transform;
                ring.position = centre + (toEye.sqrMagnitude > 1e-6f ? toEye.normalized * 0.004f : Vector3.zero);
                ring.rotation = toEye.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(toEye) : transform.rotation;
            }
        }

        // ------------------------------------------------------------------ the display

        /// <summary>
        /// The cluster's display: the interior model's piece whose texture is the game's dashboard render target
        /// (/vehicle/truck/share/dashboard.tobj - the game draws /ui/dashboard/&lt;truck&gt;.sii into it; there is no such file,
        /// so the factory draws it blank). It is darkened into a screen and given a line of gear and speed and a line of
        /// clock and fuel, in the game's own face, refreshed four times a second without allocating.
        /// </summary>
        private void MakeDisplay()
        {
            if (string.IsNullOrEmpty(_interiorModelPath) || EtsEngine.Materials == null)
            {
                return;
            }

            Transform interiorRoot = FindInteriorRoot();

            if (interiorRoot == null)
            {
                return;
            }

            EtsModelData interior = EtsEngine.LoadModel(_interiorModelPath, out _);

            if (interior == null)
            {
                return;
            }

            int look = Mathf.Max(0, interior.LookIndex(_interiorLook ?? "default"));
            string[] materials = look < interior.LookMaterials.Length ? interior.LookMaterials[look] : new string[0];
            Dictionary<int, GameObject> pieces = null;
            GameObject best = null;
            float bestArea = 0f;
            EtsScreenRect bestRect = null;

            for (int p = 0; p < interior.Pieces.Length; p++)
            {
                EtsModelPiece piece = interior.Pieces[p];
                string path = piece.MaterialIndex < materials.Length ? materials[piece.MaterialIndex] : null;
                EtsMaterialDef def = path != null ? EtsEngine.Materials.GetDefinition(path) : null;
                string texture = def != null ? def.BaseTexture : null;

                if (texture == null || !texture.EndsWith("/share/dashboard.tobj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (pieces == null)
                {
                    pieces = new Dictionary<int, GameObject>();

                    foreach (Transform part in interiorRoot)
                    {
                        foreach (Transform child in part)
                        {
                            if (child.name.StartsWith("piece ", StringComparison.Ordinal) && int.TryParse(child.name.Substring(6), out int index) && !pieces.ContainsKey(index))
                            {
                                pieces[index] = child.gameObject;
                            }
                        }
                    }
                }

                if (!pieces.TryGetValue(p, out GameObject go) || go == null || go.GetComponent<MeshRenderer>() == null)
                {
                    continue;
                }

                EtsScreenRect candidate = EtsInstrumentKit.FindScreen(piece);
                float area = candidate != null ? candidate.Width * candidate.Height : 0f;

                if (area > bestArea)
                {
                    bestArea = area;
                    best = go;
                    bestRect = candidate;
                }
            }

            if (best == null)
            {
                return;
            }

            // The game's own face once it is built or prepared (the tablet prepares it on a worker); else TextMeshPro's default;
            // else try again in a few seconds rather than build a font on this frame.
            TMP_FontAsset font = EtsFont.IsReady(EtsFont.Normal) ? EtsFont.Get(EtsFont.Normal) : null;

            if (font == null)
            {
                try
                {
                    font = TMP_Settings.defaultFontAsset;
                }
                catch (Exception)
                {
                    font = null;
                }
            }

            if (font == null)
            {
                if (++_displayTries < 20)
                {
                    _displayRetryAt = Time.time + 3f;
                }
                else
                {
                    WarnOnce("display font", "Instruments: no font for the cluster display.");
                }

                return;
            }

            // The screen's main patch as a rectangle on its plane, in the interior's (Unity) frame.
            EtsScreenRect rect = bestRect;

            if (rect == null || rect.Width < 0.02f || rect.Height < 0.01f || rect.Width > 1f || rect.Height > 1f)
            {
                return;
            }

            float width = rect.Width, height = rect.Height;
            var normal = new Vector3(rect.Normal[0], rect.Normal[1], rect.Normal[2]);
            var up = new Vector3(rect.Up[0], rect.Up[1], rect.Up[2]);
            Vector3 centre = new Vector3(rect.Centre[0], rect.Centre[1], rect.Centre[2]) + normal * 0.0015f;

            // The blank render target becomes a dark screen (this truck's own copy of the material).
            var screenRenderer = best.GetComponent<MeshRenderer>();
            Material dark = screenRenderer.sharedMaterial != null ? new Material(screenRenderer.sharedMaterial) : null;

            if (dark != null)
            {
                _madeMaterials.Add(dark);
                Color black = new Color(0.012f, 0.014f, 0.018f, 1f);
                if (dark.HasProperty("_BaseColor")) dark.SetColor("_BaseColor", black);
                if (dark.HasProperty("_Color")) dark.SetColor("_Color", black);
                if (dark.HasProperty("_BaseMap")) dark.SetTexture("_BaseMap", null);
                if (dark.HasProperty("_MainTex")) dark.SetTexture("_MainTex", null);
                if (dark.HasProperty("_EmissionColor")) dark.SetColor("_EmissionColor", Color.black);
                dark.DisableKeyword("_EMISSION");
                if (dark.HasProperty("_Smoothness")) dark.SetFloat("_Smoothness", 0.85f);
                screenRenderer.sharedMaterial = dark;
            }

            var holder = new GameObject("instrument display");
            holder.layer = best.layer;
            holder.transform.SetParent(best.transform, false);
            holder.transform.localPosition = centre;
            holder.transform.localRotation = Quaternion.LookRotation(-normal, up);
            _made.Add(holder);

            var text = holder.AddComponent<TextMeshPro>();
            text.font = font;
            text.rectTransform.sizeDelta = new Vector2(width * 0.9f, height * 0.8f);
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Truncate;
            text.richText = false;
            text.color = new Color(0.86f, 0.92f, 1f, 1f);
            text.enableAutoSizing = true;
            text.fontSizeMin = 0.005f;
            text.fontSizeMax = 5f;
            text.text = "D12   188 km/h\n00:00   100%";
            text.ForceMeshUpdate();

            // Sized once for the widest line; after that a text change is a plain layout, not a size search.
            float size = text.fontSize;
            text.enableAutoSizing = false;
            text.fontSize = size;
            text.text = "";
            var textRenderer = text.GetComponent<MeshRenderer>();

            if (textRenderer != null)
            {
                textRenderer.shadowCastingMode = ShadowCastingMode.Off;
                textRenderer.receiveShadows = false;
            }

            _displayText = text;
            _displays = 1;
        }

        /// <summary>The interior model's root: the dashboard's sibling on the same locator, named after the model file (EtsModelBuilder).</summary>
        private Transform FindInteriorRoot()
        {
            if (_rig != null && _rig.Interior != null)
            {
                return _rig.Interior.transform;
            }

            Transform mount = transform.parent;

            if (mount == null)
            {
                return null;
            }

            string file = _interiorModelPath;
            int slash = file.LastIndexOf('/');
            string name = slash < 0 ? file : file.Substring(slash + 1);

            foreach (Transform sibling in mount)
            {
                if (sibling != transform && sibling.name == name)
                {
                    return sibling;
                }
            }

            return null;
        }

        private void UpdateDisplay(EtsTruckController truck)
        {
            bool on = truck != null && (truck.EngineRunning || (truck.Driven && !truck.Puppet));

            GameObject screen = _displayText.gameObject;

            if (screen.activeSelf != on)
            {
                screen.SetActive(on);
            }

            if (!on)
            {
                return;
            }

            // "D7   86 km/h" / "14:05   72%", written into a reused buffer.
            int length = 0;
            int gear = truck.Gear;

            if (gear > 0)
            {
                _chars[length++] = 'D';
                length = Write(gear, length);
            }
            else if (gear < 0)
            {
                _chars[length++] = 'R';
                length = Write(-gear, length);
            }
            else
            {
                _chars[length++] = 'N';
            }

            _chars[length++] = ' ';
            _chars[length++] = ' ';
            _chars[length++] = ' ';
            length = Write(Mathf.RoundToInt(truck.SpeedKmh), length);
            _chars[length++] = ' ';
            _chars[length++] = 'k';
            _chars[length++] = 'm';
            _chars[length++] = '/';
            _chars[length++] = 'h';
            _chars[length++] = '\n';

            DateTime now = DateTime.Now;
            _chars[length++] = (char)('0' + now.Hour / 10);
            _chars[length++] = (char)('0' + now.Hour % 10);
            _chars[length++] = ':';
            _chars[length++] = (char)('0' + now.Minute / 10);
            _chars[length++] = (char)('0' + now.Minute % 10);
            _chars[length++] = ' ';
            _chars[length++] = ' ';
            _chars[length++] = ' ';
            length = Write(Mathf.Clamp(Mathf.RoundToInt(_state.FuelShare * 100f), 0, 100), length);
            _chars[length++] = '%';
            _displayText.SetText(_chars, 0, length);
        }

        private int Write(int value, int at)
        {
            if (value < 0) value = 0;
            if (value > 9999) value = 9999;
            int start = at;

            do
            {
                _chars[at++] = (char)('0' + value % 10);
                value /= 10;
            }
            while (value > 0 && at < _chars.Length);

            Array.Reverse(_chars, start, at - start);
            return at;
        }

        // ------------------------------------------------------------------ logging

        private static void WarnOnce(string kind, string message)
        {
            if (Logged.Add(kind))
            {
                Debug.LogWarning("[SomniumEuroTruck] " + message);
            }
        }
    }
}
