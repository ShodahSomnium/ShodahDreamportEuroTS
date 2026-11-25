using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The trees of one map item, with no GameObject per tree: instances plus far and near combined meshes, on a
    /// child "vegetation" object of the item root (owner F; [env §B.2], PLAN.md §8 F2). A MonoBehaviour in the
    /// Ets namespace, as its folder is (PLAN §7.F does the same for EtsPostFx). Phase 0 stub: an empty
    /// component that nothing adds.
    /// </summary>
    public sealed class EtsVegetationOwner : MonoBehaviour
    {
    }
}
