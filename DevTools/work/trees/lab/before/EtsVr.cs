using System.Collections.Generic;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player.Components;
using UnityEngine;
using UnityEngine.XR;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Playing in a headset: whether one is running, what the controllers are saying, and moving
    /// the player through the Bridge.
    ///
    /// The controllers are read through UnityEngine.XR (InputDevices, CommonUsages) - the Quake
    /// world's way - not the Input System package, because Somnium's server compiles uploaded
    /// worlds against a fixed list of assemblies and Unity.InputSystem is not on it (the AoM
    /// upload log lists what is: XRI, UnityEngine.UI, the XR and VR modules, the Bridge).
    ///
    /// The buttons are picked around the client's own bindings in
    /// _SomniumSpace/Inputs/VR/XRI Input Actions.inputactions: the menu buttons are the client's
    /// menu and right B is its InspectorMode overlay, so neither is used here. While seated the
    /// seat switches off locomotion, gravity and jump, so the left stick and right A are free.
    ///
    ///     right trigger        accelerate
    ///     left trigger         brake, and reverse from a standstill (the S key)
    ///     left stick           steer
    ///     right A              parking brake on / off
    ///     right stick click    horn
    ///     left X               the world's menu
    ///     grip on the wheel rim hold the wheel and turn it (EtsSteeringGrab); either hand or both; the first grab
    ///                          after sitting in starts the engine
    ///     left Y               get out of the truck
    ///     trigger on the truck get in (on foot; EtsTruckDoor, through the client's own pointer)
    ///     trigger on a control  a free hand (not holding the rim, grip open) at the blinker stalk, the hazard switch or
    ///                          the parking brake: the stalk moved up (right) or down (left), the switch or the lever
    ///                          pressed (EtsCabControls). That press is the control's, never the pedal's.
    ///
    /// Trigger capture (2026-09-25, EtsCabControls): the cab marks a hand that hovers a control every LateUpdate
    /// (SetControlHover; it counts for that frame and the next). A trigger press that STARTS (from 0) on such a hand is
    /// captured: from that frame that hand's pedal reads 0 until the trigger is let go, so not one frame of brake or
    /// throttle leaks out of a switch press. The hands are placed in the seat's LateUpdate, so this frame's pedals are
    /// decided on last frame's hover, before EtsTruckController.Update reads them. A hand that holds the rim is never
    /// marked (EtsSteeringGrab), so braking and accelerating with the hands on the wheel is unchanged. The cab gives a
    /// press back (ReleaseCapture) only where that pedal is a brake at that moment (the left trigger rolling forward, the
    /// right rolling back): when the hand leaves the control before the press did anything, or a stalk press that has
    /// switched nothing sits still. Anywhere else the pedal would be the throttle or reverse at once, so the press stays the
    /// control's until the trigger is let go.
    ///
    /// Engine: in a headset it waits, after sitting in, for the first grab of the rim, a pedal or the stick
    /// (EtsTruckController.StartEngineWhenAsked).
    ///
    /// Pointing a controller at a panel and pulling its trigger presses what it points at; that is
    /// the client's own ray, answered by the TrackedDeviceGraphicRaycaster EtsVrPanel adds.
    ///
    /// Left X and left Y are the client's too (left X: Inspector hold, Mute / Hide multi-tap; left Y tap:
    /// the tablet - XRI Player Interactions.inputactions:15,25,388-436,993, [tablet §2.7]). Once the "ETS 2"
    /// app is on the client's tablet in the new flow (EtsTabletLink.KeysRetired), MenuPressed and SeatPressed
    /// stay false - the menu and getting out are on the app's Drive page - and Help says so. With
    /// EtsFeatures.NewFlow off KeysRetired is always false: both keys and the help read as before.
    /// </summary>
    public static class EtsVr
    {
        /// <summary>
        /// Whether a headset is actually running. Asked of XRSettings once a frame (about 50 callers ask it, some several
        /// times a frame, the truck's FixedUpdate too; loadedDeviceName is a native string property, a new string each ask).
        /// </summary>
        public static bool Present
        {
            get
            {
                int frame = Time.frameCount;

                if (frame != _presentFrame)
                {
                    _presentFrame = frame;
                    _present = XRSettings.isDeviceActive && !string.IsNullOrEmpty(XRSettings.loadedDeviceName);
                }

                return _present;
            }
        }

        private static int _presentFrame = -1;
        private static bool _present;

        private const float DeadZone = 0.15f;

        private static InputDevice _left;
        private static InputDevice _right;
        private static readonly List<InputDevice> Found = new List<InputDevice>();
        private static float _nextSearch;
        private static int _polledFrame = -1;

        private static float _steer, _throttle, _brake, _gripLeft, _gripRight;
        private static bool _horn;
        private static bool _menuHeld, _seatHeld, _parkHeld;
        private static bool _menuPressed, _seatPressed, _parkPressed;

        // Per hand, [0] left and [1] right: the trigger as read, last frame's reading, whether a control has this press,
        // the last frame a control was hovered, until when a Pulse is playing (a Rumble must not cut it short), and
        // whether the controller takes impulses (asked once per controller).
        private static readonly float[] Raw = new float[2];
        private static readonly float[] LastRaw = new float[2];
        private static readonly bool[] Captured = new bool[2];
        private static readonly int[] HoverFrame = { -10, -10 };
        private static readonly float[] QuietUntil = new float[2];
        private static readonly bool[] ImpulseKnown = new bool[2];
        private static readonly bool[] ImpulseOk = new bool[2];
        private static readonly float[] ImpulseAskAgain = new float[2];

        public static float Steer { get { Poll(); return _steer; } }
        public static float Throttle { get { Poll(); return _throttle; } }
        public static float BrakePedal { get { Poll(); return _brake; } }
        public static bool Horn { get { Poll(); return _horn; } }

        /// <summary>How hard each grip is squeezed, 0 to 1 (the analog value, or 1 when only the button is reported).</summary>
        public static float GripLeft { get { Poll(); return _gripLeft; } }
        public static float GripRight { get { Poll(); return _gripRight; } }

        /// <summary>Left X went down this frame (never while the keys are retired to the client's tablet).</summary>
        public static bool MenuPressed { get { Poll(); return _menuPressed && !EtsTabletLink.KeysRetired; } }

        /// <summary>Left Y went down this frame (never while the keys are retired to the client's tablet).</summary>
        public static bool SeatPressed { get { Poll(); return _seatPressed && !EtsTabletLink.KeysRetired; } }

        /// <summary>Right A went down this frame.</summary>
        public static bool ParkingBrakePressed { get { Poll(); return _parkPressed; } }

        /// <summary>A trigger as the controller reports it (0 below 0.05), whether or not a control has captured it.</summary>
        public static float TriggerRaw(bool right)
        {
            Poll();
            return Raw[right ? 1 : 0];
        }

        /// <summary>Whether a cab control has this hand's trigger press (its pedal reads 0 until the trigger is let go).</summary>
        public static bool TriggerCaptured(bool right)
        {
            Poll();
            return Captured[right ? 1 : 0];
        }

        /// <summary>
        /// EtsCabControls, every LateUpdate: this hand is (on) or is not on a control. It counts for the next frame's
        /// reading: a trigger press that starts then is captured.
        /// </summary>
        public static void SetControlHover(bool right, bool on)
        {
            HoverFrame[right ? 1 : 0] = on ? Time.frameCount : -10;
        }

        /// <summary>Hands a captured press back to the pedal (the hand left the control before it did anything).</summary>
        public static void ReleaseCapture(bool right)
        {
            Captured[right ? 1 : 0] = false;
        }

        /// <summary>The controls as they read out on a panel: today's, or with the tablet taking left X and Y.</summary>
        public static string Help => EtsTabletLink.KeysRetired ? HelpWithTablet : HelpWithoutTablet;

        private const string HelpWithoutTablet = "Right trigger accelerate   Left trigger brake / reverse   Left stick steer\n" +
                                                 "Right A parking brake   Left X menu   Left Y get out\n" +
                                                 "Right stick: click horn, left/right indicators, up cruise control, down trailer\n" +
                                                 "Grip rim: steer (starts engine). Free hand + trigger: stalk, hazards, parking brake\n" +
                                                 "On foot: point at the truck and pull the trigger to get in";

        // Not in the locale. "Tap B or Y" is the client's tablet binding (Right/LeftHandTablet, tap under 0.4 s).
        private const string HelpWithTablet = "Right trigger accelerate   Left trigger brake / reverse   Left stick steer\n" +
                                              "Right A parking brake   Tap B or Y: tablet, ETS 2 app\n" +
                                              "Right stick: click horn, left/right indicators, up cruise control, down trailer\n" +
                                              "Grip rim: steer (starts engine). Free hand + trigger: stalk, hazards, parking brake\n" +
                                              "On foot: point at the truck and pull the trigger to get in";

        private static void Poll()
        {
            if (_polledFrame == Time.frameCount)
            {
                return;
            }

            _polledFrame = Time.frameCount;
            _menuPressed = _seatPressed = _parkPressed = false;
            _steer = _throttle = _brake = _gripLeft = _gripRight = 0f;
            _horn = false;
            Raw[0] = Raw[1] = 0f;

            if (!Present)
            {
                LastRaw[0] = LastRaw[1] = 0f;
                Captured[0] = Captured[1] = false;
                return;
            }

            if ((!_left.isValid || !_right.isValid) && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 1f;
                _left = First(InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Left);
                _right = First(InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right);
                ImpulseKnown[0] = ImpulseKnown[1] = false;
            }

            if (_left.isValid)
            {
                if (_left.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick) && Mathf.Abs(stick.x) > DeadZone)
                {
                    // Rescaled past the dead zone so the lock is still reachable at full deflection.
                    _steer = Mathf.Sign(stick.x) * Mathf.InverseLerp(DeadZone, 1f, Mathf.Abs(stick.x));
                }

                _brake = Pedal(0, Trigger(_left));
                _gripLeft = Grip(_left);
                Edge(_left, CommonUsages.primaryButton, ref _menuHeld, ref _menuPressed);
                Edge(_left, CommonUsages.secondaryButton, ref _seatHeld, ref _seatPressed);
            }
            else
            {
                LastRaw[0] = 0f;
                Captured[0] = false;
            }

            if (_right.isValid)
            {
                _throttle = Pedal(1, Trigger(_right));
                _gripRight = Grip(_right);
                // The click is the horn only with the stick near the middle: a hard flick (the indicators, cruise control,
                // the trailer - EtsTruckController) can press the stick down as well, and must not sound the horn.
                _horn = _right.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click) && click &&
                        !(_right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 aim) && aim.sqrMagnitude > 0.25f);
                Edge(_right, CommonUsages.primaryButton, ref _parkHeld, ref _parkPressed);
            }
            else
            {
                LastRaw[1] = 0f;
                Captured[1] = false;
            }
        }

        private static float Trigger(InputDevice device)
        {
            float value = device.TryGetFeatureValue(CommonUsages.trigger, out float pulled) ? pulled : 0f;
            return value > 0.05f ? value : 0f;
        }

        /// <summary>
        /// A trigger as a pedal: 0 while a cab control has the press. A press is captured when it starts (last frame 0,
        /// now more) on a hand the cab marked as on a control last LateUpdate or this one, and it stays captured until
        /// the trigger is let go (or the cab hands it back).
        /// </summary>
        private static float Pedal(int hand, float raw)
        {
            if (raw <= 0f)
            {
                Captured[hand] = false;
            }
            else if (!Captured[hand] && LastRaw[hand] <= 0f && HoverFrame[hand] >= Time.frameCount - 1)
            {
                Captured[hand] = true;
            }

            LastRaw[hand] = raw;
            Raw[hand] = raw;
            return Captured[hand] ? 0f : raw;
        }

        /// <summary>
        /// The grip, analog first (CommonUsages.grip) and the button only as a floor - the AoM
        /// lesson for the trigger: many runtimes report the float and never the bool, or the reverse.
        /// </summary>
        private static float Grip(InputDevice device)
        {
            float value = device.TryGetFeatureValue(CommonUsages.grip, out float squeezed) ? squeezed : 0f;

            if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool down) && down)
            {
                value = Mathf.Max(value, 1f);
            }

            return value;
        }

        /// <summary>
        /// A short buzz on one controller, where the runtime offers impulses; harmless where it does not. While it plays,
        /// Rumble sends nothing to that hand: a new impulse replaces the one playing (OpenXR xrApplyHapticFeedback), and
        /// the wheel's steady rumble would otherwise cut every grab, tick and bump short.
        /// </summary>
        public static void Pulse(bool right, float amplitude, float seconds)
        {
            int hand = right ? 1 : 0;
            InputDevice device = right ? _right : _left;

            if (device.isValid && ImpulseSupported(hand, device))
            {
                device.SendHapticImpulse(0u, amplitude, seconds);
                QuietUntil[hand] = Time.unscaledTime + Mathf.Max(seconds, 0.02f);
            }
        }

        /// <summary>
        /// One impulse of a steady vibration (EtsWheelHaptics re-sends these, overlapping, so the buzz is continuous);
        /// false (nothing sent) while a Pulse plays on that hand or without a controller that takes impulses. No allocation.
        /// </summary>
        public static bool Rumble(bool right, float amplitude, float seconds)
        {
            int hand = right ? 1 : 0;

            if (Time.unscaledTime < QuietUntil[hand])
            {
                return false;
            }

            InputDevice device = right ? _right : _left;
            return device.isValid && ImpulseSupported(hand, device) && device.SendHapticImpulse(0u, Mathf.Clamp01(amplitude), seconds);
        }

        /// <summary>Ends a steady vibration on that hand at once (not while a Pulse plays: that has replaced it already).</summary>
        public static void StopRumble(bool right)
        {
            int hand = right ? 1 : 0;
            InputDevice device = right ? _right : _left;

            if (device.isValid && Time.unscaledTime >= QuietUntil[hand] && ImpulseSupported(hand, device))
            {
                device.StopHaptics();
            }
        }

        /// <summary>Whether a controller takes haptic impulses (a yes is kept per controller found; a no is asked again each second).</summary>
        public static bool HasImpulse(bool right)
        {
            Poll();
            InputDevice device = right ? _right : _left;
            return device.isValid && ImpulseSupported(right ? 1 : 0, device);
        }

        private static bool ImpulseSupported(int hand, InputDevice device)
        {
            if (!ImpulseKnown[hand] || (!ImpulseOk[hand] && Time.unscaledTime >= ImpulseAskAgain[hand]))
            {
                ImpulseKnown[hand] = true;
                ImpulseOk[hand] = device.TryGetHapticCapabilities(out HapticCapabilities caps) && caps.supportsImpulse;
                ImpulseAskAgain[hand] = Time.unscaledTime + 1f;
            }

            return ImpulseOk[hand];
        }

        /// <summary>A controller's position in tracking space (the fallback when the Bridge gives no hand).</summary>
        public static bool TryGetDevicePosition(bool right, out Vector3 position)
        {
            Poll();
            InputDevice device = right ? _right : _left;
            position = default;
            return device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
        }

        /// <summary>What the controllers report for the grip, for the log (which features exist cannot be reasoned out).</summary>
        public static string DescribeGrip()
        {
            Poll();
            return "Grip reported: left " + DescribeGrip(_left) + ", right " + DescribeGrip(_right) + ".";
        }

        private static string DescribeGrip(InputDevice device)
        {
            if (!device.isValid)
            {
                return "(no controller)";
            }

            bool analog = device.TryGetFeatureValue(CommonUsages.grip, out float _);
            bool button = device.TryGetFeatureValue(CommonUsages.gripButton, out bool _);
            bool haptics = device.TryGetHapticCapabilities(out HapticCapabilities caps) && caps.supportsImpulse;
            return "'" + device.name + "' grip(float) " + (analog ? "yes" : "NO") + ", gripButton(bool) " + (button ? "yes" : "NO") + ", haptic impulse " + (haptics ? "yes" : "NO");
        }

        private static void Edge(InputDevice device, InputFeatureUsage<bool> usage, ref bool held, ref bool pressed)
        {
            bool now = device.TryGetFeatureValue(usage, out bool down) && down;
            pressed = now && !held;
            held = now;
        }

        private static InputDevice First(InputDeviceCharacteristics wanted)
        {
            Found.Clear();
            InputDevices.GetDevicesWithCharacteristics(wanted, Found);
            return Found.Count > 0 ? Found[0] : default;
        }

        /// <summary>The local player's motion, or null when there is no client (the editor).</summary>
        public static ISomniumPlayerMotion Motion => SomniumBridge.PlayersContainer?.LocalPlayer?.Features?.Motion;

        /// <summary>
        /// Moves the player's body and character controller together. Setting a transform does not
        /// do it: the client's controller still believes the player is where it left them and
        /// drags them back, or steps into empty air (AomBridge's note, learned the hard way there).
        /// The rotation is Euler degrees - every caller in the client and the Quake world passes
        /// (0, yaw, 0).
        /// </summary>
        public static bool Teleport(Vector3 position, float yawDegrees)
        {
            ISomniumPlayerMotion motion = Motion;

            if (motion == null)
            {
                return false;
            }

            motion.DoTeleportToPoint(position, new Vector3(0f, yawDegrees, 0f), null);
            return true;
        }

        /// <summary>
        /// The camera the player is looking through: Camera.main when it is live, otherwise any
        /// enabled camera drawing to the screen. The client's camera arrives late and can change,
        /// so callers ask again rather than keeping the first answer.
        /// </summary>
        public static Camera Head()
        {
            Camera main = Camera.main;

            if (main != null && main.isActiveAndEnabled)
            {
                return main;
            }

            foreach (Camera candidate in Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (candidate.isActiveAndEnabled && candidate.targetTexture == null && candidate.stereoTargetEye != StereoTargetEyeMask.None)
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
