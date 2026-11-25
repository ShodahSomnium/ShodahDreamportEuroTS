using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The engine's "token": a short identifier packed into 64 bits, base 38, least significant
    /// character first. Model looks, variants, parts and locators are all named this way, and so
    /// are the map's road looks and prefab references.
    ///
    /// The alphabet is the digits, the lower-case letters and underscore, with zero as the end
    /// of the string, which allows twelve characters. Confirmed against the archives rather than
    /// taken on trust: the value 0x1573C3E700 found in every model's look table decodes to
    /// "default" exactly as expected.
    /// </summary>
    public static class EtsToken
    {
        private const string Alphabet = "\00123456789abcdefghijklmnopqrstuvwxyz_";

        public static string Decode(ulong token)
        {
            var text = new StringBuilder(12);

            while (token != 0)
            {
                text.Append(Alphabet[(int)(token % 38)]);
                token /= 38;
            }

            return text.ToString();
        }

        /// <summary>
        /// Packs a string. Characters outside the alphabet are dropped and anything past twelve
        /// is ignored, which is what the engine's own tooling does with them.
        /// </summary>
        public static ulong Encode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            ulong token = 0;
            ulong scale = 1;
            int used = 0;

            foreach (char raw in text)
            {
                char c = char.ToLowerInvariant(raw);
                int index = Alphabet.IndexOf(c);

                if (index <= 0)
                {
                    continue;
                }

                token += (ulong)index * scale;
                scale *= 38;

                if (++used == 12)
                {
                    break;
                }
            }

            return token;
        }
    }
}
