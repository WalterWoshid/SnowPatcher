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

internal sealed class GameResources(string root, CancellationToken cancellationToken, IReadOnlyDictionary<string, byte[]?>? baseline = null)
{
    internal const int MaxBytes = 256 * 1024 * 1024;
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly Dictionary<string, byte[]?> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _timestamps = new(StringComparer.OrdinalIgnoreCase);
    private long _readBytes;
    private long _expandedBytes;
    private readonly Dictionary<(byte[] Bytes, string Format), ResourceContainer> _containers = [];
    private readonly HashSet<string> _parents = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public IReadOnlyList<CheckedFile> Snapshots => _observed.Select(p => new CheckedFile(FullPath(p.Key), p.Value is null ? "absent" : Hash(p.Value))).ToArray();

    public byte[]? Root(string relative)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_roots.TryGetValue(relative, out var cached))
        {
            return cached;
        }

        var path = FullPath(relative);
        CheckLinks(path);
        if (Directory.Exists(path))
        {
            throw new CheckFailure("unsupported", "The mod targets a folder rather than a game file.");
        }

        if (!File.Exists(path))
        {
            _observed[relative] = null;
            _timestamps[relative] = 0;
            return _roots[relative] = baseline is not null && baseline.TryGetValue(relative, out var saved) ? saved : null;
        }

        var file = new FileInfo(path);
        if (file.Length > MaxBytes)
        {
            throw new CheckFailure("unsupported", "This game file exceeds the checker's 256 MiB resource limit.");
        }

        _timestamps[relative] = file.LastWriteTimeUtc.Ticks;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = ReadBounded(input, file.Length, cancellationToken);
        _readBytes += bytes.Length;
        if (_readBytes > 1024L * 1024 * 1024)
        {
            throw new CheckFailure("unsupported", "The selected mods require too much data for one compatibility check.");
        }

        _observed[relative] = bytes;
        return _roots[relative] = baseline is not null && baseline.TryGetValue(relative, out var original) ? original : bytes;
    }

    public void RequireParentDirectory(string relative)
    {
        var path = Path.GetDirectoryName(FullPath(relative))!;
        CheckLinks(path);
        if (!Directory.Exists(path))
        {
            throw new CheckFailure("missing", "The target's parent folder does not exist.");
        }

        _parents.Add(path);
    }

    public ResourceContainer Container(byte[] data, string format)
    {
        if (_containers.TryGetValue((data, format), out var cached))
        {
            return cached;
        }

        var parsed = ResourceContainer.Read(data, format, cancellationToken, 512L * 1024 * 1024 - _expandedBytes);
        _expandedBytes += parsed.TotalBytes;
        _containers.Add((data, format), parsed);
        return parsed;
    }

    public void EnsureUnchanged()
    {
        foreach (var parent in _parents)
        {
            CheckLinks(parent);
            if (!Directory.Exists(parent))
            {
                throw new IOException("A target folder changed during the check.");
            }
        }

        foreach (var (relative, bytes) in _observed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = FullPath(relative);
            CheckLinks(path);
            if (bytes is null)
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new IOException("Game files changed while checking. Run Check mods again.");
                }

                continue;
            }

            var file = new FileInfo(path);
            if (!file.Exists || file.Length != bytes.Length || file.LastWriteTimeUtc.Ticks != _timestamps[relative])
            {
                throw new IOException("Game files changed while checking. Run Check mods again.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Hash(ReadBounded(stream, file.Length, cancellationToken)) != Hash(bytes))
            {
                throw new IOException("Game files changed while checking. Run Check mods again.");
            }
        }
    }

    private string FullPath(string relative)
    {
        PackagePath.Validate(relative);
        var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new CheckFailure("unsupported", "A mod target is outside the game folder.");
        }

        return path;
    }

    private void CheckLinks(string path)
    {
        for (var current = path; current.Length >= _root.Length; current = Path.GetDirectoryName(current) ?? "")
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new CheckFailure("unsupported", "The checker cannot follow a linked game file or folder.");
            }

            if (current == _root)
            {
                break;
            }
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] ReadBounded(Stream input, long expected, CancellationToken token)
    {
        if (expected < 0 || expected > MaxBytes)
        {
            throw new CheckFailure("unsupported", "A resource exceeds the checker's size limit.");
        }

        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (output.Length + count > expected)
            {
                throw new InvalidDataException("A resource exceeds its declared size.");
            }

            output.Write(buffer, 0, count);
        }

        if (output.Length != expected)
        {
            throw new InvalidDataException("A game resource is truncated.");
        }

        return output.ToArray();
    }
}
