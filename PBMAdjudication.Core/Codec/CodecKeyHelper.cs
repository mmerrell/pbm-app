using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace PBMAdjudication.Core.Codec;

/// <summary>
/// Utility for managing the AES-256 encryption key used by EncryptionCodec.
///
/// Generate a key once:
///   Console.WriteLine(CodecKeyHelper.GenerateKeyBase64());
///
/// Add the output to appsettings.json in BOTH Api and Worker:
///   "Temporal": { "EncryptionKeyBase64": "..." }
/// </summary>
public static class CodecKeyHelper
{
    /// <summary>
    /// Generates a cryptographically random 256-bit key, base64-encoded.
    /// Run once and store the result in both appsettings.json files.
    /// </summary>
    public static string GenerateKeyBase64() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Reads and validates the encryption key from IConfiguration.
    /// Fails fast at startup with a clear error rather than silently misbehaving.
    /// </summary>
    public static string GetKeyFromConfig(IConfiguration config)
    {
        var key = config["Temporal:EncryptionKeyBase64"];

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Temporal:EncryptionKeyBase64 is missing from appsettings.json. " +
                "Run CodecKeyHelper.GenerateKeyBase64() and add the result to " +
                "appsettings.json in both Api and Worker.");

        try
        {
            var bytes = Convert.FromBase64String(key);
            if (bytes.Length != 32)
                throw new InvalidOperationException(
                    $"Temporal:EncryptionKeyBase64 decoded to {bytes.Length} bytes; expected 32.");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Temporal:EncryptionKeyBase64 is not valid base64.");
        }

        return key;
    }
}
