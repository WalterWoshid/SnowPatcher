using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SnowPatcher.Core.Epic;

// Epic BuildPatch binary sections: metadata, chunks, file manifests. Unknown trailing sections are left alone.
internal sealed record EpicManifest(string AppName, string BuildVersion, IReadOnlyDictionary<string, EpicManifestFile> Files, string FileHash)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    public static EpicManifest Read(string path)
    {
        try
        {
            using var source = File.OpenRead(path);
            using var header = new BinaryReader(source);
            if (header.ReadUInt32() != 0x44BEC00C)
            {
                throw new InvalidDataException("Unsupported Epic game manifest.");
            }
            var headerSize = header.ReadUInt32();
            var unpackedSize = header.ReadUInt32();
            var packedSize = header.ReadUInt32();
            var expectedHash = header.ReadBytes(20);
            var flags = header.ReadByte();
            if (headerSize < 41 || headerSize > source.Length || packedSize != source.Length - headerSize || (flags & ~1) != 0 || unpackedSize > int.MaxValue)
            {
                throw new InvalidDataException("Epic's game manifest is incomplete or uses unsupported encryption.");
            }
            source.Position = headerSize;
            using var expanded = new MemoryStream();
            if ((flags & 1) != 0)
            {
                using var compressed = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true);
                CopyBody(compressed, expanded, unpackedSize);
            }
            else
            {
                CopyBody(source, expanded, unpackedSize);
            }
            var bodyBytes = expanded.GetBuffer().AsSpan(0, checked((int)expanded.Length));
            if (!Checksum(bodyBytes).AsSpan().SequenceEqual(expectedHash))
            {
                throw new InvalidDataException("Epic's game manifest failed its checksum.");
            }
            expanded.Position = 0;
            using var body = new BinaryReader(expanded, Utf8, leaveOpen: true);
            var metadataEnd = SectionEnd(body, 22);
            _ = body.ReadByte();
            _ = body.ReadUInt32();
            _ = body.ReadByte();
            _ = body.ReadUInt32();
            var app = ReadString(body, metadataEnd);
            var version = ReadString(body, metadataEnd);
            if (body.BaseStream.Position > metadataEnd || string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(version) || version.Any(char.IsControl))
            {
                throw new InvalidDataException("Epic's game manifest has invalid build metadata.");
            }
            body.BaseStream.Position = metadataEnd;
            body.BaseStream.Position = SectionEnd(body);
            var filesEnd = SectionEnd(body, 9);
            _ = body.ReadByte();
            var count = body.ReadUInt32();
            if (count > (filesEnd - body.BaseStream.Position) / 28)
            {
                throw new InvalidDataException("Epic's file list is truncated.");
            }
            var names = new string[count];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = ReadString(body, filesEnd).Replace('\\', '/');
            }
            var links = new bool[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                links[i] = ReadString(body, filesEnd).Length > 0;
            }
            var files = new Dictionary<string, EpicManifestFile>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Length; i++)
            {
                var hash = body.ReadBytes(20);
                if (hash.Length != 20 || !files.TryAdd(names[i], new(hash, links[i])))
                {
                    throw new InvalidDataException("Epic's file list has missing checksums or duplicate filenames.");
                }
            }
            if (body.BaseStream.Position > filesEnd)
            {
                throw new InvalidDataException("Epic's file checksums are truncated.");
            }
            source.Position = 0;
            return new(app, version, files, Convert.ToHexString(Checksum(source)));
        }
        catch (Exception error) when (error is EndOfStreamException or OverflowException or DecoderFallbackException)
        {
            throw new InvalidDataException("Epic's game manifest is damaged or incomplete.", error);
        }
    }

    private static long SectionEnd(BinaryReader reader, int minimumSize = 5)
    {
        var start = reader.BaseStream.Position;
        var size = reader.ReadUInt32();
        var end = start + size;
        if (size < minimumSize || end > reader.BaseStream.Length)
        {
            throw new InvalidDataException("Epic's game manifest has an invalid section.");
        }
        return end;
    }

    private static string ReadString(BinaryReader reader, long sectionEnd)
    {
        if (sectionEnd - reader.BaseStream.Position < sizeof(int))
        {
            throw new InvalidDataException("Epic's game manifest has a truncated string.");
        }
        var count = reader.ReadInt32();
        if (count == 0)
        {
            return "";
        }
        var length = count < 0 ? -(long)count * 2 : count;
        if (length > sectionEnd - reader.BaseStream.Position || length > int.MaxValue)
        {
            throw new InvalidDataException("Epic's game manifest has an invalid string.");
        }
        var bytes = reader.ReadBytes((int)length);
        var terminator = count < 0 ? 2 : 1;
        if (bytes[^1] != 0 || terminator == 2 && bytes[^2] != 0)
        {
            throw new InvalidDataException("Epic's game manifest has an unterminated string.");
        }
        Encoding encoding = count < 0 ? Utf16 : Utf8;
        return encoding.GetString(bytes, 0, bytes.Length - terminator);
    }

    private static void CopyBody(Stream source, Stream target, uint expectedSize)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            copied += count;
            if (copied > expectedSize)
            {
                throw new InvalidDataException("Epic's expanded manifest exceeds its recorded size.");
            }
            target.Write(buffer, 0, count);
        }
        if (copied != expectedSize)
        {
            throw new InvalidDataException("Epic's game manifest is truncated.");
        }
    }

    [SuppressMessage("Security", "CA5350", Justification = "Epic BuildPatch manifests use SHA-1 checksums.")]
    internal static byte[] Checksum(ReadOnlySpan<byte> bytes) => SHA1.HashData(bytes);

    [SuppressMessage("Security", "CA5350", Justification = "Epic BuildPatch manifests use SHA-1 checksums.")]
    private static byte[] Checksum(Stream stream) => SHA1.HashData(stream);
}

internal sealed record EpicManifestFile(byte[] Hash, bool IsLink);
