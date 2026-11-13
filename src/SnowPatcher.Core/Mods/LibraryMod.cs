using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Mods;

public sealed record LibraryMod(Guid Id, string SourcePath, string Sha256, bool IsEnabled, ModioSource? Source, DateTimeOffset? LastCheckedAt, ModioSource? Latest, string[] PreviousDownloads, string? PackageId = null, string? ModioUrl = null, string? DisplayName = null);
