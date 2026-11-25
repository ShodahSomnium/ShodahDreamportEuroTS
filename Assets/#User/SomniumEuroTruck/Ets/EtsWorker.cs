using System;
using System.Collections.Generic;
using System.Threading;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One background thread for pure work - archive reads, GDeflate, parsing - so the frame never
    /// waits on it. Results come back through Drain on the main thread, where anything Unity is done.
    ///
    /// Why a thread at all: measured on the 1.61 demo (harness Program1400/1401), the traffic's car
    /// pool pulls in 144 textures whose read + GDeflate + surface layout costs 508 ms of CPU (median
    /// 0.8 ms, p90 11 ms, worst 50 ms for a 1024x1024 DXT5 colour map), and each car model file costs
    /// 1-6 ms to read and parse. None of that can be split into a VR frame's 2 ms. Every job posted
    /// here must be pure: no UnityEngine object, no shared cache that the main thread writes. The
    /// archive reader is safe for it: its entry tables are written only while mounting, and the
    /// one stream read is under a lock (EtsHashFs.ReadExactly).
    ///
    /// If the thread cannot be started, jobs run on the main thread instead, one per Drain call,
    /// which is the same work in the same order, only on the frame.
    /// </summary>
    public sealed class EtsWorker : IDisposable
    {
        private sealed class Job
        {
            public Func<object> Work;
            public Action<object, Exception> Done;
            public object Result;
            public Exception Failure;
        }

        private readonly object _gate = new object();
        private readonly Queue<Job> _todo = new Queue<Job>();
        private readonly Queue<Job> _done = new Queue<Job>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _stop;

        /// <summary>True when jobs run on the background thread; false when they run in Drain.</summary>
        public bool Threaded { get; }

        /// <summary>Why the thread could not start, when it could not.</summary>
        public string Failure { get; }

        public int Waiting
        {
            get
            {
                lock (_gate)
                {
                    return _todo.Count + _done.Count;
                }
            }
        }

        public EtsWorker(string name)
        {
            try
            {
                _thread = new Thread(Run) { Name = name, IsBackground = true, Priority = ThreadPriority.BelowNormal };
                _thread.Start();
                Threaded = true;
            }
            catch (Exception e)
            {
                _thread = null;
                Threaded = false;
                Failure = e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>Queues pure work; done runs later on the main thread, from Drain, with the result or the exception.</summary>
        public void Post(Func<object> work, Action<object, Exception> done)
        {
            var job = new Job { Work = work, Done = done };

            lock (_gate)
            {
                _todo.Enqueue(job);
            }

            if (Threaded)
            {
                _wake.Set();
            }
        }

        /// <summary>
        /// Main thread: hands finished jobs to their callbacks until 'more' says stop (checked before
        /// each one, so a caller can hold a frame budget). Without the thread, runs at most one job
        /// here first. Returns how many callbacks ran.
        /// </summary>
        public int Drain(Func<bool> more)
        {
            if (!Threaded)
            {
                Job inline = null;

                lock (_gate)
                {
                    if (_todo.Count > 0 && more())
                    {
                        inline = _todo.Dequeue();
                    }
                }

                if (inline != null)
                {
                    Execute(inline);

                    lock (_gate)
                    {
                        _done.Enqueue(inline);
                    }
                }
            }

            int ran = 0;

            while (more())
            {
                Job job;

                lock (_gate)
                {
                    if (_done.Count == 0)
                    {
                        break;
                    }

                    job = _done.Dequeue();
                }

                job.Done?.Invoke(job.Result, job.Failure);
                ran++;
            }

            return ran;
        }

        private static void Execute(Job job)
        {
            try
            {
                job.Result = job.Work();
            }
            catch (Exception e)
            {
                job.Failure = e;
            }
        }

        private void Run()
        {
            while (!_stop)
            {
                Job job = null;

                lock (_gate)
                {
                    if (_todo.Count > 0)
                    {
                        job = _todo.Dequeue();
                    }
                }

                if (job == null)
                {
                    _wake.WaitOne(250);
                    continue;
                }

                Execute(job);

                lock (_gate)
                {
                    _done.Enqueue(job);
                }
            }
        }

        public void Dispose()
        {
            _stop = true;

            if (Threaded)
            {
                _wake.Set();
            }

            lock (_gate)
            {
                _todo.Clear();
                _done.Clear();
            }
        }
    }
}
