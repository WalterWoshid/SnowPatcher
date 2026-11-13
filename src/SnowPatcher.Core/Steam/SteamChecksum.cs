using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace SnowPatcher.Core.Steam;

internal static class SteamChecksum
{
    [SuppressMessage("Security", "CA5350", Justification = "Steam depot manifests require SHA-1 for file and filename checksums.")]
    public static byte[] Compute(ReadOnlySpan<byte> data) => SHA1.HashData(data);

    [SuppressMessage("Security", "CA5350", Justification = "Steam depot manifests require SHA-1 for file and filename checksums.")]
    public static ValueTask<byte[]> ComputeAsync(Stream stream, CancellationToken cancellationToken) =>
        SHA1.HashDataAsync(stream, cancellationToken);
}
