using System;
using System.Collections.Generic;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One place a profile can be kept: world storage (two copies) or PlayerPrefs (owner G; [persist §5.2, §5.4]).
    /// Implementations must not throw; the book catches it if they do.
    /// </summary>
    public interface IEtsProfileSlot
    {
        /// <summary>For the log: "world storage", "player prefs".</summary>
        string Name { get; }

        /// <summary>False when nothing is stored under the key. True with the stored text otherwise, which may still be damaged or empty.</summary>
        bool TryRead(string key, out string text);

        /// <summary>True when the text was stored.</summary>
        bool Write(string key, string text);

        /// <summary>Make earlier writes durable now (PlayerPrefs.Save). May do nothing.</summary>
        void Flush();
    }

    /// <summary>
    /// The account's profile held in memory and kept in every slot (owner G; [persist §5.4], PLAN.md §3.8, §7.G).
    /// Pure C#, no Unity: the MonoBehaviour <c>EtsProfileStore</c> owns one and the offline probe (1952, probe 1692
    /// extended) drives others against a fake of the client's BridgeWorldsLocalStorage.
    ///
    /// LOAD reads every slot, keeps the copies that decode and pass their check, and takes the one with the highest
    /// generation (then the latest save). Copies that exist but fail are backed up under a "_bad" key once, never
    /// silently overwritten unread: the client's ReadText returns "" for a file it cannot decrypt
    /// (BridgeWorldsLocalStorage.cs:23-41, [persist §2.1]), so a damaged copy looks exactly like an empty one. With
    /// no good copy it starts a new career at the game's €2,000. The winner is written back to any slot that was
    /// missing, stale or bad, so the two world-storage files converge on every visit.
    ///
    /// COMMIT raises the generation and writes every slot one after the other: WriteText is File.WriteAllText, which
    /// truncates before writing, so a crash mid-commit leaves at most one file torn and the other whole. It succeeds
    /// if any slot took the write; the record stays dirty and is retried on the next commit otherwise.
    ///
    /// CREDIT pays a delivery once per job id (the last <see cref="EtsProfileCodec.MaxRecentJobs"/> ids are kept)
    /// and appends its line to the delivery log (the last <see cref="EtsProfileCodec.MaxDeliveries"/>).
    /// </summary>
    public sealed class EtsProfileBook
    {
        public const long MaxMoney = 1_000_000_000_000L;
        public const long MaxCreditPerJob = 10_000_000L;
        public const long MaxXpPerJob = 1_000_000L;
        public const long MaxMetresPerCall = 20_000_000L;

        private readonly IEtsProfileSlot[] _slots;
        private readonly Func<long> _clock;
        private readonly string _key;

        public string UserId { get; }
        public EtsProfileRecord Record { get; private set; }

        /// <summary>A stored copy is from a newer build: nothing is written, so it is not clobbered.</summary>
        public bool ReadOnly { get; private set; }

        /// <summary>Changed in memory since the last successful commit.</summary>
        public bool Dirty { get; private set; }

        /// <summary>One line for the log saying what was found where and which copy won.</summary>
        public string LoadReport { get; private set; } = "";

        /// <summary>A copy failed its check or could not be read; worth telling the player once.</summary>
        public bool HadDamagedCopy { get; private set; }

        /// <summary>The last commit reached no slot.</summary>
        public bool LastCommitFailed { get; private set; }

        public EtsProfileBook(string userId, string nickName, IEtsProfileSlot[] slots, Func<long> clock)
        {
            UserId = EtsProfileCodec.Clean(userId);
            _slots = slots ?? new IEtsProfileSlot[0];
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _key = EtsProfileCodec.KeyFor(UserId);
            Record = NewRecord(nickName);
        }

        public string Key => _key;

        public void Load(string nickName)
        {
            var report = new StringBuilder();
            EtsProfileRecord best = null;
            string bestText = null;
            var texts = new string[_slots.Length];
            var good = new bool[_slots.Length];
            var generations = new long[_slots.Length];

            for (int i = 0; i < _slots.Length; i++)
            {
                IEtsProfileSlot slot = _slots[i];
                string text;
                bool present;

                try
                {
                    present = slot.TryRead(_key, out text);
                }
                catch (Exception e)
                {
                    report.Append(slot.Name).Append(": read threw ").Append(e.GetType().Name).Append("; ");
                    HadDamagedCopy = true;
                    continue;
                }

                if (!present)
                {
                    report.Append(slot.Name).Append(": none; ");
                    continue;
                }

                texts[i] = text;

                if (!EtsProfileCodec.TryDecode(text, UserId, out EtsProfileRecord r, out string reason, out int format))
                {
                    if (format > EtsProfileCodec.Format)
                    {
                        ReadOnly = true;
                    }

                    HadDamagedCopy = true;
                    report.Append(slot.Name).Append(": unusable, ").Append(reason).Append("; ");
                    continue;
                }

                good[i] = true;
                generations[i] = r.Generation;
                report.Append(slot.Name).Append(": gen ").Append(r.Generation).Append(" €").Append(r.Money).Append("; ");

                if (best == null || r.Generation > best.Generation || (r.Generation == best.Generation && r.SavedUnix > best.SavedUnix))
                {
                    best = r;
                    bestText = text;
                }
            }

            if (ReadOnly)
            {
                // A newer build wrote here. Show what we can, write nothing.
                if (best != null)
                {
                    Record = best;
                }

                LoadReport = report.Append("READ-ONLY (newer format present)").ToString();
                return;
            }

            // Keep every damaged copy once before anything can overwrite it.
            for (int i = 0; i < _slots.Length; i++)
            {
                if (texts[i] != null && !good[i] && texts[i].Length > 0)
                {
                    SafeWrite(_slots[i], _key + "_bad", texts[i]);
                }
            }

            if (best == null)
            {
                Record = NewRecord(nickName);
                report.Append("new career at €").Append(Record.Money);
                LoadReport = report.ToString();
                Dirty = true;
                Commit();
                return;
            }

            Record = best;

            if (!string.IsNullOrEmpty(nickName))
            {
                Record.NickName = EtsProfileCodec.Clean(nickName);
            }

            // Heal: every slot that did not hold the winning generation gets the winner's exact text.
            int healed = 0;

            for (int i = 0; i < _slots.Length; i++)
            {
                if (!good[i] || generations[i] != best.Generation)
                {
                    if (SafeWrite(_slots[i], _key, bestText))
                    {
                        healed++;
                    }
                }
            }

            if (healed > 0)
            {
                FlushAll();
            }

            report.Append("using gen ").Append(best.Generation);

            if (healed > 0)
            {
                report.Append(", healed ").Append(healed).Append(" slot(s)");
            }

            LoadReport = report.ToString();
        }

        /// <summary>
        /// Pays a finished delivery and logs it. Idempotent by job id: a job already among the recent ids is refused,
        /// so a second Deliver or a replayed event cannot pay twice. <paramref name="summary"/> is the job half of the
        /// delivery line, "cargo|from|to|km" (<see cref="EtsProfileCodec.DeliverySummary"/>); the money, XP and time
        /// are added here. Returns true when the credit is in the record (saved or, if every slot failed, pending the
        /// next commit).
        /// </summary>
        public bool Credit(string jobId, long money, long xp, string summary, out string refusal)
        {
            if (ReadOnly)
            {
                refusal = "profile is read-only (written by a newer build)";
                return false;
            }

            string id = EtsProfileCodec.CleanJobId(jobId);

            if (id.Length == 0)
            {
                refusal = "no job id";
                return false;
            }

            if (Record.RecentJobs.Contains(id))
            {
                refusal = "already paid";
                return false;
            }

            money = Clamp(money, 0, MaxCreditPerJob);
            xp = Clamp(xp, 0, MaxXpPerJob);

            Record.Money = Clamp(Record.Money + money, -MaxMoney, MaxMoney);
            Record.LifetimeEarnings = Clamp(Record.LifetimeEarnings + money, 0, MaxMoney);
            Record.Experience = Clamp(Record.Experience + xp, 0, long.MaxValue / 4);
            Record.JobsDelivered = Record.JobsDelivered < int.MaxValue ? Record.JobsDelivered + 1 : int.MaxValue;
            Record.RecentJobs.Add(id);

            while (Record.RecentJobs.Count > EtsProfileCodec.MaxRecentJobs)
            {
                Record.RecentJobs.RemoveAt(0);
            }

            Record.Deliveries.Add(EtsProfileCodec.DeliveryLine(summary, money, xp, _clock()));

            while (Record.Deliveries.Count > EtsProfileCodec.MaxDeliveries)
            {
                Record.Deliveries.RemoveAt(0);
            }

            Dirty = true;
            Commit();
            refusal = LastCommitFailed ? "credited, not yet saved" : "";
            return true;
        }

        /// <summary>Takes money if there is enough. For fuel, repairs, a truck, later.</summary>
        public bool Spend(long amount, out string refusal)
        {
            if (ReadOnly)
            {
                refusal = "profile is read-only";
                return false;
            }

            if (amount <= 0)
            {
                refusal = "amount must be positive";
                return false;
            }

            if (Record.Money < amount)
            {
                refusal = "not enough money";
                return false;
            }

            Record.Money -= amount;
            Dirty = true;
            Commit();
            refusal = LastCommitFailed ? "spent, not yet saved" : "";
            return true;
        }

        /// <summary>Free driving, no job. Kept in memory; the store commits it on its autosave or on leaving.</summary>
        public void AddDistance(long metres)
        {
            if (ReadOnly || metres <= 0)
            {
                return;
            }

            Record.DistanceMetres = Clamp(Record.DistanceMetres + Math.Min(metres, MaxMetresPerCall), 0, long.MaxValue / 4);
            Dirty = true;
        }

        /// <summary>Free-roam experience (economy_data.sii exp_free_roam), kept like distance.</summary>
        public void AddExperience(long xp)
        {
            if (ReadOnly || xp <= 0)
            {
                return;
            }

            Record.Experience = Clamp(Record.Experience + Math.Min(xp, MaxXpPerJob), 0, long.MaxValue / 4);
            Dirty = true;
        }

        public void SetHqCity(string city)
        {
            string c = EtsProfileCodec.Clean(city);

            if (ReadOnly || c == Record.HqCity)
            {
                return;
            }

            Record.HqCity = c;
            Dirty = true;
            Commit();
        }

        public void SetTruck(string unit, string paint)
        {
            string u = EtsProfileCodec.Clean(unit);
            string p = EtsProfileCodec.Clean(paint);

            if (ReadOnly || (u == Record.TruckUnit && p == Record.TruckPaint))
            {
                return;
            }

            Record.TruckUnit = u;
            Record.TruckPaint = p;
            Dirty = true;
            Commit();
        }

        /// <summary>Writes the record to every slot with the next generation. True if at least one slot took it.</summary>
        public bool Commit()
        {
            if (ReadOnly)
            {
                return false;
            }

            Record.Generation++;
            Record.SavedUnix = _clock();
            string text = EtsProfileCodec.Encode(Record);
            int ok = 0;

            foreach (IEtsProfileSlot slot in _slots)
            {
                if (SafeWrite(slot, _key, text))
                {
                    ok++;
                }
            }

            FlushAll();
            LastCommitFailed = ok == 0;

            if (ok > 0)
            {
                Dirty = false;
            }

            return ok > 0;
        }

        private EtsProfileRecord NewRecord(string nickName)
        {
            long now = _clock();
            return new EtsProfileRecord
            {
                UserId = UserId,
                NickName = EtsProfileCodec.Clean(nickName),
                CreatedUnix = now,
                SavedUnix = now,
                Money = EtsProfileCodec.StartingMoney
            };
        }

        private void FlushAll()
        {
            foreach (IEtsProfileSlot slot in _slots)
            {
                try
                {
                    slot.Flush();
                }
                catch (Exception)
                {
                    // A slot that cannot flush keeps what it has; the other slots are unaffected.
                }
            }
        }

        private static bool SafeWrite(IEtsProfileSlot slot, string key, string text)
        {
            try
            {
                return slot.Write(key, text);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static long Clamp(long v, long lo, long hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
