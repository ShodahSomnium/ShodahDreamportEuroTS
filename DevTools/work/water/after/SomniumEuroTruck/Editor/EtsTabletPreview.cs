using UnityEditor;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.EditorTools
{
    /// <summary>
    /// The "ETS 2" tablet app in the editor, while EtsFeatures.NewFlow is still off (owner E; PLAN.md §8 Phase 1, E1).
    ///
    /// Nothing in the build creates the app with the flag off (EtsNet.Awake calls EtsTabletApp.Ensure only in the new
    /// flow), so the game plays exactly as before. In Play mode these menu items add it to 'Ets Net' by hand. The
    /// editor has no Somnium tablet, so the app builds at once into its own panel - the fallback the client uses
    /// when no tablet is found - and opens it; Tab opens and closes it afterwards (Escape stays the old menu's with
    /// the flag off). The truck, camera, sound and jobs are today's scene truck's, through EtsMenu.
    ///
    /// The link is made a real one by Ensure, but with the flag off it does nothing (EtsTabletLink.Update), and the
    /// editor has no tablet to find anyway.
    ///
    /// The variants put fixed numbers in the top bar (level 3, 40 % of the XP bar) so the badge, the bar and the
    /// blocked state can be looked at without a session or a career store: the session and the store do not exist
    /// with the flag off. The money stays today's (2000 + EtsJobs.Earnings).
    /// </summary>
    public static class EtsTabletPreview
    {
        private const string Menu = "Somnium Euro Truck/Tablet/";

        [MenuItem(Menu + "Preview the ETS 2 app (Play mode)", false, 80)]
        public static void Preview()
        {
            Start(null);
        }

        [MenuItem(Menu + "Preview as the instance master", false, 81)]
        public static void PreviewMaster()
        {
            Start(new PreviewData { Master = true });
        }

        [MenuItem(Menu + "Preview as a joiner", false, 82)]
        public static void PreviewJoiner()
        {
            Start(new PreviewData { Master = false, MasterNameShown = "Anna" });
        }

        [MenuItem(Menu + "Preview blocked (loading)", false, 83)]
        public static void PreviewBlocked()
        {
            Start(new PreviewData { Master = true, BlockedShown = "LOADING ..." });
        }

        [MenuItem(Menu + "Preview the ETS 2 app (Play mode)", true)]
        [MenuItem(Menu + "Preview as the instance master", true)]
        [MenuItem(Menu + "Preview as a joiner", true)]
        [MenuItem(Menu + "Preview blocked (loading)", true)]
        public static bool CanPreview()
        {
            return EditorApplication.isPlaying;
        }

        private static void Start(IEtsTabletData data)
        {
            GameObject host = EtsNet.Instance != null ? EtsNet.Instance.gameObject : GameObject.Find("Ets Net");

            if (host == null)
            {
                host = new GameObject("Ets Tablet Preview");
            }

            EtsTabletApp app = EtsTabletApp.Ensure(host);

            if (data != null)
            {
                var preview = (PreviewData)data;
                preview.Base = new EtsTabletSessionData { Menu = app.Menu != null ? app.Menu : Object.FindFirstObjectByType<EtsMenu>() };
                app.Data = preview;
            }

            app.Open();
            Debug.Log("[SomniumEuroTruck] Tablet preview: the app on '" + host.name + "', NewFlow " + EtsFeatures.NewFlow + (data != null ? ", preview data" : "") +
                      "; Tab opens and closes its panel.");
        }

        private sealed class PreviewData : IEtsTabletData
        {
            public EtsTabletSessionData Base;
            public bool Master;
            public string MasterNameShown = "";
            public string BlockedShown = "";

            public string PlayerName => Base != null ? Base.PlayerName : "Somnium Driver";
            public bool IsMaster => Master;
            public string MasterName => Master ? "" : MasterNameShown;
            public long Money => Base != null ? Base.Money : 2000;
            public int Level => 3;
            public float LevelProgress => 0.4f;
            public string Blocked => BlockedShown;
        }
    }
}
