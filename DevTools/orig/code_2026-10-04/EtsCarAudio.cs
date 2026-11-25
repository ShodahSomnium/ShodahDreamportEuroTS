using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The AI traffic's sound: an engine and tyre roar for the cars nearest the listener. The cars are EtsTraffic's pooled
    /// clones under the 'Ets Traffic' root (EtsTrafficModels: "Ets car &lt;unit&gt;", active while shown), found here by
    /// walking that root's children twice a second - nothing in the traffic code is touched. The nearest six within
    /// <see cref="HearMetres"/> each get a pair of 3D sources (engine, tyres) that follow the car's position every frame;
    /// the sources stay on this component's own objects, never on the pooled cars, so a car handed back to the pool never
    /// takes a playing source with it. Speed and acceleration come from the car's own movement (its interpolated
    /// position), so they are right on the master and on watchers alike. The engine is pitched through a simple gearbox
    /// (petrol car, or the truck's diesel note for anything longer than 7.5 m - buses, lorries, vans with trailers), louder
    /// when accelerating; the tyre roar rises with speed. A car that brakes hard with the local player's truck or trailer
    /// right in front of it - the player cut it off - may sound its horn (at most one horn every 12 s).
    /// The same walk counts the cars within 250 m and how fast they go (NearCars, FastTraffic) for EtsAmbience's motorway
    /// roar. Everything is damped inside the cab (EtsTruckAudio.ListenerOutsideGain). No allocation per frame.
    /// Added by the per-player EtsTruckAudio (Awake).
    /// </summary>
    public sealed class EtsCarAudio : MonoBehaviour
    {
        [Range(0f, 1f)]
        public float Volume = 0.5f;

        [Tooltip("Cars farther than this are not heard.")]
        public float HearMetres = 90f;

        public const int Slots = 6;
        public const float StatsMetres = 250f;
        public const string TrafficRootName = "Ets Traffic";

        public static EtsCarAudio Instance { get; private set; }

        /// <summary>Traffic cars within 250 m of the listener (last walk).</summary>
        public static int NearCars { get; private set; }

        /// <summary>0..1: how much fast traffic (55 to 90+ km/h) is near the listener, nearer counting more.</summary>
        public static float FastTraffic { get; private set; }

        public static EtsCarAudio Ensure(GameObject host)
        {
            if (Instance != null)
            {
                return Instance;
            }

            if (host == null)
            {
                return null;
            }

            EtsCarAudio a = host.GetComponent<EtsCarAudio>();
            return a != null ? a : host.AddComponent<EtsCarAudio>();
        }

        // ---------------------------------------------------------------- clips

        /// <summary>The car engine clip is a four-cylinder at this many rpm (60 Hz firing).</summary>
        private const float CarClipRpm = 1800f;

        private static AudioClip s_engine, s_tyre, s_horn;
        private static bool s_requested;

        private static readonly float[] CarGearTops = { 18f, 34f, 52f, 75f, 105f, 999f };
        private static readonly float[] HeavyGearTops = { 8f, 15f, 24f, 34f, 46f, 60f, 75f, 999f };

        private sealed class Slot
        {
            public Transform Car;
            public Transform Holder;
            public AudioSource Engine, Tyre;
            public float Fade;
            public bool Releasing;
            public bool Keep;
            public Vector3 Last;
            public float Speed = -1f, PrevSpeed, Accel;
            public Vector3 Dir = Vector3.forward;
            public int Gear = 1;
            public bool Heavy;
            public float BaseRpm = CarClipRpm;
            public float HonkReady;
        }

        private readonly Slot[] _slots = new Slot[Slots];
        private AudioSource _horn;
        private Transform _hornCar;
        private Transform _root;
        private float _nextFind, _nextScan;

        // The walk: the last position of each child, to tell speeds without tracking every car.
        private Transform[] _seen = new Transform[128];
        private Vector3[] _seenPos = new Vector3[128];
        private int _seenCount;
        private float _seenAt;
        private readonly Transform[] _near = new Transform[Slots];
        private readonly float[] _nearD2 = new float[Slots];
        private readonly bool[] _nearTaken = new bool[Slots];
        private int _nearCount;

        private float _nextHonk;
        private bool _loggedFollow;
        private int _honkLogs;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                return;
            }

            Instance = this;
            RequestClips();

            for (int i = 0; i < Slots; i++)
            {
                var holder = new GameObject("car sound " + i);
                holder.transform.SetParent(transform, false);
                var slot = new Slot { Holder = holder.transform };
                slot.Engine = NewSource(holder, 5f, 150f, 200);
                slot.Tyre = NewSource(holder, 6f, 150f, 200);
                _slots[i] = slot;
            }

            var horn = new GameObject("car horn");
            horn.transform.SetParent(transform, false);
            _horn = NewSource(horn, 8f, 250f, 190);
            _horn.loop = false;
        }

        private static AudioSource NewSource(GameObject holder, float near, float far, int priority)
        {
            AudioSource s = holder.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = true;
            s.volume = 0f;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = near;
            s.maxDistance = far;
            s.dopplerLevel = 0.6f; // passing cars rise and fall a little
            s.priority = priority; // the first voices given up (the truck is 64, the ambience 150)
            return s;
        }

        private void OnDisable()
        {
            for (int i = 0; i < Slots; i++)
            {
                Slot s = _slots[i];
                if (s == null) continue;
                Stop(s.Engine);
                Stop(s.Tyre);
                s.Car = null;
                s.Releasing = false;
                s.Fade = 0f;
            }

            if (_horn != null) Stop(_horn);
            NearCars = 0;
            FastTraffic = 0f;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                NearCars = 0;
                FastTraffic = 0f;
            }
        }

        private static void Stop(AudioSource s)
        {
            if (s != null && s.isPlaying) s.Stop();
        }

        // ---------------------------------------------------------------- per frame (no allocation)

        private void Update()
        {
            EtsSynth.Pump();

            if (_slots[0] == null || s_engine == null || s_tyre == null)
            {
                return;
            }

            float dt = Mathf.Min(Time.deltaTime, 0.1f);

            if (dt <= 0f)
            {
                return;
            }

            float now = Time.time;
            Transform ear = EtsTruckRig.LocalView();

            if (_root == null)
            {
                NearCars = 0;
                FastTraffic = 0f;

                if (now >= _nextFind)
                {
                    _nextFind = now + 2f;
                    GameObject found = GameObject.Find(TrafficRootName);
                    _root = found != null ? found.transform : null;
                    _seenCount = 0;
                }
            }

            if (ear != null && _root != null && now >= _nextScan)
            {
                _nextScan = now + 0.5f;
                Scan(ear.position, now);
            }

            float master = Mathf.Clamp01(Volume * 2f) * EtsTruckAudio.ListenerOutsideGain;

            for (int i = 0; i < Slots; i++)
            {
                UpdateSlot(_slots[i], dt, master, now);
            }

            if (_horn.isPlaying && _hornCar != null && _hornCar.gameObject.activeInHierarchy)
            {
                _horn.transform.position = _hornCar.position + Vector3.up;
            }
        }

        /// <summary>Twice a second: every shown car's distance and speed; the nearest six are the ones heard.</summary>
        private void Scan(Vector3 ear, float now)
        {
            int count = _root.childCount;

            if (count > _seen.Length)
            {
                System.Array.Resize(ref _seen, count * 2);
                System.Array.Resize(ref _seenPos, count * 2);
            }

            float span = now - _seenAt;
            bool haveSpan = _seenAt > 0f && span > 0.2f && span < 2f;
            _seenAt = now;
            _nearCount = 0;
            int near = 0;
            float fast = 0f;
            float hear2 = HearMetres * HearMetres, stats2 = StatsMetres * StatsMetres;

            for (int i = 0; i < count; i++)
            {
                Transform t = _root.GetChild(i);

                if (!t.gameObject.activeSelf)
                {
                    _seen[i] = null;
                    continue;
                }

                Vector3 p = t.position;
                float speed = -1f;

                if (haveSpan && ReferenceEquals(_seen[i], t))
                {
                    float dx = p.x - _seenPos[i].x, dz = p.z - _seenPos[i].z;
                    float v = Mathf.Sqrt(dx * dx + dz * dz) / span;
                    if (v < 70f) speed = v;
                }

                _seen[i] = t;
                _seenPos[i] = p;
                float d2 = (p - ear).sqrMagnitude;

                if (d2 < stats2)
                {
                    near++;

                    if (speed >= 0f)
                    {
                        fast += (1f - Mathf.Sqrt(d2) / StatsMetres) * Mathf.Clamp01((speed * 3.6f - 55f) / 35f);
                    }
                }

                if (d2 < hear2)
                {
                    InsertNear(t, d2);
                }
            }

            for (int i = count; i < _seenCount; i++)
            {
                _seen[i] = null;
            }

            _seenCount = count;
            NearCars = near;
            FastTraffic = Mathf.Clamp01(fast / 2f);
            Assign();
        }

        private void InsertNear(Transform t, float d2)
        {
            int at = _nearCount;

            while (at > 0 && _nearD2[at - 1] > d2)
            {
                if (at < Slots)
                {
                    _near[at] = _near[at - 1];
                    _nearD2[at] = _nearD2[at - 1];
                }

                at--;
            }

            if (at < Slots)
            {
                _near[at] = t;
                _nearD2[at] = d2;
                if (_nearCount < Slots) _nearCount++;
            }
        }

        /// <summary>Cars still among the nearest keep their slot; the rest fade out; newcomers take free slots and fade in.</summary>
        private void Assign()
        {
            for (int s = 0; s < Slots; s++)
            {
                _slots[s].Keep = false;
            }

            for (int k = 0; k < _nearCount; k++)
            {
                _nearTaken[k] = false;

                for (int s = 0; s < Slots; s++)
                {
                    Slot slot = _slots[s];

                    if (!slot.Releasing && ReferenceEquals(slot.Car, _near[k]))
                    {
                        slot.Keep = true;
                        _nearTaken[k] = true;
                        break;
                    }
                }
            }

            for (int s = 0; s < Slots; s++)
            {
                Slot slot = _slots[s];

                if (!ReferenceEquals(slot.Car, null) && !slot.Keep)
                {
                    slot.Releasing = true;
                }
            }

            int attached = 0;

            for (int k = 0; k < _nearCount; k++)
            {
                if (_nearTaken[k])
                {
                    continue;
                }

                for (int s = 0; s < Slots; s++)
                {
                    Slot slot = _slots[s];

                    if (ReferenceEquals(slot.Car, null))
                    {
                        Attach(slot, _near[k]);
                        attached++;
                        break;
                    }
                }
            }

            if (attached > 0 && !_loggedFollow)
            {
                _loggedFollow = true;
                Debug.Log("[SomniumEuroTruck] Car sound: following the nearest " + _nearCount + " of " + NearCars + " traffic cars within " + StatsMetres.ToString("0") + " m (nearest "
                    + Mathf.Sqrt(_nearD2[0]).ToString("0") + " m; engine + tyres in 3D, lorries and buses on the diesel note).");
            }
        }

        private void Attach(Slot slot, Transform car)
        {
            slot.Car = car;
            slot.Releasing = false;
            slot.Keep = true;
            slot.Fade = 0f;
            slot.Last = car.position;
            slot.Speed = -1f;
            slot.Accel = 0f;
            slot.Gear = 1;
            slot.HonkReady = 0f;
            slot.Heavy = car.TryGetComponent(out BoxCollider box) && Mathf.Max(box.size.x, box.size.z) > 7.5f;
            AudioClip heavy = EtsTruckAudio.HeavyEngineClip;
            bool diesel = slot.Heavy && heavy != null;
            slot.BaseRpm = diesel ? EtsTruckAudio.HeavyEngineRpm : CarClipRpm;
            Stop(slot.Engine);
            Stop(slot.Tyre);
            slot.Engine.clip = diesel ? heavy : s_engine;
            slot.Tyre.clip = s_tyre;
            slot.Holder.position = slot.Last + Vector3.up * 0.6f;
        }

        private void UpdateSlot(Slot slot, float dt, float master, float now)
        {
            if (ReferenceEquals(slot.Car, null))
            {
                return;
            }

            bool alive = slot.Car != null && slot.Car.gameObject.activeInHierarchy;

            if (!alive)
            {
                slot.Releasing = true;
            }

            slot.Fade = Mathf.MoveTowards(slot.Fade, slot.Releasing ? 0f : 1f, dt / (slot.Releasing ? 0.3f : 0.6f));

            if (slot.Releasing && slot.Fade <= 0f)
            {
                Stop(slot.Engine);
                Stop(slot.Tyre);
                slot.Car = null;
                slot.Releasing = false;
                return;
            }

            if (alive)
            {
                Vector3 p = slot.Car.position;
                float dx = p.x - slot.Last.x, dz = p.z - slot.Last.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                float inst = dist / dt;
                slot.Last = p;

                if (inst > 70f)
                {
                    // A pooled car shown somewhere else: start again from silence.
                    slot.Fade = 0f;
                    slot.Speed = -1f;
                    inst = 0f;
                }

                if (dist > 1e-4f && inst <= 70f)
                {
                    slot.Dir = new Vector3(dx / dist, 0f, dz / dist);
                }

                if (slot.Speed < 0f)
                {
                    slot.Speed = inst;
                    slot.PrevSpeed = inst;
                }

                slot.Speed += (inst - slot.Speed) * (1f - Mathf.Exp(-dt / 0.25f));
                float accel = (slot.Speed - slot.PrevSpeed) / dt;
                slot.PrevSpeed = slot.Speed;
                slot.Accel += (accel - slot.Accel) * (1f - Mathf.Exp(-dt / 0.3f));
                slot.Holder.position = p + Vector3.up * 0.6f;

                if (slot.Accel < -4.5f && slot.Speed > 4f && now >= _nextHonk && now >= slot.HonkReady)
                {
                    CheckCutOff(slot, now);
                }
            }

            float kmh = Mathf.Max(slot.Speed, 0f) * 3.6f;
            float rpm = GearRpm(slot, kmh);
            float load = Mathf.Clamp01(0.45f + slot.Accel * 0.25f);
            float engine = master * slot.Fade * (slot.Heavy ? 0.42f : 0.3f) * (0.5f + 0.5f * load);
            float tyre = master * slot.Fade * (slot.Heavy ? 0.5f : 0.4f) * Mathf.Pow(Mathf.Clamp01(kmh / 100f), 1.3f);
            Drive(slot.Engine, engine, Mathf.Clamp(rpm / slot.BaseRpm, 0.35f, 2.6f));
            Drive(slot.Tyre, tyre, 0.7f + 0.55f * Mathf.Clamp01(kmh / 120f));
        }

        /// <summary>Revs from speed through a simple gearbox with hysteresis: a car 850-3800 rpm, a heavy vehicle 600-1700.</summary>
        private static float GearRpm(Slot slot, float kmh)
        {
            float[] tops = slot.Heavy ? HeavyGearTops : CarGearTops;
            int last = tops.Length;

            while (slot.Gear < last && kmh > tops[slot.Gear - 1]) slot.Gear++;
            while (slot.Gear > 1 && kmh < tops[slot.Gear - 2] - 5f) slot.Gear--;

            float lo = slot.Gear == 1 ? 0f : tops[slot.Gear - 2] - 5f;
            float hi = slot.Gear == last ? (slot.Heavy ? 95f : 130f) : tops[slot.Gear - 1];
            float x = Mathf.Clamp01((kmh - lo) / Mathf.Max(1f, hi - lo));

            if (slot.Heavy)
            {
                return slot.Gear == 1 ? Mathf.Lerp(600f, 1700f, x) : Mathf.Lerp(1050f, 1700f, x);
            }

            return kmh < 2f ? 850f : slot.Gear == 1 ? Mathf.Lerp(850f, 3800f, x) : Mathf.Lerp(2000f, 3800f, x);
        }

        private static void Drive(AudioSource s, float volume, float pitch)
        {
            if (volume > 0.0008f)
            {
                s.volume = Mathf.Min(volume, 1f);
                s.pitch = pitch;

                if (!s.isPlaying && s.clip != null)
                {
                    s.time = Random.value * s.clip.length * 0.9f;
                    s.Play();
                }
            }
            else if (s.isPlaying)
            {
                s.Stop();
            }
        }

        /// <summary>
        /// A car braking hard (over 4.5 m/s^2) with the local player's truck or trailer within 32 m ahead of it (its travel
        /// within ~50 degrees of the line to it): the player cut it off. Three drivers in four honk; one horn every 12 s.
        /// </summary>
        private void CheckCutOff(Slot slot, float now)
        {
            EtsTruckRig rig = EtsLocalTruck.Occupied != null ? EtsLocalTruck.Occupied : EtsLocalTruck.Own;
            EtsTruckController truck = rig != null ? rig.Controller : null;

            if (truck == null || !truck.Placed)
            {
                return;
            }

            Vector3 car = slot.Car.position;
            float ahead = Ahead(slot, car, truck.transform.position);
            EtsTrailer trailer = truck.Trailer;

            if (trailer != null && trailer.Attached)
            {
                ahead = Mathf.Min(ahead, Ahead(slot, car, trailer.transform.position));
            }

            if (ahead > 32f)
            {
                return;
            }

            slot.HonkReady = now + 25f;

            if (Random.value > 0.75f || s_horn == null)
            {
                return;
            }

            _nextHonk = now + 12f;
            _hornCar = slot.Car;
            _horn.transform.position = car + Vector3.up;
            _horn.clip = s_horn;
            _horn.pitch = slot.Heavy ? 0.72f : Random.Range(0.95f, 1.12f);
            _horn.volume = Mathf.Clamp01(Volume * 2f * EtsTruckAudio.ListenerOutsideGain * 0.55f);
            _horn.Play();

            if (_honkLogs < 10)
            {
                _honkLogs++;
                Debug.Log("[SomniumEuroTruck] Car sound: a car braked at " + (-slot.Accel).ToString("0.0") + " m/s^2 " + ahead.ToString("0") + " m behind the truck and honked.");
            }
        }

        /// <summary>The distance to a point when it lies ahead of the car's travel, else infinity.</summary>
        private static float Ahead(Slot slot, Vector3 car, Vector3 target)
        {
            float dx = target.x - car.x, dz = target.z - car.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);

            if (d < 1f || (slot.Dir.x * dx + slot.Dir.z * dz) / d < 0.65f)
            {
                return float.MaxValue;
            }

            return d;
        }

        // ---------------------------------------------------------------- synthesis (worker thread, EtsSynth)

        private static void RequestClips()
        {
            if (s_requested)
            {
                return;
            }

            s_requested = true;
            int r = EtsSynth.AmbientRate;
            EtsSynth.Request("ets car engine", r, () => CarEngineWave(r), c => s_engine = c);
            EtsSynth.Request("ets car tyres", r, () => CarTyreWave(r), c => s_tyre = c);
            EtsSynth.Request("ets car horn", r, () => EtsAmbience.HornWave(r, 0x7C4Bu), c => s_horn = c);
        }

        /// <summary>A four-cylinder petrol engine at 1800 rpm (60 Hz firing), one second, seamless: every term has whole cycles.</summary>
        private static float[] CarEngineWave(int rate)
        {
            int n = rate;
            var rng = new EtsSynth.Rng(0xCA4E1u);
            float[] intake = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(900, rate));
            EtsSynth.Normalise(intake, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double p = 60.0 * j / rate;
                float s1 = EtsSynth.Sin(p);
                float e = s1 * 0.55f + EtsSynth.Sin(2.0 * p) * 0.28f + EtsSynth.Sin(3.0 * p) * 0.14f + EtsSynth.Sin(4.0 * p) * 0.06f + EtsSynth.Sin(0.5 * p) * 0.18f;
                float pulse = 0.5f + 0.5f * s1;
                pulse *= pulse;
                pulse *= pulse;
                e += intake[j] * pulse * 0.25f;
                d[j] = e / (1f + 0.5f * Mathf.Abs(e));
            }

            return d;
        }

        /// <summary>Tyres on asphalt: a low roar with a little hiss on top, two seconds.</summary>
        private static float[] CarTyreWave(int rate)
        {
            int n = rate * 2;
            var rng = new EtsSynth.Rng(0x7A2Eu);
            float[] white = EtsSynth.White(n, rng);
            float[] a = EtsSynth.Lowpass(white, EtsSynth.Coef(650, rate));
            float[] b = EtsSynth.Lowpass(white, EtsSynth.Coef(170, rate));
            float[] hi = EtsSynth.Highpass(EtsSynth.White(n, rng), EtsSynth.Coef(3000, rate));
            EtsSynth.Normalise(a, 1f);
            EtsSynth.Normalise(b, 1f);
            EtsSynth.Normalise(hi, 1f);
            var d = new float[n];
            for (int j = 0; j < n; j++) d[j] = a[j] * 0.6f + b[j] * 0.5f + hi[j] * 0.06f;
            return d;
        }
    }
}
