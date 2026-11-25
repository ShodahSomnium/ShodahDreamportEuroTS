using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>One of the game's loading photographs: /material/ui/loading/bar_pics/&lt;Name&gt;.mat and its .sii.</summary>
    public sealed class EtsLoadingPicture
    {
        public string Name, MaterialPath, LocationKey, Location, Dlc;
    }

    /// <summary>
    /// What the 1.61 loading screen shows, worked out from the player's install (owner D; [gameui §3.3], PLAN.md
    /// §1 rows 10 and 16, Q-D5). Pure, so the harness checks it against the files (probe 1893).
    ///
    /// /ui/loading.sii on 1.61 is fifteen children of one window (probe 1885 and 1891): the photograph
    /// (txt.loading.pic, 20..1420 x 286..798), its four rounded corners (screenshot.frame, 15..1425 x 281..803), the
    /// ETS2 logo (525..915 x 194..389), the place the photograph was taken (screenshot.location, txt.normal.right),
    /// a DLC line (screenshot.dlc), the 3-px bar - grey loading.bar.bottom under orange loading.bar.upper,
    /// 370..1070 x 168..171, clr_loading_bar_bg and clr_loading_bar - the job line (job.info, txt.loading.job,
    /// 0..1440 x 126..152), the busy spinner (loading.icon, /video/busy.mat, 691..749 x 38..96) and a black
    /// ui_fullscreen overlay under it all. The World of Trucks logo and author line (logo.wotr, screenshot.info), the
    /// content-change notice (gen_nav_info) and the truck/car mode groups belong to cases this world never has and
    /// are left out (Hidden). So is the DLC line: the base pictures are chosen (Q-D5), and a DLC's display name is in
    /// no file anyway ([gameui §2.1]; nordic_04's "Nordic Horizons" comes from the online catalogue).
    ///
    /// The job line. txt.loading.job is "&lt;align center top&gt;&lt;normal.font&gt;&lt;clr_txt&gt;%0 &lt;offset 22&gt;%1&lt;clr_txt&gt;%2
    /// &lt;offset 22&gt;%3&lt;offset 22&gt;%4": the day and time, then %1 - whose colour the template resets before %2, so
    /// %1 carries a colour of its own - the cargo, the route, the pay. The user's 1.61 screenshot reads "Monday 10:00
    /// Current job: Stone Dust Hanover - Hanover €368", so %1 is "Current job: " (current_job_title plus the colon)
    /// and %2 the cargo. %1's colour is not visible in what was recorded: clr_txt_d, the grey the game's label/value
    /// templates put labels in (txt.truck_prefer.description, txt.jobdetail), is used and is a guess. With no job
    /// only %0 is written.
    /// </summary>
    public static class EtsLoadingContent
    {
        public const string Layout = "/ui/loading.sii";

        public const string PictureFolder = "/material/ui/loading/bar_pics";

        /// <summary>The spinner's flipsheet (effect ui.flipsheet.rfx: 30 frames, 8 columns, 30 fps, 0.125 x 0.25 uv; probe 1882).</summary>
        public const string Spinner = "/video/busy.mat";

        /// <summary>The bar's full width in the frame (370..1070).</summary>
        public const float BarLeft = 370f, BarRight = 1070f;

        /// <summary>Elements of /ui/loading.sii this world never shows (class notes).</summary>
        public static readonly string[] Hidden =
        {
            "logo.wotr", "screenshot.info", "gen_nav_info", "previous.mode.group", "new.mode.group", "screenshot.dlc"
        };

        /// <summary>
        /// The loading photographs, sorted by name: each .sii in the folder with its location key (resolved through the
        /// locale) and dlc_id. baseOnly keeps the dlc_id "base" ones (26 on 1.61: base_01..base_26; [gameui §3.3]).
        /// </summary>
        public static List<EtsLoadingPicture> Pictures(EtsFileSystem files, bool baseOnly)
        {
            var result = new List<EtsLoadingPicture>();

            if (files == null || !files.TryList(PictureFolder, out List<string> names, out _))
            {
                return result;
            }

            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.Ordinal);

            foreach (string file in sorted)
            {
                if (!file.EndsWith(".sii", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = file.Substring(0, file.Length - 4);
                EtsSiiFile sii = files.ReadSii(PictureFolder + "/" + file);
                EtsSiiUnit unit = sii != null && sii.Units.Count > 0 ? sii.Units[0] : null;

                if (unit == null)
                {
                    continue;
                }

                string dlc = unit.GetString("dlc_id", "") ?? "";

                if (baseOnly && dlc != "base")
                {
                    continue;
                }

                string key = unit.GetString("location", "") ?? "";
                result.Add(new EtsLoadingPicture
                {
                    Name = name, MaterialPath = PictureFolder + "/" + name + ".mat", LocationKey = key, Dlc = dlc,
                    Location = EtsLocale.Loaded ? EtsLocale.Resolve(key) : key
                });
            }

            return result;
        }

        /// <summary>The job line's value with only the day and time ("Monday 10:00").</summary>
        public static string DayValue(int gameMinutes)
        {
            return Clean(EtsLocale.DayTime(gameMinutes));
        }

        /// <summary>
        /// The job line's value for a job: "Monday 10:00|&lt;clr_txt_d&gt;Current job: |Stone Dust|Hanover - Hanover|€368"
        /// (class notes). Money in the locale's format (EtsLocale.Money).
        /// </summary>
        public static string JobValue(int gameMinutes, string cargo, string fromCity, string toCity, long money)
        {
            string label = "<color value=@@clr_txt_d@@>" + EtsLocale.Word("current_job_title") + ": ";
            return Clean(EtsLocale.DayTime(gameMinutes)) + "|" + label + "|" + Clean(cargo) + "|" + Clean(fromCity) + " - " + Clean(toCity) + "|" +
                   Clean(EtsLocale.Money(money));
        }

        /// <summary>The element values for a picture and a job line (keys of /ui/loading.sii).</summary>
        public static Dictionary<string, string> Values(EtsLoadingPicture picture, string jobValue)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "job.info", jobValue ?? "" }
            };

            if (picture != null)
            {
                values["picture"] = picture.MaterialPath;
                values["screenshot.location"] = Clean(picture.Location);
            }

            return values;
        }

        /// <summary>The orange bar's width for a progress of 0..1 (0..700 frame units).</summary>
        public static float BarWidth(float progress01)
        {
            float p = progress01 < 0f ? 0f : progress01 > 1f ? 1f : progress01;
            return (BarRight - BarLeft) * p;
        }

        /// <summary>
        /// Frame f of a flipsheet as a RawImage uvRect, the way EtsGameUi draws a game texture (its first row at v = 0,
        /// so the rect is given top-down): column f % columns, row f / columns counted from the top.
        /// </summary>
        public static Rect FrameUv(int frame, int frames, int columns, float frameU, float frameV)
        {
            int f = frames > 0 ? ((frame % frames) + frames) % frames : 0;
            int c = columns > 0 ? f % columns : 0, r = columns > 0 ? f / columns : 0;
            return new Rect(c * frameU, r * frameV + frameV, frameU, -frameV);
        }

        /// <summary>'|' splits a value into its %n parts: never inside one.</summary>
        public static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.Replace('|', '/');
        }
    }

    /// <summary>
    /// The 1.61 loading screen: layout, orange 3 px bar, frame corners, flipsheet spinner, job line, base-game
    /// pictures (owner D; [gameui §3.3], PLAN.md §1 rows 10 and 16, §5.2, §7.D). Full view: a screen overlay on a
    /// monitor, a panel in front of the player in a headset (EtsGameUi.CreateCanvas through EtsUiSurface, sorting order
    /// 500 over everything the world draws). Built from the game's own file by EtsGameUi.BuildLayout with
    /// EtsLoadingContent's values; nothing is drawn per frame except the spinner's uvRect, moved when its frame
    /// changes (30 fps). EtsFlow shows it for the session's Loading (EtsSession.LoadProgress, every frame) and for a job's start
    /// (EtsJobs.JobStarting to JobStarted); with EtsFeatures.NewFlow false nothing shows it and the old front end keeps
    /// its own (EtsFrontEnd.ShowLoading).
    /// </summary>
    public sealed class EtsLoadingScreen : MonoBehaviour
    {
        public const int SortingOrder = 500;

        public static EtsLoadingScreen Current { get; private set; }

        /// <summary>The screen as built (by key), or null before Show or when the archives are not mounted.</summary>
        public EtsGameScreen Screen { get; private set; }

        public EtsLoadingPicture Picture { get; private set; }

        private GameObject _surface;
        private RectTransform _bar;
        private RawImage _spinner;
        private int _frames, _columns, _lastFrame = -1;
        private float _fps, _frameU, _frameV, _shownAt;

        /// <summary>
        /// Shows the loading screen. jobLine is the job.info value (EtsLoadingContent.JobValue); null = the day and time
        /// only, from the session clock (EtsSession.GameClockMinutes; Monday 10:00 before there is one, PLAN §3.11). A
        /// screen already up is replaced. Null when the archives are not mounted.
        /// </summary>
        public static EtsLoadingScreen Show(string jobLine)
        {
            if (Current != null)
            {
                Current.Hide();
            }

            if (EtsEngine.Files == null)
            {
                Debug.Log("[SomniumEuroTruck] Loading screen: not shown - the game's archives are not mounted.");
                return null;
            }

            var host = new GameObject("Ets Loading Screen");
            var screen = host.AddComponent<EtsLoadingScreen>();
            Current = screen;
            screen.Build(jobLine);
            return screen;
        }

        private void Build(string jobLine)
        {
            _shownAt = Time.realtimeSinceStartup;
            EtsSession session = EtsSession.Instance;
            int minutes = session != null ? session.GameClockMinutes : 600;
            string job = jobLine ?? EtsLoadingContent.DayValue(minutes);

            List<EtsLoadingPicture> pictures = EtsLoadingContent.Pictures(EtsEngine.Files, true);
            Picture = pictures.Count > 0 ? pictures[UnityEngine.Random.Range(0, pictures.Count)] : null;

            _surface = EtsUiSurface.Create("Ets loading screen", SortingOrder);
            _surface.transform.SetParent(transform, false);
            RectTransform frame = EtsGameUi.Place(_surface.transform, "frame", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);
            Screen = EtsGameUi.BuildLayout(frame, EtsLoadingContent.Layout, EtsLoadingContent.Values(Picture, job), EtsLoadingContent.Hidden);

            if (Screen == null)
            {
                Debug.Log("[SomniumEuroTruck] Loading screen: " + EtsLoadingContent.Layout + " could not be read.");
                return;
            }

            _bar = Screen["loading.bar.upper"];

            if (_bar != null)
            {
                // The bar's one image (a white texel stretched over the whole element, tinted clr_loading_bar) is made to
                // follow the element's width, so SetProgress only has to change that.
                foreach (RawImage piece in _bar.GetComponentsInChildren<RawImage>(true))
                {
                    var rt = (RectTransform)piece.transform;
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
            }

            SetProgress(0f);

            RectTransform icon = Screen["loading.icon"];
            _spinner = icon != null ? icon.GetComponentInChildren<RawImage>(true) : null;

            if (_spinner != null && !EtsGameUi.Painter.TryFlipsheet(EtsLoadingContent.Spinner, out _frames, out _columns, out _fps, out _frameU, out _frameV))
            {
                _spinner = null;
            }

            Debug.Log("[SomniumEuroTruck] Loading screen: shown - " + Screen.Elements.Count + " elements of " + EtsLoadingContent.Layout +
                      ", picture " + (Picture != null ? Picture.Name + " (" + Picture.Location + ")" : "none") + " of " + pictures.Count +
                      ", job line \"" + job + "\", spinner " + (_spinner != null ? _frames + " frames at " + _fps + " fps" : "none") + ".");
        }

        /// <summary>The orange bar at progress 0..1 (the map viewer's EtsMapViewer.LoadProgress, or a LoadPlan's).</summary>
        public void SetProgress(float progress01)
        {
            if (_bar != null)
            {
                _bar.sizeDelta = new Vector2(EtsLoadingContent.BarWidth(progress01), _bar.sizeDelta.y);
            }
        }

        /// <summary>
        /// In a headset, fixes the screen's panel in the truck the player sits in, EtsVrPanel.Distance ahead of the seat's
        /// eye (truck frame), instead of leaving it in the room (PLAN §3.5, D3): a passenger's screen is up while the
        /// ridden truck is drawn at its owner's pose, and the owner may drive off before this client's map is built, which
        /// would leave a room-fixed panel behind (EtsVrPanel only catches up every 2.5 m). Does nothing on a monitor (the
        /// screen is an overlay there) or without a truck. Hide destroys the panel wherever it hangs.
        /// </summary>
        public void RideWith(Transform truck, Vector3 eyeLocal)
        {
            EtsVrPanel panel = _surface != null ? _surface.GetComponent<EtsVrPanel>() : null;

            if (panel == null || truck == null)
            {
                return;
            }

            var rect = (RectTransform)_surface.transform;
            Vector3 ahead = eyeLocal + Vector3.forward * EtsVrPanel.Distance;
            panel.AttachTo(truck, ahead, eyeLocal, rect.sizeDelta * 0.5f, EtsVrPanel.Width);
            Debug.Log("[SomniumEuroTruck] Loading screen: fixed in the truck '" + truck.name + "', " + EtsVrPanel.Distance.ToString("0.0") + " m ahead of the seat's eye " + eyeLocal.ToString("0.00") + ".");
        }

        public void Hide()
        {
            if (Current == this)
            {
                Current = null;
            }

            Debug.Log("[SomniumEuroTruck] Loading screen: hidden after " + (Time.realtimeSinceStartup - _shownAt).ToString("0.0") + " s.");

            // RideWith moves the panel out from under this object: destroyed on its own, or it would stay in the truck.
            if (_surface != null)
            {
                Destroy(_surface);
                _surface = null;
            }

            Destroy(gameObject);
        }

        private void Update()
        {
            if (_spinner == null || _frames <= 0 || _fps <= 0f)
            {
                return;
            }

            int frame = (int)((Time.realtimeSinceStartup - _shownAt) * _fps) % _frames;

            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                _spinner.uvRect = EtsLoadingContent.FrameUv(frame, _frames, _columns, _frameU, _frameV);
            }
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }

            if (_surface != null)
            {
                Destroy(_surface);
                _surface = null;
            }
        }
    }
}
