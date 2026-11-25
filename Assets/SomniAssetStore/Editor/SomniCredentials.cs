// SomniAssetStore - one shared upload token for the whole SDK.
// Entered once in the Account window; the Publisher and My Packages both read it from here.
// Downloading/installing from the store never needs a token.
#if UNITY_EDITOR
using System;
using System.Net.Http;
using UnityEditor;
using UnityEngine;

namespace SomniAssetStore.Shared
{
    /// <summary>Single source of truth for the store URL. Change this one line to self-host.</summary>
    public static class SomniConfig
    {
        public const string StoreUrl = "https://somni.world";
    }

    public static class SomniCredentials
    {
        private const string TokenKey = "SomniStore.UploadToken";

        public static string Token => EditorPrefs.GetString(TokenKey, "");
        public static bool HasToken => !string.IsNullOrEmpty(Token);

        // cached identity (resolved from the store)
        public static string Username { get; private set; }
        public static string DisplayName { get; private set; }
        public static bool IsDev { get; private set; }
        public static bool Verified { get; private set; }
        public static string Error { get; private set; }

        public static void SetToken(string token)
        {
            if (string.IsNullOrEmpty(token)) EditorPrefs.DeleteKey(TokenKey);
            else EditorPrefs.SetString(TokenKey, token);
            ResetIdentity();
        }

        public static void SignOut()
        {
            EditorPrefs.DeleteKey(TokenKey);
            ResetIdentity();
        }

        private static void ResetIdentity()
        {
            Username = null; DisplayName = null; IsDev = false; Verified = false; Error = null;
        }

        [Serializable]
        private class Identity { public string username; public string displayName; public bool verified; public bool dev; }

        /// <summary>Verify the stored token against the store and cache the account it maps to.</summary>
        public static bool Resolve()
        {
            ResetIdentity();
            if (!HasToken) { Error = "No upload token set. Open SomniAssetStore > Account."; return false; }
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(20);
                    http.DefaultRequestHeaders.Add("Authorization", "Bearer " + Token);
                    var resp = http.GetAsync(SomniConfig.StoreUrl.TrimEnd('/') + "/api/upload-identity").Result;
                    if ((int)resp.StatusCode != 200)
                    { Error = "Token rejected (" + (int)resp.StatusCode + "). Generate a fresh one on the website."; return false; }
                    var id = JsonUtility.FromJson<Identity>(resp.Content.ReadAsStringAsync().Result);
                    Username = id.username;
                    DisplayName = string.IsNullOrEmpty(id.displayName) ? id.username : id.displayName;
                    IsDev = id.dev;
                    Verified = true;
                    return true;
                }
            }
            catch (Exception e) { Error = "Could not reach the store: " + e.Message; return false; }
        }

        /// <summary>Verify if we have a token but haven't confirmed the identity this session.</summary>
        public static void EnsureResolved()
        {
            if (HasToken && !Verified && Error == null) Resolve();
        }
    }
}
#endif
