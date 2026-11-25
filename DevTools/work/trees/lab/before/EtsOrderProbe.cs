using System;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Does the client honour [DefaultExecutionOrder] for world components? (owner L; PLAN.md §4 U1, §8 Phase 0
    /// task 0.3. Phase 0 probe; removed in Phase 2.)
    ///
    /// The scene references four world scripts (Euro Truck Scene.unity: EtsModelViewer :1085, EtsMapViewer
    /// :1377, EtsNet :1453, SceneNetworking :1502), and EtsNet is the only one of them with an order
    /// ([DefaultExecutionOrder(-100)], EtsNet.cs:38); everything else ordered is added at run time with
    /// AddComponent (EtsTraffic -90 today; EtsSession -110,
    /// EtsRiders -95 and EtsTabletLink 1000 proposed). The world assembly is compiled on the server and loaded
    /// with Assembly.Load (persist §2.3), and no log so far shows whether Unity reads the attribute for types
    /// from it. The rider design needs remote bodies written before the client's order-0 scripts (PLAN §3.3,
    /// [elev §7.3.1]); PLAN §3.3 already routes every ordered pass through EtsNet, and this measures whether a
    /// later design may rely on the attribute instead.
    ///
    /// How: EtsNet.Update and four runtime-added components stamp Time.frameCount and one shared sequence
    /// counter on their first 60 Updates. Two are EtsOrderProbeEarly (-95) and two EtsOrderProbeLate (0), added
    /// in the order late, early, early, late. Unity does not promise an order between components of equal
    /// order, so if the attribute were ignored either the first-added or the last-added would tend to lead -
    /// and with a late one at each end, neither convention can put both early ones before both late ones.
    /// "early before late: yes" therefore means the attribute is honoured. Each probe class has its own file,
    /// named after it, as EtsRiders and EtsSession will have, because the editor keys a script's execution
    /// order on the script asset of the file's matching class.
    ///
    /// One line is logged, then the four components remove themselves:
    ///   [SomniumEuroTruck] Order: EtsNet first yes/no; early(-95) before late(0) yes/no; ...
    /// Begin and Mark never throw: a fault stops the probe with one "Order: no verdict ..." line instead, so
    /// EtsNet.Awake and EtsNet.Update, which call them, run as they did before Phase 0.
    /// </summary>
    public static class EtsOrderProbe
    {
        /// <summary>Participant slots. Net is EtsNet; the others are the components in the order they are added.</summary>
        public const int Net = 0;
        private const int LateAddedFirst = 1;
        private const int EarlyA = 2;
        private const int EarlyB = 3;
        private const int LateAddedLast = 4;
        private const int Slots = 5;

        /// <summary>Updates recorded per participant.</summary>
        public const int Samples = 60;

        /// <summary>A participant that never updates (disabled, or never added) must not keep the probe alive: report after this many frames anyway.</summary>
        private const int GiveUpFrames = 600;

        private static readonly string[] Names = { "EtsNet", "late#1", "early#1", "early#2", "late#2" };
        private static readonly int[,] Frames = new int[Slots, Samples];
        private static readonly int[,] Sequence = new int[Slots, Samples];
        private static readonly int[] Counts = new int[Slots];
        private static int _sequence;
        private static int _firstFrame = -1;
        private static bool _running;

        /// <summary>
        /// Starts the probe on the bootstrap object. Called from EtsNet.Awake, always (not behind EtsFeatures.NewFlow).
        /// Statics survive leaving and re-entering the world in one client session (the assembly is loaded once,
        /// persist §2.3), so every visit starts from zero and measures again.
        /// </summary>
        public static void Begin(GameObject host)
        {
            if (host == null)
            {
                return;
            }

            for (int i = 0; i < Slots; i++)
            {
                Counts[i] = 0;
            }

            _sequence = 0;
            _firstFrame = -1;
            _running = true;

            try
            {
                // Late, early, early, late: see the class comment. Slot is set after AddComponent; the first
                // Update, which is the first use of it, comes later.
                host.AddComponent<EtsOrderProbeLate>().Slot = LateAddedFirst;
                host.AddComponent<EtsOrderProbeEarly>().Slot = EarlyA;
                host.AddComponent<EtsOrderProbeEarly>().Slot = EarlyB;
                host.AddComponent<EtsOrderProbeLate>().Slot = LateAddedLast;
            }
            catch (Exception e)
            {
                // Without all four there is no fair test. Any that were added find the probe stopped on their
                // first Update and remove themselves.
                Fail("could not add the probes", e);
            }
        }

        /// <summary>Stops the probe with its one line: the probe never throws into EtsNet (PLAN.md §8 Phase 0).</summary>
        private static void Fail(string what, Exception e)
        {
            _running = false;
            Debug.LogWarning("[SomniumEuroTruck] Order: no verdict, " + what + " (" + e.GetType().Name + ": " + e.Message + ").");
        }

        /// <summary>
        /// Records one Update of a participant. Returns false once the probe is finished (the caller's cue to
        /// remove itself, and EtsNet's to stop calling). Never throws: a fault ends the probe with one line.
        /// </summary>
        public static bool Mark(int slot)
        {
            if (!_running || slot < 0 || slot >= Slots)
            {
                return false;
            }

            try
            {
                return Record(slot);
            }
            catch (Exception e)
            {
                // _running is already false if the fault was in Report; either way this is the only line.
                if (_running)
                {
                    Fail("the probe failed", e);
                }
                else
                {
                    Debug.LogWarning("[SomniumEuroTruck] Order: no verdict, the report failed (" + e.GetType().Name + ": " + e.Message + ").");
                }

                _running = false;
                return false;
            }
        }

        private static bool Record(int slot)
        {
            int frame = Time.frameCount;

            if (_firstFrame < 0)
            {
                _firstFrame = frame;
            }

            int n = Counts[slot];

            if (n < Samples)
            {
                Frames[slot, n] = frame;
                Sequence[slot, n] = ++_sequence;
                Counts[slot] = n + 1;
            }

            bool full = true;

            for (int i = 0; i < Slots; i++)
            {
                full &= Counts[i] >= Samples;
            }

            if (full || frame - _firstFrame > GiveUpFrames)
            {
                _running = false;
                Report();
                return false;
            }

            return true;
        }

        private static int SequenceAt(int slot, int frame)
        {
            for (int i = 0; i < Counts[slot]; i++)
            {
                if (Frames[slot, i] == frame)
                {
                    return Sequence[slot, i];
                }
            }

            return -1;
        }

        private static void Report()
        {
            int common = 0;
            int netFirst = 0;
            int earlyFirst = 0;
            int from = int.MaxValue;
            int to = int.MinValue;
            string firstOrder = "";
            var seq = new int[Slots];

            // Frames every participant ran in: EtsNet's frames are the reference.
            for (int i = 0; i < Counts[Net]; i++)
            {
                int frame = Frames[Net, i];
                bool all = true;

                for (int s = 0; s < Slots && all; s++)
                {
                    seq[s] = s == Net ? Sequence[Net, i] : SequenceAt(s, frame);
                    all = seq[s] >= 0;
                }

                if (!all)
                {
                    continue;
                }

                common++;
                from = Mathf.Min(from, frame);
                to = Mathf.Max(to, frame);
                int firstOther = Mathf.Min(Mathf.Min(seq[LateAddedFirst], seq[EarlyA]), Mathf.Min(seq[EarlyB], seq[LateAddedLast]));

                if (seq[Net] < firstOther)
                {
                    netFirst++;
                }

                if (Mathf.Max(seq[EarlyA], seq[EarlyB]) < Mathf.Min(seq[LateAddedFirst], seq[LateAddedLast]))
                {
                    earlyFirst++;
                }

                if (firstOrder.Length == 0)
                {
                    firstOrder = OrderOf(seq);
                }
            }

            var sb = new StringBuilder("[SomniumEuroTruck] Order: EtsNet first ");
            sb.Append(Verdict(netFirst, common)).Append("; early(-95) before late(0) ").Append(Verdict(earlyFirst, common));
            sb.Append("; ").Append(common).Append(" frames with all five");

            if (common > 0)
            {
                sb.Append(" (").Append(from).Append('-').Append(to).Append("): EtsNet first in ").Append(netFirst)
                  .Append(", both early before both late in ").Append(earlyFirst).Append("; first such frame ran ").Append(firstOrder);
            }

            sb.Append("; updates recorded");

            for (int s = 0; s < Slots; s++)
            {
                sb.Append(s == 0 ? " " : ", ").Append(Names[s]).Append(' ').Append(Counts[s]);
            }

            sb.Append(". Added late, early, early, late: 'yes' means [DefaultExecutionOrder] is honoured for runtime-added world components.");
            Debug.Log(sb.ToString());
        }

        /// <summary>The participants of one frame, in the order they ran.</summary>
        private static string OrderOf(int[] seq)
        {
            var order = new int[Slots];

            for (int s = 0; s < Slots; s++)
            {
                order[s] = s;
            }

            // Five items: an insertion sort by sequence.
            for (int i = 1; i < Slots; i++)
            {
                int item = order[i];
                int j = i - 1;

                while (j >= 0 && seq[order[j]] > seq[item])
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = item;
            }

            var sb = new StringBuilder();

            for (int i = 0; i < Slots; i++)
            {
                sb.Append(i == 0 ? "" : " > ").Append(Names[order[i]]);
            }

            return sb.ToString();
        }

        private static string Verdict(int hits, int total)
        {
            if (total == 0)
            {
                return "unknown (no common frames)";
            }

            return hits == total ? "yes" : hits == 0 ? "no" : "mixed";
        }
    }
}
