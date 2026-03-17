namespace PBMAdjudication.Core.Codec;

/// <summary>
/// Stores claim-check blobs as flat files in a local directory.
/// Each blob is written as a GUID-named file; the token is just the GUID string.
/// To swap for S3, implement IClaimCheckStore against the AWS SDK — no codec changes needed.
/// </summary>
public class FileSystemClaimCheckStore : IClaimCheckStore
{
    private readonly string _directory;

    public FileSystemClaimCheckStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public async Task<string> StoreAsync(byte[] data)
    {
        var token = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_directory, token);
        await File.WriteAllBytesAsync(path, data);
        return token;
    }

    public async Task<byte[]> FetchAsync(string token)
    {
        var path = Path.Combine(_directory, token);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Claim check blob not found: {token}", path);
        return await File.ReadAllBytesAsync(path);
    }

    public Task DeleteAsync(string token)
    {
        var path = Path.Combine(_directory, token);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }
}
