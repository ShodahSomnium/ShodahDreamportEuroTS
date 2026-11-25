using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Hands finished sign faces (EtsSignFaces, composed on their worker thread) to the main thread every frame. Lives on
    /// an object of its own in the active scene, made the first time a templated sign is queued.
    /// </summary>
    public sealed class EtsSignFacePump : MonoBehaviour
    {
        private static EtsSignFacePump _instance;

        public static void Ensure()
        {
            if (_instance != null) return;
            _instance = new GameObject("Ets sign faces").AddComponent<EtsSignFacePump>();
        }

        private void Update()
        {
            EtsSignFaces.Pump();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
