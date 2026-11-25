using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The engine and the road through the steering wheel, ever so slightly, on the hand(s) holding the rim (2026-09-25).
    /// Only the driver of the local player's own truck in a headset: EtsSteeringGrab steps it at the end of AfterRigPlaced,
    /// under its Usable rule, and stops it whenever a hand lets go, the rule fails (out of the seat, the menu, the tablet
    /// app up) or the grab is disabled.
    ///
    ///  - Engine off (EtsTruckController.EngineRunning false): nothing at all.
    ///  - Engine starting: a starter rumble on the start sound's own timeline (EtsTruckAudio.StartWave): the starter
    ///    cranking for 0.95 s with its compression chug (0.5 + 0.5 sin(8t + 1.5t^2), cubed, as the sound has it) over an
    ///    8 Hz shudder, the engine catching at 0.85 s and flaring to StartPeak at 1.2 s, settling to idle by 1.7 s. It runs
    ///    from EngineStartedAt. In a headset the engine waits, after sitting in, for the driver's first grab of the wheel
    ///    (EtsTruckController.RequestEngineStart), so the hand that takes it feels the whole start.
    ///  - Idle: a very small steady buzz (Idle).
    ///  - Turning the wheel: more while it turns, by how fast it turns (full at 180 degrees a second), a quarter of it at a
    ///    standstill rising to all of it at 80 km/h - so turning in a yard is felt, and more so on the road ("relative to
    ///    the speed" either way: the wheel's and the truck's).
    ///  - Accelerating: more by the revs, (rpm - idle) / (limit - idle), while the throttle is on.
    ///  The sum is capped at Cap (the start at StartPeak) and smoothed (0.05 s). Everything times Scale; off with Enabled.
    ///
    /// How: UnityEngine.XR impulses (the only haptics on the server's assembly list; no frequency control), re-sent every
    /// Resend seconds - or sooner when the level moves by 0.02 - each lasting Overlap seconds, so one always runs into the
    /// next and the buzz is continuous; a new impulse replaces the one playing (OpenXR xrApplyHapticFeedback). While an
    /// EtsVr.Pulse plays (the grab, the rim tick, the lock bump, a cab control) nothing is sent to that hand, so those are
    /// never cut short. Stopped with InputDevice.StopHaptics. No allocation per frame.
    ///
    /// The levels are this world's choice, not the game's (the game has no controller haptics to copy), and err toward
    /// "ever so slightly": Quest Touch controllers through the runtimes the user plays on have not been felt with them yet.
    /// Scale and the constants are the knobs; Enabled is the on/off, and CycleStrength / StrengthLabel the steps a tablet
    /// option can offer (normal, strong, off, light).
    /// </summary>
    public sealed class EtsWheelHaptics
    {
        /// <summary>Wheel vibration on or off for this visit (on by default; a tablet option may switch it).</summary>
        public static bool Enabled = true;

        /// <summary>Everything scaled by this (1 = the levels below).</summary>
        public static float Scale = 1f;

        /// <summary>
        /// The next strength, for a tablet button: normal (1) -> strong (1.6) -> off -> light (0.6) -> normal. Logged.
        /// </summary>
        public static void CycleStrength()
        {
            if (!Enabled)
            {
                Enabled = true;
                Scale = 0.6f;
            }
            else if (Scale < 0.8f)
            {
                Scale = 1f;
            }
            else if (Scale < 1.3f)
            {
                Scale = 1.6f;
            }
            else
            {
                Enabled = false;
                Scale = 1f;
            }

            Debug.Log("[SomniumEuroTruck] Wheel haptics: " + StrengthLabel + " (scale " + Scale.ToString("0.0") + ").");
        }

        /// <summary>The strength as the tablet shows it (constants: comparing them allocates nothing).</summary>
        public static string StrengthLabel => !Enabled ? "Wheel vibration: off" : Scale < 0.8f ? "Wheel vibration: light" : Scale < 1.3f ? "Wheel vibration: normal" : "Wheel vibration: strong";

        public const float Idle = 0.06f;
        public const float RevGain = 0.12f;
        public const float TurnGain = 0.10f;
        public const float TurnStandstill = 0.25f;
        public const float Cap = 0.2f;
        public const float StartPeak = 0.25f;
        public const float CrankFloor = 0.08f;
        public const float CrankChug = 0.14f;
        public const float CrankHz = 8f;
        public const float StartSeconds = 1.7f;
        public const float TurnFullDegreesPerSecond = 180f;
        public const float TurnFullKmh = 80f;
        public const float Resend = 0.05f;
        public const float Overlap = 0.12f;
        public const float Smoothing = 0.05f;

        private readonly bool[] _on = new bool[2];
        private readonly float[] _sentAt = { -1f, -1f };
        private readonly float[] _sent = new float[2];
        private float _level;
        private float _lastSteer;
        private bool _haveSteer;
        private float _turnRate;
        private bool _reportedReady, _reportedFirst;
        private float _starterReportedFor = float.NaN;

        /// <summary>Whether a hand is being vibrated now (so Stop has something to end).</summary>
        public bool Active => _on[0] || _on[1];

        /// <summary>One frame: `left` and `right` are the hands holding the rim now.</summary>
        public void Step(EtsTruckController truck, bool left, bool right)
        {
            float dt = Time.deltaTime;

            if (!Enabled || truck == null || !truck.EngineRunning || (!left && !right))
            {
                Stop();
                _haveSteer = false;
                _level = 0f;
                return;
            }

            if (!_reportedReady)
            {
                _reportedReady = true;
                Debug.Log("[SomniumEuroTruck] Wheel haptics: ready (left impulse " + (EtsVr.HasImpulse(false) ? "yes" : "NO") + ", right impulse " +
                          (EtsVr.HasImpulse(true) ? "yes" : "NO") + "), idle " + Idle.ToString("0.00") + ", revs +" + RevGain.ToString("0.00") + ", turning +" +
                          TurnGain.ToString("0.00") + " (" + (TurnStandstill * 100f).ToString("0") + " % of it at a standstill), start peak " + StartPeak.ToString("0.00") + ", cap " + Cap.ToString("0.00") + ", scale " + Scale.ToString("0.00") +
                          "; impulses of " + Overlap.ToString("0.00") + " s every " + Resend.ToString("0.00") + " s.");
            }

            // How fast the wheel turns (degrees a second), smoothed over 0.08 s.
            float steer = truck.SteerInput;
            float rate = _haveSteer && dt > 1e-5f ? Mathf.Abs(steer - _lastSteer) * EtsTruckController.WheelDegreesPerUnit / dt : 0f;
            _lastSteer = steer;
            _haveSteer = true;
            _turnRate += (rate - _turnRate) * (1f - Mathf.Exp(-dt / 0.08f));

            float turn = TurnGain * Mathf.Clamp01(_turnRate / TurnFullDegreesPerSecond) *
                         (TurnStandstill + (1f - TurnStandstill) * Mathf.Clamp01(truck.SpeedKmh / TurnFullKmh));
            float limit = truck.Spec != null ? truck.Spec.RpmLimit : 2200f;
            float revs = Mathf.Clamp01((truck.Rpm - EtsTruckController.IdleRpm) / Mathf.Max(1f, limit - EtsTruckController.IdleRpm));
            float rev = RevGain * revs * Mathf.Clamp01(truck.Throttle / 0.2f);
            float since = Time.time - truck.EngineStartedAt;
            bool starting = since >= 0f && since < StartSeconds;
            float engine = starting ? Starter(since) : Idle;
            float target = Mathf.Min(starting ? Mathf.Max(Cap, StartPeak) : Cap, engine + rev + turn) * Mathf.Max(0f, Scale);

            // The start is already shaped (and its shudder must keep its edges); everything else is smoothed.
            _level = starting ? target : _level + (target - _level) * (1f - Mathf.Exp(-dt / Smoothing));

            if (since >= 0f && since < StartSeconds && !(_starterReportedFor == truck.EngineStartedAt))
            {
                _starterReportedFor = truck.EngineStartedAt;
                Debug.Log("[SomniumEuroTruck] Wheel haptics: starter rumble on " + (left && right ? "both hands" : left ? "the left hand" : "the right hand") + " (" +
                          since.ToString("0.00") + " s into the start).");
            }

            Send(false, left);
            Send(true, right);
        }

        private void Send(bool rightHand, bool holding)
        {
            int h = rightHand ? 1 : 0;

            if (!holding)
            {
                if (_on[h])
                {
                    _on[h] = false;
                    EtsVr.StopRumble(rightHand);
                }

                return;
            }

            float now = Time.unscaledTime;

            if (_on[h] && now < _sentAt[h] + Resend && Mathf.Abs(_level - _sent[h]) < 0.02f)
            {
                return;
            }

            if (_level < 0.005f)
            {
                return;
            }

            if (EtsVr.Rumble(rightHand, _level, Overlap))
            {
                _on[h] = true;
                _sentAt[h] = now;
                _sent[h] = _level;

                if (!_reportedFirst)
                {
                    _reportedFirst = true;
                    Debug.Log("[SomniumEuroTruck] Wheel haptics: first impulse sent (" + (rightHand ? "right" : "left") + " hand, amplitude " + _level.ToString("0.000") + ").");
                }
            }
        }

        /// <summary>Both hands quiet at once.</summary>
        public void Stop()
        {
            for (int h = 0; h < 2; h++)
            {
                if (_on[h])
                {
                    _on[h] = false;
                    EtsVr.StopRumble(h == 1);
                }
            }
        }

        /// <summary>
        /// The starter rumble at t seconds into the start, on EtsTruckAudio.StartWave's timeline: cranking 0-0.95 s (its
        /// fade-in 0.05 s and fade-out 0.08 s, the chug cubed), the catch from 0.85 s (onset 0.08 s) rising with the revs
        /// (300 to 1150 rpm) to StartPeak at 1.2 s, then settling with them (to 650) to Idle by 1.7 s.
        /// </summary>
        public static float Starter(float t)
        {
            float crank = 0f;

            if (t < 0.95f)
            {
                float envelope = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((0.95f - t) / 0.08f);
                float comp = 0.5f + 0.5f * Mathf.Sin(8f * t + 1.5f * t * t);

                // Under it the starter's own shudder, a compression every 1/CrankHz s (a six-cylinder turning over at
                // about 160 rpm): full for half of each, 55 % for the other half.
                float shudder = Mathf.Repeat(t * CrankHz, 1f) < 0.5f ? 1f : 0.55f;
                crank = envelope * (CrankFloor + CrankChug * comp * comp * comp) * shudder;
            }

            float caught = 0f;

            if (t > 0.85f)
            {
                float u = t - 0.85f;
                float rising = Mathf.Clamp01(u / 0.35f);
                float settling = Mathf.Clamp01((u - 0.35f) / 0.5f);
                settling = settling * settling * (3f - 2f * settling);
                float level = u < 0.35f ? Mathf.Lerp(Idle, StartPeak, rising) : Mathf.Lerp(StartPeak, Idle, settling);
                caught = Mathf.Clamp01(u / 0.08f) * level;
            }

            return Mathf.Max(crank, caught);
        }
    }
}
