using System;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The private-testing gate (2026-10-20; ported from the Black Ops 2 Dreamport's Bo2Access, itself the Rec Room
    /// Dreamport's RrAccess). While the Dreamport is shown only to the team, the world starts only for the Somnium accounts
    /// listed in <see cref="Whitelist"/>. Everyone else sees NOTHING of it: no void room, no HQ stage, no "not found" panel,
    /// no loading screen, no tablet app, no HUD, no sound, no status text; the game's archives are not read, no network slot
    /// is claimed, no session int is written, no profile is read or saved, and they never act as the instance master.
    /// Privacy works both ways through the isolation (EtsIsolation): a visitor treats nobody as "with me", so every other
    /// player is hidden and silent on the visitor's screen; and the testers' isolation hides and silences every player who
    /// is not on the list.
    ///
    /// How it holds the world (EtsAccessCheck.cs, the Unity/Bridge half): the scene's four world scripts (EtsNet,
    /// EtsMapViewer, EtsModelViewer, SceneNetworking - every world MonoBehaviour the scene references; everything else of
    /// the world is added by them at run time) call <see cref="Hold"/> first thing in Awake. While the verdict is not
    /// Allowed the script is switched off before its OnEnable/Start/Update can run, its Awake body is kept for later, and
    /// every canvas, renderer, light and sound under it is switched off (the scene's "Status Canvas" text). On Allowed they
    /// are given back in the order they were held, and the world starts exactly as it always did; on Denied they stay held.
    /// The scene's own plain objects stay: the "Spawn Platform" (URP's default Lit plane under the spawn point, so nobody
    /// falls; respawn height -1000), the "Uploader" (SDK config + directional light) and the "Global Volume".
    ///
    /// The check reads the local player's <c>Properties.NickName</c> (the username on the name tag) and
    /// <c>Properties.Id</c> from the Bridge (proven members, DevTools/fusionapi/known_good.txt). The comparison ignores
    /// letter case and surrounding spaces. The Bridge fills the local player a moment after the world starts, so the check
    /// waits for it, with a warning in the log after <see cref="NameTimeout"/> seconds. Nothing starts while it waits, so
    /// the gate fails closed. The editor (no client) is always let in. Player.log gets one line with the verdict, the
    /// username and the id ("[SomniumEuroTruck] Access: ..."), so an id can be copied into the list as well. This half is
    /// pure C#.
    /// </summary>
    public static partial class EtsAccess
    {
        /// <summary>
        /// Who may play. Add Somnium usernames (as on the name tag; any letter case) or Somnium user ids (Player.log shows
        /// each player's at start: "[SomniumEuroTruck] Access: name (id ...)"), one quoted string per line with a comma.
        /// Leave the list empty to open the world to everyone. The same list as the Black Ops 2 Dreamport (2026-10-20).
        /// </summary>
        public static readonly string[] Whitelist =
        {
            "Shodah",
            "Wildy",
            "Meloz",
            "ꨄ Rage Quit ꨄ",
            "denevraut",   // exact name-tag text; if it does not match, her Player.log "Access:" line gives the exact name and id
            "Ninquiet",    // 2026-10-21: added at the user's request
        };

        /// <summary>After this many seconds without a username from the client, the log says the check is still waiting.</summary>
        public const float NameTimeout = 30f;

        public enum Verdict
        {
            Pending,
            Allowed,
            Denied,
        }

        public static Verdict State { get; private set; }

        /// <summary>The local player's username and Somnium id as the client reported them (null before, or in the editor).</summary>
        public static string Nick { get; private set; }

        public static string UserId { get; private set; }

        public static bool Allowed
        {
            get { return State == Verdict.Allowed; }
        }

        /// <summary>True when the username or the id is on <see cref="Whitelist"/> (letter case and spaces ignored).</summary>
        public static bool Matches(string nick, string id)
        {
            return Matches(nick, id, Whitelist);
        }

        /// <summary>The rule of <see cref="Matches(string, string)"/> against any list (blank entries never match).</summary>
        public static bool Matches(string nick, string id, string[] list)
        {
            if (list == null)
            {
                return false;
            }

            foreach (string entry in list)
            {
                string e = entry != null ? entry.Trim() : "";
                if (e.Length == 0)
                {
                    continue;
                }

                if ((!string.IsNullOrEmpty(nick) && string.Equals(e, nick.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(id) && string.Equals(e, id.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the list is in force and a known player (username known) is not on it: a visitor. An unknown name is
        /// not called a visitor here (callers that must fail closed ask <see cref="Matches(string, string)"/> instead).
        /// </summary>
        public static bool IsVisitorName(string nick, string id)
        {
            return Whitelist.Length > 0 && !string.IsNullOrEmpty(nick) && !Matches(nick, id);
        }

        /// <summary>Forgets the verdict (statics outlive a world visit; the next visit checks again).</summary>
        public static void Reset()
        {
            State = Verdict.Pending;
            Nick = null;
            UserId = null;
        }
    }
}
