using System;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Plants the map's trees a little a frame round the stream's focus (EtsVegetationBuilder.Pump): the road and
    /// terrain sides within EtsVegetationBuilder.PlantRadius, nearest first, under a per-frame budget, and submits the
    /// GPU-instanced trees every frame (EtsVegetationBuilder.DrawTrees -> EtsTreeField.Draw; on the GameObject
    /// fallback it switches planted sides off beyond HideBeyond instead). Made on the map viewer's own object by the
    /// first side queued (<see cref="Ensure"/>), so it is in the world's scene and goes with it; the queue and the
    /// instanced field go with it too.
    /// </summary>
    public sealed class EtsVegetationStream : MonoBehaviour
    {
        private static EtsVegetationStream _instance;
        private int _failures;

        /// <summary>Makes sure a driver runs: on the map viewer's object when there is one, else on an object of its own.</summary>
        public static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }

            EtsMapViewer viewer = EtsMapViewer.Current;
            GameObject host = viewer != null ? viewer.gameObject : null;

            // Not "??": in the editor GetComponent hands back a fake-null object that only == null sees.
            EtsVegetationStream found = host != null ? host.GetComponent<EtsVegetationStream>() : null;

            if (found == null)
            {
                found = host != null ? host.AddComponent<EtsVegetationStream>() : new GameObject("Ets vegetation").AddComponent<EtsVegetationStream>();
            }

            _instance = found;
        }

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                EtsVegetationBuilder.Forget();
            }
        }

        private void Update()
        {
            if (_instance != this)
            {
                return;
            }

            // The stream's own focus (EtsGroundFill.FocusTarget); none yet means the build's centre, Unity's origin.
            Transform target = EtsGroundFill.FocusTarget(null);
            bool loading = EtsGroundFill.SessionLoading || !EtsMapViewer.AnyCompleted;

            Vector3 focus = target != null ? target.position : Vector3.zero;

            // Guarded: an exception out of Update would be logged again every frame.
            try
            {
                EtsVegetationBuilder.Pump(focus, loading);
            }
            catch (Exception e)
            {
                if (_failures++ < 3)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Terrain: planting the vegetation failed: " + e.GetType().Name + ": " + e.Message);
                }
            }

            // The instanced trees are submitted here, in Update, before any camera renders this frame (the headset's,
            // the mirrors', and a camera rendered by hand from a coroutine, which runs after Update).
            try
            {
                EtsVegetationBuilder.DrawTrees(focus);
            }
            catch (Exception e)
            {
                if (_failures++ < 3)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Trees: drawing the instanced trees failed: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }
}
