using System;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Truck damage as ETS2 does it (2026-10-04, the user: "Add the truck damage."). The rules and every number are the
    /// game's, read by EtsDamageRules from /def/damage_data.sii, /def/economy_data.sii and the truck's accessory files (see
    /// there for the evidence and the two values no file sets). This is the glue to the world:
    ///
    ///   - HITS. The local player's own truck and its coupled trailer, from their bodies' OnCollisionEnter
    ///     (EtsTruckController / EtsTrailer call TruckCollision / TrailerCollision). The impact speed is the relative
    ///     velocity along the contact normal; contacts under the box (the normal pushing up in the truck's frame: road,
    ///     kerb tops) are the wheels' business and skipped; a contact pushing down on it is the roof (roof_damage_factor),
    ///     one across it a side (side_damage_factor). A traffic car hit (dynamic, its own vehicle_mass) weighs
    ///     m_car / (m_truck + m_car); walls, buildings, parked or remote trucks weigh 1. The avatar, the VR hands and the UI
    ///     never count. Several colliders touched in one knock count once: within KnockSeconds only what exceeds the
    ///     knock's largest hit is added. Puppets (other players' trucks) and passengers never take damage here - each
    ///     player's own client damages their own truck.
    ///   - WEAR per game km (EtsJobs.AccountDriving hands over the km it already counts for the career).
    ///   - WHAT IT DOES. The HUD (the desktop status line and the VR dash plate): the game's damage widget's three figures
    ///     (truck, trailer, cargo - the adviser's damage group in /ui/adviser_gps.sii) whenever there is damage, and the
    ///     whole line "Damage: 5%  Trailer damage: 1%  Cargo damage: 1%" (locale damage / trailer_damage / cargo_damage) for
    ///     a moment after a hit changes it. Above the 10 % of cond.damage.core_max10, the adviser's engine / transmission
    ///     damage lamps (adv_hud_lgd_engine, adv_hud_lgd_transmission) and the malfunction: while driving, every
    ///     CheckSeconds a deterministic roll (chance (d - 10 %) / 90 %) stalls the engine (locale engine_malfunction) or lets
    ///     the gearbox slip (no drive for a moment). The rate is not in any file.
    ///   - DELIVERY. Cargo damage takes the game's toll off the pay and XP (EtsDamageRules.CargoPay / CargoXp) and grades the
    ///     delivery (reward_damage_level); EtsJobs.Deliver calls Deliver.
    ///   - REPAIR. The tablet's Vehicle Diagnostics (the game's truck.diag link, services category; EtsPageService) shows the
    ///     game's damage report and repairs every truck part for its price x damage (EtsProfileStore.Spend). The game repairs
    ///     only at a service station or own garage; this world has no service stations yet, so it repairs wherever the
    ///     truck stands still. A job's trailer is the company's: its damage counts for the cargo and goes with the trailer.
    ///   - SAVED with the career (EtsProfileRecord.Damage, an extra key a record without damage never writes).
    /// The game draws no deformation on a player's truck (no damage model or look in its truck files), so neither does this.
    /// </summary>
    public static class EtsDamage
    {
        /// <summary>Hits on one body this close together are one knock (several colliders of one wall).</summary>
        public const float KnockSeconds = 0.3f;

        /// <summary>Driving seconds between malfunction rolls (not in the files).</summary>
        public const float CheckSeconds = 20f;

        /// <summary>A stalled engine stays off at least this long (not in the files; a keyboard restarts it after).</summary>
        public const float StallSeconds = 2.5f;

        /// <summary>A slipping gearbox gives no drive for this long (not in the files).</summary>
        public const float SlipSeconds = 1.5f;

        private const int MaxHitLines = 60;

        private static EtsDamageRules _rules;
        private static readonly EtsDamageState _state = new EtsDamageState();

        private static float _truckKnockUntil, _trailerKnockUntil;
        private static double _truckKnockMax, _trailerKnockMax;
        private static EtsTrailer _trailer;
        private static EtsProfileStore _loadedFrom;
        private static bool _dirty;
        private static float _nextPush;
        private static float _nextHud;
        private static float _driveClock;
        private static int _checks;
        private static int _hitLines;
        private static bool _readyLogged;

        private static int[] _prices;
        private static string _pricesFor;
        private static string _pricesMissing = "";

        private static int _hudKey = int.MinValue;
        private static string _hudNote = "";
        private static string _sayLine = "";
        private static int _lastShownKey;

        /// <summary>The rules (EtsDamageRules.Load once the archives are mounted; the 1.61 values until then).</summary>
        public static EtsDamageRules Rules
        {
            get
            {
                if (_rules == null && EtsEngine.IsMounted)
                {
                    _rules = EtsDamageRules.Load(EtsEngine.Files);
                    Debug.Log("[SomniumEuroTruck] Damage: rules " + (_rules.FromGame ? "from /def/damage_data.sii" : "NOT read (1.61 values)") +
                              " - truck_damage_coef " + _rules.TruckCoef + ", side x" + _rules.SideFactor + ", roof x" + _rules.RoofFactor +
                              ", truck_to_trailer " + _rules.TruckToTrailer + "; cargo toll " + _rules.CargoDamageCost + " EUR + " + (_rules.CargoDamageCostFactor * 100f) + " % per 1 %" +
                              (_rules.EconomyFromGame ? " (economy_data.sii)" : " (1.61 values)") + "; malfunctions above " + (_rules.MalfunctionLimit * 100f).ToString("0") + " %.");
                }

                return _rules ?? (_fallback ?? (_fallback = new EtsDamageRules()));
            }
        }

        private static EtsDamageRules _fallback;

        /// <summary>The local player's truck, trailer and cargo condition.</summary>
        public static EtsDamageState State => _state;

        /// <summary>
        /// The damage figures for the status line and the VR dash plate ("   Damage 5%  Trailer damage 1%  Cargo damage 1%",
        /// with the engine / transmission lamp words above the malfunction limit), "" while there is nothing to show. Rebuilt
        /// only when a whole percent or a lamp changes: reading it allocates nothing.
        /// </summary>
        public static string HudNote => _hudNote;

        /// <summary>The own truck, driven by this client (not a puppet, not a passenger's ride).</summary>
        public static bool IsOwn(EtsTruckController truck)
        {
            if (truck == null || !truck.Driven || truck.Puppet)
            {
                return false;
            }

            EtsTruckRig own = EtsLocalTruck.Own;
            return own == null || ReferenceEquals(own.Controller, truck);
        }

        // ------------------------------------------------------------------ hits

        /// <summary>EtsTruckController.OnCollisionEnter.</summary>
        public static void TruckCollision(EtsTruckController truck, Collision collision)
        {
            if (!IsOwn(truck) || collision == null)
            {
                return;
            }

            Rigidbody body = truck.Body;

            if (body == null || body.isKinematic)
            {
                return;
            }

            EtsTrailer trailer = truck.Trailer;
            Rigidbody trailerBody = trailer != null ? trailer.Body : null;

            if (!Measure(collision, truck.transform, body, trailerBody, out float speed, out int side, out double weight))
            {
                return;
            }

            EtsDamageRules r = Rules;
            double d = Knock(ref _truckKnockUntil, ref _truckKnockMax, r.HitDamage(false, speed, weight, side));

            if (d <= 0.0)
            {
                return;
            }

            bool coupled = trailer != null && trailer.Attached;
            Track(trailer);
            _state.HitTruck(r, d, coupled, coupled && CarriesCargo(trailer));
            Hit("truck", truck, collision.collider, speed, side, weight, d);
        }

        /// <summary>EtsTrailer.OnCollisionEnter: the own truck's coupled trailer only.</summary>
        public static void TrailerCollision(EtsTrailer trailer, Collision collision)
        {
            if (trailer == null || collision == null || trailer.IsPuppet || !trailer.Attached || !IsOwn(trailer.Truck))
            {
                return;
            }

            Rigidbody body = trailer.Body;

            if (body == null || body.isKinematic)
            {
                return;
            }

            if (!Measure(collision, trailer.transform, body, trailer.Truck.Body, out float speed, out int side, out double weight))
            {
                return;
            }

            EtsDamageRules r = Rules;
            double d = Knock(ref _trailerKnockUntil, ref _trailerKnockMax, r.HitDamage(true, speed, weight, side));

            if (d <= 0.0)
            {
                return;
            }

            Track(trailer);
            _state.HitTrailer(r, d, true, CarriesCargo(trailer));
            Hit("trailer", trailer.Truck, collision.collider, speed, side, weight, d);
        }

        /// <summary>
        /// The hit's impact speed (m/s along the contact normal), side (0 front/back, 1 side, 2 roof) and the other body's
        /// weight. False for what never damages: the avatar, the hands, the UI, the own truck/trailer pair, contacts only
        /// under the box. No allocation (GetContact).
        /// </summary>
        private static bool Measure(Collision c, Transform self, Rigidbody body, Rigidbody partner, out float speed, out int side, out double weight)
        {
            speed = 0f;
            side = 0;
            weight = 1.0;
            Collider other = c.collider;

            if (other == null)
            {
                return false;
            }

            int layer = other.gameObject.layer;

            if (layer == EtsLayers.IgnoreRaycast || layer == EtsLayers.Ui || layer == EtsLayers.PhysicalObject || layer == EtsLayers.Player)
            {
                return false;
            }

            Rigidbody otherBody = c.rigidbody;

            if (otherBody != null && (otherBody == body || (partner != null && otherBody == partner)))
            {
                return false;
            }

            Vector3 rel = c.relativeVelocity;
            float bestScore = 0f;
            int n = c.contactCount;

            for (int i = 0; i < n; i++)
            {
                ContactPoint p = c.GetContact(i);
                Vector3 normal = p.normal;
                Vector3 local = self.InverseTransformDirection(normal);

                // Pushed up from below: the underside on a kerb or a step - the wheels' business, not a hit.
                if (local.y > 0.7f)
                {
                    continue;
                }

                int s = local.y < -0.7f ? 2 : Mathf.Abs(local.x) > Mathf.Abs(local.z) ? 1 : 0;
                float v = Mathf.Abs(Vector3.Dot(rel, normal));
                float score = v * v * Rules.DirectionFactor(s);

                if (score > bestScore)
                {
                    bestScore = score;
                    speed = v;
                    side = s;
                }
            }

            if (bestScore <= 0f)
            {
                return false;
            }

            weight = otherBody != null && !otherBody.isKinematic ? EtsDamageRules.Weight(body.mass, otherBody.mass) : 1.0;
            return true;
        }

        private static double Knock(ref float until, ref double largest, double d)
        {
            float now = Time.time;

            if (now < until)
            {
                if (d <= largest)
                {
                    return 0.0;
                }

                double extra = d - largest;
                largest = d;
                return extra;
            }

            until = now + KnockSeconds;
            largest = d;
            return d;
        }

        private static readonly string[] SideNames = { "front/back", "side", "roof" };

        private static void Hit(string what, EtsTruckController truck, Collider other, float speed, int side, double weight, double d)
        {
            _dirty = true;
            _nextPush = 0f;
            RefreshHud();

            if (_hitLines < MaxHitLines && d >= 0.0005)
            {
                _hitLines++;
                Debug.Log("[SomniumEuroTruck] Damage: " + what + " hit " + (other != null ? other.name + " (layer " + other.gameObject.layer + ")" : "?") +
                          " on its " + SideNames[side] + " at " + speed.ToString("0.0") + " m/s (" + (speed * 3.6f).ToString("0") + " km/h) along the normal, weight " +
                          weight.ToString("0.00") + " -> +" + (d * 100.0).ToString("0.00") + " %; truck " + Pct(_state.TruckShown) + ", trailer " + Pct(_state.TrailerShown) +
                          ", cargo " + Pct(_state.Cargo) + (_hitLines == MaxHitLines ? " (the last hit line this visit)" : "") + ".");
            }

            int key = ShownKey();

            if (key != _lastShownKey)
            {
                _lastShownKey = key;
                _sayLine = Word("damage", "Damage") + ": " + Pct(_state.TruckShown) +
                           (_trailer != null ? "   " + Word("trailer_damage", "Trailer damage") + ": " + Pct(_state.TrailerShown) : "") +
                           (_trailer != null && CarriesCargo(_trailer) ? "   " + Word("cargo_damage", "Cargo damage") + ": " + Pct(_state.Cargo) : "");

                if (truck != null)
                {
                    truck.Say(_sayLine);
                }
            }
        }

        private static string Pct(double f)
        {
            return Mathf.RoundToInt((float)(f * 100.0)) + "%";
        }

        private static int ShownKey()
        {
            return Mathf.RoundToInt((float)(_state.TruckShown * 100.0)) * 10201 + Mathf.RoundToInt((float)(_state.TrailerShown * 100.0)) * 101 + Mathf.RoundToInt((float)(_state.Cargo * 100.0));
        }

        /// <summary>Whether this trailer holds the job's cargo (EtsJobs: a job running with this trailer).</summary>
        private static bool CarriesCargo(EtsTrailer trailer)
        {
            EtsJobs jobs = EtsJobs.Instance;
            return trailer != null && jobs != null && jobs.Current != null && ReferenceEquals(jobs.Trailer, trailer);
        }

        /// <summary>A new trailer (a job's) starts without damage, and so does its load.</summary>
        private static void Track(EtsTrailer trailer)
        {
            if (trailer == null || ReferenceEquals(trailer, _trailer))
            {
                return;
            }

            _trailer = trailer;
            _state.NewTrailer();
            RefreshHud();
            Debug.Log("[SomniumEuroTruck] Damage: a new trailer (" + trailer.Description + ") - trailer and cargo damage start at 0 %.");
        }

        // ------------------------------------------------------------------ wear, malfunctions, the HUD, the career

        /// <summary>EtsJobs.AccountDriving: game km driven by the own truck (world metres x the map scale, as the career counts).</summary>
        public static void AddWear(double gameKm, EtsTruckController truck)
        {
            if (!(gameKm > 0.0) || gameKm > 10.0 || !IsOwn(truck))
            {
                return;
            }

            EtsTrailer trailer = truck.Trailer;
            Track(trailer);
            _state.Wear(Rules, gameKm, trailer != null && trailer.Attached);
            _dirty = true;
        }

        /// <summary>EtsTruckController.Update, the own truck while this client drives it: the career's figures, malfunctions, the HUD.</summary>
        public static void Tick(EtsTruckController truck)
        {
            if (!IsOwn(truck) || !truck.Placed)
            {
                return;
            }

            if (!_readyLogged && EtsEngine.IsMounted)
            {
                _readyLogged = true;
                EtsDamageRules r = Rules;
                Debug.Log("[SomniumEuroTruck] Damage: ready on " + (truck.Spec != null ? truck.Spec.TruckId : "the truck") + " - truck " + Pct(_state.TruckShown) +
                          " (parts cabin/chassis/engine/transmission/wheels " + Parts() + "), hits from collisions, wear per game km, malfunctions above " +
                          (r.MalfunctionLimit * 100f).ToString("0") + " %.");
            }

            NoteTruck(truck);
            LoadFromCareer(truck);
            Track(truck.Trailer);
            Malfunctions(truck);

            // Into the career's record (saved by its next commit) - only once the saved figures have been read, so a visit's
            // first kilometres never overwrite them.
            EtsProfileStore career = EtsProfileStore.Current;

            if (_dirty && Time.unscaledTime >= _nextPush && career != null && ReferenceEquals(career, _loadedFrom))
            {
                _dirty = false;
                _nextPush = Time.unscaledTime + 10f;
                career.SetDamage(_state.Encode());
            }

            // The note follows wear and the job's trailer 4 times a second (rebuilt only when a whole percent or a lamp changes).
            if (Time.unscaledTime >= _nextHud)
            {
                _nextHud = Time.unscaledTime + 0.25f;
                RefreshHud();
            }
        }

        private static string Parts()
        {
            return Pct(_state.TruckPart(0)) + "/" + Pct(_state.TruckPart(1)) + "/" + Pct(_state.TruckPart(2)) + "/" + Pct(_state.TruckPart(3)) + "/" + Pct(_state.TruckPart(4));
        }

        // The truck model the numbers are for: another model (Company - another truck) is another truck, like new.
        private static void NoteTruck(EtsTruckController truck)
        {
            string id = truck.Spec != null ? truck.Spec.TruckId ?? "" : "";

            if (id.Length == 0 || id == _state.TruckId)
            {
                return;
            }

            if (_state.TruckId.Length > 0 && _state.Encode().Length > 0)
            {
                Debug.Log("[SomniumEuroTruck] Damage: another truck (" + _state.TruckId + " -> " + id + ") - its condition starts new.");

                for (int i = 0; i < _state.Truck.Length; i++)
                {
                    _state.Truck[i] = 0.0;
                    _state.TruckUnfixable[i] = 0.0;
                }

                _dirty = true;
                _nextPush = 0f;
            }

            _state.TruckId = id;
            _prices = null;
            RefreshHud();
        }

        // Once per career store (a visit): the saved condition of this truck, kept with anything this visit already did.
        private static void LoadFromCareer(EtsTruckController truck)
        {
            EtsProfileStore store = EtsProfileStore.Current;

            // Not before the truck's model is known: the saved figures belong to one model (EtsDamageState.TruckId).
            if (store == null || ReferenceEquals(store, _loadedFrom) || !store.IsReady || store.Profile == null || _state.TruckId.Length == 0)
            {
                return;
            }

            _loadedFrom = store;
            _hitLines = 0;
            string saved = store.SavedDamage;
            var loaded = new EtsDamageState();

            if (saved.Length > 0 && loaded.TryDecode(saved) && (loaded.TruckId.Length == 0 || loaded.TruckId == _state.TruckId))
            {
                for (int i = 0; i < _state.Truck.Length; i++)
                {
                    _state.Truck[i] = Math.Max(_state.Truck[i], loaded.Truck[i]);
                    _state.TruckUnfixable[i] = Math.Max(_state.TruckUnfixable[i], loaded.TruckUnfixable[i]);
                }

                Debug.Log("[SomniumEuroTruck] Damage: the career's truck condition loaded - " + Pct(_state.TruckShown) + " (cabin/chassis/engine/transmission/wheels " + Parts() + ").");
            }
            else
            {
                Debug.Log("[SomniumEuroTruck] Damage: the career has no saved damage for " + _state.TruckId + (saved.Length > 0 ? " (it holds another truck's: " + saved.Split('|')[Math.Min(1, saved.Split('|').Length - 1)] + ")" : "") + " - " + Pct(_state.TruckShown) + ".");
            }

            _dirty = true;
            _nextPush = 0f;
            RefreshHud();
        }

        private static void Malfunctions(EtsTruckController truck)
        {
            if (!truck.EngineRunning || truck.SpeedKmh < 10f || truck.Throttle < 0.05f)
            {
                return;
            }

            _driveClock += Time.deltaTime;

            if (_driveClock < CheckSeconds)
            {
                return;
            }

            _driveClock = 0f;
            _checks++;
            EtsDamageRules r = Rules;

            if (Roll(EtsDamageRules.Engine, r))
            {
                double d = _state.TruckPart(EtsDamageRules.Engine);
                Debug.Log("[SomniumEuroTruck] Damage: engine malfunction at " + Pct(d) + " engine damage - the engine stalls at " + truck.SpeedKmh.ToString("0") + " km/h.");
                truck.Malfunction(true, StallSeconds, Plain(Word("engine_malfunction", "Vehicle malfunction! Check vehicle condition or consider visiting a service.")));
            }
            else if (Roll(EtsDamageRules.Transmission, r))
            {
                double d = _state.TruckPart(EtsDamageRules.Transmission);
                Debug.Log("[SomniumEuroTruck] Damage: transmission malfunction at " + Pct(d) + " transmission damage - no drive for " + SlipSeconds + " s.");
                truck.Malfunction(false, SlipSeconds, Plain(Word("engine_malfunction", "Vehicle malfunction! Check vehicle condition or consider visiting a service.")));
            }
        }

        // Deterministic (no System.Random): the roll is a hash of the check count, the part and its damage.
        private static bool Roll(int part, EtsDamageRules r)
        {
            double d = _state.TruckPart(part);
            double limit = r.MalfunctionLimit;

            if (d <= limit)
            {
                return false;
            }

            double chance = (d - limit) / Math.Max(1e-6, 1.0 - limit);
            uint h = (uint)_checks * 2654435761u ^ (uint)(part + 1) * 2246822519u ^ (uint)(d * 1e6) * 3266489917u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / 16777216.0 < chance;
        }

        private static string Plain(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("<br>", " ").Replace("<br/>", " ");
        }

        /// <summary>The game's words for a key, the English fallback before the locale is read.</summary>
        public static string Word(string key, string fallback)
        {
            if (EtsLocale.Loaded && EtsLocale.TryWord(key, out string w) && !string.IsNullOrEmpty(w))
            {
                return EtsLocale.Word(key);
            }

            return fallback;
        }

        private static void RefreshHud()
        {
            EtsDamageRules r = Rules;
            int t = Mathf.RoundToInt((float)(_state.TruckShown * 100.0));
            int tr = _trailer != null ? Mathf.RoundToInt((float)(_state.TrailerShown * 100.0)) : 0;
            int c = _trailer != null && CarriesCargo(_trailer) ? Mathf.RoundToInt((float)(_state.Cargo * 100.0)) : 0;
            bool engine = _state.TruckPart(EtsDamageRules.Engine) > r.MalfunctionLimit;
            bool gearbox = _state.TruckPart(EtsDamageRules.Transmission) > r.MalfunctionLimit;
            int key = t * 1030301 + tr * 10201 + c * 101 + (engine ? 1 : 0) + (gearbox ? 2 : 0) + 4;

            if (key == _hudKey)
            {
                return;
            }

            _hudKey = key;

            if (t <= 0 && tr <= 0 && c <= 0 && !engine && !gearbox)
            {
                _hudNote = "";
                return;
            }

            _hudNote = "   " + Word("damage", "Damage") + " " + t + "%" +
                       (tr > 0 ? "  " + Word("trailer_damage", "Trailer damage") + " " + tr + "%" : "") +
                       (c > 0 ? "  " + Word("cargo_damage", "Cargo damage") + " " + c + "%" : "") +
                       (engine ? "  " + Word("adv_hud_lgd_engine", "Engine Damage").ToUpperInvariant() : "") +
                       (gearbox ? "  " + Word("adv_hud_lgd_transmission", "Transmission Damage").ToUpperInvariant() : "");
        }

        // ------------------------------------------------------------------ delivery and repair

        /// <summary>
        /// EtsJobs.Deliver, before the career is paid: the cargo damage's toll on the job's pay and XP (economy_data.sii), the
        /// grade (reward_damage_level), the line for the driver ("" without damage). The trailer goes with the delivery.
        /// </summary>
        public static string Deliver(EtsJobs.Offer job)
        {
            if (job == null)
            {
                return "";
            }

            EtsDamageRules r = Rules;
            double cargo = _state.Cargo;
            int pay = r.CargoPay(job.Reward, cargo);
            int xp = r.CargoXp(job.Xp, cargo);
            int grade = r.Rating(cargo);
            string gradeName = Plain(EtsLocale.Loaded ? EtsLocale.Resolve(r.RewardName[grade]) : r.RewardName[grade].Trim('@'));
            Debug.Log("[SomniumEuroTruck] Damage: delivered with cargo damage " + (cargo * 100.0).ToString("0.00") + " % - pay " + job.Reward + " -> " + pay +
                      " EUR, XP " + job.Xp + " -> " + xp + ", grade " + gradeName + " (reward_damage_level " + r.RewardDamageLevel[grade] + " %).");
            string line = cargo > 0.0 ? Word("cargo_damage", "Cargo damage") + " " + Pct(cargo) + ": -" + (job.Reward - pay).ToString("N0") + " EUR (" + gradeName + "). " : "";
            job.Reward = pay;
            job.Xp = xp;
            _state.NewTrailer();
            _trailer = null;
            _dirty = true;
            RefreshHud();
            return line;
        }

        /// <summary>The truck's five part prices (cabin, chassis, engine, transmission, wheels), read once per truck model.</summary>
        public static int[] Prices(EtsTruckController truck)
        {
            EtsVehicleSpec spec = truck != null ? truck.Spec : null;

            if (spec == null || !EtsEngine.IsMounted)
            {
                return _prices;
            }

            if (_prices == null || _pricesFor != spec.TruckId)
            {
                _prices = EtsDamageRules.TruckPartPrices(EtsEngine.Files, spec.Folder, spec.ChassisPath, spec.EnginePath, spec.TransmissionPath, out _pricesMissing);
                _pricesFor = spec.TruckId;
                Debug.Log("[SomniumEuroTruck] Damage: part prices of " + spec.TruckId + " cabin/chassis/engine/transmission/wheels " + string.Join("/", Array.ConvertAll(_prices, p => p.ToString())) +
                          " EUR" + (_pricesMissing.Length > 0 ? " (not found: " + _pricesMissing + ")" : "") + "; a repair costs price x damage.");
            }

            return _prices;
        }

        /// <summary>The own truck when there is one (the rig's controller, else the menu's).</summary>
        public static EtsTruckController OwnTruck(EtsMenu menu)
        {
            EtsTruckRig own = EtsLocalTruck.Own;

            if (own != null && own.Controller != null)
            {
                return own.Controller;
            }

            return menu != null ? menu.Truck : null;
        }

        /// <summary>
        /// The service's Repair all: every truck part's damage, for its price, out of the career's money. False with the
        /// reason when it cannot (moving, nothing to repair, no career open, not enough money).
        /// </summary>
        public static bool Repair(EtsTruckController truck, out string message)
        {
            if (truck == null || truck.Puppet)
            {
                message = "Choose your truck first (Company)"; // not in the locale
                return false;
            }

            if (truck.SpeedKmh > 1f)
            {
                message = "Stop the truck to have it repaired."; // not in the locale
                return false;
            }

            long cost = _state.TruckRepairCost(Rules, Prices(truck));

            if (cost <= 0)
            {
                message = Word("total_restoration_no_need", "Your vehicle is in perfect condition.");
                return false;
            }

            EtsProfileStore store = EtsProfileStore.Current;

            if (store == null || !store.IsReady || store.Profile == null || !ReferenceEquals(store, _loadedFrom))
            {
                message = "Your career is still opening - a moment ..."; // not in the locale
                return false;
            }

            var before = new double[_state.Truck.Length];
            Array.Copy(_state.Truck, before, before.Length);
            _state.RepairTruck();
            store.SetDamage(_state.Encode()); // in the record before Spend commits it

            if (!store.Spend(cost, "truck repair"))
            {
                Array.Copy(before, _state.Truck, before.Length);
                store.SetDamage(_state.Encode());
                message = "Not enough money: the repair costs " + cost.ToString("N0") + " EUR, you have " + store.Money.ToString("N0") + " EUR."; // not in the locale
                Debug.Log("[SomniumEuroTruck] Damage: repair refused - " + cost + " EUR, balance " + store.Money + " EUR.");
                return false;
            }

            _dirty = false;
            _hudKey = int.MinValue;
            RefreshHud();
            message = "Repaired: " + cost.ToString("N0") + " EUR."; // not in the locale
            Debug.Log("[SomniumEuroTruck] Damage: repaired the truck for " + cost + " EUR (balance " + store.Money + " EUR); wear kept " + Parts() + ".");
            truck.Say(message);
            return true;
        }
    }

    /// <summary>
    /// The game's Vehicle Diagnostics (link truck.diag in the services category, /ui/desc/truck.sui: title truck_diag_t,
    /// the service screen /ui/truck_service.sii): the VEHICLE DAMAGE REPORT with the game's rows (engine, transmission,
    /// chassis, cabin, wheels) and columns (Wear, Damage, Cost), the TRAILER DAMAGE REPORT, the warning, "Total to pay:",
    /// "Your money:" and Repair all (EtsDamage.Repair).
    /// </summary>
    public sealed class EtsPageService : EtsTabletPage
    {
        public const string PageId = "Service";

        public override string Caption => EtsDamage.Word("truck_diag_t", "VEHICLE DIAGNOSTICS");
        public override string Icon => "/material/ui/link/truck_service.mat";
        public override string Id => PageId;
        public override string Category => "services";
        public override string ScreenLink => "truck.diag";

        private static readonly string[] PartKeys = { "cabin", "chassis", "engine", "transmission", "wheels" };
        private static readonly string[] PartEnglish = { "Cabin", "Chassis", "Engine", "Transmission", "Wheels" };

        private readonly TextMeshProUGUI[] _wear = new TextMeshProUGUI[5];
        private readonly TextMeshProUGUI[] _damage = new TextMeshProUGUI[5];
        private readonly TextMeshProUGUI[] _cost = new TextMeshProUGUI[5];
        private TextMeshProUGUI _trailer, _warning, _total, _money, _result;
        private Button _repair;
        private long _shownKey = long.MinValue;

        protected override void Build()
        {
            const float l = 270f, r = 1170f, b = 100f, t = 830f;
            EtsTabletApp.Window(Root, Caption, l, r, b, t);
            Color sel = EtsTabletApp.Colour("clr_sel", EtsTabletApp.SelectedText);
            Color normal = EtsTabletApp.Colour("clr_txt", EtsTabletApp.NormalText);
            Color dim = EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText);

            float y = t - 100f;
            EtsTabletApp.Text(EtsGameUi.Place(Root, "vehicle report", l + 30f, r - 30f, y, y + 36f), EtsDamage.Word("vehicle_service_vehicle_t", "VEHICLE DAMAGE REPORT"), EtsFont.Big, TextAlignmentOptions.Left, sel);
            y -= 44f;

            // Columns: part | Wear | Damage | Cost (the game's headers, /ui/truck_service.sii).
            float c0 = l + 30f, c1 = l + 330f, c2 = l + 500f, c3 = l + 670f, c4 = r - 30f;
            EtsTabletApp.Text(EtsGameUi.Place(Root, "h wear", c1, c2, y, y + 32f), EtsDamage.Word("wear_unfixable", "Wear"), EtsFont.Normal, TextAlignmentOptions.Center, dim);
            EtsTabletApp.Text(EtsGameUi.Place(Root, "h damage", c2, c3, y, y + 32f), EtsDamage.Word("wear", "Damage"), EtsFont.Normal, TextAlignmentOptions.Center, dim);
            EtsTabletApp.Text(EtsGameUi.Place(Root, "h cost", c3, c4, y, y + 32f), EtsDamage.Word("truck_diag_cost", "Cost"), EtsFont.Normal, TextAlignmentOptions.Right, dim);

            for (int k = 0; k < EtsDamageRules.ServiceOrder.Length; k++)
            {
                int part = EtsDamageRules.ServiceOrder[k];
                y -= 44f;
                EtsTabletApp.Text(EtsGameUi.Place(Root, "part " + PartKeys[part], c0, c1, y, y + 36f), EtsDamage.Word(PartKeys[part], PartEnglish[part]), EtsFont.Normal, TextAlignmentOptions.Left, normal);
                _wear[part] = EtsTabletApp.Text(EtsGameUi.Place(Root, "wear " + PartKeys[part], c1, c2, y, y + 36f), "", EtsFont.Normal, TextAlignmentOptions.Center, normal);
                _damage[part] = EtsTabletApp.Text(EtsGameUi.Place(Root, "damage " + PartKeys[part], c2, c3, y, y + 36f), "", EtsFont.Normal, TextAlignmentOptions.Center, normal);
                _cost[part] = EtsTabletApp.Text(EtsGameUi.Place(Root, "cost " + PartKeys[part], c3, c4, y, y + 36f), "", EtsFont.Normal, TextAlignmentOptions.Right, normal);
            }

            y -= 64f;
            EtsTabletApp.Text(EtsGameUi.Place(Root, "trailer report", l + 30f, r - 30f, y, y + 36f), EtsDamage.Word("truck_service_trailer_t", "TRAILER DAMAGE REPORT"), EtsFont.Big, TextAlignmentOptions.Left, sel);
            y -= 44f;
            _trailer = EtsTabletApp.Text(EtsGameUi.Place(Root, "trailer", l + 30f, r - 30f, y, y + 36f), "", EtsFont.Normal, TextAlignmentOptions.Left, normal);
            y -= 52f;
            _warning = EtsTabletApp.Text(EtsGameUi.Place(Root, "warning", l + 30f, r - 30f, y, y + 36f), "", EtsFont.Normal, TextAlignmentOptions.Left, sel);
            y -= 52f;
            _total = EtsTabletApp.Text(EtsGameUi.Place(Root, "total", l + 30f, l + 450f, y, y + 36f), "", EtsFont.Big, TextAlignmentOptions.Left, normal);
            _money = EtsTabletApp.Text(EtsGameUi.Place(Root, "money", l + 460f, r - 30f, y, y + 36f), "", EtsFont.Normal, TextAlignmentOptions.Right, dim);
            _repair = App.Button(Root, EtsDamage.Word("truck_service_repair_all", "Repair all"), r - 330f, r - 30f, b + 30f, b + 80f, Repair);
            _result = EtsTabletApp.Text(EtsGameUi.Place(Root, "result", l + 30f, r - 350f, b + 30f, b + 80f), "", EtsFont.Normal, TextAlignmentOptions.Left, sel);
            _shownKey = long.MinValue;
        }

        private void Repair()
        {
            EtsTruckController truck = EtsDamage.OwnTruck(App.Menu);
            bool ok = EtsDamage.Repair(truck, out string message);

            if (_result != null)
            {
                _result.text = message;
            }

            if (!ok)
            {
                App.Toast(message);
            }

            _shownKey = long.MinValue;
            Tick();
        }

        public override void Tick()
        {
            EtsTruckController truck = EtsDamage.OwnTruck(App != null ? App.Menu : null);
            EtsDamageState s = EtsDamage.State;
            EtsDamageRules rules = EtsDamage.Rules;
            int[] prices = EtsDamage.Prices(truck);
            long cost = s.TruckRepairCost(rules, prices);
            EtsProfileStore store = EtsProfileStore.Current;
            long money = store != null ? store.Money : 0;

            // Rewritten only when a shown figure changes (tenths of a percent, the cost, the balance).
            long key = cost * 31 + money * 17;

            for (int i = 0; i < 5; i++)
            {
                key = key * 31 + (long)Math.Round(s.Truck[i] * 1000.0) * 7 + (long)Math.Round(s.TruckUnfixable[i] * 1000.0);
            }

            key = key * 31 + (long)Math.Round(s.TrailerShown * 1000.0) * 7 + (long)Math.Round(s.Cargo * 1000.0);

            if (key == _shownKey || _total == null)
            {
                return;
            }

            _shownKey = key;
            bool poor = false, any = false;

            for (int i = 0; i < 5; i++)
            {
                if (_wear[i] == null) continue;
                _wear[i].text = Pct(s.TruckUnfixable[i]);
                _damage[i].text = Pct(s.Truck[i]);
                _cost[i].text = prices == null ? "-" : Math.Round(s.PartRepairCost(rules, prices, i)).ToString("N0") + " EUR";
                poor |= s.TruckPart(i) > rules.MalfunctionLimit;
                any |= s.Truck[i] > 0.0005;
            }

            _trailer.text = EtsDamage.Word("trailer_damage", "Trailer damage") + ": " + Pct(s.TrailerShown) + "     " + EtsDamage.Word("cargo_damage", "Cargo damage") + ": " + Pct(s.Cargo);
            _warning.text = poor ? EtsDamage.Word("truck_diag_warning", "Your vehicle is in poor condition. Service it as soon as possible.") :
                            any ? EtsDamage.Word("truck_diag_warning_minor_dmg", "You can repair your vehicle at the closest service station.") : "";
            _total.text = EtsDamage.Word("total", "Total to pay:") + " " + cost.ToString("N0") + " EUR";
            _money.text = EtsDamage.Word("your_money", "Your money:") + " " + money.ToString("N0") + " EUR";
            EtsTabletApp.SetUsable(_repair, cost > 0 && store != null && store.Money >= cost && truck != null && truck.SpeedKmh <= 1f, true);
        }

        private static string Pct(double f)
        {
            return (f * 100.0).ToString(f > 0.0 && f < 0.1 ? "0.0" : "0") + "%";
        }
    }
}
