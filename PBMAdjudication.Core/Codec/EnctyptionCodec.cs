using System.Security.Cryptography;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace PBMAdjudication.Core.Codec;

/// <summary>
/// Temporal IPayloadCodec that encrypts all workflow payloads using AES-256-GCM.
///
/// When registered on both the Worker and the Temporal Client, every payload
/// that flows through Temporal (activity inputs/outputs, workflow inputs/outputs,
/// signals, queries) is encrypted before leaving the process and decrypted on
/// the way back in.
///
/// With encryption ON, the Temporal UI shows:
///   { "metadata": { "encoding": "YmluYXJ5L2VuY3J5cHRlZA==" }, "data": "<blob>" }
/// instead of readable PII JSON — which is the whole point of this demo.
/// </summary>
public class EncryptionCodec : IPayloadCodec
{
    private const string EncodingType = "binary/encrypted";

    private readonly byte[] _key;

    public EncryptionCodec(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (256 bits) when base64-decoded.");
    }

    /// <summary>
    /// Encrypts payloads before Temporal writes them to history / sends over the wire.
    /// </summary>
    public Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        var result = payloads.Select(Encrypt).ToList();
        return Task.FromResult<IReadOnlyCollection<Payload>>(result);
    }

    /// <summary>
    /// Decrypts payloads when Temporal reads from history / receives over the wire.
    /// Passes through any payload that wasn't encrypted by us.
    /// </summary>
    public Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        var result = payloads.Select(Decrypt).ToList();
        return Task.FromResult<IReadOnlyCollection<Payload>>(result);
    }

    // ── Private helpers ────────────────────────────────────────────────

    private Payload Encrypt(Payload payload)
    {
        // AES-256-GCM: authenticated encryption with a fresh nonce per payload.
        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);

        var plaintext  = payload.ToByteArray();
        var nonce      = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize); // 12 bytes
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];                         // 16 bytes
        var ciphertext = new byte[plaintext.Length];

        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Wire format: nonce (12) || tag (16) || ciphertext
        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce,      0, combined, 0,                         nonce.Length);
        Buffer.BlockCopy(tag,        0, combined, nonce.Length,              tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);

        return new Payload
        {
            Metadata =
            {
                // This encoding label is what makes the Temporal UI show a blob.
                ["encoding"] = ByteString.CopyFromUtf8(EncodingType)
            },
            Data = ByteString.CopyFrom(combined)
        };
    }

    private Payload Decrypt(Payload payload)
    {
        // Only decrypt payloads we encrypted — pass everything else through.
        if (!payload.Metadata.TryGetValue("encoding", out var encoding) ||
            encoding.ToStringUtf8() != EncodingType)
        {
            return payload;
        }

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);

        var combined  = payload.Data.ToByteArray();
        var nonceSize = AesGcm.NonceByteSizes.MaxSize; // 12
        var tagSize   = AesGcm.TagByteSizes.MaxSize;   // 16

        var nonce      = combined[..nonceSize];
        var tag        = combined[nonceSize..(nonceSize + tagSize)];
        var ciphertext = combined[(nonceSize + tagSize)..];
        var plaintext  = new byte[ciphertext.Length];

        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Payload.Parser.ParseFrom(plaintext);
    }
}
