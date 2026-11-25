using System;
using System.Collections.Generic;
using System.Diagnostics;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Speeding fines the game's way, for the local player's own truck while they drive it (not a puppet, not a
    /// passenger's ride). The rules are Ets/EtsFinesRules.cs (police_data.sii, the speed_camera hookups on the map's
    /// camera signs, the truck rows of speed_limits.sii, the signs' traffic rules); this is the world's side of it:
    ///
    ///   - the map: each sector the map viewer has read round the truck is gathered (cameras, speed-rule signs, roads,
    ///     cities) a slice of signs per frame; which sign models are cameras is scanned once on a worker thread (8,551
    ///     models, about 2 s of reading that must not land on a frame).
    ///   - the referee (EtsFineJudge) runs ten times a second on the truck's pose; the road's class and the country are
    ///     looked up twice a second.
    ///   - a catch: the fine (EtsPoliceData.Fine with the profile's level) is taken with EtsProfileStore.Spend. The game
    ///     lets the balance go below zero (/def/bank_data.sii overdraft_duration 5 days); the profile store refuses to,
    ///     so a fine it refuses is owed and taken as soon as the balance covers it (logged, not saved across visits).
    ///   - the message: the program's own text (offence_speeding, " (+N km/h)", the money as a loss; EtsPoliceData.Markup)
    ///     on the driver's status line and the VR dash plate (EtsTruckController.Say) and, on a desktop, as the game's HUD
    ///     notification - /ui/hud_hint.sii's group at 570..870 x 792..860 drawn with its own look templates
    ///     txt.hud.background and txt.hud.icon (icon /material/ui/hud/speed_camera.mat, offence_icon[3]) for 5 s
    ///     (trig_action.hud_message's default). The background's colour is the hint handler's red kind (it has three:
    ///     clr_notice, clr_sel, clr_red - eurotrucks2.exe 0x10642ec..0x106436a); which kind an offence uses is not decoded.
    ///   - no flash and no sound: the camera models have no light or flare part, and nothing in /effect, /sound or /def
    ///     is a camera flash; the only "fine" effect in the files is police_color_effect in /def/color_feedback.sii,
    ///     which drives RGB keyboard lighting.
    ///   - the limit in force is shown the way the game's route advisor shows it (txt.adv_navi_spd: limit_sign.mat with
    ///     the figure in clr_black) on the advisor's own panel at adviser_gps.sii's place (EtsRouteAdvisor.SpeedLimit:
    ///     1154..1186 x 183..215), desktop and VR dash copy alike; the dash plate also carries it (DashNote).
    /// Police cars (2026-10-14, the traffic now spawns them from traffic_storage_police.sii): offence 6 (speeding,
    /// offence_probabilty 0, police 0.9 after 3 s within police_car.basic's 55 m / 60 degrees, at most every 15 s:
    /// EtsPoliceWatch, EtsTraffic.PoliceAround), and a camera fine is doubled with a police car near (police_nearby_fine_rate).
    /// </summary>
    public sealed class EtsFines : MonoBehaviour
    {
        private const float TickSeconds = 0.1f, LocateSeconds = 0.5f, ScanSectorsSeconds = 1f, PopupSeconds = 5f, OwedRetrySeconds = 5f;
        private const int SignsPerFrame = 250;

        private static EtsFines s_instance;
        private static readonly Func<bool> Always = () => true;

        /// <summary>The figure the dash shows ("   limit 80"), for the truck being judged only; empty otherwise.</summary>
        private static string s_dashNote = "";
        private static EtsTruckController s_judged;

        private EtsWorker _worker;
        private EtsPoliceData _police;
        private EtsFineMap _fineMap;
        private EtsFineJudge _judge;
        private EtsMap _map;
        private bool _scanPosted, _announced;
        private float _nextTick, _nextLocate, _nextScan, _nextOwed, _nextAdvisorLook;
        private Vector3 _lastPosition;
        private bool _haveLast;
        private int _shownFigure = -1;
        private long _owed;
        private string _limitLine = "";
        private readonly Stopwatch _scanClock = new Stopwatch();
        private readonly Queue<EtsMapSector> _toGather = new Queue<EtsMapSector>();
        private readonly HashSet<long> _queued = new HashSet<long>();
        private EtsFineMap.Gathering _gathering;

        private GameObject _hud;
        private RectTransform _frame;
        private RectTransform _popup;
        private float _popupUntil;
        private EtsRouteAdvisor _advisor;
        private bool _popupFitLogged;

        /// <summary>Makes the one referee if there is none (EtsTruckController.Start calls it; Unity's null test, as statics outlive a visit).</summary>
        public static void Ensure()
        {
            if (s_instance != null)
            {
                return;
            }

            var host = new GameObject("Ets fines");
            s_instance = host.AddComponent<EtsFines>();
        }

        /// <summary>The dash plate's extra for a truck: "   limit 80" while it is the one being judged and a limit is known. No allocation per call.</summary>
        public static string DashNote(EtsTruckController truck)
        {
            return truck != null && ReferenceEquals(truck, s_judged) ? s_dashNote : "";
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(s_instance, this))
            {
                s_instance = null;
                s_judged = null;
                s_dashNote = "";
            }

            _worker?.Dispose();
            _worker = null;

            if (_hud != null)
            {
                Destroy(_hud);
            }
        }

        private void Update()
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                // One line per failure kind would need a table; this rarely fires and must never stop the drive.
                Debug.LogWarning("[SomniumEuroTruck] Fines: " + e.GetType().Name + ": " + e.Message);
                _nextTick = Time.unscaledTime + 2f;
            }
        }

        private void Run()
        {
            _worker?.Drain(Always);
            UpdatePopup();

            EtsMapViewer viewer = EtsMapViewer.Current;
            EtsFileSystem files = EtsEngine.Files;

            if (viewer == null || viewer.Map == null || files == null)
            {
                return;
            }

            if (!ReferenceEquals(_map, viewer.Map))
            {
                Begin(files, viewer.Map);
            }

            EtsTruckController truck = DrivenTruck();

            if (!ReferenceEquals(truck, s_judged))
            {
                s_judged = truck;
                s_dashNote = "";
                _haveLast = false;
                _judge.Reset();
                ShowSign(0);
            }

            if (truck == null)
            {
                return;
            }

            float now = Time.unscaledTime;
            Vector3 centre = viewer.CenterGame;
            Vector3 p = truck.transform.position;
            float gx = p.x + centre.x, gy = p.y + centre.y, gz = centre.z - p.z;

            if (now >= _nextScan)
            {
                _nextScan = now + ScanSectorsSeconds;
                QueueSectors(gx, gz);
            }

            GatherSome();

            if (_owed > 0 && now >= _nextOwed)
            {
                _nextOwed = now + OwedRetrySeconds;
                CollectOwed();
            }

            if (now < _nextTick)
            {
                return;
            }

            _nextTick = now + TickSeconds;

            // A jump (a job start, a respawn) is not driving past signs: start the reckoning again.
            if (_haveLast && (p - _lastPosition).sqrMagnitude > 50f * 50f)
            {
                _judge.Reset();
                Debug.Log("[SomniumEuroTruck] Fines: the truck moved " + (p - _lastPosition).magnitude.ToString("0") + " m at once; sign state reset.");
            }

            _lastPosition = p;
            _haveLast = true;

            Vector3 f = truck.transform.forward;
            float hl = Mathf.Sqrt(f.x * f.x + f.z * f.z);
            if (hl < 1e-3f) return;
            bool locate = now >= _nextLocate;
            if (locate) _nextLocate = now + LocateSeconds;

            EtsFineJudge.Caught caught = _judge.Step(gx, gy, gz, f.x / hl, -f.z / hl, truck.ForwardSpeed, locate);
            UpdateLimit();

            // Police cars among the traffic (2026-10-14): one within police_car.basic's reach doubles a camera fine
            // (police_nearby_fine_rate), and one that sees the truck speeding fines offence 6 itself (EtsPoliceWatch).
            EtsTraffic traffic = EtsTraffic.Instance;
            bool sees = false;
            _policeNear = traffic != null && traffic.PoliceAround(p, _police.PoliceRange, _police.PoliceFov, out sees);
            float limit = _judge.LimitKmh / 3.6f;

            if (caught != null)
            {
                Fine(truck, caught);
            }
            else if (_watch.Step(TickSeconds, now, sees, truck.ForwardSpeed, limit))
            {
                FineByPolice(truck, Mathf.Abs(truck.ForwardSpeed) - limit, limit);
            }
        }

        private EtsPoliceWatch _watch;
        private bool _policeNear;

        /// <summary>Offence 6: a police car saw the truck speeding (EtsPoliceWatch); the game's fine, message and icon for it.</summary>
        private void FineByPolice(EtsTruckController truck, float overspeed, float limit)
        {
            EtsProfileStore store = EtsProfileStore.Current;
            int level = store != null ? store.Level : 0;
            long fine = _police.Fine(EtsPoliceData.Speeding, level, overspeed, false);
            string paid;

            if (store == null)
            {
                paid = "no profile to take it from";
            }
            else if (store.Spend(fine, "speeding fine (police)"))
            {
                paid = "paid";
            }
            else
            {
                _owed += fine;
                _nextOwed = Time.unscaledTime + OwedRetrySeconds;
                paid = "refused, owed (" + EtsLocale.Money(_owed) + " in all)";
            }

            Debug.Log("[SomniumEuroTruck] Fines: a police car saw the truck speeding - " + ((Mathf.Abs(truck.ForwardSpeed)) * 3.6f).ToString("0.0") + " km/h where the limit is " +
                      (limit * 3.6f).ToString("0.#") + ", over by " + (overspeed * 3.6f).ToString("0.0") + " km/h, level " + level + " -> €" + fine + " (offence 6, police check " +
                      _watch.Checks + ", fined " + _watch.Caught + "), " + paid + ".");

            truck.Say(StatusText(overspeed, fine));
            ShowPopup(EtsPoliceData.Speeding, overspeed, fine);
        }

        /// <summary>The local player's own truck while they drive it: not a puppet, and not while they ride in another.</summary>
        private static EtsTruckController DrivenTruck()
        {
            EtsTruckRig own = EtsLocalTruck.Own;

            if (own == null || own.IsPuppet)
            {
                return null;
            }

            EtsTruckRig occupied = EtsLocalTruck.Occupied;

            if (occupied != null && !ReferenceEquals(occupied, own))
            {
                return null;
            }

            EtsTruckController controller = own.Controller;
            return controller != null && controller.Driven && !controller.Puppet && controller.Placed ? controller : null;
        }

        // ------------------------------------------------------------------ the map

        private void Begin(EtsFileSystem files, EtsMap map)
        {
            _map = map;
            _police = EtsPoliceData.Load(files);
            _fineMap = new EtsFineMap(files, map);
            _judge = new EtsFineJudge(_fineMap);
            _watch = new EtsPoliceWatch(_police, Environment.TickCount);
            Debug.Log("[SomniumEuroTruck] Fines: police watch on - a police car within " + _police.PoliceRange.ToString("0") + " m and " + _police.PoliceFov.ToString("0") +
                      " degrees (police_car.basic" + (_police.PoliceFromFile ? "" : ", built-in") + ") fines speeding (offence 6) with chance " + _police.Chance(EtsPoliceData.Speeding, true) +
                      " after " + _police.Delay(EtsPoliceData.Speeding, true) + " s, at most every " + _police.PoliceNearbyTimer + " s; near a camera it makes the fine x" + _police.PoliceNearbyRate + ".");
            _toGather.Clear();
            _queued.Clear();
            _gathering = null;

            if (!_announced)
            {
                _announced = true;
                Debug.Log("[SomniumEuroTruck] Fines: police_data.sii " + (_police.FromFile ? "read" : "MISSING, built-in figures") + " - camera fine €" +
                          _police.Amount(EtsPoliceData.SpeedingCamera) + ", level factor " + _police.FactorBase + " + " + _police.FactorStep + "/level up to " +
                          _police.FactorLimit + ", over-speed x" + _police.OverspeedMultiplier + " per " + (_police.OverspeedStep * 3.6f).ToString("0") + " km/h up to x" +
                          _police.OverspeedLimit + ", rounded up to €" + _police.Rounding + "; " + _fineMap.HookupUnits + " speed camera hookup units.");
            }

            if (!_scanPosted)
            {
                _scanPosted = true;
                _worker = new EtsWorker("Ets fines");
                EtsFileSystem scanFiles = files;
                _scanClock.Restart();

                _worker.Post(() => EtsFineMap.ScanCameraModels(scanFiles), (result, error) =>
                {
                    var table = result as Dictionary<string, List<EtsFineMap.CameraLocator>>;

                    if (error != null || table == null)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Fines: the camera model scan failed (" + (error != null ? error.Message : "no result") + "); cameras are read per sign instead.");
                        return;
                    }

                    if (_fineMap != null && ReferenceEquals(scanFiles, EtsEngine.Files))
                    {
                        _fineMap.SetCameraModels(table);
                        _queued.Clear();
                        _toGather.Clear();
                        _gathering = null;
                        _nextScan = 0f;
                    }

                    Debug.Log("[SomniumEuroTruck] Fines: " + table.Count + " sign models carry a speed camera (speedcam hookup), scanned in " +
                              _scanClock.ElapsedMilliseconds + " ms " + (_worker != null && _worker.Threaded ? "on a worker thread" : "on the main thread") + ".");
                });
            }
        }

        /// <summary>The sectors round the truck that the map has read and this has not gathered yet, nearest first; far ones forgotten.</summary>
        private void QueueSectors(float gx, float gz)
        {
            if (!_fineMap.CameraModelsKnown)
            {
                return;   // gathered once the scan is in: no per-sign file reads on a frame
            }

            EtsMapSector.SectorOf(gx, gz, out int sx, out int sz);

            for (int ring = 0; ring <= 1; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        int x = sx + dx, z = sz + dz;
                        long key = ((long)x << 32) ^ (uint)z;
                        if (_fineMap.HasSector(x, z) || _queued.Contains(key) || !_map.IsRead(x, z)) continue;
                        EtsMapSector sector = _map.GetSector(x, z);
                        if (sector == null) continue;
                        _queued.Add(key);
                        _toGather.Enqueue(sector);
                    }
                }
            }

            // Forget sectors three or more away (they come back when the truck does).
            List<long> far = null;

            foreach (EtsFineSector s in _fineMap.Sectors)
            {
                if (Math.Abs(s.SectorX - sx) > 2 || Math.Abs(s.SectorZ - sz) > 2)
                {
                    (far ??= new List<long>()).Add(((long)s.SectorX << 32) ^ (uint)s.SectorZ);
                }
            }

            if (far != null)
            {
                foreach (long key in far)
                {
                    int x = (int)(key >> 32), z = (int)(uint)key;
                    _fineMap.Forget(x, z);
                    _queued.Remove(key);
                }
            }
        }

        private void GatherSome()
        {
            if (_gathering == null)
            {
                if (_toGather.Count == 0) return;
                _gathering = _fineMap.Begin(_toGather.Dequeue());
            }

            if (_fineMap.Continue(_gathering, SignsPerFrame))
            {
                EtsFineSector done = _gathering.Result;
                _gathering = null;
                Debug.Log("[SomniumEuroTruck] Fines: sector " + done.SectorX + "," + done.SectorZ + " gathered - " + done.Cameras.Count + " speed camera locators, " +
                          done.Signs.Count + " speed-rule signs, " + done.Roads.Count + " roads.");
            }
        }

        // ------------------------------------------------------------------ the limit shown

        private void UpdateLimit()
        {
            int figure = _judge.SignFigure;

            if (figure != _shownFigure)
            {
                _shownFigure = figure;
                s_dashNote = figure > 0 ? "   limit " + figure : "";
                string line = figure > 0
                    ? figure + (_judge.Imperial ? " mph" : " km/h") + " (" + (_judge.Country ?? "?") + ", " + (_judge.SpeedClass ?? "?") + (_judge.Urban ? ", urban" : "") +
                      (_judge.SignLimit > 0f ? ", sign " + _judge.SignRule : "") + ")"
                    : "unknown";

                if (line != _limitLine)
                {
                    _limitLine = line;
                    Debug.Log("[SomniumEuroTruck] Fines: limit now " + line + ".");
                }
            }

            float now = Time.unscaledTime;

            if (now >= _nextAdvisorLook)
            {
                _nextAdvisorLook = now + 2f;
                if (_advisor == null) _advisor = FindFirstObjectByType<EtsRouteAdvisor>();
            }

            ShowSign(figure);
        }

        /// <summary>
        /// The limit sign now lives on the advisor's own panel (EtsRouteAdvisor.SpeedLimit, adviser_gps.sii 1302 at
        /// 1154..1186 x 183..215 under its speed box), so it sits where the game puts it on the desktop and on the VR dash
        /// copy alike and hides with the panel (2026-10-14; it was a separate canvas placed by hand).
        /// </summary>
        private void ShowSign(int figure)
        {
            if (_advisor != null)
            {
                _advisor.SpeedLimit = figure;
            }
        }

        // ------------------------------------------------------------------ a catch

        private void Fine(EtsTruckController truck, EtsFineJudge.Caught caught)
        {
            EtsProfileStore store = EtsProfileStore.Current;
            int level = store != null ? store.Level : 0;
            // A police car within its trigger distance: police_nearby_fine_rate (x2) - the exe's police factor.
            long fine = _police.Fine(caught.Offence, level, caught.OverspeedMs, _policeNear);
            EtsSpeedCamera cam = caught.Camera;
            string what = "speeding fine (camera " + cam.SignModel + " " + cam.Locator + (_policeNear ? ", police near" : "") + ")";
            string paid;

            if (store == null)
            {
                paid = "no profile to take it from";
            }
            else if (store.Spend(fine, what))
            {
                paid = "paid";
            }
            else
            {
                _owed += fine;
                _nextOwed = Time.unscaledTime + OwedRetrySeconds;
                paid = "refused, owed (" + EtsLocale.Money(_owed) + " in all)";
            }

            Debug.Log("[SomniumEuroTruck] Fines: caught by " + cam.SignModel + " " + cam.Locator + " (" + cam.Hookup + ") at game (" + cam.X.ToString("0.0") + ", " +
                      cam.Z.ToString("0.0") + "): " + caught.SpeedKmh.ToString("0.0") + " km/h where the limit is " + caught.LimitKmh.ToString("0.#") + " (fires from " +
                      ((1f + cam.Tolerance) * caught.LimitKmh).ToString("0.0") + "), over by " + (caught.OverspeedMs * 3.6f).ToString("0.0") + " km/h, level " + level +
                      (_policeNear ? " x" + _police.PoliceNearbyRate + " (a police car near)" : "") + " -> €" + fine + ", " + paid + ".");

            truck.Say(StatusText(caught.OverspeedMs, fine));
            ShowPopup(caught.Offence, caught.OverspeedMs, fine);
        }

        private void CollectOwed()
        {
            EtsProfileStore store = EtsProfileStore.Current;

            if (store == null || !store.IsReady || store.Money < _owed)
            {
                return;
            }

            if (store.Spend(_owed, "speeding fines owed"))
            {
                Debug.Log("[SomniumEuroTruck] Fines: owed fines of €" + _owed + " taken.");
                _owed = 0;
            }
        }

        private static string s_selHex;

        /// <summary>The program's message as one line of TextMeshPro rich text: the offence, then in clr_sel the over-speed and the money.</summary>
        private string StatusText(float overspeed, long fine)
        {
            if (s_selHex == null)
            {
                s_selHex = EtsLocale.TryColour("clr_sel", out uint argb) ? "#" + (argb & 0xFFFFFFu).ToString("X6") : "#FAB003";
            }

            string word = EtsLocale.TryWord("offence_speeding", out string w) && !string.IsNullOrEmpty(w) ? w : "Speeding offence.";
            string over = overspeed >= _police.OverspeedStep ? " (+" + (int)(overspeed * 3.6f + 0.5f) + " km/h)" : "";
            return word + "<color=" + s_selHex + ">" + over + " " + EtsLocale.Money(-fine) + "</color>";
        }

        // ------------------------------------------------------------------ the HUD notification (desktop)

        private RectTransform Frame()
        {
            if (_frame != null)
            {
                return _frame;
            }

            if (EtsVr.Present || EtsEngine.Files == null)
            {
                return null;
            }

            // A plain overlay canvas, not EtsGameUi.CreateCanvas: that one hangs itself in front of a headset (EtsVrPanel.Adopt).
            _hud = new GameObject("Ets fines HUD");
            var canvas = _hud.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 61; // over the route advisor (60) the sign sits on, under the tablet (350)
            var scaler = _hud.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(EtsGameUi.ReferenceWidth, EtsGameUi.ReferenceHeight);
            // /ui/hud_hint.sii's window is keep_aspect: center - the 1440 x 900 frame scaled to fit the screen and centred.
            // The old 0.5 width/height match anchored at the bottom left put a 16:9 screen's frame 46 units over the top
            // (the hint at 792..860 was cut) and 39 units left of the middle (2026-10-14).
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            _frame = EtsGameUi.Place(_hud.transform, "frame", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);
            _frame.anchorMin = _frame.anchorMax = new Vector2(0.5f, 0.5f);
            _frame.pivot = new Vector2(0.5f, 0.5f);
            _frame.anchoredPosition = Vector2.zero;
            return _frame;
        }

        private void ShowPopup(int offence, float overspeed, long fine)
        {
            if (Frame() == null)
            {
                return;
            }

            if (_popup != null)
            {
                Destroy(_popup.gameObject);
            }

            // /ui/hud_hint.sii: group 1000 at 570..870 x 792..860; background (id 100) the same box, txt.hud.background with a
            // colour; message (id 200) 570..870 x 796..860, txt.hud.icon "icon|text|vshift" (the layout's own value has 13).
            // The group is fitting: true - it grows to its content. txt.hud.icon sets the text 64 units in (after the 64 x 64
            // icon), 20 short of the right edge and 13 under the top (vshift 13), left-aligned, so a message that wraps to
            // more lines than the 51 units under the icon's top allow makes the plate taller, downwards from its top at 860
            // (the old fixed 68 let a third line hang under the plate).
            string markup = _police.Markup(offence, overspeed, fine, "km/h").Replace("|", "/");
            string value = _police.Icon(offence) + "|" + markup + "|13";
            float height = 68f;

            for (int pass = 0; pass < 2; pass++)
            {
                if (_popup != null)
                {
                    _popup.gameObject.SetActive(false);
                    Destroy(_popup.gameObject);
                }

                _popup = EtsGameUi.Place(_frame, "hud hint (fine)", 570f, 870f, 860f - height, 860f);
                EtsGameUi.Template(_popup, "txt.hud.background", "@@clr_red@@", 0f, 300f, 0f, height);
                RectTransform message = EtsGameUi.Template(_popup, "txt.hud.icon", value, 0f, 300f, 4f, height);
                TMPro.TextMeshProUGUI text = pass == 0 && message != null ? message.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) : null;

                if (text == null)
                {
                    break;
                }

                float content = Mathf.Max(64f, 13f + text.GetPreferredValues(text.text, 216f, 0f).y);

                if (!_popupFitLogged)
                {
                    _popupFitLogged = true;
                    Debug.Log("[SomniumEuroTruck] Fines: HUD hint laid out as hud_hint.sii (570..870 below 860, centred frame): text " + text.GetPreferredValues(text.text, 216f, 0f).y.ToString("0") +
                              " units tall from vshift 13, plate " + Mathf.Max(68f, content + 4f).ToString("0") + ".");
                }

                if (content + 4f <= height + 0.5f)
                {
                    break;
                }

                height = content + 4f;
            }

            _popupUntil = Time.unscaledTime + PopupSeconds;
        }

        private void UpdatePopup()
        {
            // On a desktop the notification gives way to any open tablet, as the advisor does (2026-10-14).
            if (_hud != null)
            {
                bool hide = EtsRouteAdvisor.AnyTabletOpen;
                if (_hud.activeSelf == hide) _hud.SetActive(!hide);
            }

            if (_popup != null && Time.unscaledTime >= _popupUntil)
            {
                Destroy(_popup.gameObject);
                _popup = null;
            }
        }
    }
}
