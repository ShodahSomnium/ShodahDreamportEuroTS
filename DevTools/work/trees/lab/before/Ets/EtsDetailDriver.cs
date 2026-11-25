using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The one object that keeps the map's small details alive. It does two jobs a frame:
    ///
    /// 1. Builds the details a prefab asked for (its traffic lights and the lamps and signs its descriptor
    ///    lists - a Scandinavian garage names 105 of them) a few at a time under <see cref="FrameBudgetMs"/>,
    ///    so a prefab arriving while the truck drives never costs a frame (VR at 90 Hz has 11 ms for
    ///    everything). A job whose prefab was unloaded meanwhile finds its parent gone and does nothing.
    /// 2. Switches every traffic light's lamps from the shared clock (<see cref="EtsSemaphores.Tick"/>).
    ///
    /// It lives on the map viewer's own object (<see cref="Ensure(GameObject)"/>), so it is in the world's
    /// scene and goes with it; anything that queues work before that makes one of its own.
    /// </summary>
    public sealed class EtsDetailDriver : MonoBehaviour
    {
        /// <summary>Milliseconds of detail building per frame at most (one job always runs).</summary>
        public static float FrameBudgetMs = 1.5f;

        private static EtsDetailDriver _instance;
        private static readonly Queue<Action> Jobs = new Queue<Action>();

        /// <summary>Jobs that threw (each is also logged once per kind by its owner).</summary>
        public static int JobFailures;

        /// <summary>Jobs run since the session started.</summary>
        public static int JobsRun;

        /// <summary>Jobs waiting.</summary>
        public static int Pending => Jobs.Count;

        /// <summary>Called when the queue empties after work (the viewer's "Details:" line hangs here).</summary>
        public static Action Drained;

        private readonly Stopwatch _clock = new Stopwatch();
        private bool _hadWork;

        /// <summary>Queues a small build for a later frame.</summary>
        public static void Enqueue(Action job)
        {
            if (job == null)
            {
                return;
            }

            Jobs.Enqueue(job);
            Ensure(null);
        }

        /// <summary>
        /// Makes sure a driver runs: on <paramref name="host"/> when one is given and none runs yet, else on an object
        /// of its own in the active scene.
        /// </summary>
        public static void Ensure(GameObject host)
        {
            if (_instance != null)
            {
                return;
            }

            // Not "??": in the editor GetComponent hands back a fake-null object that only == null sees.
            EtsDetailDriver found = host != null ? host.GetComponent<EtsDetailDriver>() : null;

            if (found == null)
            {
                found = host != null ? host.AddComponent<EtsDetailDriver>() : new GameObject("Ets details").AddComponent<EtsDetailDriver>();
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
            }
        }

        private void Update()
        {
            if (_instance != this)
            {
                return;
            }

            if (Jobs.Count > 0)
            {
                _hadWork = true;
                _clock.Restart();
                int done = 0;

                while (Jobs.Count > 0 && (done == 0 || _clock.Elapsed.TotalMilliseconds < FrameBudgetMs))
                {
                    Action job = Jobs.Dequeue();
                    done++;
                    JobsRun++;

                    try
                    {
                        job();
                    }
                    catch (Exception e)
                    {
                        JobFailures++;
                        EtsSemaphores.Warn("detail build", e);
                    }
                }
            }
            else if (_hadWork)
            {
                _hadWork = false;

                try
                {
                    Drained?.Invoke();
                }
                catch (Exception e)
                {
                    EtsSemaphores.Warn("details report", e);
                }
            }

            // Guarded: an exception out of Update would be logged again every frame.
            try
            {
                EtsSemaphores.Tick();
            }
            catch (Exception e)
            {
                EtsSemaphores.Warn("switching the traffic lights", e);
            }
        }
    }
}
