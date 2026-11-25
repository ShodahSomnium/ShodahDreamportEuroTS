using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The early participant of the execution-order probe (owner L; PLAN.md §4 U1, §8 Phase 0 task 0.3. Phase 0
    /// probe; removed in Phase 2). Order -95 is the one [elev §7.3.1] proposed for EtsRiders. Added twice at run
    /// time by EtsOrderProbe.Begin, never by the scene; see EtsOrderProbe for the method. In a file of its own,
    /// named after the class, so the editor gives it a script asset of its own, as EtsRiders would have.
    /// </summary>
    [DefaultExecutionOrder(-95)]
    public sealed class EtsOrderProbeEarly : MonoBehaviour
    {
        /// <summary>Which participant this is (set by EtsOrderProbe.Begin right after AddComponent); -1 = none, removes itself.</summary>
        public int Slot = -1;

        private void Update()
        {
            if (!EtsOrderProbe.Mark(Slot))
            {
                Destroy(this);
            }
        }
    }
}
