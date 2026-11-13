using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SnowPatcher.Core.Compatibility;

internal static class ContainerWriter
{
    public static byte[] Rebuild(byte[] original, string format, IReadOnlyDictionary<string, byte[]?> changes, CancellationToken token)
    {
        var source = ResourceContainer.Read(original, format, token);
        var members = source.Names.ToDictionary(n => n, n => source.Get(n)!, StringComparer.Ordinal);
        foreach (var (name, data) in changes)
        {
            _ = source.Get(name); // Reject case aliases instead of creating a second logical member.
            if (data is null)
            {
                members.Remove(name);
            }
            else
            {
                members[name] = data;
            }
        }
        if (members.Count > 100000 || members.Values.Sum(b => (long)b.Length) > GameResources.MaxBytes)
        {
            throw new CheckFailure("unsupported", "The generated container exceeds the supported size limit.");
        }

        byte[] result;
        using (var output = new MemoryStream())
        {
            if (format == "zip")
            {
                // Update a copy: untouched ZIP members retain their compressed data and metadata.
                output.Write(original);
                output.Position = 0;
                using (var zip = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
                {
                    foreach (var (name, data) in changes)
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = zip.Entries.SingleOrDefault(e => e.FullName.Replace('\\', '/') == name);
                        if (data is null)
                        {
                            entry?.Delete();
                            continue;
                        }
                        if (entry is null)
                        {
                            entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        }
                        using var stream = entry.Open();
                        stream.SetLength(0);
                        stream.Write(data);
                    }
                }
            }
            else
            {
                var headerLength = format == "cache" ? 64 : 20;
                var header = original[..headerLength];
                if (format == "sslbundle")
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(11), (uint)members.Count);
                }

                using var writer = new BinaryWriter(output, new UTF8Encoding(false, true), leaveOpen: true);
                writer.Write(header);
                if (format == "cache")
                {
                    writer.Write(1U);
                    writer.Write((byte)1);
                    writer.Write((uint)members.Count);
                    writer.Write(4U);
                    writer.Write((byte)1);
                }
                // Keep the game's original name encodings, including cache namespace spelling.
                using var input = new BinaryReader(new MemoryStream(original), new UTF8Encoding(false, true));
                input.BaseStream.Position = format == "cache" ? 78 : 20;
                var rawNames = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var logical in source.Names)
                {
                    rawNames.Add(logical, input.ReadBytes(checked((int)input.ReadUInt32())));
                }

                foreach (var name in members.Keys)
                {
                    var encoded = rawNames.GetValueOrDefault(name) ?? Encoding.UTF8.GetBytes(format == "cache" ? CacheName(name) : name);
                    writer.Write((uint)encoded.Length);
                    writer.Write(encoded);
                }
                if (format == "cache")
                {
                    writer.Write((byte)1);
                    ulong offset = 0;
                    foreach (var data in members.Values)
                    {
                        writer.Write(offset);
                        offset += (ulong)data.Length;
                    }
                }
                writer.Write((byte)1);
                foreach (var data in members.Values)
                {
                    writer.Write((uint)data.Length);
                }

                if (format == "cache")
                {
                    writer.Write((byte)1);
                    foreach (var _ in members)
                    {
                        writer.Write(0U);
                    }
                }
                foreach (var data in members.Values)
                {
                    token.ThrowIfCancellationRequested();
                    writer.Write(data);
                }
            }
            if (output.Length > GameResources.MaxBytes)
            {
                throw new CheckFailure("unsupported", "The generated container is too large.");
            }

            result = output.ToArray();
        }
        var reread = ResourceContainer.Read(result, format, token);
        if (!reread.Names.SequenceEqual(members.Keys) || members.Any(p => !p.Value.AsSpan().SequenceEqual(reread.Get(p.Key))))
        {
            throw new InvalidDataException("The generated container failed its content verification.");
        }

        return result;
    }

    private static string CacheName(string logical)
    {
        var split = logical.IndexOf("]/", StringComparison.Ordinal);
        var path = logical[(split + 2)..].Replace('/', '\\');
        return "<" + logical[1..split] + ">" + (path.Contains('\\') ? "\\" : ":") + path;
    }
}
