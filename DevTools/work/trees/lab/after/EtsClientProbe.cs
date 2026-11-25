using System;
using Fusion;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.LocalStorage;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Two client facts the plan builds on and nobody has seen yet (owner L; PLAN.md §4 U3 and U6, §8 Phase 0
    /// tasks 0.5 and 0.6. Phase 0 probe; removed in Phase 2). Added by EtsNet.Awake, always (not behind
    /// EtsFeatures.NewFlow); it logs each line once and then removes itself.
    ///
    /// U6, storage. The career store is Bridge LocalStorage (PLAN §12, the user's own request), keyed by the
    /// account id LocalPlayer.Properties.Id [persist §2.5]. The folder WorldsLocalStorage/8354 shows the feature
    /// exists, but this world has never run it, and in the SDK editor SomniumBridge.LocalStorage is null (no
    /// Bridge instance: persist §2.1, bridge_sdk.il 185-211). So: wait for the id (up to StorageWait), then
    /// write "ets_probe", read it back, and log
    ///   [SomniumEuroTruck] Storage: ok|failed ..., id cf5c, after T s
    /// T is the seconds from scene start to the id's arrival. Only the id's first four characters are ever
    /// logged: Player.log gets shared in bug reports, and four characters are enough to tell two accounts
    /// apart in one's own logs. The value written is a time stamp, so the next visit can say whether an
    /// earlier one's value survived (U6's "persistence across rejoin", PLAN §9).
    ///
    /// U3, master. The server compiles worlds against a Fusion without NetworkRunner.GetMasterClient (the upload
    /// of 2026-09-23 failed on it), so the master is named from the session. Once SceneNetworking says the local player has
    /// joined (IsNetworkReady), log
    ///   [SomniumEuroTruck] Master: IsSharedModeMasterClient = b, local = M (N players ...)
    /// 2 s after joining, once.
    /// </summary>
    public sealed class EtsClientProbe : MonoBehaviour
    {
        public const string StorageKey = "ets_probe";

        [Tooltip("Seconds to wait for the account id before probing the storage without it.")]
        public float StorageWait = 60f;

        [Tooltip("Seconds to wait for the room before saying there is none.")]
        public float MasterWait = 60f;

        private float _startedAt;
        private float _nextPoll;
        private float _joinedAt = -1f;
        private bool _storageDone;
        private bool _masterDone;

        /// <summary>The probe on this object, added if missing. Unity's null test, not ??: a missing component is a fake null in the editor.</summary>
        public static EtsClientProbe Begin(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            try
            {
                EtsClientProbe probe = host.GetComponent<EtsClientProbe>();

                if (probe == null)
                {
                    probe = host.AddComponent<EtsClientProbe>();
                }

                return probe;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Storage and Master: no probe (" + e.GetType().Name + ": " + e.Message + ").");
                return null;
            }
        }

        private void Start()
        {
            _startedAt = Time.unscaledTime;
        }

        private void Update()
        {
            // Four polls a second: both facts arrive within seconds, and each poll is a handful of property reads.
            if (Time.unscaledTime < _nextPoll)
            {
                return;
            }

            _nextPoll = Time.unscaledTime + 0.25f;
            float waited = Time.unscaledTime - _startedAt;

            // Each probe is guarded on its own: a fault ends that probe with its one line, instead of Unity
            // logging the same exception four times a second.
            if (!_storageDone)
            {
                try
                {
                    ProbeStorage(waited);
                }
                catch (Exception e)
                {
                    _storageDone = true;
                    Debug.Log("[SomniumEuroTruck] Storage: failed (the probe threw " + e.GetType().Name + ": " + e.Message + "), after " + waited.ToString("0.0") + " s.");
                }
            }

            if (!_masterDone)
            {
                try
                {
                    ProbeMaster(waited);
                }
                catch (Exception e)
                {
                    _masterDone = true;
                    Debug.Log("[SomniumEuroTruck] Master: unknown (the probe threw " + e.GetType().Name + ": " + e.Message + "), " + waited.ToString("0.0") + " s after scene start.");
                }
            }

            if (_storageDone && _masterDone)
            {
                Destroy(this);
            }
        }

        // ---------------------------------------------------------------- U6: storage

        private void ProbeStorage(float waited)
        {
            string id = null;

            try
            {
                // Only the id is polled. LocalStorage is read once, below: its getter logs "[Bridge] LocalStorage
                // was not found" when a Bridge instance exists without storage (persist §2.1), and a 4 Hz poll
                // must not repeat that. (SomniumBridge.HasInstance would say it first, but it is not public.)
                id = SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.Id;
            }
            catch (Exception)
            {
                // No client (the editor), or the player not built yet: keep waiting.
            }

            if (string.IsNullOrEmpty(id) && waited < StorageWait)
            {
                return;
            }

            _storageDone = true;
            string result;

            try
            {
                result = WriteAndReadBack();
            }
            catch (Exception e)
            {
                result = "failed (" + e.GetType().Name + ": " + e.Message + ")";
            }

            Debug.Log("[SomniumEuroTruck] Storage: " + result + ", id " + Abbreviated(id) + ", after " + waited.ToString("0.0") + " s.");
        }

        private static string WriteAndReadBack()
        {
            ISomniumWorldsLocalStorage storage = SomniumBridge.LocalStorage;

            if (storage == null)
            {
                return "failed (SomniumBridge.LocalStorage is null: no Bridge instance, as in the SDK editor, or a Bridge without storage)";
            }

            bool existed = storage.TextExists(StorageKey);
            string earlier = existed ? storage.ReadText(StorageKey) : null;
            string value = "SomniumEuroTruck storage probe, written " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
            bool wrote = storage.WriteText(StorageKey, value);
            string back = storage.ReadText(StorageKey);
            bool same = back == value;

            return (wrote && same ? "ok" : "failed") +
                   " (WriteText '" + StorageKey + "' " + (wrote ? "true" : "FALSE") +
                   ", read back " + (same ? "the same " + value.Length + " characters" : back == null ? "null" : "'" + Clip(back) + "'") +
                   "; an earlier visit's value " + (existed ? "was there: '" + Clip(earlier) + "'" : "was not there") + ")";
        }

        /// <summary>The id's first four characters, or "none". Never the whole id (see the class comment).</summary>
        private static string Abbreviated(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "none";
            }

            return id.Length <= 4 ? id : id.Substring(0, 4);
        }

        private static string Clip(string text)
        {
            if (text == null)
            {
                return "";
            }

            return text.Length <= 80 ? text : text.Substring(0, 80) + "...";
        }

        // ---------------------------------------------------------------- U3: master

        private void ProbeMaster(float waited)
        {
            NetworkRunner runner = SceneNetworking.NetworkRunnerRef;

            if (!SceneNetworking.IsNetworkReady || runner == null)
            {
                if (waited >= MasterWait)
                {
                    _masterDone = true;
                    Debug.Log("[SomniumEuroTruck] Master: no room joined after " + waited.ToString("0") + " s (offline, or the editor without the Community Modules session).");
                }

                return;
            }

            if (_joinedAt < 0f)
            {
                _joinedAt = Time.unscaledTime;
            }

            // 2 s after joining, so the room's master flag has settled. No GetMasterClient: the server compiles
            // worlds against a Fusion without it (upload of 2026-09-23, CS1061); who the master is travels in the
            // session instead (EtsSessionLogic.Master / StampMaster).
            if (Time.unscaledTime - _joinedAt < 2f)
            {
                return;
            }

            _masterDone = true;
            string shared;
            string local;

            try
            {
                shared = runner.IsSharedModeMasterClient ? "true" : "false";
            }
            catch (Exception e)
            {
                shared = "threw " + e.GetType().Name;
            }

            try
            {
                local = runner.LocalPlayer.PlayerId.ToString();
            }
            catch (Exception e)
            {
                local = "threw " + e.GetType().Name;
            }

            int players = 0;

            try
            {
                foreach (PlayerRef unused in runner.ActivePlayers)
                {
                    players++;
                }
            }
            catch (Exception)
            {
                players = -1;
            }

            Debug.Log("[SomniumEuroTruck] Master: IsSharedModeMasterClient = " + shared + ", local = " + local +
                      " (" + players + " players in the room, " + (Time.unscaledTime - _joinedAt).ToString("0.0") + " s after joining, " +
                      waited.ToString("0.0") + " s after scene start).");
        }
    }
}
