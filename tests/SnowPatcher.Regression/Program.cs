using System.IO.Compression;
using System.Text.Json;
using SnowPatcher.Core;
using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Patching;

const string pak = "preload/paks/client/initial.pak";
const string english = "[strings]/strings_english.str";
const string czech = "[strings]/strings_czech.str";
const string original = "ORIGINAL \"stock\"\n";
const string unsupported = original + "EXAMPLE \"translation\" // inline comment\n";
const string warning = "The language file uses syntax this checker does not support.";
var directory = Directory.CreateTempSubdirectory("snowpatcher-regression-");
try
{
    var legacy = Package("repair-transfer", "1.4.0", [Strings("english", english), Strings("czech", czech)]);
    if (args is ["--reproduce"])
    {
        var (installer, game) = Fixture("reproduce", unsupported);
        var check = await installer.CheckAsync(game, [legacy]);
        Require(check.Report.Mods[0].Detail.Contains(warning, StringComparison.Ordinal), "The Czech parser failure should be reproduced.");
        Console.WriteLine($"Can apply: {check.CanApply}\n{check.Report.Mods[0].Detail}");
        return;
    }

    var (failedTranslation, failedGame) = Fixture("unsupported", unsupported);
    var skipped = await failedTranslation.CheckAsync(failedGame, [legacy]);
    Require(skipped.CanApply && skipped.Report.Mods[0].Status == "Translation skipped" &&
        skipped.Report.Mods[0].Detail.Contains(warning, StringComparison.Ordinal), "Legacy Czech syntax should warn and allow apply.");
    var applied = await failedTranslation.ApplyAsync(failedGame, [legacy]);
    Require(applied.Changed && Read(failedGame, english).Contains("MOD_CAPTION", StringComparison.Ordinal) &&
        Read(failedGame, czech) == unsupported, "Apply must write English and preserve the rejected Czech member.");
    var repeat = await failedTranslation.CheckAsync(failedGame, [legacy]);
    Require(!repeat.HasPendingChanges && repeat.MatchesAppliedSelection, "A skipped translation must not cause a reapply loop.");
    await failedTranslation.ApplyAsync(failedGame, []);
    Require(Read(failedGame, english) == original && Read(failedGame, czech) == unsupported, "Disabling must restore the original.");

    var (clean, cleanGame) = Fixture("clean", original);
    var available = await clean.CheckAsync(cleanGame, [legacy]);
    Require(available.CanApply && available.Report.Mods[0].Status == "Matches", "A supported Czech file should pass normally.");
    await clean.ApplyAsync(cleanGame, [legacy]);
    Require(Read(cleanGame, czech).Contains("MOD_CAPTION", StringComparison.Ordinal), "A supported translation must be installed.");

    var explicitOptional = Package("new-mod", "1.5.0", [Strings("english", english), Strings("czech", czech, optional: true)]);
    var (optIn, optInGame) = Fixture("opt-in", unsupported);
    Require((await optIn.CheckAsync(optInGame, [explicitOptional])).Report.Mods[0].Status == "Translation skipped",
        "A package using the explicit flag should warn.");

    var strict = Package("strict", "1.5.0", [Strings("english", english), Strings("czech", czech)]);
    var (required, requiredGame) = Fixture("required", unsupported);
    var blocked = await required.CheckAsync(requiredGame, [strict]);
    Require(!blocked.CanApply && blocked.Report.Mods[0].Status == "Needs review", "A failed required edit must still block.");
    try
    {
        await required.ApplyAsync(requiredGame, [strict]);
        throw new InvalidOperationException("Apply accepted a failed required edit.");
    }
    catch (InvalidOperationException error) when (error.Message == blocked.BlockReason)
    {
        Require(Read(requiredGame, english) == original, "A blocked selection must not write the valid English edit.");
    }

    var (conflict, conflictGame) = Fixture("conflict", original);
    var requiredCzech = Package("required-czech", "1.5.0", [Strings("czech", czech, text: "Required Czech")]);
    var overlap = await conflict.CheckAsync(conflictGame, [legacy, requiredCzech]);
    Require(overlap.CanApply && overlap.Report.Mods[0].Status == "Translation skipped", "A required translation must take priority on the same key.");
    await conflict.ApplyAsync(conflictGame, [legacy, requiredCzech]);
    Require(Read(conflictGame, czech).Contains("Required Czech", StringComparison.Ordinal), "The required translation was overwritten.");

    var (requiredConflict, requiredConflictGame) = Fixture("required-conflict", original);
    var otherEnglish = Package("required-english", "1.5.0", [Strings("english", english, text: "Other English")]);
    Require(!(await requiredConflict.CheckAsync(requiredConflictGame, [legacy, otherEnglish])).CanApply,
        "Conflicting required edits must still block.");

    var dependent = Package("dependent", "1.5.0", [Strings("dependent-czech", czech, before: "Caption")], "repair-transfer");
    var (dependency, dependencyGame) = Fixture("dependency", original);
    Require(!(await dependency.CheckAsync(dependencyGame, [legacy, dependent])).CanApply,
        "A required operation must not be allowed to depend on optional output.");

    try
    {
        Package("invalid-opt-in", "1.5.0", [Strings("czech", czech, optional: true)]);
        throw new InvalidOperationException("An optional translation without English was accepted.");
    }
    catch (InvalidDataException error) when (error.Message.Contains("English", StringComparison.Ordinal)) { }

    Console.WriteLine("Optional translation regression checks passed.");
}
finally
{
    directory.Delete(recursive: true);
}

(PatchInstaller Installer, GameInstallation Game) Fixture(string name, string czechText)
{
    var game = new GameInstallation(Path.Combine(directory.FullName, name));
    Directory.CreateDirectory(Path.GetDirectoryName(game.InitialPakPath)!);
    using var zip = ZipFile.Open(game.InitialPakPath, ZipArchiveMode.Create);
    Write(zip, english, original);
    Write(zip, czech, czechText);
    return (new PatchInstaller(Path.Combine(directory.FullName, name + "-storage")), game);
}

CompatibilityInput Package(string id, string minimum, object[] operations, string? dependency = null)
{
    var path = Path.Combine(directory.FullName, id + ".zip");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
    {
        Write(zip, "snowpatcher.json", JsonSerializer.Serialize(new
        {
            formatVersion = 3,
            id,
            name = id,
            version = "1.0.0",
            minSnowPatcherVersion = minimum,
            authors = new[] { id },
            game = "snowrunner",
            files = Array.Empty<object>(),
            operations,
            dependencies = dependency is null ? [] : new[] { new { id = dependency, minVersion = "1.0.0" } }
        }));
    }

    return new CompatibilityInput(Guid.NewGuid(), ModArchiveReader.Read(path));
}

object Strings(string id, string member, bool? optional = null, string text = "Caption", string? before = null)
{
    var operation = new Dictionary<string, object>
    {
        ["id"] = id,
        ["type"] = "strings.edit",
        ["target"] = new { file = pak, members = new[] { new { format = "zip", name = member } } },
        ["edits"] = new[] { new { key = "MOD_CAPTION", before = before is null ? (object)new { state = "absent" } : new { state = "present", value = before }, value = text } }
    };
    if (optional is { } flag)
    {
        operation.Add("optional", flag);
    }

    return operation;
}

static void Write(ZipArchive zip, string member, string text)
{
    using var writer = new StreamWriter(zip.CreateEntry(member).Open());
    writer.Write(text);
}

static string Read(GameInstallation game, string member)
{
    using var zip = ZipFile.OpenRead(game.InitialPakPath);
    using var reader = new StreamReader(zip.GetEntry(member)!.Open());
    return reader.ReadToEnd();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
