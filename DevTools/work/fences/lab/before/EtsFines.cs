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
    ///     the figure in clr_black) at the world advisor's sign place (EtsRouteAdvisor: 1079..1111 x 223..255) while the
    ///     advisor is up on a desktop; in a headset the dash plate carries it (DashNote).
    /// Police cars: offence 6 (speeding, offence_probabilty 0, police 0.9 within police_car.basic's 55 m / 60 degrees)
    /// needs police among the traffic; the world's traffic spawns none (EtsTrafficCatalog reads traffic_storage_car only),
    /// so only cameras fine today.
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
        private RectTransform _sign;
        private EtsRouteAdvisor _advisor;

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

            if (caught != null)
            {
                Fine(truck, caught);
            }
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

            bool show = !EtsVr.Present && _advisor != null && _advisor.Visible ? figure > 0 : false;
            ShowSign(show ? figure : 0);
        }

        private int _signFigure;

        private void ShowSign(int figure)
        {
            if (figure == _signFigure)
            {
                return;
            }

            _signFigure = figure;

            if (_sign != null)
            {
                Destroy(_sign.gameObject);
                _sign = null;
            }

            if (figure > 0 && Frame() != null)
            {
                _sign = EtsGameUi.Template(_frame, "txt.adv_navi_spd", figure.ToString(), 1079f, 1111f, 223f, 255f);
            }
        }

        // ------------------------------------------------------------------ a catch

        private void Fine(EtsTruckController truck, EtsFineJudge.Caught caught)
        {
            EtsProfileStore store = EtsProfileStore.Current;
            int level = store != null ? store.Level : 0;
            long fine = _police.Fine(caught.Offence, level, caught.OverspeedMs, false);
            EtsSpeedCamera cam = caught.Camera;
            string what = "speeding fine (camera " + cam.SignModel + " " + cam.Locator + ")";
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
                      " -> €" + fine + ", " + paid + ".");

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
            canvas.sortingOrder = 45;
            var scaler = _hud.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(EtsGameUi.ReferenceWidth, EtsGameUi.ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _frame = EtsGameUi.Place(_hud.transform, "frame", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);
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
            _popup = EtsGameUi.Place(_frame, "hud hint (fine)", 570f, 870f, 792f, 860f);
            EtsGameUi.Template(_popup, "txt.hud.background", "@@clr_red@@", 0f, 300f, 0f, 68f);
            string markup = _police.Markup(offence, overspeed, fine, "km/h").Replace("|", "/");
            EtsGameUi.Template(_popup, "txt.hud.icon", _police.Icon(offence) + "|" + markup + "|13", 0f, 300f, 4f, 68f);
            _popupUntil = Time.unscaledTime + PopupSeconds;
        }

        private void UpdatePopup()
        {
            if (_popup != null && Time.unscaledTime >= _popupUntil)
            {
                Destroy(_popup.gameObject);
                _popup = null;
            }
        }
    }
}
