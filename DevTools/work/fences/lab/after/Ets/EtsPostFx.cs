using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The world's global post-processing Volume: the game's tone curve, colour grading and bloom as far as URP's
    /// stock effects follow them ([env §D.2], lighting owner 2026-09-23).
    ///
    /// Why a Volume at all: in a world the client runs NO tonemapping and no bloom (its default profile has
    /// Tonemapping None and Bloom 0, and "Player is in a World, ignoring Bloom setting"), so the world's HDR
    /// lighting was clipped at 1. The player cameras' volume mask is Default only, hence layer 0; priority 100
    /// over the client's own.
    ///
    /// The game (def_tonemap, /effect/deferred/_shd/915c99bf3109a427c01cc4b3bff097a2.glsl.fso): scene x the
    /// adapted exposure, + bloom (intensity bloom_intensity), saturation around luma (color_saturation), colour
    /// balance, then a piecewise log-log tone curve on luminance built on the CPU from target_gray,
    /// shoulder_length and contrast, hue kept, desaturating to white at the top. Here:
    /// - exposure: EtsClimate.Exposure, already in every light and colour; PostExposure (EV) on top, 0 by default;
    /// - the curve: URP Neutral (0.18 -> 0.18, 0.26 -> 0.25, 1 -> 0.63, white at 5.3: identity through the mid
    ///   tones with a soft shoulder, like the game's around its target_gray);
    /// - contrast: the curve's mid-tone slope in log space. URP's contrast is exactly that (LogC around 0.18), and
    ///   the file's 0.31 read as a log-log slope of 1.31 would be 31. ContrastScale (1/3) takes a third of it,
    ///   10 at 10:00 (the environment design's starting value), because the game's auto-exposure lifts dark
    ///   views (a cab interior filling a headset) that a fixed exposure cannot, and more contrast darkens them;
    /// - saturation: (color_saturation - 1) x 100 = +7.1, the same form as the game's;
    /// - bloom: the game's intensity (0.095 at 10:00), threshold 0 (the file's 1.5e-5), clamp bloom_limit x
    ///   exposure, a tight blur (the game's sigma is 3.5 px): quarter resolution, 3 iterations - cheap at 90 Hz;
    /// - no depth of field, sunshafts, SSAO or auto-exposure (not in URP stock, or uncomfortable in a headset).
    ///
    /// PhaseWeight is EtsFlow's handle: 0 while the HQ stage or a loading screen is up (the void black, the
    /// game's full-view screens un-tonemapped), 1 in the world. It is applied to the Volume's weight at once.
    /// </summary>
    public sealed class EtsPostFx : MonoBehaviour
    {
        public static EtsPostFx Current { get; private set; }

        private static float _phaseWeight = 1f;

        /// <summary>
        /// EtsFlow sets it: 0 during HQ and Loading, 1 in the world. With the tone curve off (0) the climate's lights go back
        /// to the level the void and the avatars were tuned on (EtsClimate.FullViewPhase; 2026-09-25 the exposure became the
        /// game's, 2.4 x the old one, which untonemapped would blow an avatar in the void out to white).
        /// </summary>
        public static float PhaseWeight
        {
            get => _phaseWeight;
            set
            {
                _phaseWeight = Mathf.Clamp01(value);
                EtsClimate.FullViewPhase = _phaseWeight < 0.5f;

                if (Current != null)
                {
                    Current.ApplyWeight();
                }
            }
        }

        /// <summary>Extra exposure in EV on top of the climate's (EtsMapViewer's _postExposure).</summary>
        public static float PostExposure;

        /// <summary>How much of the file's contrast (as a log-space slope) is applied; see the class notes.</summary>
        public static float ContrastScale = 1f / 3f;

        /// <summary>The line for the client log, set by Apply.</summary>
        public string Summary { get; private set; } = "Look: post volume not applied.";

        private Volume _volume;
        private VolumeProfile _profile;
        private Tonemapping _tone;
        private ColorAdjustments _grade;
        private Bloom _bloom;
        private bool _xr;

        /// <summary>Builds the Volume under <paramref name="parent"/> (so it lives and dies with the world's scene), or re-points the existing one.</summary>
        public static EtsPostFx Create(Transform parent, EtsClimate climate, bool xr)
        {
            if (Current != null)
            {
                Current._xr = xr;
                Current.Apply(climate);
                return Current;
            }

            var go = new GameObject("Ets Post") { layer = 0 };

            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            EtsPostFx fx = go.AddComponent<EtsPostFx>();
            fx._xr = xr;
            fx.Build();
            fx.Apply(climate);
            return fx;
        }

        private void Build()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "ets post";
            _tone = _profile.Add<Tonemapping>(false);
            _grade = _profile.Add<ColorAdjustments>(false);
            _bloom = _profile.Add<Bloom>(false);

            _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.sharedProfile = _profile;
            Current = this;
            ApplyWeight();
        }

        /// <summary>Every value from the climate (again when the climate changes).</summary>
        public void Apply(EtsClimate climate)
        {
            if (_profile == null || climate == null)
            {
                return;
            }

            CultureInfo i = CultureInfo.InvariantCulture;
            float contrast = Mathf.Clamp(climate.Contrast * 100f * ContrastScale, -100f, 100f);
            float saturation = Mathf.Clamp((climate.Saturation - 1f) * 100f, -100f, 100f);
            float clamp = Mathf.Max(0.5f, climate.BloomLimit * climate.Exposure);

            _tone.mode.Override(TonemappingMode.Neutral);
            _grade.postExposure.Override(PostExposure);
            _grade.contrast.Override(contrast);
            _grade.saturation.Override(saturation);

            bool bloom = climate.BloomIntensity > 0.001f;
            _bloom.active = bloom;
            _bloom.threshold.Override(0f);
            _bloom.intensity.Override(bloom ? climate.BloomIntensity : 0f);
            _bloom.scatter.Override(0.5f);
            _bloom.clamp.Override(clamp);
            _bloom.highQualityFiltering.Override(false);
            _bloom.downscale.Override(BloomDownscaleMode.Quarter);
            _bloom.maxIterations.Override(3);
            ApplyWeight();

            Summary = "Look: post volume Neutral, exposure " + (PostExposure >= 0f ? "+" : "") + PostExposure.ToString("0.0", i) + " EV over the climate's x" +
                      climate.Exposure.ToString("0.0000", i) + ", contrast " + contrast.ToString("0.0", i) + " (file " + climate.Contrast.ToString("0.000", i) + " x " +
                      ContrastScale.ToString("0.00", i) + "), saturation " + (saturation >= 0f ? "+" : "") + saturation.ToString("0.0", i) + ", bloom " +
                      (bloom ? climate.BloomIntensity.ToString("0.000", i) + " threshold 0 clamp " + clamp.ToString("0.00", i) + " quarter-res x3" : "off") +
                      ", colour balance " + (climate.ColorBalance.r != 0f || climate.ColorBalance.g != 0f || climate.ColorBalance.b != 0f ? "NOT APPLIED " + climate.ColorBalance.ToString("0.000") : "none") +
                      ", no DOF/sunshafts/AO" + (_xr ? " (XR)" : "") + ", weight " + _phaseWeight.ToString("0", i) + " (EtsFlow's phase), layer 0 priority 100.";
        }

        private void ApplyWeight()
        {
            if (_volume != null)
            {
                _volume.weight = _phaseWeight;
            }
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }

            if (_profile != null)
            {
                Destroy(_profile);
                _profile = null;
            }
        }
    }
}
