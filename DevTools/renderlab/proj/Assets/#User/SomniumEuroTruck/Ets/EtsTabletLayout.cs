using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>One category of the game's desktop menu bar (/def/ui_desktop_ind_full.sii), as read.</summary>
    public sealed class EtsMenuCategory
    {
        public string Key;      // the unit name: drive, jobs, maps, online, personal, fleet, services, browsers, media, quit
        public string Icon;     // /material/ui/link/<Icon>.mat
        public string NameKey;  // "@@desktop_menu_category_jobs@@"
        public bool NoMenu;     // type no_menu (Drive: straight back to driving)
        public int Group;       // the separators split the bar into groups
        public readonly List<EtsMenuLink> Links = new List<EtsMenuLink>();
    }

    /// <summary>One screen link of a category, with its /ui/!desc.sii icon and label key.</summary>
    public sealed class EtsMenuLink
    {
        public string ScreenLink; // "jobs.quick"
        public string Icon;       // "quick_job"
        public string LabelKey;   // "@@job_quick@@"
    }

    /// <summary>
    /// The tablet app's arithmetic and rules, Unity-free so the harness tests exactly what the app runs (owner E;
    /// probe 1902). Every rectangle is in the game's 1440 x 900 frame, x from the left and y up from the bottom
    /// (/def/ui.sii ref_res_x 1440, ref_res_y 900), as EtsGameUi.Place takes it; each is quoted from the 1.61 layout
    /// file named beside it (dumps in S/design/gameui/dump, read from the player's install by probe 1641).
    ///
    /// Two things are not in the files and are marked "placement": where the engine puts the menu bar's buttons
    /// inside its 1426-unit strip (the ui_desktop_menu_bar unit gives only the strip and the looks), and the gap
    /// the two "separator" categories leave. They are centred, with the menu's own margin (7) as the gap.
    /// </summary>
    public static class EtsTabletLayout
    {
        public const float FrameWidth = 1440f;
        public const float FrameHeight = 900f;

        // /ui/desktop_top_bar.sii: the plate txt.window.bcg_rect4 @@clr_desktop_bg_main@@ at 7..1433 x 853..893 and
        // its sections (burger 17..65 is the game's own burger menu and is not drawn: it has no page here).
        public const float TopBarLeft = 7f, TopBarRight = 1433f, TopBarBottom = 853f, TopBarTop = 893f;
        public const float HomeLeft = 66f, HomeRight = 110f;
        public const float PersonalLeft = 111f, PersonalRight = 367f;
        public const float CompanyLeft = 367f, CompanyRight = 623f;
        public const float NotificationLeft = 637f, NotificationRight = 1037f;
        public const float CountryLeft = 1041f, CountryRight = 1105f;
        public const float BankLeft = 1125f, BankRight = 1265f;
        public const float ClockLeft = 1285f, ClockRight = 1425f;

        /// <summary>btn.desktop.top_bar.personal / .company: the 32 x 32 picture at x + 9, text from x + 50 / x + 48.</summary>
        public const float BadgeIconLeft = 9f, BadgeIconSize = 32f, CompanyTextLeft = 48f, PersonalTextLeft = 50f;

        /// <summary>btn.desktop.top_bar.personal: the XP bar, 192 x 14 (clr_personal_progress_bar[_bg]).</summary>
        public const int XpBarWidth = 192;

        // /ui/desktop.sii ui_desktop_menu_bar at 7..1433 x 7..133; btn.desktop_link and btn.desktop.drive are 94 x 126;
        // menu_horizontal_margin, menu_top_margin, menu_bottom_margin, menu_y_offset, menu_to_bounds_offset are all 7.
        public const float BarLeft = 7f, BarRight = 1433f, BarBottom = 7f, BarTop = 133f;
        public const float LinkWidth = 94f, LinkHeight = 126f;
        public const float MenuMargin = 7f;

        // btn.desktop.screen_link is 200 x 60; every category of /def/ui_index_full_config.sui has column_count 2.
        public const float ScreenLinkWidth = 200f, ScreenLinkHeight = 60f;
        public const int ScreenLinkColumns = 2;

        // /ui/navigation_bar.sii: the plate at 7..1433 x 7..77, the button row 78..1362 x 7..77 of
        // btn.screen_link.nav_bar (145 x 70).
        public const float NavLeft = 7f, NavRight = 1433f, NavBottom = 7f, NavTop = 77f;
        public const float NavRowLeft = 78f, NavRowRight = 1362f, NavLinkWidth = 145f, NavLinkHeight = 70f;

        /// <summary>/ui/quick_job.sii's window, 7..1433 x 80..850: what a screen owns between the two bars.</summary>
        public const float ScreenLeft = 7f, ScreenRight = 1433f, ScreenBottom = 80f, ScreenTop = 850f;

        /// <summary>
        /// Lays the menu bar's buttons out: groups (split by the "separator" categories) of LinkWidth buttons, centred
        /// in BarLeft..BarRight with <paramref name="gap"/> between groups (placement). Writes each button's left edge
        /// into <paramref name="lefts"/> in order and returns how many; a bar too wide for the strip starts at BarLeft.
        /// </summary>
        public static int LayOutMenuBar(int[] groupSizes, float gap, float[] lefts)
        {
            int buttons = 0;
            int groups = 0;

            foreach (int size in groupSizes)
            {
                if (size > 0)
                {
                    buttons += size;
                    groups++;
                }
            }

            float width = buttons * LinkWidth + Math.Max(0, groups - 1) * gap;
            float x = Math.Max(BarLeft, BarLeft + (BarRight - BarLeft - width) * 0.5f);
            int written = 0;
            bool first = true;

            foreach (int size in groupSizes)
            {
                if (size <= 0)
                {
                    continue;
                }

                if (!first)
                {
                    x += gap;
                }

                first = false;

                for (int i = 0; i < size && written < lefts.Length; i++)
                {
                    lefts[written++] = x;
                    x += LinkWidth;
                }
            }

            return written;
        }

        /// <summary>
        /// The look a category button takes by its place in its group, as ui_desktop_menu_bar names them
        /// (category_button_look[_left|_right|_single]); the hover plate of .left/.right/.single rounds the group's ends.
        /// </summary>
        public static string LinkLook(int indexInGroup, int groupSize)
        {
            if (groupSize <= 1) return "btn.desktop_link.single";
            if (indexInGroup == 0) return "btn.desktop_link.left";
            if (indexInGroup == groupSize - 1) return "btn.desktop_link.right";
            return "btn.desktop_link";
        }

        /// <summary>
        /// A category's menu of screen links (menu_look txt.window.bcg_rect4): ScreenLinkColumns columns of
        /// btn.desktop.screen_link rows inside a MenuMargin frame, standing MenuMargin above the bar
        /// (menu_y_offset), centred on its button and kept MenuMargin inside the frame (menu_to_bounds_offset).
        /// Row r, column c of the menu is at left + MenuMargin + c * ScreenLinkWidth, top - MenuMargin - (r + 1) * ScreenLinkHeight.
        /// </summary>
        public static void Popup(float buttonLeft, float buttonRight, int links, out float left, out float right, out float bottom, out float top)
        {
            int columns = Math.Max(1, Math.Min(ScreenLinkColumns, links));
            int rows = Math.Max(1, (links + columns - 1) / columns);
            float width = columns * ScreenLinkWidth + 2f * MenuMargin;
            float height = rows * ScreenLinkHeight + 2f * MenuMargin;
            float centre = (buttonLeft + buttonRight) * 0.5f;
            left = centre - width * 0.5f;
            left = Math.Max(MenuMargin, Math.Min(FrameWidth - MenuMargin - width, left));
            right = left + width;
            bottom = BarTop + MenuMargin;
            top = bottom + height;
        }

        /// <summary>
        /// Link <paramref name="index"/> of <paramref name="links"/> in the navigation bar's row, the row centred in
        /// NavRowLeft..NavRowRight (placement). Eight 145-unit links fit the 1284-unit row; more share it evenly.
        /// </summary>
        public static void NavLink(int links, int index, out float left, out float right)
        {
            float row = NavRowRight - NavRowLeft;
            float each = links > 0 ? Math.Min(NavLinkWidth, row / links) : NavLinkWidth;
            float start = NavRowLeft + (row - links * each) * 0.5f;
            left = start + index * each;
            right = left + each;
        }

        /// <summary>How the 1440 x 900 frame is scaled into a host rectangle: whole, centred, aspect kept.</summary>
        public static float Fit(float width, float height)
        {
            return Math.Min(width / FrameWidth, height / FrameHeight);
        }

        /// <summary>
        /// The Somnium tablet's HomeTab canvas is 960 x 1080 Small and 1920 x 1080 Large
        /// (Tablet.XR.HomeTab.prefab:424-434); Large is anything wider than the frame.
        /// </summary>
        public static bool IsLarge(float homeWidth)
        {
            return homeWidth > FrameWidth;
        }

        /// <summary>The Resize button is pressed only when it exists, is wanted, and the tablet measures Small ([tablet §3] row 9).</summary>
        public static bool ShouldPressResize(bool forceLarge, bool haveResize, float homeWidth)
        {
            return forceLarge && haveResize && !IsLarge(homeWidth);
        }

        /// <summary>The XP bar's lit width for a level progress 0..1.</summary>
        public static int XpBar(float progress)
        {
            if (float.IsNaN(progress)) progress = 0f;
            return (int)Math.Round(XpBarWidth * Math.Max(0f, Math.Min(1f, progress)));
        }

        /// <summary>
        /// The menu bar as the player's install defines it: the categories in order from /def/ui_desktop_ind_full.sii
        /// and its includes (ui_index_full_config.sui, ui_quit_config.sui), the "separator" units splitting them into
        /// groups, and each screen link's icon and label from /ui/!desc.sii. A "*.selector" link has no icon of its
        /// own and names the screens it chooses between: it takes the first of those that has one (probe 1901:
        /// options.selector -> options.overview, icon "options", label @@options@@). On 1.61 this is 10 categories
        /// in 3 groups with 40 links (probes 1900-1902). Null when the files cannot be read.
        /// </summary>
        public static List<EtsMenuCategory> ReadMenu(EtsFileSystem files)
        {
            if (files == null)
            {
                return null;
            }

            string barText = files.ReadText("/def/ui_desktop_ind_full.sii");

            if (barText == null)
            {
                return null;
            }

            EtsSiiFile bar = EtsSii.Parse(barText, "/def/ui_desktop_ind_full.sii", files.ReadText);
            var descs = new Dictionary<string, EtsSiiUnit>(StringComparer.Ordinal);
            string descText = files.ReadText("/ui/!desc.sii");

            if (descText != null)
            {
                foreach (EtsSiiUnit unit in EtsSii.Parse(descText, "/ui/!desc.sii", files.ReadText).Units)
                {
                    if (!string.IsNullOrEmpty(unit.Name))
                    {
                        descs[unit.Name.TrimStart('.')] = unit;
                    }
                }
            }

            var categories = new List<EtsMenuCategory>();
            int group = 0;

            foreach (EtsSiiUnit unit in bar.Units)
            {
                string type = unit.GetString("type", "menu");

                if (type == "separator")
                {
                    group++;
                    continue;
                }

                var category = new EtsMenuCategory
                {
                    Key = (unit.Name ?? "").TrimStart('.'),
                    Icon = unit.GetString("icon", "").Trim('"'),
                    NameKey = unit.GetString("name", ""),
                    NoMenu = type == "no_menu",
                    Group = group,
                };

                foreach (string id in unit.GetArray("screen_links"))
                {
                    EtsSiiUnit desc = DescOf(descs, id);
                    category.Links.Add(new EtsMenuLink
                    {
                        ScreenLink = id,
                        Icon = desc != null ? desc.GetString("icon", "").Trim('"') : "",
                        LabelKey = desc != null ? desc.GetString("icon_label", "") : "",
                    });
                }

                categories.Add(category);
            }

            return categories.Count > 0 ? categories : null;
        }

        /// <summary>
        /// A city's country for the top bar's flag, from the game's own defs: the city's unit ("city_data : city.szczecin",
        /// "country: poland") and that country's ("country_data : country.data.poland", "iso_country_code: pol"), which
        /// btn.desktop.top_bar.country turns into /material/ui/flags/pol.mat (/ui/template/button.sii:5008). Each is read
        /// from its own include file first - the names /def/city.sii and /def/country.sii @include ("city/szczecin.sui",
        /// "country/poland.sui") - and from the whole list with its includes when it is not there. "" when not found.
        /// Pure (EtsFileSystem.ReadText, EtsSii): the app runs it on a worker thread; probe 2073 runs it on every HQ city.
        /// </summary>
        public static string CountryIsoOf(EtsFileSystem files, string cityName)
        {
            if (files == null || string.IsNullOrEmpty(cityName))
            {
                return "";
            }

            EtsSiiUnit city = DefUnit(files, "/def/city/" + cityName + ".sui", "/def/city.sii", "city." + cityName);
            string country = city != null ? (city.GetString("country", "") ?? "").Trim().Trim('"') : "";

            if (country.Length == 0)
            {
                return "";
            }

            EtsSiiUnit data = DefUnit(files, "/def/country/" + country + ".sui", "/def/country.sii", "country.data." + country);
            return data != null ? (data.GetString("iso_country_code", "") ?? "").Trim().Trim('"') : "";
        }

        private static EtsSiiUnit DefUnit(EtsFileSystem files, string own, string list, string unitName)
        {
            string text = files.ReadText(own);

            if (text != null)
            {
                EtsSiiUnit unit = EtsSii.Parse(text, own, files.ReadText).Find(unitName);

                if (unit != null)
                {
                    return unit;
                }
            }

            string all = files.ReadText(list);
            return all != null ? EtsSii.Parse(all, list, files.ReadText).Find(unitName) : null;
        }

        private static EtsSiiUnit DescOf(Dictionary<string, EtsSiiUnit> descs, string id)
        {
            if (!descs.TryGetValue(id, out EtsSiiUnit desc))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(desc.GetString("icon", "")))
            {
                return desc;
            }

            foreach (KeyValuePair<string, EtsSiiValue> attribute in desc.Attributes)
            {
                string target = attribute.Value != null ? attribute.Value.AsString().Trim('"') : null;

                if (target != null && descs.TryGetValue(target, out EtsSiiUnit screen) && !string.IsNullOrEmpty(screen.GetString("icon", "")))
                {
                    return screen;
                }
            }

            return desc;
        }

        /// <summary>
        /// The ten categories as probe 1900 read them from 1.61, for when the archives are not mounted: no screen links
        /// (only the world's own pages then fill the menus).
        /// </summary>
        public static List<EtsMenuCategory> MeasuredMenu()
        {
            string[] keys = { "drive", "jobs", "maps", "online", "personal", "fleet", "services", "browsers", "media", "quit" };
            string[] icons = { "drive", "job_market", "navigation", "community", "personal", "fleet", "services", "content_browser", "photo_music", "exit_mbox" };
            string[] names = { "@@drive@@", "@@desktop_menu_category_jobs@@", "@@desktop_menu_category_map@@", "@@desktop_menu_category_social@@",
                               "@@desktop_menu_category_personal@@", "@@desktop_menu_category_company@@", "@@desktop_menu_category_services@@",
                               "@@desktop_menu_category_content_browsers@@", "@@desktop_menu_category_media@@", "@@desktop_menu_category_system@@" };
            var categories = new List<EtsMenuCategory>();

            for (int i = 0; i < keys.Length; i++)
            {
                categories.Add(new EtsMenuCategory { Key = keys[i], Icon = icons[i], NameKey = names[i], NoMenu = i == 0, Group = i == 0 ? 0 : i == keys.Length - 1 ? 2 : 1 });
            }

            return categories;
        }

        /// <summary>What a world key does. None: nothing of the world's (the client's own binding stands).</summary>
        public enum KeyUse { None, OldMenu, AppPanel, GetOut }

        /// <summary>
        /// The client's Escape (Exit), R (PopUpMenu), left X (Inspector/Mute/Hide) and left Y tap (tablet) are the
        /// world's only while the "ETS 2" app is not on the client's tablet ([tablet §2.7, §4.9], Q-E4): retired
        /// when the new flow is on AND a tablet is linked. With the new flow off nothing changes, linked or not.
        /// </summary>
        public static bool KeysRetired(bool newFlow, bool linked)
        {
            return newFlow && linked;
        }

        /// <summary>Escape and VR left X: the old menu (today), the app's own panel (new flow, no tablet), or nothing (retired).</summary>
        public static KeyUse MenuKey(bool newFlow, bool linked, bool allowEscape)
        {
            if (!allowEscape || KeysRetired(newFlow, linked)) return KeyUse.None;
            return newFlow ? KeyUse.AppPanel : KeyUse.OldMenu;
        }

        /// <summary>VR left Y: get out of the truck when seated (today), nothing when retired or on foot.</summary>
        public static KeyUse SeatKey(bool newFlow, bool linked, bool allowEscape, bool seated)
        {
            if (!allowEscape || !seated || KeysRetired(newFlow, linked)) return KeyUse.None;
            return KeyUse.GetOut;
        }

        /// <summary>R: the truck back on the road (EtsTruckController, owner C), unless retired.</summary>
        public static bool RespawnKey(bool newFlow, bool linked)
        {
            return !KeysRetired(newFlow, linked);
        }

        /// <summary>
        /// Desktop: the client snaps its tablet onto the camera's TabletPoint every Update while open
        /// (TabletControl.cs:263-291) and EtsTruckCamera moves the camera later, in LateUpdate, so the tablet
        /// trails by one frame's travel (0.42 m at 90 km/h and 60 fps). Re-placing it is only right when it is that
        /// frame's travel away from the point, never when it is somewhere else altogether (another spawn point,
        /// a client change): <paramref name="maxLag"/> metres (1.5 m is a frame at 90 km/h down to 17 fps, 160 km/h down to 30 fps).
        /// </summary>
        public static bool DesktopSnap(bool desktop, bool open, float metresFromPoint, float maxLag)
        {
            return desktop && open && metresFromPoint <= maxLag;
        }

        // ------------------------------------------------------------------ the Somnium tablet's home grid (Phase 2)

        /// <summary>
        /// The home grid as the client's prefab sets it (Tablet.XR.HomeTab.prefab:273-286): a GridLayoutGroup with no
        /// padding and no spacing, 235 x 250 cells, start corner UpperLeft (0), start axis Horizontal (0), child alignment
        /// UpperCenter (1), constraint Flexible (0). It is on "ContentView", which also carries the Mask (showMaskGraphic 0)
        /// and is the HomeTab rect less 80 units at the bottom (anchors 0..1, sizeDelta (0,-80), anchoredPosition (0,40):
        /// :207-211): 960 x 1000 Small, 1920 x 1000 Large.
        /// </summary>
        public const float GridCellWidth = 235f, GridCellHeight = 250f, GridViewInset = 80f;

        /// <summary>
        /// Where child <paramref name="index"/> of the <paramref name="shown"/> children the grid lays out (inactive ones are
        /// not laid out: LayoutGroup.rectChildren) lands in a view of the given size, exactly as
        /// UnityEngine.UI.GridLayoutGroup.SetCellsAlongAxis computes it for the prefab's settings: Flexible, so
        /// columns = floor((width + 0.001) / 235) and rows = floor((height + 0.001) / 250); UpperCenter, so the used columns
        /// are centred and the rows start at the top. <paramref name="left"/> is from the view's left edge and
        /// <paramref name="top"/> down from its top edge. Returns whether the whole cell is inside the view - not cut by the
        /// Mask. Probe 1603: 14 of the 19 buttons show in XR and 12 on desktop, so the "ETS 2" button, first, is cell 1 of 15
        /// (XR) or 13 (desktop), at 10..245 x 0..250 of a 960 x 1000 view.
        /// </summary>
        public static bool HomeGridCell(float viewWidth, float viewHeight, int shown, int index, out float left, out float top)
        {
            int columns = Math.Max(1, (int)Math.Floor((viewWidth + 0.001f) / GridCellWidth));
            int children = Math.Max(1, shown);
            int usedColumns = Math.Max(1, Math.Min(columns, children));
            float startX = (viewWidth - usedColumns * GridCellWidth) * 0.5f; // UpperCenter: the used columns centred across
            left = startX + (index % columns) * GridCellWidth;
            top = (index / columns) * GridCellHeight;                         // UpperCenter: rows from the top down
            return index >= 0 && index < children && left >= -0.01f && left + GridCellWidth <= viewWidth + 0.01f && top + GridCellHeight <= viewHeight + 0.01f;
        }

        // ------------------------------------------------------------------ what the app says and does on its own (Phase 2)

        /// <summary>The HUD hint's stages (PLAN §5.2 InWorld row; Q-E1: the world never opens the tablet itself).</summary>
        public enum HintStage { None, ChooseTruck, GetIn }

        /// <summary>How long the "your truck is ready" line may stay after the truck appears, if the player never gets in.</summary>
        public const float GetInHintSeconds = 60f;

        /// <summary>
        /// Which hint the HUD shows: "open your tablet" from arrival until the player has a truck (the task's "until a truck
        /// exists"), then "your truck is ready, get in" until they first sit in it (at most GetInHintSeconds). Nothing
        /// before arrival (the HQ stage and the loading screen are full view), nothing while the app itself is on
        /// screen, and nothing at all with the new flow off.
        /// </summary>
        public static HintStage Hint(bool newFlow, bool inWorld, bool appVisible, bool hasTruck, bool onFoot, bool seatedSinceTruck, float secondsSinceTruck)
        {
            if (!newFlow || !inWorld || appVisible)
            {
                return HintStage.None;
            }

            if (!hasTruck)
            {
                return HintStage.ChooseTruck;
            }

            return onFoot && !seatedSinceTruck && secondsSinceTruck < GetInHintSeconds ? HintStage.GetIn : HintStage.None;
        }

        /// <summary>
        /// The hint's words (not in the locale; "ETS 2" is the app's caption on the Somnium tablet). The keys are the
        /// client's where a tablet is linked or still expected - Tab on a monitor, a tap of B or Y in a headset
        /// ([tablet §2.7]) - and the world's own panel keys once it stands in for a missing tablet (Tab / Escape, left X).
        /// </summary>
        public static string HintText(HintStage stage, bool vr, bool worldPanel)
        {
            switch (stage)
            {
                case HintStage.ChooseTruck:
                    if (worldPanel)
                    {
                        return vr ? "Press left X - ETS 2 - choose your truck" : "Press Tab - ETS 2 - choose your truck";
                    }

                    return vr ? "Tap B or Y to open your tablet - ETS 2 - choose your truck" : "Open your tablet (Tab) - ETS 2 - choose your truck";
                case HintStage.GetIn:
                    return vr ? "Your truck is ready: point at it and pull the trigger to get in" : "Your truck is ready: click it to get in (or ETS 2 - Drive)";
                default:
                    return "";
            }
        }

        /// <summary>
        /// The first time the app is on screen in the world, it opens at Company - Truck Preference when the player has no
        /// truck yet (the task's "the first open lands on Company > Truck Preference"); never while the app is blocked (the
        /// session still starting), and never without a session (the flag off, the editor preview).
        /// </summary>
        public static bool LandOnTruckPreference(bool haveSession, bool blocked, bool hasTruck)
        {
            return haveSession && !blocked && !hasTruck;
        }

        /// <summary>
        /// Whether the local driver's truck is held (no keys, no pedals, EtsTruckController.InputEnabled false): while the
        /// app is on screen ([tablet §4.5]), and in a headset also while the Somnium tablet is open at all, because the
        /// trigger that presses its buttons - the "ETS 2" button on its home grid among them - is the throttle
        /// (EtsVr: right trigger). On a monitor the client's own tablet pages leave the keys alone: its buttons take the
        /// mouse, not W/S.
        /// </summary>
        public static bool HoldTruck(bool driving, bool appVisible, bool vr, bool tabletOpen)
        {
            return driving && (appVisible || (vr && tabletOpen));
        }

        /// <summary>
        /// While HoldTruck holds: whether it is the Somnium tablet alone that holds a driver who had the keys - open in a
        /// headset but not on the app (its home, a client tab, or after the app's Back or Drive). The app then says so on
        /// the HUD once each time this begins (checker, Phase 2): nothing on screen said why the truck only coasted.
        /// </summary>
        public static bool HeldByTabletAlone(bool appVisible, bool hadKeys)
        {
            return !appVisible && hadKeys;
        }

        // ------------------------------------------------------------------ Convoys (Phase 3, E3; PLAN C4, §5.5 "Passenger")

        /// <summary>
        /// A player's state as the Convoys page words it: EtsPlayerWire.Status (owner A) mapped by name in the page, so
        /// this file stays Unity-free for the harness (probe 2270).
        /// </summary>
        public enum ConvoyState { Joining, Loading, ChoosingHq, Waiting, OnFoot, Driving, Passenger, Standing }

        /// <summary>What a passenger or a standing rider is on (EtsTargetKind, owner A: Tractor, Trailer, TrafficCar).</summary>
        public enum RideOn { None, Truck, Trailer, Car }

        /// <summary>Rows of players on one Convoys page: nine rows of 56 + 6 between the header (t-95) and the result line.</summary>
        public const int ConvoyRows = 9;

        /// <summary>
        /// How long the page waits for RequestSeat's answer before it says "did not answer" itself. A's RequestSeat answers
        /// within 3 s by contract ([net §2.7] step 3: the owner's 0.25 s scan plus the network, then its own timeout); B's
        /// EtsRiders lets its publishing hold lapse after 5 s (EtsRiders.HoldLimit). Six seconds is past both, so this only
        /// fires when no answer ever comes, and the page never shows a request as pending for ever.
        /// </summary>
        public const float SeatAnswerSeconds = 6f;

        /// <summary>
        /// "Go to" stands the player this far beyond the passenger seat's side, as EtsTruckSeat.StandBeside does for getting
        /// out (1.6 m past the seat's x: clear of a 2.55 m wide cab and its mirror).
        /// </summary>
        public const float GoToSideMetres = 1.6f;

        /// <summary>
        /// Above this drawn speed "Go to" is refused: the player would be stood on the road beside a truck already gone,
        /// in the path of its trailer. The same 10 km/h as a passenger's exit (Q-B2) and the trucks' contact rule (Q-A3).
        /// </summary>
        public const float GoToMaxKmh = 10f;

        /// <summary>
        /// How long a "Go to" landing pad may stand under a player whose destination's ground has not streamed in yet
        /// (EtsTabletApp.GoToPad). EtsMapViewer builds the first ring around a new focus within the loading screen's
        /// 20 s truck wait (PLAN §5.5, [net §2.9]); 60 s is three times that.
        /// </summary>
        public const float GoToPadSeconds = 60f;

        /// <summary>The status column: the game has no words for these (not in the locale).</summary>
        public static string ConvoyStatus(ConvoyState state, int loadPercent, RideOn on, string owner, bool ownerIsMe)
        {
            string whose = ownerIsMe ? "your" : string.IsNullOrEmpty(owner) ? "a" : owner + "'s";

            switch (state)
            {
                case ConvoyState.Loading:
                    return "Loading " + Math.Max(0, Math.Min(100, loadPercent)) + " %";
                case ConvoyState.ChoosingHq:
                    return "Choosing the headquarters";
                case ConvoyState.Waiting:
                    return "Waiting for the headquarters";
                case ConvoyState.OnFoot:
                    return "On foot";
                case ConvoyState.Driving:
                    return "Driving";
                case ConvoyState.Passenger:
                    return ownerIsMe ? "Passenger with you" : string.IsNullOrEmpty(owner) ? "Passenger" : "Passenger with " + owner;
                case ConvoyState.Standing:
                    switch (on)
                    {
                        case RideOn.Truck: return "On " + whose + " truck";
                        case RideOn.Trailer: return "On " + whose + " trailer";
                        case RideOn.Car: return "On a car";
                        default: return "Riding on a vehicle";
                    }
                default:
                    return "Joining ...";
            }
        }

        /// <summary>The job column: "Cargo to City", or none.</summary>
        public static string ConvoyJob(bool hasJob, string cargo, string city)
        {
            if (!hasJob)
            {
                return "No job";
            }

            bool haveCargo = !string.IsNullOrEmpty(cargo), haveCity = !string.IsNullOrEmpty(city);
            return haveCargo && haveCity ? cargo + " to " + city : haveCargo ? cargo : haveCity ? "Delivering to " + city : "On a job";
        }

        /// <summary>
        /// Everything "Ride along" and "Go to" depend on, for one row. Built by the page at 4 Hz from EtsNet.Players (A),
        /// EtsLocalTruck and the seat (B), and the drawn puppet (C); the rules below turn it into a refusal or none.
        /// </summary>
        public struct ConvoyAsk
        {
            public bool Mine;            // the row is the local player
            public bool InWorld;         // this client is in the world (the app not blocked)
            public bool HavePlayer;      // there is a Somnium player to seat or move (not the editor)
            public bool TheyHaveTruck;   // their slot says a truck is present
            public bool TruckDrawn;      // their truck is drawn on this client and placed
            public bool SeatFree;        // their passenger seat is free in their seat table (ints 26-27)
            public bool Asking;          // a seat request of this client's is out (to anyone)
            public bool Seated;          // the local player sits in a truck
            public bool SeatedAsDriver;  // ... in the driver's seat (always their own truck, C10)
            public bool RidingWithThem;  // ... as the passenger of this row's truck
            public float TheirKmh;       // their truck's drawn speed
        }

        /// <summary>Why a row's button does nothing now; None = it does something.</summary>
        public enum ConvoyRefusal { None, Mine, NotInWorld, NoPlayer, Asking, NoTruck, NotDrawn, AlreadyRiding, GetOutOfYours, GetOutOfTheirs, SeatTaken, OnTheMove }

        /// <summary>
        /// "Ride along" (PLAN §5.5 "Passenger", [net §2.7]): only from on foot (changing seats is getting out and in again,
        /// [elev §7.5] step 4; a driver would leave the own truck parked on the road), only to a truck drawn here (the seat
        /// is shown in the drawn cab, and the grant sits the player in it), one request at a time, and only when the owner's
        /// seat table shows the seat free (the owner would refuse anyway, "The seat is taken"). A moving truck is fine: the
        /// plan's client test is exactly clicking a moving truck (§8 Phase 3 acceptance).
        /// </summary>
        public static ConvoyRefusal RideAlong(in ConvoyAsk a)
        {
            if (a.Mine) return ConvoyRefusal.Mine;
            if (!a.InWorld) return ConvoyRefusal.NotInWorld;
            if (a.RidingWithThem) return ConvoyRefusal.AlreadyRiding;
            if (a.Asking) return ConvoyRefusal.Asking;
            if (!a.TheyHaveTruck) return ConvoyRefusal.NoTruck;
            if (!a.TruckDrawn) return ConvoyRefusal.NotDrawn;
            if (a.Seated) return a.SeatedAsDriver ? ConvoyRefusal.GetOutOfYours : ConvoyRefusal.GetOutOfTheirs;
            if (!a.HavePlayer) return ConvoyRefusal.NoPlayer;
            if (!a.SeatFree) return ConvoyRefusal.SeatTaken;
            return ConvoyRefusal.None;
        }

        /// <summary>
        /// "Go to" ([tablet §4] row 4): a teleport beside their truck, on foot only (a seated player's pose belongs to the
        /// seat, B), and not beside a truck that is driving off (GoToMaxKmh).
        /// </summary>
        public static ConvoyRefusal GoTo(in ConvoyAsk a)
        {
            if (a.Mine) return ConvoyRefusal.Mine;
            if (!a.InWorld) return ConvoyRefusal.NotInWorld;
            if (!a.TheyHaveTruck) return ConvoyRefusal.NoTruck;
            if (!a.TruckDrawn) return ConvoyRefusal.NotDrawn;
            if (a.RidingWithThem) return ConvoyRefusal.AlreadyRiding;
            if (a.Seated) return a.SeatedAsDriver ? ConvoyRefusal.GetOutOfYours : ConvoyRefusal.GetOutOfTheirs;
            if (!a.HavePlayer) return ConvoyRefusal.NoPlayer;
            if (a.TheirKmh > GoToMaxKmh) return ConvoyRefusal.OnTheMove;
            return ConvoyRefusal.None;
        }

        /// <summary>
        /// The words for a refusal (not in the locale), made only when a button is pressed. "Get out of {name}'s truck first"
        /// is PLAN §3.5's own wording for a passenger; "The seat is taken" is [net §2.7]'s refusal.
        /// </summary>
        public static string Why(ConvoyRefusal refusal, string name, string seatedWith, string askedName)
        {
            string who = string.IsNullOrEmpty(name) ? "that driver" : name;

            switch (refusal)
            {
                case ConvoyRefusal.None: return "";
                case ConvoyRefusal.Mine: return "That is you.";
                case ConvoyRefusal.NotInWorld: return "Not before you have arrived.";
                case ConvoyRefusal.NoPlayer: return "There is no Somnium player to move here (editor).";
                case ConvoyRefusal.Asking: return "Waiting for " + (string.IsNullOrEmpty(askedName) ? "an answer" : askedName + "'s answer") + " ...";
                case ConvoyRefusal.NoTruck: return who + " has no truck yet.";
                case ConvoyRefusal.NotDrawn: return who + "'s truck is not here yet - it is still being built.";
                case ConvoyRefusal.AlreadyRiding: return "You already ride with " + who + ".";
                case ConvoyRefusal.GetOutOfYours: return "Get out of your truck first.";
                case ConvoyRefusal.GetOutOfTheirs: return "Get out of " + (string.IsNullOrEmpty(seatedWith) ? "this" : seatedWith + "'s") + " truck first.";
                case ConvoyRefusal.SeatTaken: return "The seat is taken.";
                case ConvoyRefusal.OnTheMove: return who + " is driving - Ride along instead, or Go to once they stop.";
                default: return "";
            }
        }

        /// <summary>The page of rows that shows row `index` (0-based), and how many pages `count` rows make (at least one).</summary>
        public static int ConvoyPages(int count)
        {
            return Math.Max(1, (count + ConvoyRows - 1) / ConvoyRows);
        }

        /// <summary>The first row shown on `page` (clamped to the pages there are), and how many rows it shows.</summary>
        public static int ConvoyPageRows(int count, ref int page, out int shown)
        {
            int pages = ConvoyPages(count);
            page = Math.Max(0, Math.Min(page, pages - 1));
            int first = page * ConvoyRows;
            shown = Math.Max(0, Math.Min(ConvoyRows, count - first));
            return first;
        }

        /// <summary>
        /// The Convoys order: the local player first, then everyone else by slot (the order players claimed them, [net
        /// §2.3], stable while they stay). In place, no allocation: an insertion sort over at most 50 rows.
        /// </summary>
        public static void ConvoyOrder(int[] slots, bool[] mine, int[] order, int count)
        {
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }

            for (int i = 1; i < count; i++)
            {
                int item = order[i];
                int j = i - 1;

                while (j >= 0 && Before(item, order[j], slots, mine))
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = item;
            }
        }

        private static bool Before(int a, int b, int[] slots, bool[] mine)
        {
            if (mine[a] != mine[b])
            {
                return mine[a];
            }

            // A slot of -1 (the local row before its claim) sorts last among the others.
            int sa = slots[a] < 0 ? int.MaxValue : slots[a], sb = slots[b] < 0 ? int.MaxValue : slots[b];
            return sa < sb;
        }

        /// <summary>
        /// The Drive page's line for a passenger: whose truck, its speed, and when getting out is allowed (Q-B2). Whether it
        /// is allowed is the seat's answer (EtsTruckSeat.CanGetOut, owner B), not worked out again here. Not in the locale.
        /// </summary>
        public static string PassengerLine(string owner, int kmh, bool canGetOut, float exitMaxKmh)
        {
            string line = "Passenger with " + (string.IsNullOrEmpty(owner) ? "another driver" : owner) + ": " + kmh + " km/h";
            return canGetOut ? line : line + " - you can get out below " + Math.Round(exitMaxKmh) + " km/h";
        }
    }
}
