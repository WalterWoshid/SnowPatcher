/*
Cache-block layout reference/adaptation: chase-000/SnowRunnerTools.

MIT License

Copyright (c) 2020 chase <s.chubukov@protonmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal sealed partial class ResourceContainer(bool caseSensitive)
{
    private readonly Dictionary<string, byte[]> _members = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldedNames = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Names => _members.Keys;
    public long TotalBytes => _members.Values.Sum(b => (long)b.Length);

    public byte[]? Get(string name)
    {
        if (_members.TryGetValue(name, out var bytes))
        {
            return bytes;
        }

        if (!caseSensitive && _foldedNames.Contains(name))
        {
            throw new CheckFailure("unsupported", $"The target's letter case does not match the container: {name}");
        }

        return null;
    }

    public static ResourceContainer Read(byte[] bytes, string format, CancellationToken token, long maxExpanded = 512L * 1024 * 1024)
    {
        var result = new ResourceContainer(format == "sslbundle");
        void Add(string name, byte[] data)
        {
            if (result._members.ContainsKey(name) || (format != "sslbundle" && !result._foldedNames.Add(name)))
            {
                throw new InvalidDataException("The container has duplicate or ambiguous member names.");
            }

            result._members.Add(name, data);
        }

        if (format == "zip")
        {
            using var stream = new MemoryStream(bytes, false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > 100000)
            {
                throw new InvalidDataException("The ZIP has too many entries.");
            }

            long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    PackagePath.Validate(name.TrimEnd('/'));
                    continue;
                }

                PackagePath.Validate(name);
                var attributes = unchecked((uint)entry.ExternalAttributes);
                if ((attributes & 0x400) != 0 || ((attributes >> 16) & 0xf000) is not (0 or 0x8000))
                {
                    throw new InvalidDataException("The ZIP contains a link or special file.");
                }

                total += entry.Length;
                if (total > maxExpanded)
                {
                    throw new CheckFailure("unsupported", "The expanded containers exceed this check's memory limit.");
                }

                using var input = entry.Open();
                Add(name, GameResources.ReadBounded(input, entry.Length, token));
            }

            return result;
        }

        using var memory = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(memory, new UTF8Encoding(false, true));
        void Marker(uint expected, bool wide = false)
        {
            if ((wide ? reader.ReadUInt32() : reader.ReadByte()) != expected)
            {
                throw new InvalidDataException("Unsupported container table layout.");
            }
        }

        uint count;
        if (format == "cache")
        {
            if (bytes.Length < 78 || !bytes.AsSpan(0, 16).SequenceEqual("1SERcache_block\0"u8))
            {
                throw new InvalidDataException("Unsupported cache header.");
            }

            memory.Position = 64;
            Marker(1, true);
            Marker(1);
            count = reader.ReadUInt32();
            Marker(4, true);
            Marker(1);
        }
        else if (format == "sslbundle")
        {
            if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("S3DB"u8))
            {
                throw new InvalidDataException("Unsupported SSL bundle header.");
            }

            memory.Position = 11;
            count = reader.ReadUInt32();
            memory.Position = 20;
        }
        else
        {
            throw new CheckFailure("unsupported", "Unsupported resource container.");
        }

        if (count > 100000)
        {
            throw new InvalidDataException("The container has too many members.");
        }

        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var length = reader.ReadUInt32();
            if (length > 8192 || length > memory.Length - memory.Position)
            {
                throw new InvalidDataException("Invalid container member name.");
            }

            var name = new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length));
            if (format == "cache")
            {
                var match = CacheName().Match(name);
                if (!match.Success)
                {
                    throw new InvalidDataException("Unsupported cache member name.");
                }

                var directory = match.Groups[2].Value;
                name = $"[{match.Groups[1].Value}]/" + (directory == ":" ? "" : directory.Trim('\\').Replace('\\', '/') + "/") + match.Groups[3].Value;
                PackagePath.Validate(name);
            }

            names.Add(name);
        }

        var offsets = new ulong[count];
        if (format == "cache")
        {
            Marker(1);
            for (var i = 0; i < count; i++)
            {
                offsets[i] = reader.ReadUInt64();
            }
        }

        Marker(1);
        var sizes = new uint[count];
        for (var i = 0; i < count; i++)
        {
            sizes[i] = reader.ReadUInt32();
        }

        if (format == "cache")
        {
            Marker(1);
            for (var i = 0; i < count; i++)
            {
                Marker(0, true);
            }
        }

        ulong expectedOffset = 0;
        var identities = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if ((format == "cache" && offsets[i] != expectedOffset) || sizes[i] > memory.Length - memory.Position)
            {
                throw new InvalidDataException("Invalid container member bounds.");
            }

            if (expectedOffset + sizes[i] > (ulong)Math.Max(0, maxExpanded))
            {
                throw new CheckFailure("unsupported", "The expanded containers exceed this check's memory limit.");
            }

            var body = reader.ReadBytes(checked((int)sizes[i]));
            if (format == "sslbundle")
            {
                SslModule.ValidateHeader(body);
                if (!identities.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8))))
                {
                    throw new InvalidDataException("The bundle contains duplicate SSLB identities.");
                }
            }

            Add(names[i], body);
            expectedOffset += sizes[i];
        }

        if (memory.Position != memory.Length)
        {
            throw new InvalidDataException("Unexpected container trailing data.");
        }

        return result;
    }

    [GeneratedRegex(@"^<([^>]+)>(:|\\.+\\)([^\\]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheName();
}
