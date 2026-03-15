using System.Security.Cryptography;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace PBMAdjudication.Core.Codec;

/// <summary>
/// Temporal IPayloadCodec that encrypts all workflow payloads using AES-256-GCM.
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

    public Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        var result = payloads.Select(Encrypt).ToList();
        return Task.FromResult<IReadOnlyCollection<Payload>>(result);
    }

    public Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        var result = payloads.Select(Decrypt).ToList();
        return Task.FromResult<IReadOnlyCollection<Payload>>(result);
    }

    private Payload Encrypt(Payload payload)
    {
        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);

        var plaintext  = payload.ToByteArray();
        var nonce      = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];

        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce,      0, combined, 0,                         nonce.Length);
        Buffer.BlockCopy(tag,        0, combined, nonce.Length,              tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);

        return new Payload
        {
            Metadata = { ["encoding"] = ByteString.CopyFromUtf8(EncodingType) },
            Data = ByteString.CopyFrom(combined)
        };
    }

    private Payload Decrypt(Payload payload)
    {
        if (!payload.Metadata.TryGetValue("encoding", out var encoding) ||
            encoding.ToStringUtf8() != EncodingType)
        {
            return payload;
        }

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);

        var combined  = payload.Data.ToByteArray();
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize   = AesGcm.TagByteSizes.MaxSize;

        var nonce      = combined[..nonceSize];
        var tag        = combined[nonceSize..(nonceSize + tagSize)];
        var ciphertext = combined[(nonceSize + tagSize)..];
        var plaintext  = new byte[ciphertext.Length];

        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Payload.Parser.ParseFrom(plaintext);
    }
}

/// <summary>
/// Wraps EncryptionCodec with a live-switchable flag.
/// The TemporalClient is a singleton — we can't swap it at runtime — but we CAN
/// check a flag on every encode/decode call, giving us hot-swap behavior without
/// restarting either the Api or Worker process.
///
/// Note: flipping encryption mid-flight means workflows started under one mode
/// will need to finish under the same mode (their history is encoded one way).
/// New workflows started after the flip will use the new setting. This is fine
/// for a demo — in production you'd drain workflows before switching.
/// </summary>
public class DynamicEncryptionCodec : IPayloadCodec
{
    private readonly EncryptionCodec _inner;

    // Volatile ensures reads/writes are not cached in CPU registers across threads.
    private volatile bool _enabled;

    public bool IsEnabled => _enabled;

    public DynamicEncryptionCodec(string base64Key, bool initiallyEnabled)
    {
        _inner   = new EncryptionCodec(base64Key);
        _enabled = initiallyEnabled;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    public Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        // Always decrypt — we need to be able to read payloads encoded either way.
        // Only encrypt if the flag is on.
        return _enabled
            ? _inner.EncodeAsync(payloads)
            : Task.FromResult(payloads);
    }

    public Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        // Always attempt decode — the inner codec passes through non-encrypted payloads.
        return _inner.DecodeAsync(payloads);
    }
}
