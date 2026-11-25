using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The late participant of the execution-order probe (owner L; PLAN.md §4 U1, §8 Phase 0 task 0.3. Phase 0
    /// probe; removed in Phase 2). Order 0 is the order of the client's own receive drivers and of
    /// NonXRRootsMovementHandler, which places desktop avatars (PLAN §5.4 step 4). Added twice at run time by
    /// EtsOrderProbe.Begin, first and last; see EtsOrderProbe for the method. The attribute is written out,
    /// although 0 is the default, so both probes carry one and differ only in its value.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public sealed class EtsOrderProbeLate : MonoBehaviour
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
