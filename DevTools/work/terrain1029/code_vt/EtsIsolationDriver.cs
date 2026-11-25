using System;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Ticks the remote-player list and its isolation every frame (the BO2 Dreamport's Bo2IsolationDriver, from the Rec Room
    /// Dreamport's RrVisitorDriver; ported 2026-10-20). EtsAccessGate adds it to the "Ets Access" object at once, before the
    /// whitelist verdict (EtsAccess):
    /// - While the check waits, and for good for a visitor who is not on the whitelist, it is the ONLY thing of the world that
    ///   runs; its rule is "nobody is with me", so every other player is hidden and silent on the visitor's screen.
    /// - For a whitelisted player it runs beside the Dreamport; its rule is "the other player is on the whitelist too"
    ///   (EtsAccess.IsTesterId), so visitors are hidden and silent, and testers are shown.
    /// LateUpdate at order 9985: after the client's Update work, before EtsAvatarSync (9990) and EtsNameplate (10000).
    /// The isolation restores everyone when this component or the world goes away (idempotent).
    /// </summary>
    [DefaultExecutionOrder(9985)]
    public sealed class EtsIsolationDriver : MonoBehaviour
    {
        private int _errors;

        private void LateUpdate()
        {
            try
            {
                EtsRemotes.Tick();
            }
            catch (Exception e)
            {
                _errors++;
                if (_errors <= 3)
                {
                    EtsAccess.Warn("Access: hiding other players failed (" + _errors + "): " + e.Message);
                }
            }
        }

        private void OnDestroy()
        {
            EtsRemotes.Reset();
        }
    }
}
