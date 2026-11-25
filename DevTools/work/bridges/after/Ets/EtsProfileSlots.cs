using System;
using System.Collections.Generic;
using SomniumSpace.Bridge.LocalStorage;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The Somnium world storage the Bridge offers, SomniumBridge.LocalStorage: the user's own choice for the money
    /// (PLAN.md §12, S/design/persistence/USER_NOTE.md) and the only storage API the Bridge has [persist §2.1] (owner
    /// G). In the client it is BridgeWorldsLocalStorage: one folder per world id under Application.temporaryCachePath
    /// (%LOCALAPPDATA%\Temp\Somnium Space Ltd\Somnium Space VR\WorldsLocalStorage\12510 for this world), a file
    /// "&lt;key&gt;.txt" per key, AES-encrypted with a key hashed from the file name, 50 MB per world. ReadText returns
    /// "" for a missing or undecryptable file (BridgeWorldsLocalStorage.cs:23-41), so existence is asked first.
    /// Null in the SDK editor (no Bridge instance, bridge_sdk.il 185-211).
    ///
    /// Two of these are used, the second with suffix "_2": WriteText is File.WriteAllText (lines 61, 67), which
    /// truncates before writing, so a crash mid-write leaves one file empty. The book writes the two one after the
    /// other, so at least one whole copy always survives (probe 1692 test 5b).
    ///
    /// A file that exists but reads as "" (torn, undecryptable, or not openable at that moment: ReadText's catch-all,
    /// lines 37-40) is copied first, byte for byte, to &lt;key&gt;_bad&lt;suffix&gt;.txt (usePlainText: the ciphertext as it
    /// is), because the book cannot keep what it was never given: it starts a new career over two unreadable copies,
    /// and without this copy that new career overwrote the only record (checker probe 1961 R5,
    /// S/phase1/GX/probe/out1961.txt).
    /// To recover, rename the _bad file back to the key's name; the client decrypts it with the key's own entropy.
    /// </summary>
    public sealed class EtsBridgeStorageSlot : IEtsProfileSlot
    {
        private readonly ISomniumWorldsLocalStorage _storage;
        private readonly string _suffix;

        public EtsBridgeStorageSlot(ISomniumWorldsLocalStorage storage, string suffix = "")
        {
            _storage = storage;
            _suffix = suffix ?? "";
        }

        public string Name => _suffix.Length == 0 ? "world storage" : "world storage" + _suffix;

        public bool TryRead(string key, out string text)
        {
            text = null;

            if (_storage == null || !_storage.TextExists(key + _suffix))
            {
                return false;
            }

            text = _storage.ReadText(key + _suffix) ?? "";

            if (text.Length == 0)
            {
                KeepUnreadable(key);
            }

            return true;
        }

        // The raw file under the book's "_bad" name for this slot (EtsProfileBook.Load writes key + "_bad" through Write,
        // which adds the suffix). Only when the raw read gives something: a file that cannot even be opened is left alone.
        private void KeepUnreadable(string key)
        {
            try
            {
                string raw = _storage.ReadText(key + _suffix, true);

                if (!string.IsNullOrEmpty(raw))
                {
                    _storage.WriteText(key + "_bad" + _suffix, raw, true);
                }
            }
            catch (Exception)
            {
                // A slot must not throw (IEtsProfileSlot); the book goes on as before.
            }
        }

        public bool Write(string key, string text)
        {
            return _storage != null && _storage.WriteText(key + _suffix, text);
        }

        public void Flush()
        {
            // WriteText is File.WriteAllText: already on disk when it returns.
        }
    }

    /// <summary>
    /// Unity PlayerPrefs, which in the Somnium client is the registry key HKCU\Software\Somnium Space Ltd\Somnium
    /// Space VR, shared with the client and every world - hence the "SomniumEuroTruck." prefix. Measured: the Quake
    /// world's SomniumQuake.RailColor, Blades Edge's BEO_* values and spacetimedb.identity_token are all there,
    /// written by world code [persist §2.2]. Survives Windows' temp cleaning and a change of world id; plain text
    /// (the record's MAC is the only check). The optional mirror: EtsProfileStore.MirrorToPlayerPrefs, off (Q-G1,
    /// the user kept the default, PLAN §12).
    /// </summary>
    public sealed class EtsPlayerPrefsSlot : IEtsProfileSlot
    {
        public const string Prefix = "SomniumEuroTruck.";

        public string Name => "player prefs";

        public bool TryRead(string key, out string text)
        {
            text = null;
            string k = Prefix + key;

            if (!PlayerPrefs.HasKey(k))
            {
                return false;
            }

            text = PlayerPrefs.GetString(k, "");
            return true;
        }

        public bool Write(string key, string text)
        {
            try
            {
                PlayerPrefs.SetString(Prefix + key, text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Flush()
        {
            // Without Save a client crash loses every PlayerPrefs write since the last save.
            PlayerPrefs.Save();
        }
    }

    /// <summary>
    /// Keeps the profile for this session only. Used in the SDK editor, where the Bridge has no storage, so Play
    /// mode works the same way without writing anywhere [persist §5.2, copy 4].
    /// </summary>
    public sealed class EtsMemorySlot : IEtsProfileSlot
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Name => "memory (editor)";

        public bool TryRead(string key, out string text)
        {
            return _values.TryGetValue(key, out text);
        }

        public bool Write(string key, string text)
        {
            _values[key] = text;
            return true;
        }

        public void Flush()
        {
        }
    }
}
