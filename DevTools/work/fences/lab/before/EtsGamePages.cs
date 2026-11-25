using System;
using System.Collections.Generic;
using System.Globalization;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// What the game's pages write, from the game's own words (owner D; [gameui §3.2, §3.5, §5]). Pure, so the harness
    /// lays each value out through its template and checks it (probe 1893). Every value is a template's
    /// |-separated %0..%F; '|' never appears inside a part (Clean).
    /// </summary>
    public static class EtsGamePagesContent
    {
        public const string QuickJob = "/ui/quick_job.sii";
        public const string JobDetail = "/ui/quick_job_detail.sii";
        public const string Delivery = "/ui/delivery.sii";
        public const string TruckPreference = "/ui/truck_preference.sii";
        public const string TruckPreferenceRight = "/ui/truck_preference_right.sii";
        public const string Career = "/ui/career.sii";

        /// <summary>btn.job_offer is 618 x 118 (/ui/template/button.sii:2063); five rows and four 4-unit gaps fill the list portal's 606 exactly.</summary>
        public const int RowsPerPage = 5;
        public const float RowWidth = 618f, RowHeight = 118f, RowGap = 4f;

        private static readonly HashSet<string> QuickJobDropLooks = new HashSet<string>(StringComparer.Ordinal)
        {
            "btn.listbox.sorting", "btn.list.item.sorting", "txt.frame", "txt.ttip_simple", "txt.ttip_truck", "txt.thin.left.top", "txt.hhint_up", "txt.hhint_down",
            "txt.hhint_left", "btn.cargo_top", "btn.brand_top.new", "inp.input_left", "btn.input.clear", "txt.overlay.anchor", "null", "txt.window.bcg_rect10"
        };

        private static readonly HashSet<string> QuickJobDropKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "brand.group", "cargo.group", "trucktt", "icontt", "route_notify", "brand.top.button.arrow", "cargo.top.button.arrow", "search", "input.clear.button"
        };

        /// <summary>
        /// What of /ui/quick_job.sii this world builds: the window, both portals, "Take Job", the page arrows and dots,
        /// the empty state. The sort list and its label, the search line, the brand and cargo filters, the hints, the
        /// tooltips and the map's corner box are for a job market this world does not have (probe 1891 lists all 68).
        /// </summary>
        public static bool QuickJobKeep(EtsUiElement e)
        {
            if (QuickJobDropLooks.Contains(e.Look) || QuickJobDropKeys.Contains(e.Key)) return false;
            if (e.Look == "txt.normal.right" && e.Value.IndexOf("sort_by", StringComparison.Ordinal) >= 0) return false;
            if (e.Kind == EtsUiKind.Markup && e.Left >= 550f && e.Left <= 560f) return false; // the plate of the map's corner box (txt.thin.left.top)
            if (e.Type == "ui::list_common" || e.Type == "ui::droplist_common" || e.Type == "ui::inputline_common") return false;
            return true;
        }

        /// <summary>/ui/quick_job_detail.sii without its tooltip, route box, notice, controls anchor and the engine's 3D truck preview.</summary>
        public static bool JobDetailKeep(EtsUiElement e)
        {
            return e.Key != "city.tooltip" && e.Key != "route.info" && e.Key != "route.info.backg" && e.Key != "route_notify" && e.Type != "ui::overlay_point" &&
                   e.Type != "ui_vehicle_preview";
        }

        /// <summary>/ui/delivery.sii without the engine's reward table rows and button row (the online-job count is hidden by key).</summary>
        public static bool DeliveryKeep(EtsUiElement e)
        {
            return e.Type != "ui::button_row" && e.Type != "ui::table_row_list_common";
        }

        /// <summary>/ui/truck_preference.sii without its full-screen fader (a black ui_fullscreen over the whole page).</summary>
        public static bool TruckPreferenceKeep(EtsUiElement e)
        {
            return e.Key != "fader";
        }

        /// <summary>/ui/career.sii without the gamepad tab hints (txt.ovr_icon glyphs).</summary>
        public static bool CareerKeep(EtsUiElement e)
        {
            return e.Look != "txt.ovr_icon";
        }

        /// <summary>
        /// A job offer row (btn.job_offer): %0 the cargo (orange title), %1 -> %2 from and to city (either side of the
        /// arrow), %3 / %4 the grey lines (from and to company), %5 / %6 the company logos
        /// (/material/ui/company/small/&lt;token&gt;.mat), %7 / %8 the orange/white pair in the dark box (distance, pay),
        /// %9 the job icon and %A..%F the skill icons ("none": /material/ui/icon/none.mat exists, probe 1894). What
        /// each %n means is read from its position and colour in the markup, not stated anywhere ([gameui §3.5]).
        /// </summary>
        public static string OfferValue(string cargo, string fromCity, string toCity, string fromCompany, string toCompany,
                                        string fromLogo, string toLogo, float gameKm, long reward)
        {
            var parts = new string[16];
            parts[0] = Clean(cargo);
            parts[1] = Clean(fromCity);
            parts[2] = Clean(toCity);
            parts[3] = Clean(fromCompany);
            parts[4] = Clean(toCompany);
            parts[5] = Clean(fromLogo);
            parts[6] = Clean(toLogo);
            parts[7] = Km(gameKm);
            parts[8] = Clean(EtsLocale.Money(reward));

            for (int i = 9; i < 16; i++)
            {
                parts[i] = "none";
            }

            return string.Join("|", parts);
        }

        public static string OfferValue(EtsJobs.Offer offer)
        {
            return offer == null ? "" : OfferValue(offer.CargoName, offer.FromCityName, offer.ToCityName, offer.FromCompanyName, offer.ToCompanyName,
                                                   offer.From != null ? offer.From.Name : "", offer.To != null ? offer.To.Name : "", offer.GameKm, offer.Reward);
        }

        /// <summary>
        /// txt.jobdetail's eight parts (/ui/quick_job_detail.sii "values", sample "City 1|Company 1|City 2|Company 2|
        /// 00h 00 min|€1.000.-|€1.00 / km|10.000 kg"): from city / company, to city / company, the time expected ("-":
        /// jobs have no deadline until the session clock holds one, EtsJobs.Offer.Urgency), the income, per km, the mass.
        /// </summary>
        public static string DetailValue(string fromCity, string fromCompany, string toCity, string toCompany, long reward, float gameKm, float massKg)
        {
            string perKm = gameKm > 0.01f ? Prefix() + (reward / gameKm).ToString("0.00", CultureInfo.InvariantCulture) + Suffix() + " / " + EtsLocale.Word("km") : "-";
            return Clean(fromCity) + "|" + Clean(fromCompany) + "|" + Clean(toCity) + "|" + Clean(toCompany) + "|-|" + Clean(EtsLocale.Money(reward)) + "|" +
                   Clean(perKm) + "|" + EtsLocale.Group((long)Math.Round(massKg)) + " " + EtsLocale.Word("kg");
        }

        /// <summary>"$CARGO$ delivered from $ORIGIN$ to $DESTINATION$." (job_done_desc) filled in.</summary>
        public static string DoneLine(string cargo, string from, string to)
        {
            return EtsLocale.Word("job_done_desc").Replace("$CARGO$", cargo ?? "").Replace("$ORIGIN$", from ?? "").Replace("$DESTINATION$", to ?? "");
        }

        /// <summary>
        /// /ui/delivery.sii's values by key for one delivery. Distance is the route's game km (EtsJobs.Offer.GameKm, the km
        /// pay and XP are counted on); time is the real time the drive took, in the layout sample's "10 h. 45 min." form
        /// (no locale key found for it); the XP bar's words come from the career's level before and after (EtsXpTable).
        /// </summary>
        public static Dictionary<string, string> DeliveryValues(string cargo, string from, string to, float gameKm, float seconds, float fuelLitres,
                                                                long reward, long xpGained, long experienceAfter)
        {
            int level = EtsXpTable.LevelFor(experienceAfter, out long into, out long step);
            int minutes = (int)Math.Round(seconds / 60f);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "job.description", Clean(DoneLine(cargo, from, to)) },
                { "driven.distance.val", Km(gameKm) },
                { "time.taken.val", (minutes / 60) + " h. " + (minutes % 60) + " min." },
                { "fuel.consumed.val", Math.Round(fuelLitres).ToString(CultureInfo.InvariantCulture) + " l" },
                { "level.low", "@@profile_level@@ <color value=@@clr_white@@>" + level },
                { "level.high", "@@profile_level@@ <color value=@@clr_white@@>" + (level + 1) },
                { "current.xp", EtsLocale.Group(into) + "/" + EtsLocale.Group(step) + " @@xp@@" },
                { "current.level", Clean(EtsLocale.Word(EtsXpTable.NameKey(level))) },
                { "title.high", Clean(EtsLocale.Word(EtsXpTable.NameKey(level + 1))) },
                { "total.money", "<color value=@@clr_green@@>+ " + Clean(EtsLocale.Money(reward)) },
                { "total.experience", "<color value=@@clr_green@@>+ " + EtsLocale.Group(xpGained) + " @@xp@@" }
            };
        }

        /// <summary>The XP bar's share before and after a delivery (0..1 of the level's step), for level.bar and level.barup.</summary>
        public static void XpShares(long experienceBefore, long experienceAfter, out float before, out float after)
        {
            int levelAfter = EtsXpTable.LevelFor(experienceAfter, out long into, out long step);
            int levelBefore = EtsXpTable.LevelFor(experienceBefore);
            after = step > 0 ? Math.Min(1f, (float)into / step) : 1f;
            before = levelBefore < levelAfter ? 0f : step > 0 ? Math.Max(0f, (float)(into - (experienceAfter - experienceBefore)) / step) : 0f;
        }

        /// <summary>txt.truck_prefer.description's eight values (Series .. Fuel tank) from the truck catalogue (owner C).</summary>
        public static string DescriptionValue(EtsPreviewTruck truck)
        {
            if (truck == null || truck.SpecValues == null)
            {
                return "";
            }

            var parts = new string[8];

            for (int i = 0; i < 8; i++)
            {
                parts[i] = i < truck.SpecValues.Length ? Clean(truck.SpecValues[i]) : "";
            }

            return string.Join("|", parts);
        }

        /// <summary>The career's statistics rows (label, value): level and its name, experience, money, deliveries, distance.</summary>
        public static List<KeyValuePair<string, string>> CareerRows(long money, long experience, int jobsDelivered, long distanceMetres, long lifetimeEarnings)
        {
            int level = EtsXpTable.LevelFor(experience, out long into, out long step);
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(EtsLocale.Word("profile_level"), level + " - " + EtsLocale.Word(EtsXpTable.NameKey(level))),
                new KeyValuePair<string, string>(EtsLocale.Word("experience"), EtsLocale.Group(into) + "/" + EtsLocale.Group(step) + " " + EtsLocale.Word("xp")),
                new KeyValuePair<string, string>(EtsLocale.Word("money"), EtsLocale.Money(money)),
                new KeyValuePair<string, string>(EtsLocale.Word("career_progress_stats_delivery_count"), EtsLocale.Group(jobsDelivered)),
                new KeyValuePair<string, string>(EtsLocale.Word("distance_driven"), EtsLocale.Group(distanceMetres / 1000) + " " + EtsLocale.Word("km")),
                new KeyValuePair<string, string>(EtsLocale.Word("career_progres_delivery_reward"), EtsLocale.Money(lifetimeEarnings))
            };
        }

        /// <summary>One delivery log line ("cargo|from|to|km|money|xp|unix", EtsProfileStore.Deliveries) as a row of text; null when unreadable.</summary>
        public static string LogLine(string stored)
        {
            if (!EtsProfileCodec.TryParseDelivery(stored, out EtsDelivery d))
            {
                return null;
            }

            string when = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, d.Unix)).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return when + "   " + d.Cargo + "   " + d.From + " - " + d.To + "   " + EtsLocale.Group(d.Km) + " " + EtsLocale.Word("km") + "   " +
                   EtsLocale.Money(d.Money) + "   +" + EtsLocale.Group(d.Xp) + " " + EtsLocale.Word("xp");
        }

        /// <summary>"JOB COMPLETED" as "Job Completed": each word's first letter kept, the rest lowered (invariant culture).</summary>
        public static string TitleCase(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            var built = new System.Text.StringBuilder(text.Length);
            bool start = true;

            foreach (char c in text)
            {
                built.Append(start ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                start = c == ' ';
            }

            return built.ToString();
        }

        public static string Km(float gameKm)
        {
            return EtsLocale.Group((long)Math.Round(gameKm)) + " " + EtsLocale.Word("km");
        }

        private static string Prefix()
        {
            return EtsLocale.TryWord("europ", out string p) ? p : "€";
        }

        private static string Suffix()
        {
            return EtsLocale.TryWord("euros", out string s) ? s : "";
        }

        public static string Clean(string text)
        {
            return EtsLoadingContent.Clean(text);
        }
    }

    /// <summary>
    /// A tablet page built from one of the game's layout files (owner D; PLAN.md C3): the file's screen built into the
    /// page's Root inside an EtsUiHost scope (EtsUiSurface: a rectangle over the whole 1440 x 900 frame, no canvas of its
    /// own), so every coordinate lands where the game puts it and E's top and bottom bars draw over the margins the game
    /// leaves them. Build runs again, from scratch, whenever E re-hosts the page.
    /// </summary>
    public abstract class EtsGamePage : EtsTabletPage
    {
        protected EtsGameScreen Screen { get; private set; }
        protected RectTransform Surface { get; private set; }

        protected EtsGameScreen BuildGameScreen(string uiPath, IDictionary<string, string> values, ICollection<string> hidden, Func<EtsUiElement, bool> keep)
        {
            Screen = null;

            if (Root == null || EtsEngine.Files == null)
            {
                return null;
            }

            // The level steps and names (/def/economy_data_xp.sii) that Career and the delivery report print: G's
            // EtsProfileStore loads them in the world; without it (the editor, a store still waiting for its account) the
            // table would say "Level 0 -" with no name (probe 2045's first picture).
            if (!EtsXpTable.FromGame)
            {
                EtsXpTable.Load(EtsEngine.Files);
            }

            using (EtsUiHost.Into(Root))
            {
                GameObject surface = EtsUiSurface.Create("Ets page " + Id, 0);
                Surface = (RectTransform)surface.transform;
                Screen = EtsGameUi.BuildLayout(Surface, uiPath, values, hidden, keep);
            }

            if (Screen == null)
            {
                Debug.Log("[SomniumEuroTruck] Tablet page " + Id + ": " + uiPath + " could not be read.");
            }

            return Screen;
        }

        /// <summary>
        /// The tablet draws its navigation bar over the page's bottom (EtsTabletLayout.NavBottom..NavTop, 7..77), where a
        /// full-screen game screen puts buttons: Truck Preference's arrows, Confirm and Random at 48..80/96, the job
        /// detail's Resume Delivery and Abandon at 27..57 and 67..97 (probe 2046). Those are moved up, by the same step for
        /// every button of a screen so the row keeps its shape, until the lowest stands NavClearance above the bar. The
        /// only departure from the files' rectangles on these pages; the rest of each screen is where the file puts it.
        /// </summary>
        protected static void LiftAboveNavBar(EtsGameScreen screen, params string[] keys)
        {
            if (screen == null)
            {
                return;
            }

            float lowest = float.MaxValue;

            foreach (string key in keys)
            {
                EtsUiElement e = screen.ElementOf(key);
                if (e != null) lowest = Math.Min(lowest, e.Bottom);
            }

            float dy = lowest < float.MaxValue ? Math.Max(0f, EtsTabletLayout.NavTop + NavClearance - lowest) : 0f;

            if (dy <= 0f)
            {
                return;
            }

            foreach (string key in keys)
            {
                RectTransform rect = screen[key];
                if (rect != null) rect.anchoredPosition += new Vector2(0f, dy);
            }
        }

        /// <summary>The gap kept above the navigation bar's top edge (EtsTabletLayout.NavTop, 77).</summary>
        protected const float NavClearance = 11f;

        /// <summary>The key of the first built element matching (the files leave many unnamed).</summary>
        protected string KeyOf(Func<EtsUiElement, bool> match)
        {
            if (Screen != null)
            {
                foreach (EtsUiElement e in Screen.Elements)
                {
                    if (match(e)) return e.Key;
                }
            }

            return null;
        }

        /// <summary>A plain label in one of the game's faces, in frame coordinates, for what the files leave to the engine.</summary>
        protected TextMeshProUGUI Words(RectTransform parent, string name, string text, float left, float right, float bottom, float top, string face,
                                        TextAlignmentOptions alignment, string colourKey)
        {
            TextMeshProUGUI label = EtsGameUi.Label(EtsGameUi.Place(parent, name, left, right, bottom, top), text ?? "", face, alignment,
                                                    EtsGameUi.Colour(colourKey, EtsGameUi.TextColour));
            label.richText = true;
            return label;
        }
    }

    /// <summary>
    /// The desktop, the app's home (Category "home"; E draws its top bar and menu bar over it): the game's desktop
    /// colour and its ETS2 logo. The game's own desktop background is a 3D garage scene (/ui/desktop_background.sii, a
    /// ui_truck_scene_base) that a tablet page cannot hold.
    /// </summary>
    public sealed class EtsPageDesktop : EtsGamePage
    {
        public override string Caption => EtsLocale.Word("desktop");
        public override string Icon => "/material/ui/link/desktop.mat";
        public override string Id => "desktop";
        public override string Category => EtsTabletApp.HomeId;

        protected override void Build()
        {
            if (Root == null)
            {
                return;
            }

            RectTransform plate = EtsGameUi.Place(Root, "desktop", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);
            var fill = plate.gameObject.AddComponent<Image>();
            fill.color = EtsGameUi.Colour("clr_desktop_bg_main", EtsGameUi.PanelColour);
            fill.raycastTarget = false;
            Texture2D logo = EtsEngine.Files != null ? EtsGameUi.Material("/material/ui/logo/ets2_logo.mat") : null;

            if (logo != null)
            {
                EtsGameUi.PictureFit(EtsGameUi.Place(plate, "logo", 420f, 1020f, 400f, 700f), logo);
            }
        }
    }

    /// <summary>
    /// Jobs - Quick Job (/ui/quick_job.sii; the game's "jobs.quick" link): the offers of EtsJobs (owner G), five
    /// btn.job_offer rows a page in the list portal, the page arrows and dots, "Take Job" for the chosen row, the empty
    /// state (no_job_found with the busy spinner) while there are none, and the map portal showing the chosen job's
    /// from and to on the game's map picture. The file's sort list, search, brand and cargo filters, hints and tooltips
    /// are for a job market this world does not have and are left out. Taking a job asks EtsJobs.CanTake first (PLAN
    /// §3.5) and tells the reason on the tablet's toast when it may not.
    /// </summary>
    public sealed class EtsPageQuickJob : EtsGamePage
    {
        public override string Caption => EtsLocale.Word("job_quick");
        public override string Icon => "/material/ui/link/quick_job.mat";
        public override string Id => "quick_job";
        public override string Category => "jobs";
        public override string ScreenLink => "jobs.quick";

        private readonly List<RectTransform> _rows = new List<RectTransform>();
        private RectTransform _list, _mapPortal;
        private string _takeKey;
        private int _page, _chosen = -1, _seenCount = -1;
        private string _seenFirst = "";
        private TextMeshProUGUI _why;
        private string _seenWhy;

        protected override void Build()
        {
            _rows.Clear();
            _seenCount = -1;
            _seenWhy = null;

            if (BuildGameScreen(EtsGamePagesContent.QuickJob, null, null, EtsGamePagesContent.QuickJobKeep) == null)
            {
                return;
            }

            // Why "Take Job" is greyed (EtsJobs.CanTake, PLAN §3.5: "Choose your truck first (Company)", "Get out of
            // {name}'s truck first", ...), in the strip between the page arrows (140..172) and the job list (194..800) -
            // a place the file does not give; the words are not in the locale.
            _why = Words(Surface, "why not", "", 802f, 1420f, 174f, 193f, EtsFont.Small, TextAlignmentOptions.Left, "clr_sel");
            _why.textWrappingMode = TextWrappingModes.NoWrap;
            _why.enableAutoSizing = true;
            _why.fontSizeMin = 10f;
            _why.fontSizeMax = EtsFont.Size(EtsFont.Small);

            _takeKey = KeyOf(e => e.Look == "btn.normal");
            string listKey = KeyOf(e => e.Kind == EtsUiKind.Portal && e.Left >= 800f);
            string mapKey = KeyOf(e => e.Kind == EtsUiKind.Portal && e.Left < 100f);
            _list = Screen[listKey];
            _mapPortal = Screen[mapKey];

            if (_mapPortal != null)
            {
                _mapPortal.gameObject.AddComponent<RectMask2D>();
            }

            Screen.OnClick(_takeKey, Take);
            Screen.OnClick("prepage", () => Page(_page - 1));
            Screen.OnClick("nextpage", () => Page(_page + 1));

            for (int i = 0; i < 27; i++)
            {
                int page = i;
                Screen.OnClick("p" + i, () => Page(page));
            }

            Refresh();
        }

        public override void Shown()
        {
            Refresh();
        }

        public override void Tick()
        {
            EtsJobs jobs = EtsJobs.Instance;
            int count = jobs != null ? jobs.Offers.Count : 0;
            string first = count > 0 ? jobs.Offers[0].Id : "";

            if (count != _seenCount || first != _seenFirst || TakeBlockedBy(jobs) != _seenWhy)
            {
                Refresh();
            }
        }

        /// <summary>EtsJobs.CanTake's reason, "" when a job may be taken.</summary>
        private static string TakeBlockedBy(EtsJobs jobs)
        {
            if (jobs == null)
            {
                return "Reading the map"; // not in the locale (EtsJobs.CanTake's own words)
            }

            return jobs.CanTake(out string why) ? "" : why ?? "";
        }

        private void Refresh()
        {
            if (Screen == null)
            {
                return;
            }

            EtsJobs jobs = EtsJobs.Instance;
            List<EtsJobs.Offer> offers = jobs != null ? jobs.Offers : null;
            int count = offers != null ? offers.Count : 0;
            _seenCount = count;
            _seenFirst = count > 0 ? offers[0].Id : "";
            int pages = Math.Max(1, (count + EtsGamePagesContent.RowsPerPage - 1) / EtsGamePagesContent.RowsPerPage);
            _page = Math.Max(0, Math.Min(_page, pages - 1));

            if (_chosen >= count)
            {
                _chosen = -1;
            }

            foreach (RectTransform row in _rows)
            {
                if (row != null) UnityEngine.Object.Destroy(row.gameObject);
            }

            _rows.Clear();

            for (int i = 0; i < EtsGamePagesContent.RowsPerPage && _list != null; i++)
            {
                int index = _page * EtsGamePagesContent.RowsPerPage + i;

                if (index >= count)
                {
                    break;
                }

                float top = _list.sizeDelta.y - i * (EtsGamePagesContent.RowHeight + EtsGamePagesContent.RowGap);
                RectTransform row = EtsGameUi.Template(_list, "btn.job_offer", EtsGamePagesContent.OfferValue(offers[index]), 0f, EtsGamePagesContent.RowWidth,
                                                       top - EtsGamePagesContent.RowHeight, top, () => Choose(index));

                if (row != null)
                {
                    EtsGameUi.SetChosen(row, index == _chosen);
                    _rows.Add(row);
                }
            }

            string empty = KeyOf(e => e.Key == "job.empty.screen");
            Screen.SetShown(empty, count == 0);
            Screen.SetEnabled("prepage", _page > 0);
            Screen.SetEnabled("nextpage", _page < pages - 1);

            for (int i = 0; i < 27; i++)
            {
                Screen.SetShown("p" + i, count > EtsGamePagesContent.RowsPerPage && i < pages);
                Screen.SetChosen("p" + i, i == _page);
            }

            string why = TakeBlockedBy(jobs);
            bool can = why.Length == 0;
            _seenWhy = why;
            Screen.SetEnabled(_takeKey, _chosen >= 0 && can);

            if (_why != null && _why.text != why)
            {
                _why.text = why;
            }
            ShowRoute(_chosen >= 0 && _chosen < count ? offers[_chosen] : null);
        }

        private void Page(int page)
        {
            _page = page;
            Refresh();
        }

        private void Choose(int index)
        {
            _chosen = index;
            Refresh();
        }

        private void Take()
        {
            EtsJobs jobs = EtsJobs.Instance;

            if (jobs == null || _chosen < 0)
            {
                return;
            }

            if (!jobs.CanTake(out string reason))
            {
                App?.Toast(reason);
                return;
            }

            jobs.Take(_chosen);
            _chosen = -1;
        }

        /// <summary>The chosen job's two cities on the game's map picture (map.mat), framed on them (the route line is Phase 5).</summary>
        private void ShowRoute(EtsJobs.Offer offer)
        {
            if (_mapPortal == null)
            {
                return;
            }

            for (int i = _mapPortal.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_mapPortal.GetChild(i).gameObject);
            }

            EtsMapViewer viewer = EtsMapViewer.Current;

            if (offer == null || viewer == null)
            {
                return;
            }

            var frame = EtsUiMapFrame.Read(EtsEngine.Files);
            Vector3 centre = viewer.CenterGame;
            float fu = frame.U(offer.FromDepot.x + centre.x), fv = frame.V(centre.z - offer.FromDepot.z);
            float tu = frame.U(offer.ToBay.x + centre.x), tv = frame.V(centre.z - offer.ToBay.z);
            var view = new EtsHqMapView(frame)
            {
                PortalLeft = 0f, PortalRight = _mapPortal.sizeDelta.x, PortalBottom = 0f, PortalTop = _mapPortal.sizeDelta.y
            };
            float margin = Math.Max(0.004f, Math.Max(Math.Abs(fu - tu), Math.Abs(fv - tv)) * 0.25f);
            view.FitRange(Math.Min(fu, tu) - margin, Math.Max(fu, tu) + margin, Math.Min(fv, tv) - margin, Math.Max(fv, tv) + margin);

            // The whole map in one picture (map.mat), not the four quarter tiles: EtsFlow prefetched map.mat off the main
            // thread in the HQ void and it stays in the texture cache, while the quarters are four 2048-square BGRA32
            // pictures nobody prefetches - loading them here cost 391 ms of CPU and 64 MB of uploads on the frame of the
            // first offer chosen (probe 2056, Phase 2 check of D), a freeze in a headset. The price is half the texels at
            // this zoom (about 0.5 a frame unit for a 45 km route against the quarters' 1).
            view.WholeBounds(out float l, out float r, out float b, out float t);
            EtsGameUi.Picture(EtsGameUi.Place(_mapPortal, "map", l, r, b, t), EtsGameUi.Material(EtsHqMapView.Whole), false);

            Marker(view, fu, fv, "/material/ui/map/city_source_hint.mat");
            Marker(view, tu, tv, "/material/ui/map/city_destination_hint.mat");
        }

        private void Marker(EtsHqMapView view, float u, float v, string material)
        {
            Texture2D texture = EtsGameUi.Material(material);

            if (texture == null)
            {
                return;
            }

            Vector2 at = view.ToFrame(u, v);
            float w = texture.width, h = texture.height;
            EtsGameUi.PictureFit(EtsGameUi.Place(_mapPortal, "marker", at.x - w / 2f, at.x + w / 2f, at.y, at.y + h), texture);
        }
    }

    /// <summary>
    /// Jobs - the current job (/ui/quick_job_detail.sii, the game's job.detail.quick screen; not a menu link, so it
    /// joins the Jobs menu after the game's links): the cargo, from / to / expected / income / mass in txt.jobdetail,
    /// "Resume Delivery" (closes the tablet) and "Abandon" (asks once more: a second press within 4 s abandons, the
    /// confirmation's form is not the game's). With no job: the locale's no_job line.
    /// </summary>
    public sealed class EtsPageCurrentJob : EtsGamePage
    {
        public override string Caption => EtsLocale.Word("current_job_title");
        public override string Icon => "/material/ui/link/quick_job.mat";
        public override string Id => "current_job";
        public override string Category => "jobs";

        private TextMeshProUGUI _none;
        private string _shownJob = "";
        private float _armedUntil;

        protected override void Build()
        {
            _shownJob = "\u0000";

            if (BuildGameScreen(EtsGamePagesContent.JobDetail, null, null, EtsGamePagesContent.JobDetailKeep) == null)
            {
                return;
            }

            LiftAboveNavBar(Screen, "resume", "abort.job");
            // Closes the app wherever it is: the Somnium tablet, or the world's own panel when there is none.
            Screen.OnClick("resume", () => App?.Close());
            Screen.OnClick("abort.job", Abandon);

            for (int i = 0; i < 6; i++)
            {
                Screen.Set("icon" + i, "none");
            }

            _none = Words(Surface, "no job", EtsLocale.Word("no_job"), 17f, 1423f, 400f, 460f, EtsFont.Big, TextAlignmentOptions.Center, "clr_txt");
            Refresh();
        }

        public override void Shown()
        {
            Refresh();
        }

        public override void Tick()
        {
            Refresh();
        }

        private void Refresh()
        {
            if (Screen == null)
            {
                return;
            }

            EtsJobs.Offer job = EtsJobs.Instance != null ? EtsJobs.Instance.Current : null;
            string id = job != null ? job.Id : "";

            if (id == _shownJob)
            {
                return;
            }

            _shownJob = id;
            bool has = job != null;

            foreach (EtsUiElement e in Screen.Elements)
            {
                if (e.Key != "backg") Screen.SetShown(e.Key, has);
            }

            if (_none != null) _none.gameObject.SetActive(!has);

            if (has)
            {
                Screen.Set("cargo.name", EtsGamePagesContent.Clean(job.CargoName));
                Screen.Set("values", EtsGamePagesContent.DetailValue(job.FromCityName, job.FromCompanyName, job.ToCityName, job.ToCompanyName, job.Reward,
                                                                     job.GameKm, job.CargoMass));
            }
        }

        private void Abandon()
        {
            EtsJobs jobs = EtsJobs.Instance;

            if (jobs == null || jobs.Current == null)
            {
                return;
            }

            if (Time.unscaledTime > _armedUntil)
            {
                _armedUntil = Time.unscaledTime + 4f;
                App?.Toast(EtsLocale.Word("abort_job") + "?"); // the question mark is not the game's
                return;
            }

            _armedUntil = 0f;
            jobs.Abandon();
            Refresh();
        }
    }

    /// <summary>
    /// Jobs - the delivery report (/ui/delivery.sii, the game's job.delivery screen, Q-E2: in the tablet, not full
    /// view): the last delivery's words and numbers by key (EtsGamePagesContent.DeliveryValues), the XP bar from the
    /// career's experience before and after it. EtsFlow hands each delivery over (Record) and selects the page. The reward table's
    /// rows and the online-job count are the engine's and left out; the medal keeps the file's (the world grades no
    /// delivery).
    /// </summary>
    public sealed class EtsPageDelivery : EtsGamePage
    {
        /// <summary>The page's Id: EtsFlow selects it after a delivery (Q-E2).</summary>
        public const string PageId = "delivery";

        /// <summary>
        /// The menu word: the screen's own title, "JOB COMPLETED" (job_completed_t, "keep in upper case where possible"),
        /// in the case the menu's other links use ("Job Completed") - in capitals it broke over three lines in the tablet's
        /// navigation bar (probe 2045). The locale's "Delivery Report" (delivery_info) is commented out on 1.61.
        /// </summary>
        public override string Caption => EtsGamePagesContent.TitleCase(EtsLocale.Word("job_completed_t"));
        public override string Icon => "/material/ui/link/job_log.mat";
        public override string Id => PageId;
        public override string Category => "jobs";

        private static EtsJobs.DeliveryStats _last;
        private static long _experienceBefore, _experienceAfter;
        private static int _version;
        private int _shown = -1;
        private TextMeshProUGUI _none;

        /// <summary>A delivery to report (EtsFlow, from EtsJobs.Delivered), with the career's experience before and after it.</summary>
        public static void Record(EtsJobs.DeliveryStats stats, long experienceBefore, long experienceAfter)
        {
            _last = stats;
            _experienceBefore = experienceBefore;
            _experienceAfter = experienceAfter;
            _version++;
        }

        protected override void Build()
        {
            _shown = -1;
            Refresh();
        }

        public override void Shown()
        {
            Refresh();
        }

        /// <summary>
        /// A delivery made while this page was already the current one (EtsFlow selects it; the app calls Shown only on a
        /// switch): the 4 Hz tick sees the new record and rebuilds. Nothing else happens per tick.
        /// </summary>
        public override void Tick()
        {
            Refresh();
        }

        private void Refresh()
        {
            if (Root == null || _shown == _version)
            {
                return;
            }

            _shown = _version;

            if (Surface != null)
            {
                UnityEngine.Object.Destroy(Surface.gameObject);
            }

            if (_none != null)
            {
                UnityEngine.Object.Destroy(_none.gameObject);
                _none = null;
            }

            if (_last == null || _last.Job == null)
            {
                _none = Words(Root, "no delivery", EtsLocale.Word("no_job"), 17f, 1423f, 400f, 460f, EtsFont.Big, TextAlignmentOptions.Center, "clr_txt");
                return;
            }

            EtsJobs.Offer job = _last.Job;
            Dictionary<string, string> values = EtsGamePagesContent.DeliveryValues(job.CargoName, job.FromCityName, job.ToCityName, job.GameKm, _last.Seconds,
                                                                                  _last.FuelLitres, _last.Reward, _last.Xp, _experienceAfter);

            if (BuildGameScreen(EtsGamePagesContent.Delivery, values, new[] { "online.job.count" }, EtsGamePagesContent.DeliveryKeep) == null)
            {
                return;
            }

            EtsGamePagesContent.XpShares(_experienceBefore, _experienceAfter, out float before, out float after);
            Bar("level.bar", 0f, before);
            Bar("level.barup", before, after);
        }

        /// <summary>A bar element (one stretched white texel) shown over [from, to] of its width.</summary>
        private void Bar(string key, float from, float to)
        {
            RectTransform bar = Screen[key];
            EtsUiElement e = Screen.ElementOf(key);

            if (bar == null || e == null)
            {
                return;
            }

            float width = e.Right - e.Left;
            bar.anchoredPosition = new Vector2(e.Left + width * from, bar.anchoredPosition.y);
            bar.sizeDelta = new Vector2(Mathf.Max(0f, width * (to - from)), bar.sizeDelta.y);

            foreach (RawImage piece in bar.GetComponentsInChildren<RawImage>(true))
            {
                var rt = (RectTransform)piece.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
        }
    }

    /// <summary>
    /// Company - Truck Preference (/ui/truck_preference.sii and its _right part; the page [gameui §3.2] describes), in
    /// the Company menu at the game's own "screen.truckman" link (Truck Manager; the page's title is the file's
    /// preffered_truck_title, "Truck Preference" - PLAN §12, D2/E2): the title and subtitle, the brand logo
    /// (txt.brand.logo.left.bottom), the eight spec rows from C's EtsTruckCatalog, the page arrows, "Confirm" and
    /// "Random", over the game's dark studio with the chosen truck standing in it (EtsTruckStudio: /def/empty_dark_scene.sii
    /// drawn into a texture behind the layout, as /ui/truck_preference_bcg.sii puts its ui_truck_scene_base under the page).
    ///
    /// A truck the harness has not verified (EtsPreviewTruck.BuildVerified) shows with Confirm greyed and "Not yet
    /// available"; one not yet driven in a Somnium client (ClientConfirmed, PLAN §12) carries a note only. Confirm spawns
    /// the truck at the yard's stand (EtsNet.SpawnOwnTruck, owner A; the reason from EtsNet.CanSpawnTruck on the toast
    /// when it may not, e.g. "Finish or cancel your job first") and keeps it in the career (EtsProfileStore.SetTruck).
    /// The first truck shown is the career's, else the own truck's, else the DAF XF Euro 6 (the one truck confirmed in
    /// the client). The full-screen fader and the brand filter (_left) are left out; the studio's floor reflection is
    /// not reproduced (EtsGarageScene).
    /// </summary>
    public sealed class EtsPageTruckPreference : EtsGamePage
    {
        public override string Caption => EtsLocale.Word("preffered_truck_title");
        public override string Icon => "/material/ui/link/truck_man.mat";
        public override string Id => EtsTabletApp.TruckPreferenceId;
        public override string Category => "fleet";

        /// <summary>The game's Company link this page stands for (/ui/!desc.sii screen.truckman, "Truck Manager"; probe 1900).</summary>
        public override string ScreenLink => "screen.truckman";

        /// <summary>The truck the page opens on when the career names none (the verified, client-driven DAF).</summary>
        public const string DefaultStem = "daf_xf_euro6";

        /// <summary>Not in the locale (PLAN §3.1).</summary>
        public const string NotYetAvailable = "Not yet available";

        /// <summary>Not in the locale (PLAN §12: a note only).</summary>
        public const string NotYetDriven = "Not yet driven in Somnium";

        private int _index = -1;
        private EtsGameScreen _right;
        private RawImage _studioPicture;
        private TextMeshProUGUI _note;

        /// <summary>EtsNet.SpawningTruck as last shown (A's request): Confirm greyed and the note saying so while a truck is built or placed.</summary>
        private bool _spawningShown;

        /// <summary>EtsNet.CanSpawnTruck's own words for a spawn under way (not in the locale).</summary>
        public const string OnItsWay = "Your truck is on its way.";

        private static bool Spawning()
        {
            EtsNet net = EtsNet.Instance;
            return net != null && net.SpawningTruck;
        }

        /// <summary>The career's truck, else the own truck's, else DefaultStem, else the first; -1 without a catalogue.</summary>
        public static int DefaultIndex(EtsTruckCatalog catalog)
        {
            if (catalog == null || catalog.Trucks.Count == 0)
            {
                return -1;
            }

            EtsProfileStore store = EtsProfileStore.Current;
            int kept = store != null && store.IsReady && store.Profile != null ? catalog.IndexOf(store.Profile.TruckUnit) : -1;

            if (kept < 0 && EtsLocalTruck.Own != null)
            {
                kept = catalog.IndexOf(EtsLocalTruck.Own.Stem);
            }

            if (kept < 0)
            {
                kept = catalog.IndexOf(DefaultStem);
            }

            return kept >= 0 ? kept : 0;
        }

        private static EtsTruckCatalog Catalog()
        {
            // Warmed in the HQ void by EtsFlow; loaded here if not (21 ms plus the locale, probe 1862).
            return EtsTruckCatalog.Current != null ? EtsTruckCatalog.Current : EtsEngine.Files != null ? EtsTruckCatalog.Load(EtsEngine.Files) : null;
        }

        protected override void Build()
        {
            EtsTruckCatalog catalog = Catalog();

            if (_index < 0)
            {
                _index = DefaultIndex(catalog);
            }

            if (BuildGameScreen(EtsGamePagesContent.TruckPreference, null, null, EtsGamePagesContent.TruckPreferenceKeep) == null)
            {
                return;
            }

            // The studio's picture under everything the layout draws (black until the studio has drawn).
            RectTransform backdrop = EtsGameUi.Place(Surface, "studio", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);
            backdrop.SetAsFirstSibling();
            _studioPicture = backdrop.gameObject.AddComponent<RawImage>();
            _studioPicture.color = Color.black;
            _studioPicture.raycastTarget = false;

            _right = EtsGameUi.BuildLayout(Surface, EtsGamePagesContent.TruckPreferenceRight, null, null);
            LiftAboveNavBar(Screen, "left", "confirm", "right");
            LiftAboveNavBar(_right, "random.button", "random.text");
            // Under the eight spec rows (656 down, about 21 units a row), a place the file does not give.
            _note = Words(Surface, "note", "", 1103f, 1420f, 400f, 430f, EtsFont.Normal, TextAlignmentOptions.Left, "clr_txt_d");

            Screen.OnClick("left", () => Step(-1));
            Screen.OnClick("right", () => Step(1));
            Screen.OnClick("confirm", Confirm);
            _right?.OnClick("random.button", Random);
            Show();
        }

        public override void Shown()
        {
            Wake();
        }

        public override void Tick()
        {
            Wake();

            // 4 Hz; the page changes only when the spawn starts or ends (a spawn takes about 0.2-2 s, EtsTruckFactory.LastPace).
            if (Spawning() != _spawningShown)
            {
                Show();
            }
        }

        public override void Hidden()
        {
            EtsTruckStudio.Current?.Sleep();
        }

        /// <summary>The studio drawing for the next 0.6 s with this page's truck in it (the app ticks a page at 4 Hz while it is on screen).</summary>
        private void Wake()
        {
            EtsTruckStudio studio = EtsTruckStudio.Get();

            if (studio == null)
            {
                return;
            }

            studio.Want(_index);
            studio.KeepAwake();

            if (_studioPicture != null && _studioPicture.texture != studio.Texture)
            {
                _studioPicture.texture = studio.Texture;
                _studioPicture.color = Color.white;
                _studioPicture.uvRect = new Rect(0f, 0f, 1f, 1f);
            }
        }

        private void Step(int by)
        {
            EtsTruckCatalog catalog = Catalog();
            int n = catalog != null ? catalog.Trucks.Count : 0;
            if (n == 0) return;
            _index = ((Math.Max(0, _index) + by) % n + n) % n;
            Show();
        }

        private void Random()
        {
            EtsTruckCatalog catalog = Catalog();

            if (catalog == null || catalog.Trucks.Count == 0)
            {
                return;
            }

            var verified = new List<int>();

            for (int i = 0; i < catalog.Trucks.Count; i++)
            {
                if (catalog.Trucks[i].BuildVerified) verified.Add(i);
            }

            if (verified.Count > 0)
            {
                _index = verified[UnityEngine.Random.Range(0, verified.Count)];
                Show();
            }
        }

        private void Show()
        {
            EtsTruckCatalog catalog = Catalog();
            EtsPreviewTruck truck = catalog != null && _index >= 0 && _index < catalog.Trucks.Count ? catalog.Trucks[_index] : null;

            if (Screen == null)
            {
                return;
            }

            Screen.Set("logo", truck != null ? truck.Brand ?? "" : "");
            Screen.Set("desc", EtsGamePagesContent.DescriptionValue(truck));
            _spawningShown = Spawning();
            Screen.SetEnabled("confirm", truck != null && truck.BuildVerified && !_spawningShown);

            if (_note != null)
            {
                _note.text = truck == null ? "" : _spawningShown ? OnItsWay : !truck.BuildVerified ? NotYetAvailable : !truck.ClientConfirmed ? NotYetDriven : "";
                _note.color = EtsGameUi.Colour(truck != null && !truck.BuildVerified ? "clr_sel" : "clr_txt_d", EtsGameUi.TextColour);
            }

            if (EtsTruckStudio.Current != null)
            {
                EtsTruckStudio.Current.Want(_index);
            }

            if (truck != null)
            {
                EtsGameUi.Note("Truck Preference: " + truck.Stem + " (" + truck.TruckId + ")" + (truck.BuildVerified ? "" : " - not yet available"));
            }
        }

        private void Confirm()
        {
            EtsTruckCatalog catalog = Catalog();
            EtsPreviewTruck truck = catalog != null && _index >= 0 && _index < catalog.Trucks.Count ? catalog.Trucks[_index] : null;
            EtsNet net = EtsNet.Instance;

            if (truck == null || !truck.BuildVerified)
            {
                App?.Toast(NotYetAvailable);
                return;
            }

            string reason = "Not available in this world."; // not in the locale (E's WhyNot)

            if (net == null || !net.CanSpawnTruck(out reason))
            {
                App?.Toast(reason);
                return;
            }

            EtsProfileStore.Current?.SetTruck(truck.Stem, "");
            Debug.Log("[SomniumEuroTruck] Truck Preference: confirmed " + truck.Stem + " (" + truck.TruckId + ") - asking for it at the yard.");
            net.SpawnOwnTruck(catalog.Identity(truck.Stem, EtsTruckIdentity.SavePaint),
                              rig => EtsGameUi.Note("truck " + truck.Stem + " spawned"),
                              failure => App?.Toast(failure));
        }
    }

    /// <summary>
    /// Career - Career Progress (/ui/career.sii, the game's "career.progress" link): its two tabs, Statistics and
    /// Delivery Log, over the content portal the engine fills in the game. Here the portal holds the career record's
    /// own numbers (EtsProfileStore, owner G) as label / value rows in txt.normal.left / txt.emph.right, and the log's
    /// last ten lines; which rows the game's statistics tab shows is the engine's, so these are the record's, under the
    /// locale's own labels.
    /// </summary>
    public sealed class EtsPageCareer : EtsGamePage
    {
        public override string Caption => EtsLocale.Word("career_progress");
        public override string Icon => "/material/ui/link/career_progress.mat";
        public override string Id => "career";
        public override string Category => "personal";
        public override string ScreenLink => "career.progress";

        private bool _log;
        private RectTransform _content;
        private long _seenGeneration = -1;

        protected override void Build()
        {
            _seenGeneration = -1;

            if (BuildGameScreen(EtsGamePagesContent.Career, null, null, EtsGamePagesContent.CareerKeep) == null)
            {
                return;
            }

            Screen.OnClick("statistics.tab.button", () => Tab(false));
            Screen.OnClick("delivery.log.tab.button", () => Tab(true));
            Tab(_log);
        }

        public override void Shown()
        {
            _seenGeneration = -1;
            Fill();
        }

        public override void Tick()
        {
            EtsProfileStore store = EtsProfileStore.Current;
            long generation = store != null && store.Profile != null ? store.Profile.Generation : 0;

            if (generation != _seenGeneration)
            {
                Fill();
            }
        }

        private void Tab(bool log)
        {
            _log = log;
            Screen?.SetChosen("statistics.tab.button", !log);
            Screen?.SetChosen("delivery.log.tab.button", log);
            _seenGeneration = -1;
            Fill();
        }

        private void Fill()
        {
            RectTransform portal = Screen != null ? Screen["content.portal"] : null;

            if (portal == null)
            {
                return;
            }

            if (_content != null)
            {
                UnityEngine.Object.Destroy(_content.gameObject);
            }

            _content = EtsGameUi.Place(portal, "content", 0f, portal.sizeDelta.x, 0f, portal.sizeDelta.y);
            EtsProfileStore store = EtsProfileStore.Current;
            EtsProfileRecord record = store != null ? store.Profile : null;
            _seenGeneration = record != null ? record.Generation : 0;
            float top = portal.sizeDelta.y - 20f;

            if (!_log)
            {
                List<KeyValuePair<string, string>> rows = EtsGamePagesContent.CareerRows(store != null ? store.Money : EtsProfileCodec.StartingMoney,
                    store != null ? store.Experience : 0, record != null ? record.JobsDelivered : 0, record != null ? record.DistanceMetres : 0,
                    record != null ? record.LifetimeEarnings : 0);

                foreach (KeyValuePair<string, string> row in rows)
                {
                    EtsGameUi.Template(_content, "txt.normal.left", EtsGamePagesContent.Clean(row.Key), 380f, 700f, top - 30f, top);
                    EtsGameUi.Template(_content, "txt.emph.right", EtsGamePagesContent.Clean(row.Value), 700f, 1020f, top - 30f, top);
                    top -= 34f;
                }

                return;
            }

            IReadOnlyList<string> lines = store != null ? store.Deliveries : null;

            if (lines == null || lines.Count == 0)
            {
                EtsGameUi.Template(_content, "txt.normal.center", EtsGamePagesContent.Clean(EtsLocale.Word("no_job")), 0f, portal.sizeDelta.x, top - 30f, top);
                return;
            }

            foreach (string stored in lines)
            {
                string line = EtsGamePagesContent.LogLine(stored);
                if (line == null) continue;
                EtsGameUi.Template(_content, "txt.normal.left", EtsGamePagesContent.Clean(line), 40f, portal.sizeDelta.x - 40f, top - 30f, top);
                top -= 34f;
            }
        }
    }

    /// <summary>
    /// The game's own screens as pages of the tablet app: Desktop, Quick Job, Current Job, Delivery, Truck
    /// Preference, Career (owner D; [gameui §3.2, §3.4, §3.5], PLAN.md C3, §7.D). Each derives from E's
    /// EtsTabletPage and builds with using (EtsUiHost.Into(Root)) into the page (EtsGamePage). E's EtsTabletApp adds
    /// them in Start - it exists only in the new flow (EtsNet.Awake calls EtsTabletApp.Ensure only when
    /// EtsFeatures.NewFlow) or from the editor menu, so with the flag off none of this is built.
    /// </summary>
    public static class EtsGamePages
    {
        public static IEnumerable<EtsTabletPage> Create()
        {
            return new EtsTabletPage[]
            {
                new EtsPageDesktop(), new EtsPageQuickJob(), new EtsPageCurrentJob(), new EtsPageDelivery(), new EtsPageTruckPreference(), new EtsPageCareer()
            };
        }
    }
}
