using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The screen the demo finishes on, rebuilt from the game's own layout.
    ///
    /// /ui/desktop_standalone_demo.sii is eight units: the window frame, a dark panel from 465 to
    /// 975 across and 138 to 893 up, a centred title reading "demo ended", the game's logo drawn
    /// from /material/ui/logo/ets2_logo.mat, a big centred line reading "demo complete", the trial
    /// background /material/ui/window/bcg_trial.mat, and one button to the store page. All of it is
    /// placed here at the coordinates the file gives, in the 1440 x 900 frame /def/ui.sii sets.
    ///
    /// The button does not go anywhere. Sending a player to a shop is not this world's business, so
    /// it closes the screen and lets the driving continue instead, which is also the only sensible
    /// end for a Somnium world that people are standing in.
    /// </summary>
    public class EtsDemoEndScreen : MonoBehaviour
    {
        public const string ScreenPath = "/ui/desktop_standalone_demo.sii";

        private static EtsDemoEndScreen _shown;
        private GameObject _screen;
        private EtsJobs _jobs;
        private Action _onDismiss;

        /// <summary>
        /// Shows the end screen. onDismiss runs on "Keep driving": the front end passes its
        /// StartDriving, which gives back the menu, the keys and the truck's door - without it the
        /// player was left in the cab with no controls and no way out.
        /// </summary>
        public static void Show(EtsJobs jobs, Action onDismiss = null)
        {
            if (_shown != null)
            {
                return;
            }

            var holder = new GameObject("Ets demo end screen");
            _shown = holder.AddComponent<EtsDemoEndScreen>();
            _shown._jobs = jobs;
            _shown._onDismiss = onDismiss;
            _shown.Build();
        }

        private void Build()
        {
            if (EventSystem.current == null)
            {
                var events = new GameObject("EventSystem");
                events.AddComponent<EventSystem>();
                events.AddComponent<StandaloneInputModule>();
            }

            var actions = new Dictionary<string, Action>(StringComparer.Ordinal)
            {
                { EtsGameUi.Text("@@go_to_store_page@@"), Dismiss }
            };

            _screen = EtsGameUi.BuildScreen(ScreenPath, 500, actions, out string report);

            if (_screen == null)
            {
                Debug.Log("[SomniumEuroTruck] Demo end screen: " + report);
                Fallback();
                return;
            }

            _screen.transform.SetParent(transform, false);

            // The layout has one button, to the store. Say what it does here instead.
            foreach (TextMeshProUGUI label in _screen.GetComponentsInChildren<TextMeshProUGUI>())
            {
                if (label.text == EtsGameUi.Text("@@go_to_store_page@@"))
                {
                    label.text = "Keep driving";
                }
            }

            string earned = _jobs != null ? _jobs.Earnings.ToString("N0") + " EUR earned" : "";
            var line = EtsGameUi.Place(_screen.transform.GetChild(0), "summary", 477f, 963f, 380f, 412f);
            EtsGameUi.Label(line, earned, 19f, TextAlignmentOptions.Center, new Color(0.75f, 0.78f, 0.8f));
            Debug.Log("[SomniumEuroTruck] Demo end screen from the game's own layout - " + report + ".");
        }

        private void Fallback()
        {
            Canvas canvas = EtsGameUi.CreateCanvas("Ets demo end screen", 500);
            canvas.transform.SetParent(transform, false);
            var frame = EtsGameUi.Place(canvas.transform, "frame", 465f, 975f, 138f, 893f);
            var panel = frame.gameObject.AddComponent<UnityEngine.UI.Image>();
            panel.color = new Color(0.06f, 0.07f, 0.08f, 0.93f);
            EtsGameUi.Label(EtsGameUi.Place(frame, "title", 0f, 510f, 700f, 740f), EtsGameUi.Text("@@demo_ended@@"), 30f, TextAlignmentOptions.Center, new Color(1f, 0.78f, 0.24f));
            EtsGameUi.Label(EtsGameUi.Place(frame, "line", 10f, 500f, 380f, 480f), EtsGameUi.Text("@@standalone_demo_complete@@"), 22f, TextAlignmentOptions.Center, Color.white);
            EtsGameUi.MakeButton(EtsGameUi.Place(frame, "button", 95f, 415f, 44f, 76f), "Keep driving", Dismiss);
            _screen = canvas.gameObject;
        }

        private void Dismiss()
        {
            if (_jobs != null)
            {
                // Past the demo's one delivery, the world carries on rather than stopping dead.
                _jobs.DemoRun = false;
                _jobs.MakeOffers();
            }

            _shown = null;
            Destroy(gameObject);
            _onDismiss?.Invoke();
        }
    }
}
