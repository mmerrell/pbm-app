using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace PBMAdjudication.Core.Codec;

/// <summary>
/// Temporal IPayloadCodec that implements the Claim Check pattern.
///
/// When a payload exceeds <see cref="ThresholdBytes"/>, the raw bytes are written
/// to <see cref="IClaimCheckStore"/> and replaced with a tiny sentinel payload
/// containing only the storage token. On decode the sentinel is detected and the
/// original bytes are fetched back transparently.
///
/// Stack order in CompositePayloadCodec:
///   Encode: ClaimCheck FIRST  → large bytes leave the pipeline early
///   Decode: ClaimCheck LAST   → bytes are restored after any inner codecs run
/// </summary>
public class ClaimCheckCodec : IPayloadCodec
{
    private const string TokenMetadataKey = "claim-check-token";
    private const string EncodingType = "binary/claim-check";

    public int ThresholdBytes { get; }

    private readonly IClaimCheckStore _store;

    public ClaimCheckCodec(IClaimCheckStore store, int thresholdBytes = 128 * 1024)
    {
        _store = store;
        ThresholdBytes = thresholdBytes;
    }

    public async Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        var result = new List<Payload>(payloads.Count);
        foreach (var payload in payloads)
            result.Add(await MaybeOffloadAsync(payload));
        return result;
    }

    public async Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        var result = new List<Payload>(payloads.Count);
        foreach (var payload in payloads)
            result.Add(await MaybeFetchAsync(payload));
        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<Payload> MaybeOffloadAsync(Payload payload)
    {
        var bytes = payload.ToByteArray();
        if (bytes.Length <= ThresholdBytes)
            return payload;

        var token = await _store.StoreAsync(bytes);

        return new Payload
        {
            Metadata =
            {
                ["encoding"]          = ByteString.CopyFromUtf8(EncodingType),
                [TokenMetadataKey]    = ByteString.CopyFromUtf8(token)
            },
            // Data is intentionally empty — the token in metadata is sufficient.
            Data = ByteString.Empty
        };
    }

    private async Task<Payload> MaybeFetchAsync(Payload payload)
    {
        if (!payload.Metadata.TryGetValue("encoding", out var encoding) ||
            encoding.ToStringUtf8() != EncodingType)
        {
            return payload;
        }

        if (!payload.Metadata.TryGetValue(TokenMetadataKey, out var tokenBytes))
            throw new InvalidOperationException("Claim check payload is missing token metadata.");

        var token = tokenBytes.ToStringUtf8();
        var originalBytes = await _store.FetchAsync(token);
        return Payload.Parser.ParseFrom(originalBytes);
    }
}

/// <summary>
/// Wraps ClaimCheckCodec with a live-switchable flag — same hot-swap pattern
/// as DynamicEncryptionCodec. When disabled, payloads pass through untouched,
/// allowing the demo to show the raw ErrBlobSizeExceedsLimit failure in Temporal UI.
/// </summary>
public class DynamicClaimCheckCodec : IPayloadCodec
{
    private readonly ClaimCheckCodec _inner;
    private volatile bool _enabled;

    public bool IsEnabled => _enabled;
    public int ThresholdBytes => _inner.ThresholdBytes;

    public DynamicClaimCheckCodec(IClaimCheckStore store, bool initiallyEnabled, int thresholdBytes = 128 * 1024)
    {
        _inner   = new ClaimCheckCodec(store, thresholdBytes);
        _enabled = initiallyEnabled;
    }

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads) =>
        _enabled ? _inner.EncodeAsync(payloads) : Task.FromResult(payloads);

    public Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads) =>
        // Always attempt decode — inner codec passes through non-claim-check payloads.
        _inner.DecodeAsync(payloads);
}
