namespace PBMAdjudication.Core.Codec;

/// <summary>
/// Abstraction for claim check storage backends.
/// Swap FileSystemClaimCheckStore for an S3 or Azure Blob implementation
/// without touching the codec itself.
/// </summary>
public interface IClaimCheckStore
{
    /// <summary>Persist <paramref name="data"/> and return an opaque token.</summary>
    Task<string> StoreAsync(byte[] data);

    /// <summary>Retrieve data previously stored under <paramref name="token"/>.</summary>
    Task<byte[]> FetchAsync(string token);

    /// <summary>Delete data stored under <paramref name="token"/> (best-effort cleanup).</summary>
    Task DeleteAsync(string token);
}
