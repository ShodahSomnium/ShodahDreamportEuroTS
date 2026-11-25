using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Keeps every truck's (the own, the puppets') and its coupled trailer's model lamps in step with the truck
    /// (2026-10-04, vehicle indicator lamps; the lamps themselves are Ets/EtsVehicleLamps): the turn signals with
    /// EtsTruckController.Indicator and IndicatorLit (the controller also switches them on every blink edge through
    /// EtsIndicatorLamps, so a blink is never a frame late), the brake lamps with Brake (the pedal; a puppet's comes
    /// off the wire) and the reverse lamps with a reverse gear. A truck's lamps are built the first time any of them
    /// is to burn, when the truck is whole. One object for the world, made when the first lamp lens is built; it finds
    /// the trucks every 2 s and otherwise only compares a few numbers a frame.
    /// </summary>
    public sealed class EtsVehicleLampDriver : MonoBehaviour
    {
        private static EtsVehicleLampDriver _instance;
        private static readonly Dictionary<Transform, EtsVehicleLamps> Built = new Dictionary<Transform, EtsVehicleLamps>();
        private static readonly List<Transform> Dead = new List<Transform>();

        /// <summary>The brake pedal share above which the brake lamps burn.</summary>
        public const float BrakeLampThreshold = 0.05f;

        private EtsTruckController[] _trucks = new EtsTruckController[0];
        private readonly Dictionary<EtsTruckController, EtsTrailer> _trailerOf = new Dictionary<EtsTruckController, EtsTrailer>();
        private float _nextFind;

        /// <summary>Makes the world's driver once (EtsVehicleLamps.NoteLampPiece, main thread).</summary>
        public static void Ensure()
        {
            if (!ReferenceEquals(_instance, null) && _instance != null)
            {
                return;
            }

            var host = new GameObject("Ets vehicle lamps");
            _instance = host.AddComponent<EtsVehicleLampDriver>();
        }

        /// <summary>The truck's lamps, built on first ask (its own lamp setup by its truck id; never its trailer's lenses).</summary>
        public static EtsVehicleLamps For(EtsTruckController truck)
        {
            if (truck == null)
            {
                return null;
            }

            Transform root = truck.transform;

            if (Built.TryGetValue(root, out EtsVehicleLamps lamps))
            {
                return lamps;
            }

            string truckId = null;

            try
            {
                EtsTruckRig rig = truck.GetComponentInParent<EtsTruckRig>();
                if (rig == null) rig = truck.GetComponentInChildren<EtsTruckRig>(true);
                truckId = rig != null && rig.Assembly != null ? rig.Assembly.TruckId : null;
            }
            catch (Exception)
            {
                truckId = null;
            }

            EtsLampSetup setup = EtsLampSetup.For(truckId, ReadSii);
            lamps = EtsVehicleLamps.Build(root, setup, t => Foreign(t, root));
            Built[root] = lamps;
            Debug.Log("[SomniumEuroTruck] Indicator lamps: " + (truckId ?? root.name) + " - " + lamps.Describe() + ".");
            return lamps;
        }

        /// <summary>A trailer's lamps (the default lamp setup: no trailer has one of its own in the demo).</summary>
        public static EtsVehicleLamps For(EtsTrailer trailer)
        {
            if (trailer == null)
            {
                return null;
            }

            Transform root = trailer.transform;

            if (Built.TryGetValue(root, out EtsVehicleLamps lamps))
            {
                return lamps;
            }

            lamps = EtsVehicleLamps.Build(root, EtsLampSetup.For(null, ReadSii), t => Foreign(t, root));
            Built[root] = lamps;
            Debug.Log("[SomniumEuroTruck] Indicator lamps: trailer '" + root.name + "' - " + lamps.Describe() + ".");
            return lamps;
        }

        private static EtsSiiFile ReadSii(string path)
        {
            return EtsEngine.Files != null ? EtsEngine.Files.ReadSii(path) : null;
        }

        /// <summary>True for a lens that belongs to another vehicle hung under this one (a trailer, a truck).</summary>
        private static bool Foreign(Transform t, Transform root)
        {
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                if (p.GetComponent<EtsTrailer>() != null || p.GetComponent<EtsTruckController>() != null)
                {
                    return true;
                }
            }

            return false;
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 2f;
                _trucks = FindObjectsByType<EtsTruckController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                Forget();
            }

            for (int i = 0; i < _trucks.Length; i++)
            {
                EtsTruckController truck = _trucks[i];

                if (truck == null)
                {
                    continue;
                }

                try
                {
                    Drive(truck);
                }
                catch (Exception e)
                {
                    _trucks[i] = null;
                    Debug.LogWarning("[SomniumEuroTruck] Indicator lamps: '" + truck.name + "' failed and is left alone: " + e.Message);
                }
            }
        }

        private void Drive(EtsTruckController truck)
        {
            int indicator = truck.Indicator;
            bool lit = truck.IndicatorLit;
            bool left = lit && (indicator == -1 || indicator == 2);
            bool right = lit && (indicator == 1 || indicator == 2);
            bool brake = truck.Brake > BrakeLampThreshold;
            bool reverse = truck.Gear < 0;
            bool any = left || right || brake || reverse;

            if (!any && !Built.ContainsKey(truck.transform))
            {
                return; // Nothing to light yet: built when something first burns (the truck is whole by then).
            }

            For(truck).Set(left, right, brake, reverse);

            EtsTrailer trailer = truck.Trailer;
            _trailerOf.TryGetValue(truck, out EtsTrailer before);

            if (before != trailer)
            {
                if (before != null && Built.TryGetValue(before.transform, out EtsVehicleLamps old))
                {
                    old.Set(false, false, false, false);
                }

                _trailerOf[truck] = trailer;
            }

            if (trailer != null && (any || Built.ContainsKey(trailer.transform)))
            {
                For(trailer).Set(left, right, brake, reverse);
            }
        }

        /// <summary>Drops the entries of vehicles that are gone.</summary>
        private void Forget()
        {
            Dead.Clear();

            foreach (KeyValuePair<Transform, EtsVehicleLamps> entry in Built)
            {
                if (entry.Key == null)
                {
                    Dead.Add(entry.Key);
                }
            }

            foreach (Transform t in Dead)
            {
                Built.Remove(t);
            }

            if (_trailerOf.Count > 0)
            {
                var gone = new List<EtsTruckController>();

                foreach (KeyValuePair<EtsTruckController, EtsTrailer> entry in _trailerOf)
                {
                    if (entry.Key == null) gone.Add(entry.Key);
                }

                foreach (EtsTruckController t in gone) _trailerOf.Remove(t);
            }
        }
    }
}
