// SomniAssetStore - package signature verification.
// The store signs every approved archive with its RSA private key. The public key is
// embedded here; a compromised CDN/DB cannot forge a valid signature without the private
// key, so verified bytes are authentic even if the transport or listing is tampered.
#if UNITY_EDITOR
using System;
using System.Security.Cryptography;

namespace SomniAssetStore.Importer
{
    public static class SomniSigning
    {
        // Public key of the official store (RSA-2048, standard base64 of the raw modulus/exponent).
        // This is the production somni.world key. Replace when deploying your own store instance.
        public const string PublicModulusB64 =
            "sS+P+R0MrRggFQXqtA6KC92JoYw62kNFvuBa4/5Hceh1IriLkSfWqW91VaO5ZhQa/W8t4GKbdbtwcYHtK8FtjaokOIJ+4B847bMoPJmg068NZqOt8Dt2pWijsv8FFaIH6d1hgJYO1fqtl3U5ME4nLf+cEQ0DFAH2uyFSyeOljEo051+z+Sx/KE8b9SO4N327/tZ/tHtyhAVMqJf9aSuVA2VCRHyM0GQiYJrZ0xouFadSjQMqaG1p2TYwiuniUOT0Srsddq3AUy7rJF1gyKtW8ymacpWvbG5GTLPpGNToSkYLoniPXbczpj1if2M1RZsKywwXq/KObGmeY3AcfthOfw==";
        public const string PublicExponentB64 = "AQAB";

        /// <summary>Verify a base64 RSA-SHA256 signature over the given archive bytes.</summary>
        public static bool VerifyArchive(byte[] data, string signatureB64)
        {
            if (data == null || string.IsNullOrEmpty(signatureB64)) return false;
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = Convert.FromBase64String(PublicModulusB64),
                        Exponent = Convert.FromBase64String(PublicExponentB64),
                    });
                    using (var sha = SHA256.Create())
                    {
                        byte[] hash = sha.ComputeHash(data);
                        var def = new RSAPKCS1SignatureDeformatter(rsa);
                        def.SetHashAlgorithm("SHA256");
                        return def.VerifySignature(hash, Convert.FromBase64String(signatureB64));
                    }
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
#endif
