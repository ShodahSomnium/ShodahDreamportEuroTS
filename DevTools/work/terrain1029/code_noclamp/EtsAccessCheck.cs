using System;
using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Bridge.Player.Components;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The Unity/Bridge half of <see cref="EtsAccess"/> (EtsAccess.cs has the list, the rule and the notes): the check
    /// (BO2's Bo2AccessCheck), the hold on the scene's world scripts, and the questions the networking asks about other
    /// players (who is a tester, who is a visitor).
    /// </summary>
    public static partial class EtsAccess
    {
        // ------------------------------------------------------------------ the log

        public static void Info(string text)
        {
            Debug.Log("[SomniumEuroTruck] " + text);
        }

        public static void Warn(string text)
        {
            Debug.LogWarning("[SomniumEuroTruck] " + text);
        }

        public static void Error(string text)
        {
            Debug.LogError("[SomniumEuroTruck] " + text);
        }

        // ------------------------------------------------------------------ the check

        /// <summary>
        /// Decides <see cref="State"/>. Waits for the username; run by <see cref="EtsAccessGate"/> before anything of the
        /// Dreamport starts. Finishes without yielding when the list is empty or in the editor (the held scripts then start
        /// in the same frame, as before the gate existed).
        /// </summary>
        public static IEnumerator Check()
        {
            State = Verdict.Pending;
            if (Whitelist.Length == 0)
            {
                State = Verdict.Allowed;
                Info("Access: the whitelist is empty: the world is open to everyone");
                yield break;
            }

            if (Application.isEditor)
            {
                State = Verdict.Allowed;
                Info("Access: editor: the whitelist (" + Whitelist.Length + " name(s)) is not applied");
                yield break;
            }

            // Nothing starts until the username is known, however long the client takes: a slow client only delays the
            // start, and nobody gets in without a name.
            float warnAt = Time.realtimeSinceStartup + NameTimeout;
            bool warned = false;
            while (!ReadLocal())
            {
                if (!warned && Time.realtimeSinceStartup >= warnAt)
                {
                    warned = true;
                    Warn("Access: the client has not reported the local username after " + NameTimeout.ToString("0") + " s: still waiting, nothing is started meanwhile");
                }

                yield return null;
            }

            Decide();
        }

        private static bool ReadLocal()
        {
            try
            {
                ISomniumPlayersContainer c = SomniumBridge.PlayersContainer;
                ISomniumPlayer local = c != null ? c.LocalPlayer : null;
                ISomniumPlayerProperties p = local != null ? local.Properties : null;
                if (p == null)
                {
                    return false;
                }

                Nick = p.NickName;
                UserId = p.Id;
                return !string.IsNullOrEmpty(Nick);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The verdict once the username is known, with its one Player.log line.</summary>
        private static void Decide()
        {
            string who = Nick + " (id " + (string.IsNullOrEmpty(UserId) ? "unknown" : UserId) + ")";
            State = Matches(Nick, UserId) ? Verdict.Allowed : Verdict.Denied;
            Info("Access: " + (State == Verdict.Allowed
                ? who + " is on the whitelist: starting the Dreamport"
                : who + " is not on the whitelist: nothing of the Dreamport is loaded or shown, other players are hidden and silent"));
        }

        // ------------------------------------------------------------------ the hold

        private sealed class Held
        {
            public MonoBehaviour Who;
            public Action Resume;
            public string Name;
        }

        private static readonly List<Held> HeldList = new List<Held>();
        private static readonly List<Behaviour> HiddenBehaviours = new List<Behaviour>();
        private static readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        private static readonly List<Canvas> ScratchCanvases = new List<Canvas>();
        private static readonly List<Light> ScratchLights = new List<Light>();
        private static readonly List<AudioSource> ScratchAudio = new List<AudioSource>();
        private static readonly List<Renderer> ScratchRenderers = new List<Renderer>();
        private static EtsAccessGate _gate;

        /// <summary>
        /// The first line of Awake of every world script the scene holds (EtsNet, EtsMapViewer, EtsModelViewer,
        /// SceneNetworking): <c>if (EtsAccess.Hold(this, Awake)) return;</c>. The first call of a visit creates the
        /// "Ets Access" object (<see cref="EtsAccessGate"/>), which starts the isolation and the check. True while the
        /// verdict is not Allowed: the script is switched off at once (so its OnEnable, Start and Update never run), every
        /// canvas, renderer, light and sound under it is switched off, and <paramref name="resume"/> (its Awake, which
        /// then passes this test) is kept. On Allowed the gate runs each kept Awake and switches the script back on, in
        /// the order they were held; on Denied they stay held for good. False: carry on (Allowed, the editor, an empty list).
        /// </summary>
        public static bool Hold(MonoBehaviour who, Action resume)
        {
            try
            {
                Engage(who);
            }
            catch (Exception e)
            {
                // Fails closed: without the gate nobody decides, so the script stays held.
                Error("Access: the gate could not start: " + e);
            }

            if (State == Verdict.Allowed)
            {
                return false;
            }

            if (who == null)
            {
                return true;
            }

            bool known = false;
            for (int i = 0; i < HeldList.Count; i++)
            {
                if (ReferenceEquals(HeldList[i].Who, who))
                {
                    known = true;
                }
            }

            if (!known)
            {
                HeldList.Add(new Held { Who = who, Resume = resume, Name = who.GetType().Name });
                HideUnder(who);
            }

            who.enabled = false;
            return true;
        }

        /// <summary>
        /// True while this script is held (its Awake body never ran): its OnDestroy must do nothing then. Stays true after
        /// the visit ends (the gate's OnDestroy may come first), until the next visit's gate starts.
        /// </summary>
        public static bool IsHeld(MonoBehaviour who)
        {
            for (int i = 0; i < HeldList.Count; i++)
            {
                if (ReferenceEquals(HeldList[i].Who, who))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The gate of this visit: made once, as a child of the scene root of the script that asks first ("Ets Net", "Ets Map
        /// Viewer" or "Ets Model Viewer" - roots that stay active; only the scripts on them are held), so it lives in the
        /// world's scene and is destroyed with it (Unity's null test then starts the next visit afresh).
        /// </summary>
        private static void Engage(MonoBehaviour who)
        {
            if (_gate != null)
            {
                return;
            }

            // A new visit: the statics still hold the last one's verdict and lists.
            _gate = null;
            Reset();
            HeldList.Clear();
            HiddenBehaviours.Clear();
            HiddenRenderers.Clear();

            var go = new GameObject("Ets Access");
            if (who != null)
            {
                go.transform.SetParent(who.transform.root, false);
            }

            // Its Awake starts the isolation and the check, and in the editor or with an empty list decides at once.
            go.AddComponent<EtsAccessGate>();
        }

        /// <summary>The gate's Awake.</summary>
        internal static void Attach(EtsAccessGate gate)
        {
            _gate = gate;
        }

        /// <summary>The gate's OnDestroy: the verdict is forgotten; the held list stays for the held scripts' OnDestroy.</summary>
        internal static void Detach(EtsAccessGate gate)
        {
            if (_gate == gate || _gate == null)
            {
                _gate = null;
                Reset();
            }
        }

        /// <summary>Everything under a held script that draws or sounds (the scene's "Status Canvas" under Ets Model Viewer).</summary>
        private static void HideUnder(MonoBehaviour who)
        {
            try
            {
                who.GetComponentsInChildren(true, ScratchCanvases);
                foreach (Canvas c in ScratchCanvases)
                {
                    HideBehaviour(c);
                }

                who.GetComponentsInChildren(true, ScratchLights);
                foreach (Light l in ScratchLights)
                {
                    HideBehaviour(l);
                }

                who.GetComponentsInChildren(true, ScratchAudio);
                foreach (AudioSource a in ScratchAudio)
                {
                    HideBehaviour(a);
                }

                who.GetComponentsInChildren(true, ScratchRenderers);
                foreach (Renderer r in ScratchRenderers)
                {
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        HiddenRenderers.Add(r);
                    }
                }
            }
            catch (Exception e)
            {
                Error("Access: hiding " + who.GetType().Name + "'s objects failed: " + e.Message);
            }
            finally
            {
                ScratchCanvases.Clear();
                ScratchLights.Clear();
                ScratchAudio.Clear();
                ScratchRenderers.Clear();
            }
        }

        private static void HideBehaviour(Behaviour b)
        {
            if (b != null && b.enabled)
            {
                b.enabled = false;
                HiddenBehaviours.Add(b);
            }
        }

        /// <summary>The gate, on Allowed: everything hidden back on, then each held script's Awake and OnEnable, in order.</summary>
        internal static void ReleaseAll()
        {
            foreach (Behaviour b in HiddenBehaviours)
            {
                if (b != null)
                {
                    b.enabled = true;
                }
            }

            foreach (Renderer r in HiddenRenderers)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }

            HiddenBehaviours.Clear();
            HiddenRenderers.Clear();

            if (HeldList.Count == 0)
            {
                return;
            }

            var held = new List<Held>(HeldList);
            HeldList.Clear();
            string names = "";
            foreach (Held h in held)
            {
                if (h.Who == null)
                {
                    continue;
                }

                names += (names.Length > 0 ? ", " : "") + h.Name;
                try
                {
                    if (h.Resume != null)
                    {
                        h.Resume();
                    }
                }
                catch (Exception e)
                {
                    Error("Access: starting " + h.Name + " (its Awake) threw: " + e);
                }

                try
                {
                    h.Who.enabled = true;
                }
                catch (Exception e)
                {
                    Error("Access: switching " + h.Name + " on threw: " + e);
                }
            }

            Info("Access: the held world scripts started after the verdict: " + names);
        }

        // ------------------------------------------------------------------ other players

        /// <summary>The local player's body root (Bridge), for the isolation's guards; null without a client.</summary>
        public static Transform LocalRoot
        {
            get
            {
                try
                {
                    ISomniumPlayersContainer c = SomniumBridge.PlayersContainer;
                    ISomniumPlayer local = c != null ? c.LocalPlayer : null;
                    ISomniumPlayerReferences refs = local != null ? local.References : null;
                    ISomniumPlayerBody body = refs != null ? refs.Body : null;
                    return body != null ? body.Root : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// The testers' isolation rule (BO2's Bo2Gear.HasRecord whitelist stand-in): the remote player with this Photon id is
        /// on the whitelist too. Unknown players and players without a name yet are not (fails closed: hidden until known).
        /// </summary>
        public static bool IsTesterId(int photonId)
        {
            IReadOnlyList<EtsRemotes.Remote> all = EtsRemotes.All;
            for (int i = 0; i < all.Count; i++)
            {
                EtsRemotes.Remote r = all[i];
                if (r == null || r.Id != photonId)
                {
                    continue;
                }

                return Matches(r.RawNick, RemoteId(r.Player));
            }

            return false;
        }

        /// <summary>
        /// A known visitor (named, not on the list) with this Fusion PlayerId: the testers' traffic takeover queue skips them
        /// (a visitor never runs the traffic). Unknown players count as players, as before the gate.
        /// </summary>
        public static bool IsVisitorId(int playerId)
        {
            if (Whitelist.Length == 0)
            {
                return false;
            }

            IReadOnlyList<EtsRemotes.Remote> all = EtsRemotes.All;
            for (int i = 0; i < all.Count; i++)
            {
                EtsRemotes.Remote r = all[i];
                if (r != null && r.Id == playerId)
                {
                    return IsVisitorName(r.RawNick, RemoteId(r.Player));
                }
            }

            return false;
        }

        /// <summary>A remote Bridge player who is a known visitor: the testers' traffic neither streams round them nor stops for them.</summary>
        public static bool IsVisitor(ISomniumPlayer player)
        {
            if (player == null || Whitelist.Length == 0)
            {
                return false;
            }

            try
            {
                ISomniumPlayerProperties p = player.Properties;
                return p != null && !p.IsLocal && IsVisitorName(p.NickName, p.Id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string RemoteId(ISomniumPlayer player)
        {
            try
            {
                ISomniumPlayerProperties p = player != null ? player.Properties : null;
                return p != null ? p.Id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
