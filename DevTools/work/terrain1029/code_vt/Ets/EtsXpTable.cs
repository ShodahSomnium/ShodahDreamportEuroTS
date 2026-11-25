using System.Collections.Generic;
using System.Globalization;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Levels from experience, as /def/economy_data_xp.sii states them (owner G; [persist §2.8], PLAN.md §7.G): unit
    /// "xp_level_data : economy.data.xp_levels", level_xp[0..29] = experience from one level to the next (200, 500,
    /// 700 ... 6800), level_names[] = "firstLevel|@@lvl_nameN@@" (0, 5, 10, 15, 20, 25, 30, 40, 50, 100). Read with
    /// harness Program1690 on the 1.61 demo (S/design/persistence/probe/out1690.txt). The fallback below is that
    /// measurement, used only when the file cannot be read.
    ///
    /// NOT DETERMINED: what the game does past the table's last entry (here the last step repeats), and whether it
    /// shows a new profile as level 0 or 1 (here: 0 = no threshold passed; the UI decides what to print).
    ///
    /// Static game data, not player data: it may outlive a world visit (the client keeps the assembly loaded,
    /// [persist §2.3]) because it only ever holds what the installed game says.
    /// </summary>
    public static class EtsXpTable
    {
        private static readonly long[] Fallback =
        {
            200, 500, 700, 900, 1000, 1100, 1300, 1600, 1700, 2100, 2300, 2600, 2700, 2900, 3000,
            3100, 3400, 3700, 4000, 4300, 4600, 4700, 4900, 5200, 5700, 5900, 6000, 6200, 6600, 6800
        };

        private static long[] _steps = Fallback;
        private static readonly List<KeyValuePair<int, string>> _names = new List<KeyValuePair<int, string>>();

        /// <summary>The table came from the game's file (not the fallback).</summary>
        public static bool FromGame { get; private set; }

        /// <summary>Reads the table from the game. Safe to call more than once; keeps the last good table on any failure.</summary>
        public static void Load(EtsFileSystem files)
        {
            EtsSiiFile sii = files?.ReadSii("/def/economy_data_xp.sii");

            if (sii == null)
            {
                return;
            }

            foreach (EtsSiiUnit unit in sii.OfType("xp_level_data"))
            {
                List<string> xp = unit.GetArray("level_xp");
                var steps = new List<long>();

                foreach (string s in xp)
                {
                    if (long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v > 0)
                    {
                        steps.Add(v);
                    }
                }

                if (steps.Count == 0)
                {
                    continue;
                }

                _steps = steps.ToArray();
                _names.Clear();

                foreach (string entry in unit.GetArray("level_names"))
                {
                    int bar = entry.IndexOf('|');

                    if (bar > 0 && int.TryParse(entry.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out int from))
                    {
                        _names.Add(new KeyValuePair<int, string>(from, entry.Substring(bar + 1)));
                    }
                }

                FromGame = true;
                return;
            }
        }

        public static int StepCount => _steps.Length;

        /// <summary>Levels passed with this much experience; the experience into the current level and the size of its step.</summary>
        public static int LevelFor(long experience, out long into, out long step)
        {
            int level = 0;
            long left = experience < 0 ? 0 : experience;

            while (true)
            {
                step = _steps[level < _steps.Length ? level : _steps.Length - 1];

                if (left < step || level >= 10000)
                {
                    into = left;
                    return level;
                }

                left -= step;
                level++;
            }
        }

        public static int LevelFor(long experience)
        {
            return LevelFor(experience, out _, out _);
        }

        /// <summary>How far into the current level, 0..1 (the tablet's XP bar, PLAN §7.E LevelProgress).</summary>
        public static float Progress(long experience)
        {
            LevelFor(experience, out long into, out long step);
            return step > 0 ? (float)into / step : 0f;
        }

        /// <summary>The "@@lvl_nameN@@" key for a level (to be resolved through EtsLocale), or "" without the file.</summary>
        public static string NameKey(int level)
        {
            string key = "";

            foreach (KeyValuePair<int, string> n in _names)
            {
                if (level >= n.Key)
                {
                    key = n.Value;
                }
            }

            return key;
        }
    }
}
