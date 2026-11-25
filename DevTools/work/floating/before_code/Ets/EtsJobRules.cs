using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Why a job cannot be taken now (PLAN §3.5), in the order EtsJobs.CanTake asks. EtsJobs turns it into the line
    /// the tablet's Quick Job page shows (EtsJobRules.TakeReason); none of the words are in the game's locale.
    /// </summary>
    public enum EtsTakeBlock
    {
        None = 0,
        JobRunning,     // "Finish or abandon your current job first"
        JobStarting,    // "A job is starting"
        ReadingMap,     // "Reading the map"
        Passenger,      // "Get out of {name}'s truck first"
        NoOwnTruck,     // "Choose your truck first (Company)"
        TruckNotOnRoad  // the old flow's scene truck: "The truck is not on the road yet"
    }

    /// <summary>
    /// What the local player's own slot says about their job (PLAN §5.3; [net §2.4] ints 19-20 and 28-31). Jobs stay
    /// per player (PLAN §1 row 17, [net §2.6] "Only the summary (ints 28-31) and the trailer are visible to others"):
    /// this is all of a job that crosses the wire. EtsJobs.Published fills it; EtsNet packs it
    /// (EtsPlayerWire.PackJob, PackTrailer).
    /// </summary>
    public struct EtsJobSummary
    {
        /// <summary>A job is running: its cargo and destination city are shown to others (the Convoys line, the nameplate's "→ city").</summary>
        public bool HasJob;
        public ulong CargoToken;
        public ulong DestinationCityToken;

        /// <summary>The own truck pulls the jobs' trailer (EtsTruckRig.Trailer): watchers build a puppet trailer from Trailer.</summary>
        public bool HasTrailer;
        public EtsTrailerIdentity Trailer;
    }

    /// <summary>
    /// The per-player job rules of the multiplayer phase (PLAN §3.5, §8 Phase 3 task G3), pure so that they are tested
    /// offline (probe 2280) and asked by EtsJobs every frame without allocating:
    /// - who may take a job (TakeBlock): never a passenger in someone else's truck;
    /// - when a delivery counts (DeliveryCounts): only the own truck's own job, never while riding in another's truck,
    ///   so a passenger is never paid for the driver's delivery (the driver's client pays the driver, [persist §5.7]);
    /// - what the own slot shows (the job summary and the trailer identity, JobTrailer, CargoIndex, CompanyHash).
    /// </summary>
    public static class EtsJobRules
    {
        /// <summary>
        /// The trailer family and variant of every job's trailer: EtsJobs builds it with EtsTrailer.Create(parent, null,
        /// null, ...), which is EtsTrailer.DefaultChassisDefinition, the scs_box curtain sider (the only body the job
        /// offers use: EtsJobs.CurtainsideCargo keeps cargo whose body_types has "curtainside"). Family 0 / variant 0
        /// is that trailer; [net §2.11] has any index that cannot be resolved fall back to the same curtain sider.
        /// </summary>
        public const int JobTrailerFamily = 0;
        public const int JobTrailerVariant = 0;

        /// <summary>A cargo not in the list: the 16 bits of EtsPlayerWire int 19 all set, out of range for any reader.</summary>
        public const int UnknownCargo = 0xFFFF;

        /// <summary>The speed below which a truck standing in the bay counts as stopped (EtsJobs' arrival test).</summary>
        public const float StoppedKmh = 1.5f;

        /// <summary>
        /// Whether a job may be taken, and if not why (PLAN §3.5 "Taking a job requires: an own truck, spawned and
        /// placed; the local player not seated as a passenger; no current job"). newFlow picks the rule for the truck:
        /// the own truck (ownTruckPlaced) with the new flow, the scene's shared truck (sceneTruckPlaced) with the old.
        /// </summary>
        public static EtsTakeBlock TakeBlock(bool hasJob, bool starting, bool mapIndexed, bool newFlow, bool seatedInOthersTruck, bool ownTruckPlaced, bool sceneTruckPlaced)
        {
            if (hasJob)
            {
                return EtsTakeBlock.JobRunning;
            }

            if (starting)
            {
                return EtsTakeBlock.JobStarting;
            }

            if (!mapIndexed)
            {
                return EtsTakeBlock.ReadingMap;
            }

            if (newFlow)
            {
                if (seatedInOthersTruck)
                {
                    return EtsTakeBlock.Passenger;
                }

                return ownTruckPlaced ? EtsTakeBlock.None : EtsTakeBlock.NoOwnTruck;
            }

            return sceneTruckPlaced ? EtsTakeBlock.None : EtsTakeBlock.TruckNotOnRoad;
        }

        /// <summary>
        /// The Quick Job page's line for a block (PLAN §3.5 wording; not in the game's locale). Passenger names the
        /// truck's owner, "that truck" when there is no name. Allocates for Passenger only: EtsJobs caches that one.
        /// </summary>
        public static string TakeReason(EtsTakeBlock block, string ownerName)
        {
            switch (block)
            {
                case EtsTakeBlock.JobRunning: return "Finish or abandon your current job first";
                case EtsTakeBlock.JobStarting: return "A job is starting";
                case EtsTakeBlock.ReadingMap: return "Reading the map";
                case EtsTakeBlock.Passenger: return string.IsNullOrEmpty(ownerName) ? "Get out of that truck first" : "Get out of " + ownerName + "'s truck first";
                case EtsTakeBlock.NoOwnTruck: return "Choose your truck first (Company)";
                case EtsTakeBlock.TruckNotOnRoad: return "The truck is not on the road yet";
                default: return "";
            }
        }

        /// <summary>
        /// Seated in a truck that is not the own one: a passenger (PLAN §5.5 "EtsLocalTruck.Occupied = the puppet").
        /// Compared by reference by the caller (a despawned rig is a Unity fake null).
        /// </summary>
        public static bool SeatedInOthersTruck(bool seated, bool seatedInOwn)
        {
            return seated && !seatedInOwn;
        }

        /// <summary>
        /// Whether the trailer standing in the bay counts towards a delivery this frame (EtsJobs keeps it up for 1.5 s,
        /// then Deliver credits the career once, by job id). With the new flow it must be the own truck, not a puppet
        /// (jobs bind only to EtsLocalTruck.Own), and the player must not be riding in another player's truck: a
        /// passenger's client never credits a delivery, whoever brings a trailer where. Their own job is delivered
        /// once they are out, if their own trailer is still in its bay. A NaN distance never counts.
        /// </summary>
        public static bool DeliveryCounts(bool newFlow, bool truckIsOwn, bool truckIsPuppet, bool seatedInOthersTruck, float trailerToBay, float arrivalRadius, float truckKmh)
        {
            if (newFlow && (!truckIsOwn || truckIsPuppet || seatedInOthersTruck))
            {
                return false;
            }

            return trailerToBay < arrivalRadius && truckKmh < StoppedKmh;
        }

        /// <summary>The cargo's token for the slot: "cargo.carbn_pwdr_c" is the token "carbn_pwdr_c" (EtsToken); 0 when there is none.</summary>
        public static ulong CargoToken(string cargoUnit)
        {
            string name = CargoName(cargoUnit);
            return string.IsNullOrEmpty(name) ? 0UL : EtsToken.Encode(name);
        }

        /// <summary>
        /// The sorted /def cargo list of [net §2.4] int 19 ("cargo index in the sorted /def cargo list"): the unit names
        /// of /def/cargo (EtsMapIndex.Cargo) without their "cargo." prefix, distinct, in ordinal order - the same on
        /// every client, since every client reads the same install (the catalogue hash guards the trucks the same way).
        /// Built once per visit, at the first job start.
        /// </summary>
        public static string[] SortedCargo(IEnumerable<string> unitNames)
        {
            var names = new List<string>();

            if (unitNames != null)
            {
                foreach (string unit in unitNames)
                {
                    string name = CargoName(unit);

                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }

            names.Sort(StringComparer.Ordinal);

            for (int i = names.Count - 1; i > 0; i--)
            {
                if (string.Equals(names[i], names[i - 1], StringComparison.Ordinal))
                {
                    names.RemoveAt(i);
                }
            }

            return names.ToArray();
        }

        /// <summary>The cargo's index in SortedCargo's list ("cargo.x" or "x"), or -1.</summary>
        public static int CargoIndex(string[] sorted, string cargoUnit)
        {
            string name = CargoName(cargoUnit);

            if (sorted == null || string.IsNullOrEmpty(name))
            {
                return -1;
            }

            int at = Array.BinarySearch(sorted, name, StringComparer.Ordinal);
            return at >= 0 ? at : -1;
        }

        /// <summary>The reader's side of CargoIndex: the cargo name ("carbn_pwdr_c") at an index, or null out of range (UnknownCargo).</summary>
        public static string CargoAt(string[] sorted, int index)
        {
            return sorted != null && index >= 0 && index < sorted.Length ? sorted[index] : null;
        }

        /// <summary>
        /// [net §2.4] int 20, "trailer company token hash (32-bit, for the logo)": the company token (EtsMapCompany
        /// .CompanyToken) times the 64-bit golden-ratio constant, high 32 bits. A reader hashes the companies it knows
        /// and takes the one that matches. 0 means no company; a company that hashes to 0 is given 1. Probe 2281 finds
        /// no two of the demo's companies with the same hash.
        /// </summary>
        public static int CompanyHash(ulong companyToken)
        {
            if (companyToken == 0UL)
            {
                return 0;
            }

            uint hash = (uint)((companyToken * 0x9E3779B97F4A7C15UL) >> 32);
            return hash == 0u ? 1 : unchecked((int)hash);
        }

        /// <summary>
        /// The identity of a job's trailer ([net §2.4] ints 19-20): the curtain sider (JobTrailerFamily/Variant), the
        /// cargo's index (UnknownCargo when -1) and the shipper's company - the job's source company, whose trailer it
        /// is (the game's company trailers carry the sender's livery).
        /// </summary>
        public static EtsTrailerIdentity JobTrailer(int cargoIndex, ulong sourceCompanyToken)
        {
            return new EtsTrailerIdentity
            {
                Family = JobTrailerFamily,
                Variant = JobTrailerVariant,
                Cargo = cargoIndex >= 0 && cargoIndex < UnknownCargo ? cargoIndex : UnknownCargo,
                CompanyHash = CompanyHash(sourceCompanyToken)
            };
        }

        /// <summary>The summary the own slot shows; with no job the cargo and city are 0, whatever was passed.</summary>
        public static EtsJobSummary Summary(bool hasJob, ulong cargoToken, ulong destinationCityToken, bool hasTrailer, in EtsTrailerIdentity trailer)
        {
            return new EtsJobSummary
            {
                HasJob = hasJob,
                CargoToken = hasJob ? cargoToken : 0UL,
                DestinationCityToken = hasJob ? destinationCityToken : 0UL,
                HasTrailer = hasTrailer,
                Trailer = hasTrailer ? trailer : default
            };
        }

        private static string CargoName(string cargoUnit)
        {
            if (string.IsNullOrEmpty(cargoUnit))
            {
                return null;
            }

            return cargoUnit.StartsWith("cargo.", StringComparison.Ordinal) ? cargoUnit.Substring(6) : cargoUnit;
        }
    }
}
