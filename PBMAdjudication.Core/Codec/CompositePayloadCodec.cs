using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace PBMAdjudication.Core.Codec;

/// <summary>
/// Chains multiple IPayloadCodec instances into a single codec.
///
/// Encode order: codecs applied left → right  (ClaimCheck, then Encryption)
/// Decode order: codecs applied right → left  (Encryption first, then ClaimCheck)
///
/// This mirrors the standard envelope pattern: the outermost layer (encryption)
/// must be unwrapped first before the inner layer (claim check token) is visible.
/// </summary>
public class CompositePayloadCodec : IPayloadCodec
{
    private readonly IPayloadCodec[] _codecs;

    /// <param name="codecs">Ordered encode sequence. Decode runs in reverse.</param>
    public CompositePayloadCodec(params IPayloadCodec[] codecs)
    {
        if (codecs.Length == 0)
            throw new ArgumentException("At least one codec is required.", nameof(codecs));
        _codecs = codecs;
    }

    public async Task<IReadOnlyCollection<Payload>> EncodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        foreach (var codec in _codecs)
            payloads = await codec.EncodeAsync(payloads);
        return payloads;
    }

    public async Task<IReadOnlyCollection<Payload>> DecodeAsync(IReadOnlyCollection<Payload> payloads)
    {
        foreach (var codec in _codecs.Reverse())
            payloads = await codec.DecodeAsync(payloads);
        return payloads;
    }
}
