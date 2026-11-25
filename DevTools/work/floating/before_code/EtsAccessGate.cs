using System;
using System.Collections;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The "Ets Access" object (2026-10-20 whitelist gate; BO2's Bo2Game.Gate). Made by <see cref="EtsAccess.Hold"/> when the
    /// first of the scene's world scripts wakes (the scene file is not edited), in the world's scene, so it goes with it.
    /// Awake: the isolation with the rule "nobody is with me" (every other player hidden and silent while the check waits),
    /// then the check (<see cref="EtsAccess.Check"/>). Allowed: the isolation's rule becomes "the other player is on the
    /// whitelist too" and the held world scripts start (<see cref="EtsAccess.ReleaseAll"/>). Denied: nothing more ever
    /// runs - the visitor stands on the scene's plain spawn platform in an empty world and sees and hears nobody.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public sealed class EtsAccessGate : MonoBehaviour
    {
        private void Awake()
        {
            EtsAccess.Attach(this);
            StartIsolation(NobodyRule("waiting for the whitelist check"));
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            IEnumerator check = EtsAccess.Check();
            while (check.MoveNext())
            {
                yield return check.Current;
            }

            if (!EtsAccess.Allowed)
            {
                StartIsolation(NobodyRule("visitor (not on the whitelist)"));
                yield break;
            }

            StartIsolation(WhitelistRule());
            EtsAccess.ReleaseAll();
        }

        private EtsIsolation.Rule NobodyRule(string who)
        {
            return new EtsIsolation.Rule
            {
                Name = who + ": nobody is with you",
                IsWithMe = id => false,
                WithText = "with you",
                ApartText = "not shown to a visitor",
                WorldRoot = transform,
            };
        }

        /// <summary>A whitelisted player's rule: the other player is on the whitelist too. Everyone else is a visitor: hidden and silent.</summary>
        private EtsIsolation.Rule WhitelistRule()
        {
            return new EtsIsolation.Rule
            {
                Name = "whitelisted: other whitelisted players",
                IsWithMe = EtsAccess.IsTesterId,
                WithText = "is on the whitelist (a Dreamport player)",
                ApartText = "not on the whitelist (a visitor)",
                ShowDelay = 0f,
                DecideInterval = 0f,
                WorldRoot = transform,
            };
        }

        private void StartIsolation(EtsIsolation.Rule rule)
        {
            try
            {
                EtsIsolation.Start(rule);
                if (GetComponent<EtsIsolationDriver>() == null)
                {
                    gameObject.AddComponent<EtsIsolationDriver>();
                }
            }
            catch (Exception e)
            {
                EtsAccess.Error("Access: starting the isolation failed: " + e);
            }
        }

        private void OnDestroy()
        {
            // Other players' hidden renderers, colliders and voices first, then the verdict is forgotten (statics survive the
            // visit). The driver's own OnDestroy restores too (idempotent).
            try
            {
                EtsRemotes.Reset();
            }
            catch (Exception e)
            {
                EtsAccess.Error("Access: isolation shutdown failed: " + e);
            }

            EtsAccess.Detach(this);
        }
    }
}
