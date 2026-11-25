using System;
using System.Collections.Generic;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.LocalStorage;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The local player's career, kept between visits: money, experience, totals, the HQ city they last chose as
    /// master, the truck they last took, and their last ten deliveries (owner G; [persist §5.6], PLAN.md §3.8, §7.G).
    /// Per Somnium ACCOUNT (Bridge LocalPlayer.Properties.Id, never a Fusion PlayerId, PLAN C17), per PC: nothing
    /// leaves this machine.
    ///
    /// Kept in Somnium's world storage, SomniumBridge.LocalStorage - the user's own choice ("For money storage, we
    /// have local storage in SDK on the bridge", PLAN §12) and the Bridge's only storage API [persist §2.1] - as two
    /// check-summed files, so a crash mid-write cannot lose it: ets_profile_&lt;id&gt;.txt and ets_profile_&lt;id&gt;_2.txt in
    /// %LOCALAPPDATA%\Temp\Somnium Space Ltd\Somnium Space VR\WorldsLocalStorage\12510. That folder is under %TEMP%,
    /// and Storage Sense on this PC is set to clean temporary files [persist §2.1, measured]; the optional PlayerPrefs
    /// copy (MirrorToPlayerPrefs) would survive that, but the user kept it off (Q-G1, PLAN §12). The copy with the
    /// highest generation wins on load and is written back to the others. In the SDK editor (no Bridge storage) the
    /// career lives in memory for the session.
    ///
    /// Jobs are not shared, so each client pays itself: EtsJobs calls Credit on its own delivery, once per job id.
    /// Nothing here is networked.
    ///
    /// Lives on 'Ets Net' (EtsNet.Awake with the new flow; EtsJobs.Start adds it there before that, so money is kept
    /// from Phase 1 on). Not DontDestroyOnLoad: it must go when the player leaves the world. Statics survive a
    /// re-entry (the client keeps the world assembly loaded, [persist §2.3]), so Current is cleared in OnDestroy and
    /// the record is re-read every visit; nothing about the player is held in a static.
    /// </summary>
    public sealed class EtsProfileStore : MonoBehaviour
    {
        public enum StoreState
        {
            /// <summary>The Bridge has not yet said who the local player is. Credits are queued.</summary>
            WaitingForIdentity,

            /// <summary>Loaded; changes are saved as they happen.</summary>
            Ready,

            /// <summary>A newer build's record is stored: shown, never written.</summary>
            ReadOnly
        }

        private sealed class PendingCredit
        {
            public string JobId;
            public long Money;
            public long Xp;
            public string Summary;
        }

        public static EtsProfileStore Current { get; private set; }

        [Tooltip("Seconds between saves of free-roam distance and experience while driving. Deliveries, HQ and truck choices save at once.")]
        public float AutosaveSeconds = 60f;

        [Tooltip("In the editor, where the Bridge has no player, use this account id after two seconds.")]
        public string EditorUserId = "editor";

        [Tooltip("Also keep a copy in PlayerPrefs (the Somnium client's registry key). Protects the career from Windows " +
                 "emptying %TEMP% and from the world being uploaded under a new id. Off: the user's choice (Q-G1).")]
        public bool MirrorToPlayerPrefs;

        public StoreState State { get; private set; } = StoreState.WaitingForIdentity;
        public string UserId { get; private set; } = "";
        public string NickName { get; private set; } = "";

        /// <summary>The loaded record. Read it; change it only through the methods here. Null until the profile is open.</summary>
        public EtsProfileRecord Profile => _book != null ? _book.Record : null;

        /// <summary>Open (Ready or ReadOnly): money, level and the log are the player's own.</summary>
        public bool IsReady => State != StoreState.WaitingForIdentity;

        /// <summary>The balance; the game's starting €2,000 until the profile is open.</summary>
        public long Money => _book != null ? _book.Record.Money : EtsProfileCodec.StartingMoney;

        public long Experience => _book != null ? _book.Record.Experience : 0;
        public int Level => EtsXpTable.LevelFor(Experience);

        /// <summary>How far into the current level, 0..1 (the XP bar).</summary>
        public float LevelProgress => EtsXpTable.Progress(Experience);

        /// <summary>Everything driven in this world, in the game's km (the record keeps game metres; EtsJobs.AccountDriving), for the Career page.</summary>
        public double DistanceKm => _book != null ? _book.Record.DistanceMetres / 1000.0 : 0.0;

        /// <summary>The delivery log, newest first: "cargo|from|to|km|money|xp|unix" (EtsProfileCodec.TryParseDelivery reads one).</summary>
        public IReadOnlyList<string> Deliveries => _newestFirst;

        /// <summary>Anything shown changed (money, level, HQ, truck, the log) or the profile finished loading.</summary>
        public event Action Changed;

        /// <summary>Something the player should be told once ("your saved career was damaged ...").</summary>
        public event Action<string> Notice;

        private EtsProfileBook _book;
        private readonly List<PendingCredit> _pending = new List<PendingCredit>();
        private readonly List<string> _newestFirst = new List<string>();
        private float _awakeAt;
        private float _nextPoll;
        private float _nextAutosave;
        private bool _xpTableLoaded;

        /// <summary>The store on this object, added if missing (PLAN §3.2). Unity's null test, not ??.</summary>
        public static EtsProfileStore Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsProfileStore store = host.GetComponent<EtsProfileStore>();

            if (store == null)
            {
                store = host.AddComponent<EtsProfileStore>();
            }

            return store;
        }

        private void Awake()
        {
            Current = this;
            _awakeAt = Time.unscaledTime;
        }

        private void OnDestroy()
        {
            SaveIfDirty("leaving the world");

            if (Current == this)
            {
                Current = null;
            }
        }

        private void OnApplicationQuit()
        {
            SaveIfDirty("client closing");
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                SaveIfDirty("paused");
            }
        }

        private void Update()
        {
            if (!_xpTableLoaded && EtsEngine.IsMounted)
            {
                _xpTableLoaded = true;
                EtsXpTable.Load(EtsEngine.Files);
                Changed?.Invoke();
            }

            if (State == StoreState.WaitingForIdentity)
            {
                if (Time.unscaledTime >= _nextPoll)
                {
                    _nextPoll = Time.unscaledTime + 0.5f;
                    TryOpen();
                }

                return;
            }

            // The minute autosave of driven distance and free-roam XP waits while the own truck is driven faster than
            // 5 km/h (EtsJobs.DrivingFast): a save is two encrypted files, 7.7 ms median and 16 ms at worst under Mono
            // (Phase 1 check, probe 1961) - a dropped frame in a headset at speed. It saves at the next stop, or after
            // five autosave periods anyway; deliveries, HQ and truck choices still save at once, and leaving saves.
            if (_book != null && _book.Dirty && Time.unscaledTime >= _nextAutosave)
            {
                EtsJobs jobs = EtsJobs.Instance;
                bool moving = jobs != null && jobs.DrivingFast;

                if (!moving || Time.unscaledTime >= _nextAutosave + 4f * Mathf.Max(5f, AutosaveSeconds))
                {
                    _nextAutosave = Time.unscaledTime + Mathf.Max(5f, AutosaveSeconds);
                    _book.Commit();
                }
            }
        }

        // ------------------------------------------------------------------ the API

        /// <summary>
        /// Pays a finished delivery to this player, once per job id, and adds it to the delivery log. summary is
        /// "cargo|from|to|km" (EtsProfileCodec.DeliverySummary). Queued while the profile is still opening. XP is
        /// the caller's (EtsEconomy.DeliveryXp: one per game km). True when it is credited (or queued).
        /// </summary>
        public bool Credit(string jobId, long money, long xp, string summary)
        {
            if (_book == null)
            {
                foreach (PendingCredit p in _pending)
                {
                    if (p.JobId == jobId)
                    {
                        Debug.Log("[SomniumEuroTruck] Profile: delivery " + jobId + " is already queued.");
                        return false;
                    }
                }

                _pending.Add(new PendingCredit { JobId = jobId, Money = money, Xp = xp, Summary = summary });
                Debug.Log("[SomniumEuroTruck] Profile: delivery " + jobId + " (€" + money + ", " + xp + " XP) queued until the profile has loaded.");
                return true;
            }

            bool ok = _book.Credit(jobId, money, xp, summary, out string refusal);
            Debug.Log("[SomniumEuroTruck] Profile: delivery " + jobId + " €" + money + " " + xp + " XP -> " +
                      (ok ? "balance €" + _book.Record.Money + (refusal.Length > 0 ? " (" + refusal + ")" : ", saved gen " + _book.Record.Generation) : "refused: " + refusal) + ".");

            if (ok)
            {
                RebuildLog();
                Changed?.Invoke();
            }

            return ok;
        }

        public bool Spend(long amount, string what)
        {
            if (_book == null)
            {
                return false;
            }

            bool ok = _book.Spend(amount, out string refusal);
            Debug.Log("[SomniumEuroTruck] Profile: spend €" + amount + " on " + what + " -> " + (ok ? "balance €" + _book.Record.Money : "refused: " + refusal) + ".");

            if (ok)
            {
                Changed?.Invoke();
            }

            return ok;
        }

        /// <summary>
        /// Distance driven (the game's metres: EtsJobs counts world metres times the map scale, as the jobs' km are) and
        /// free-roam XP. Whole units: fractions are dropped, so EtsJobs hands them over every 2 s. Kept in memory and saved
        /// on the autosave, not every frame (Phase 2, G2). Changed is raised when the level moves.
        /// </summary>
        public void AddDriving(float metres, float xp)
        {
            if (_book == null)
            {
                return;
            }

            int level = Level;
            _book.AddDistance((long)metres);
            _book.AddExperience((long)xp);

            if (Level != level)
            {
                Debug.Log("[SomniumEuroTruck] Profile: level " + level + " -> " + Level + " (" + Experience + " XP, free driving).");
                Changed?.Invoke();
            }
        }

        public void SetHqCity(string cityToken)
        {
            if (_book == null)
            {
                return;
            }

            _book.SetHqCity(cityToken);
            Changed?.Invoke();
        }

        /// <summary>The truck chosen on the tablet: a preview stem (PLAN C12) and its paint as "RRGGBB".</summary>
        public void SetTruck(string previewStem, string paintHex)
        {
            if (_book == null)
            {
                return;
            }

            _book.SetTruck(previewStem, paintHex);
            Changed?.Invoke();
        }

        /// <summary>
        /// The truck's condition (EtsDamage, 2026-10-04; EtsProfileRecord.Damage). Kept in the record only - it is written
        /// by the next commit: the driving autosave (the truck was moving when it was hit or worn), a delivery, a repair's
        /// Spend, or leaving the world. Nothing while the career is not open or is read-only.
        /// </summary>
        public void SetDamage(string text)
        {
            if (_book == null || _book.ReadOnly || _book.Record == null)
            {
                return;
            }

            _book.Record.Damage = text ?? "";
        }

        /// <summary>The saved condition of the truck ("" when like new or no career is open).</summary>
        public string SavedDamage => _book != null && _book.Record != null ? _book.Record.Damage ?? "" : "";

        /// <summary>Saves now if anything is unsaved.</summary>
        public void SaveIfDirty(string why)
        {
            if (_book != null && _book.Dirty && _book.Commit())
            {
                Debug.Log("[SomniumEuroTruck] Profile: saved (" + why + "), gen " + _book.Record.Generation + ".");
            }
            else if (_book == null && _pending.Count > 0 && why == "leaving the world")
            {
                // [persist §5.5]: the player left before Somnium said who they are; the queued pay is lost (Q-G4's sibling).
                Debug.LogWarning("[SomniumEuroTruck] Profile: " + _pending.Count + " queued delivery(ies) lost - the account id never arrived.");
            }
        }

        // ------------------------------------------------------------------ opening

        private void TryOpen()
        {
            string id = null;
            string nick = null;

            try
            {
                var props = SomniumBridge.PlayersContainer?.LocalPlayer?.Properties;
                id = props?.Id;
                nick = props?.NickName;
            }
            catch (Exception)
            {
                // No client (the editor), or the player not built yet: keep waiting.
            }

            if (string.IsNullOrEmpty(id))
            {
                // In the SDK editor there is no Bridge player; the client fills it a few frames after spawn [persist §2.5].
                if (Application.isEditor && Time.unscaledTime - _awakeAt > 2f)
                {
                    id = EditorUserId;
                    nick = "Editor";
                }
                else
                {
                    return;
                }
            }

            var slots = new List<IEtsProfileSlot>();
            ISomniumWorldsLocalStorage storage = null;

            try
            {
                // Read once: the getter logs "[Bridge] LocalStorage was not found" when a Bridge exists without storage.
                storage = SomniumBridge.LocalStorage;
            }
            catch (Exception)
            {
                // No Bridge: the memory slot below.
            }

            if (storage != null)
            {
                // Two files in the world's storage: a crash in the middle of one write leaves the other whole.
                slots.Add(new EtsBridgeStorageSlot(storage));
                slots.Add(new EtsBridgeStorageSlot(storage, "_2"));
            }

            if (MirrorToPlayerPrefs)
            {
                slots.Add(new EtsPlayerPrefsSlot());
            }

            if (slots.Count == 0)
            {
                // The SDK editor: no Bridge storage. Play mode keeps the career for the session only.
                slots.Add(new EtsMemorySlot());

                if (!Application.isEditor)
                {
                    // Not expected in the client (PLAN U6; the Phase 0 "Storage:" line says whether it is there).
                    Debug.LogWarning("[SomniumEuroTruck] Profile: SomniumBridge.LocalStorage is null in the client - the career is kept for this visit only.");
                }
            }

            UserId = id;
            NickName = nick ?? "";
            _book = new EtsProfileBook(id, NickName, slots.ToArray(), () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _book.Load(NickName);
            State = _book.ReadOnly ? StoreState.ReadOnly : StoreState.Ready;
            _nextAutosave = Time.unscaledTime + AutosaveSeconds;
            RebuildLog();

            Debug.Log("[SomniumEuroTruck] Profile: " + NickName + " (" + Mask(id) + ") " + State + " - €" + _book.Record.Money +
                      ", " + _book.Record.Experience + " XP (level " + Level + "), " + _book.Record.JobsDelivered + " deliveries, HQ '" + _book.Record.HqCity +
                      "', gen " + _book.Record.Generation + " - " + _book.LoadReport + ".");

            if (_book.HadDamagedCopy)
            {
                Notice?.Invoke(State == StoreState.ReadOnly
                    ? "Your career was saved by a newer version of this world. It is shown but will not be changed."
                    : "One saved copy of your career could not be read; the other copy was used.");
            }

            foreach (PendingCredit p in _pending)
            {
                Credit(p.JobId, p.Money, p.Xp, p.Summary);
            }

            _pending.Clear();
            Changed?.Invoke();
        }

        private void RebuildLog()
        {
            _newestFirst.Clear();

            if (_book == null)
            {
                return;
            }

            List<string> log = _book.Record.Deliveries;

            for (int i = log.Count - 1; i >= 0; i--)
            {
                _newestFirst.Add(log[i]);
            }
        }

        /// <summary>The id's first four characters, as EtsClientProbe logs it; never the whole id.</summary>
        private static string Mask(string id)
        {
            return string.IsNullOrEmpty(id) ? "none" : id.Length <= 4 ? id : id.Substring(0, 4) + "…";
        }
    }
}
