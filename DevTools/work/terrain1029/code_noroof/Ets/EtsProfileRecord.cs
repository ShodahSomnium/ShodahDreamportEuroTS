using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One Somnium account's career in this world: the bank, the experience, the totals, the choices the tablet
    /// remembers (HQ city, last truck) and the last ten deliveries (owner G; [persist §5.1], PLAN.md C15, §3.8, §7.G).
    /// Plain data: <see cref="EtsProfileCodec"/> turns it into text and back, <see cref="EtsProfileBook"/> decides
    /// which stored copy is true.
    ///
    /// The starting balance is the game's own: /def/initial_save/normal/game.sii gives bank.money_account 2000
    /// (harness Program1690, S/design/persistence/probe/out1690.txt). Experience is kept raw; the level is derived
    /// from /def/economy_data_xp.sii by <see cref="EtsXpTable"/>, so a later change to how XP is earned or levelled
    /// never needs a data migration.
    ///
    /// Deliveries (PLAN §3.8) is in format 1 from the start: it was added before the format was first shipped,
    /// so no stored record anywhere lacks it for a reason other than "no delivery yet".
    /// </summary>
    public sealed class EtsProfileRecord
    {
        public int Format = EtsProfileCodec.Format;

        /// <summary>The Somnium account id (Bridge LocalPlayer.Properties.Id, a GUID string). Never a Fusion PlayerId (PLAN C17).</summary>
        public string UserId = "";

        /// <summary>The Somnium nickname when last saved. Display only, never a key.</summary>
        public string NickName = "";

        /// <summary>Raised by one on every commit; the copy with the highest generation wins on load.</summary>
        public long Generation;

        public long CreatedUnix;
        public long SavedUnix;

        /// <summary>bank.money_account, whole euros.</summary>
        public long Money = EtsProfileCodec.StartingMoney;

        /// <summary>economy.experience_points.</summary>
        public long Experience;

        /// <summary>
        /// Everything this account has driven in this world, in the game's metres (world metres times the map scale, 19 or
        /// 3 in a city area, as the jobs' km are counted; EtsJobs.AccountDriving since Phase 2 - nothing wrote it before,
        /// so every stored record held 0 until then).
        /// </summary>
        public long DistanceMetres;

        public int JobsDelivered;
        public long LifetimeEarnings;

        /// <summary>City token the player last chose as master on the HQ map (e.g. "szczecin"). Preselects the map (PLAN §5.5).</summary>
        public string HqCity = "";

        /// <summary>The truck last chosen on the tablet: a preview stem of /def/initial_save/preview (PLAN C12, §7.G SetTruck).</summary>
        public string TruckUnit = "";

        /// <summary>Its paint, "RRGGBB".</summary>
        public string TruckPaint = "";

        /// <summary>
        /// The truck's condition (2026-10-04, EtsDamageState.Encode: "1|truckId|damage x5|wear x5"), "" when like new. Stored as
        /// one more key=value line ("damage"), written only when not empty, so an undamaged career's record is byte for byte
        /// what it was; the format stays 1: older builds keep every key in a dictionary and read only the ones they know, the
        /// MAC covers the stored bytes whatever they hold, and an older build re-saving simply drops the line.
        /// </summary>
        public string Damage = "";

        /// <summary>Ids of the last <see cref="EtsProfileCodec.MaxRecentJobs"/> deliveries paid, oldest first. A job is never paid twice.</summary>
        public readonly List<string> RecentJobs = new List<string>();

        /// <summary>
        /// The last <see cref="EtsProfileCodec.MaxDeliveries"/> deliveries, oldest first, each
        /// "cargo|from|to|km|money|xp|unix" (PLAN §3.8; <see cref="EtsProfileCodec.DeliveryLine"/> writes them,
        /// <see cref="EtsProfileCodec.TryParseDelivery"/> reads them). km is the game's km (EtsEconomy.RouteGameKm).
        /// </summary>
        public readonly List<string> Deliveries = new List<string>();

        public EtsProfileRecord Clone()
        {
            var fresh = new EtsProfileRecord
            {
                Format = Format,
                UserId = UserId,
                NickName = NickName,
                Generation = Generation,
                CreatedUnix = CreatedUnix,
                SavedUnix = SavedUnix,
                Money = Money,
                Experience = Experience,
                DistanceMetres = DistanceMetres,
                JobsDelivered = JobsDelivered,
                LifetimeEarnings = LifetimeEarnings,
                HqCity = HqCity,
                TruckUnit = TruckUnit,
                TruckPaint = TruckPaint,
                Damage = Damage
            };
            fresh.RecentJobs.AddRange(RecentJobs);
            fresh.Deliveries.AddRange(Deliveries);
            return fresh;
        }
    }

    /// <summary>One line of the delivery log, read back (<see cref="EtsProfileCodec.TryParseDelivery"/>).</summary>
    public struct EtsDelivery
    {
        public string Cargo;
        public string From;
        public string To;
        public int Km;
        public long Money;
        public long Xp;
        public long Unix;
    }

    /// <summary>
    /// The stored form: UTF-8 lines of key=value in a fixed order, a header line naming the format, and a last
    /// line carrying an HMAC-SHA256 of everything above it, keyed by a constant in this assembly and bound to the
    /// account id. Line text rather than JsonUtility so it can be tested outside Unity (probe 1952) and the MAC
    /// input is exactly the stored bytes [persist §5.3]. 778 bytes with 16 job ids (probe 1692); each delivery
    /// line adds up to about 180 more.
    ///
    /// The MAC is tamper-EVIDENCE, not security: the key ships inside the world's assembly. It stops a stored copy
    /// being edited by hand or pasted from one account to another.
    /// </summary>
    public static class EtsProfileCodec
    {
        public const int Format = 1;
        public const long StartingMoney = 2000;
        public const int MaxRecentJobs = 16;

        /// <summary>PLAN §3.8: the delivery log keeps the last ten.</summary>
        public const int MaxDeliveries = 10;

        /// <summary>Each text field of a delivery line (cargo, from, to) is cut to this many characters.</summary>
        public const int MaxDeliveryField = 48;

        public const string Header = "ets-profile";

        // Not a secret (it ships in the world's assembly); change it and every stored profile fails its check.
        private static readonly byte[] MacKey = Encoding.UTF8.GetBytes("SomniumEuroTruck/profile/v1/7d2c4e5a9b1f4c6e");

        public static string Encode(EtsProfileRecord r)
        {
            var sb = new StringBuilder(768 + r.Deliveries.Count * 160);
            Line(sb, Header, Format.ToString(CultureInfo.InvariantCulture));
            Line(sb, "user", Clean(r.UserId));
            Line(sb, "nick", Clean(r.NickName));
            Line(sb, "gen", r.Generation);
            Line(sb, "created", r.CreatedUnix);
            Line(sb, "saved", r.SavedUnix);
            Line(sb, "money", r.Money);
            Line(sb, "xp", r.Experience);
            Line(sb, "distance", r.DistanceMetres);
            Line(sb, "jobs", r.JobsDelivered);
            Line(sb, "earned", r.LifetimeEarnings);
            Line(sb, "hq", Clean(r.HqCity));
            Line(sb, "truck", Clean(r.TruckUnit));
            Line(sb, "paint", Clean(r.TruckPaint));
            Line(sb, "recent", string.Join(",", r.RecentJobs));

            // The truck's condition (2026-10-04): only when it has any, so an undamaged record is unchanged.
            string damage = Clean(r.Damage, 200, false);

            if (damage.Length > 0)
            {
                Line(sb, "damage", damage);
            }

            // The newest MaxDeliveries only, oldest first, one key each: the decoder keeps the last value of a key.
            int first = Math.Max(0, r.Deliveries.Count - MaxDeliveries);

            for (int i = first; i < r.Deliveries.Count; i++)
            {
                Line(sb, "delivery" + (i - first).ToString(CultureInfo.InvariantCulture), CleanDeliveryLine(r.Deliveries[i]));
            }

            string body = sb.ToString();
            return body + "mac=" + Mac(body) + "\n";
        }

        /// <summary>
        /// Reads a stored copy. False with a reason for anything that is not a whole, untampered record of
        /// <paramref name="expectUserId"/>: empty, truncated, wrong header, newer format, bad MAC, another account's.
        /// </summary>
        public static bool TryDecode(string text, string expectUserId, out EtsProfileRecord record, out string reason, out int storedFormat)
        {
            record = null;
            storedFormat = 0;

            if (string.IsNullOrEmpty(text))
            {
                reason = "empty";
                return false;
            }

            int macAt = text.LastIndexOf("\nmac=", StringComparison.Ordinal);

            if (macAt < 0)
            {
                reason = "no mac line (truncated?)";
                return false;
            }

            string body = text.Substring(0, macAt + 1);
            string mac = text.Substring(macAt + 5).Trim();

            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string raw in body.Split('\n'))
            {
                if (raw.Length == 0)
                {
                    continue;
                }

                int eq = raw.IndexOf('=');

                if (eq <= 0)
                {
                    reason = "malformed line";
                    return false;
                }

                values[raw.Substring(0, eq)] = raw.Substring(eq + 1);
            }

            if (!values.TryGetValue(Header, out string f) || !int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out storedFormat))
            {
                reason = "no header";
                return false;
            }

            if (storedFormat > Format)
            {
                reason = "written by a newer build (format " + storedFormat + ")";
                return false;
            }

            if (!FixedTimeEquals(mac, Mac(body)))
            {
                reason = "check failed (edited or damaged)";
                return false;
            }

            var r = new EtsProfileRecord();
            r.Format = storedFormat;
            r.UserId = Get(values, "user");
            r.NickName = Get(values, "nick");
            r.Generation = GetLong(values, "gen", 0);
            r.CreatedUnix = GetLong(values, "created", 0);
            r.SavedUnix = GetLong(values, "saved", 0);
            r.Money = GetLong(values, "money", StartingMoney);
            r.Experience = GetLong(values, "xp", 0);
            r.DistanceMetres = GetLong(values, "distance", 0);
            r.JobsDelivered = (int)Math.Max(0, Math.Min(int.MaxValue, GetLong(values, "jobs", 0)));
            r.LifetimeEarnings = GetLong(values, "earned", 0);
            r.HqCity = Get(values, "hq");
            r.TruckUnit = Get(values, "truck");
            r.TruckPaint = Get(values, "paint");
            r.Damage = values.TryGetValue("damage", out string damageLine) ? Clean(damageLine, 200, false) : "";

            foreach (string id in Get(values, "recent").Split(','))
            {
                string clean = CleanJobId(id);

                if (clean.Length > 0)
                {
                    r.RecentJobs.Add(clean);
                }
            }

            for (int i = 0; i < MaxDeliveries; i++)
            {
                if (values.TryGetValue("delivery" + i.ToString(CultureInfo.InvariantCulture), out string line) && line.Length > 0)
                {
                    r.Deliveries.Add(CleanDeliveryLine(line));
                }
            }

            if (!string.Equals(r.UserId, Clean(expectUserId), StringComparison.OrdinalIgnoreCase))
            {
                reason = "belongs to another account";
                return false;
            }

            record = r;
            reason = "ok";
            return true;
        }

        /// <summary>Storage key for an account: letters, digits, '-' and '_' only, so it is a safe file name and registry value name.</summary>
        public static string KeyFor(string userId)
        {
            var sb = new StringBuilder("ets_profile_");

            foreach (char c in userId ?? "")
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                {
                    sb.Append(c);
                }

                if (sb.Length >= 76)
                {
                    break;
                }
            }

            return sb.ToString();
        }

        /// <summary>Job ids are hex (Guid "N" form), at most 32 characters; anything else is dropped.</summary>
        public static string CleanJobId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "";
            }

            var sb = new StringBuilder(32);

            foreach (char c in id.Trim().ToLowerInvariant())
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                {
                    sb.Append(c);
                }

                if (sb.Length == 32)
                {
                    break;
                }
            }

            return sb.ToString();
        }

        /// <summary>No line breaks, no '\0', at most 128 characters.</summary>
        public static string Clean(string s)
        {
            return Clean(s, 128, false);
        }

        // ------------------------------------------------------------------ the delivery log (PLAN §3.8)

        /// <summary>
        /// The job half of a delivery line, "cargo|from|to|km", for <see cref="EtsProfileBook.Credit"/>: each text
        /// field loses '|', line breaks and '\0' and is cut to <see cref="MaxDeliveryField"/> characters, so a name
        /// can never shift the fields after it. km is rounded to whole game km.
        /// </summary>
        public static string DeliverySummary(string cargo, string from, string to, float gameKm)
        {
            int km = float.IsNaN(gameKm) || gameKm < 0f ? 0 : gameKm > 1000000f ? 1000000 : (int)Math.Round(gameKm, MidpointRounding.AwayFromZero);
            return Field(cargo) + "|" + Field(from) + "|" + Field(to) + "|" + km.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A whole delivery line, "cargo|from|to|km|money|xp|unix": the first four '|'-separated parts of
        /// <paramref name="summary"/> (missing ones empty, extra ones dropped, each cleaned as in DeliverySummary)
        /// and the three numbers.
        /// </summary>
        public static string DeliveryLine(string summary, long money, long xp, long unix)
        {
            string[] parts = (summary ?? "").Split('|');
            var sb = new StringBuilder(200);

            for (int i = 0; i < 4; i++)
            {
                string p = i < parts.Length ? parts[i] : "";

                if (i == 3)
                {
                    long km = long.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long k) && k >= 0 ? Math.Min(k, 1000000) : 0;
                    sb.Append(km.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(Field(p)).Append('|');
                }
            }

            sb.Append('|').Append(money.ToString(CultureInfo.InvariantCulture));
            sb.Append('|').Append(xp.ToString(CultureInfo.InvariantCulture));
            sb.Append('|').Append(unix.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Reads a delivery line back. False when it does not have the seven fields or its numbers do not parse.</summary>
        public static bool TryParseDelivery(string line, out EtsDelivery delivery)
        {
            delivery = default;
            string[] parts = (line ?? "").Split('|');

            if (parts.Length != 7 ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int km) ||
                !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long money) ||
                !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long xp) ||
                !long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix))
            {
                return false;
            }

            delivery.Cargo = parts[0];
            delivery.From = parts[1];
            delivery.To = parts[2];
            delivery.Km = km;
            delivery.Money = money;
            delivery.Xp = xp;
            delivery.Unix = unix;
            return true;
        }

        // A stored line is written by DeliveryLine; on decode it is only kept one line long and bounded.
        private static string CleanDeliveryLine(string line)
        {
            return Clean(line, 4 * MaxDeliveryField + 72, false);
        }

        private static string Field(string s)
        {
            return Clean(s, MaxDeliveryField, true).Trim();
        }

        private static string Clean(string s, int max, bool dropBars)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            var sb = new StringBuilder(Math.Min(s.Length, max));

            foreach (char c in s)
            {
                if (c != '\n' && c != '\r' && c != '\0' && (!dropBars || c != '|'))
                {
                    sb.Append(c);
                }

                if (sb.Length == max)
                {
                    break;
                }
            }

            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string key, long value)
        {
            Line(sb, key, value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Line(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').Append(value).Append('\n');
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string v) ? v : "";
        }

        private static long GetLong(Dictionary<string, string> values, string key, long fallback)
        {
            return values.TryGetValue(key, out string v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : fallback;
        }

        private static string Mac(string body)
        {
            using (var h = new HMACSHA256(MacKey))
            {
                byte[] hash = h.ComputeHash(Encoding.UTF8.GetBytes(body));
                var sb = new StringBuilder(64);

                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;

            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }
    }
}
