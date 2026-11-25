// SomniAssetStore - HTTP client for the store API (editor only).
#if UNITY_EDITOR
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using SomniAssetStore.Shared;
using UnityEngine;

namespace SomniAssetStore.Importer
{
    public static class SomniStoreClient
    {
        public const string DefaultServerUrl = SomniConfig.StoreUrl;
        // Get-only so in-project code (e.g. a freshly imported package) can't repoint the client
        // at a hostile host and capture the upload token. There is no UI to change it.
        public static string ServerUrl => DefaultServerUrl;

        /// <summary>Hard cap on a downloaded archive (defends against a hostile server / huge body).</summary>
        public const long MaxDownloadBytes = 64L * 1024 * 1024;

        public static StoreListResponse ListPackages()
        {
            RequireSafeUrl();
            using (var http = NewClient())
            {
                // Send the token when signed in so each listing carries the caller's own rating.
                if (SomniCredentials.HasToken)
                    http.DefaultRequestHeaders.Add("Authorization", "Bearer " + SomniCredentials.Token);
                string json = http.GetStringAsync(Url("/api/packages")).Result;
                return JsonUtility.FromJson<StoreListResponse>(json);
            }
        }

        /// <summary>
        /// Download a .somnipkg and verify it against <paramref name="expectedSha"/> (from the
        /// authenticated listing) AND its cryptographic signature. Fails closed: no trusted hash,
        /// invalid signature, or (for a remote store) a missing signature all reject the download.
        /// </summary>
        public static byte[] DownloadPackage(string id, string version, string expectedSha, string signatureB64)
        {
            RequireSafeUrl();
            if (string.IsNullOrEmpty(expectedSha) || expectedSha.Length != 64)
                throw new Exception("refusing to download without a trusted archive hash");

            using (var http = NewClient())
            {
                // Send the token when signed in so the store records this install and the
                // account becomes eligible to rate the package. Downloads still work without one.
                if (SomniCredentials.HasToken)
                    http.DefaultRequestHeaders.Add("Authorization", "Bearer " + SomniCredentials.Token);
                var resp = http.GetAsync(Url("/api/packages/" + Uri.EscapeDataString(id) + "/" +
                    Uri.EscapeDataString(version) + "/download")).Result;
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("download failed: HTTP " + (int)resp.StatusCode);

                long? len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxDownloadBytes)
                    throw new Exception("archive too large (" + len.Value + " bytes)");

                byte[] data = ReadCapped(resp);

                using (var sha = SHA256.Create())
                {
                    string actual = Publisher.SomniPackageBuilder.Hex(sha.ComputeHash(data));
                    if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("archive hash does not match the store listing (tampered or corrupt).");
                }

                // Cryptographic signature: authentic bytes even if the transport/DB is compromised.
                // Enforced for remote stores; skipped on localhost so a local dev store (with its own
                // signing key) still works without embedding that key here.
                if (IsRemoteHost())
                {
                    if (string.IsNullOrEmpty(signatureB64))
                        throw new Exception("package is unsigned; refusing to install it from a remote store.");
                    if (!SomniSigning.VerifyArchive(data, signatureB64))
                        throw new Exception("package signature is INVALID — tampered archive or wrong signing key.");
                }
                return data;
            }
        }

        private static bool IsRemoteHost()
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri)) return true;
            return !(uri.IsLoopback || uri.Host == "localhost");
        }

        [Serializable] private class RatingResp { public float avg; public int count; public int mine; }
        [Serializable] private class ErrResp { public string error; }

        public struct RatingResult { public float avg; public int count; public int mine; }

        /// <summary>Submit a 1-5 star rating (requires a signed-in account that installed it).</summary>
        public static RatingResult RatePackage(string id, int stars)
        {
            RequireSafeUrl();
            if (!SomniCredentials.HasToken) throw new Exception("set your upload token in Somni Tools to rate.");
            using (var http = NewClient())
            {
                http.DefaultRequestHeaders.Add("Authorization", "Bearer " + SomniCredentials.Token);
                var content = new StringContent("{\"stars\":" + stars + "}",
                    System.Text.Encoding.UTF8, "application/json");
                var resp = http.PostAsync(Url("/api/packages/" + Uri.EscapeDataString(id) + "/rating"), content).Result;
                string body = resp.Content.ReadAsStringAsync().Result;
                if (!resp.IsSuccessStatusCode)
                {
                    string msg = "HTTP " + (int)resp.StatusCode;
                    try { var e = JsonUtility.FromJson<ErrResp>(body); if (e != null && !string.IsNullOrEmpty(e.error)) msg = e.error; }
                    catch { }
                    throw new Exception(msg);
                }
                var r = JsonUtility.FromJson<RatingResp>(body);
                return new RatingResult { avg = r.avg, count = r.count, mine = r.mine };
            }
        }

        /// <summary>Fetch all versions of a package (with manifests) for dependency resolution.</summary>
        public static PackageDetailResponse GetPackage(string id)
        {
            RequireSafeUrl();
            using (var http = NewClient())
            {
                var resp = http.GetAsync(Url("/api/packages/" + Uri.EscapeDataString(id))).Result;
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    throw new Exception("dependency not found on the store: " + id);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("store error " + (int)resp.StatusCode + " for " + id);
                return JsonUtility.FromJson<PackageDetailResponse>(resp.Content.ReadAsStringAsync().Result);
            }
        }

        /// <summary>Fetch a package screenshot (png/jpg/webp bytes), or null if there is none.</summary>
        public static byte[] DownloadImage(string id, string version)
        {
            RequireSafeUrl();
            using (var http = NewClient())
            {
                var resp = http.GetAsync(Url("/api/packages/" + Uri.EscapeDataString(id) + "/" +
                    Uri.EscapeDataString(version) + "/image")).Result;
                if (!resp.IsSuccessStatusCode) return null;
                long? len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > 8L * 1024 * 1024) return null;
                return resp.Content.ReadAsByteArrayAsync().Result;
            }
        }

        /// <summary>Read the response body but never buffer more than the cap into memory.</summary>
        private static byte[] ReadCapped(HttpResponseMessage resp)
        {
            using (var stream = resp.Content.ReadAsStreamAsync().Result)
            using (var ms = new MemoryStream())
            {
                byte[] chunk = new byte[64 * 1024];
                int read;
                long total = 0;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                        throw new Exception("archive exceeds " + MaxDownloadBytes + " bytes; aborting.");
                    ms.Write(chunk, 0, read);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Require https for any non-loopback host so tokens/archives are not sent in the clear.</summary>
        public static void RequireSafeUrl()
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
                throw new Exception("invalid server URL: " + ServerUrl);
            bool loopback = uri.IsLoopback || uri.Host == "localhost";
            if (uri.Scheme != Uri.UriSchemeHttps && !loopback)
                throw new Exception("refusing to use a plain-http store URL for a remote host; use https://");
        }

        private static HttpClient NewClient()
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            http.MaxResponseContentBufferSize = MaxDownloadBytes;
            return http;
        }

        private static string Url(string path) => ServerUrl.TrimEnd('/') + path;
    }
}
#endif
