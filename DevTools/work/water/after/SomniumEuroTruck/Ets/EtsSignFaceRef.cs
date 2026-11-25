using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>On a sign that shows shared template faces: gives its share back when the sign is unloaded (EtsSignFaces.Unref).</summary>
    public sealed class EtsSignFaceRef : MonoBehaviour
    {
        public string Key;

        private void OnDestroy()
        {
            EtsSignFaces.Unref(Key);
        }
    }
}
