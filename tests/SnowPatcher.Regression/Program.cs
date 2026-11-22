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
const string unrelatedRows = "ORIGINAL \"preexisting\"\r\n" +
    "OTHER_WITH_COMMENT \"text // inside quotes\" // trailing comment\r\n" +
    "OTHER_WITH_ESCAPE \"unknown \\q escape\"\r\n" +
    "OTHER_WITH_SEPARATOR = \"MOD_CAPTION\"\r\n" +
    "DUPLICATE \"one\"\r\nDUPLICATE \"two\"";
const string unsupported = original + "MOD_CAPTION: \"unknown syntax for the target key\"\n";
const string warning = "Cannot safely edit language key MOD_CAPTION. It appears in an unsupported row on line 2.";
var directory = Directory.CreateTempSubdirectory("snowpatcher-regression-");
try
{
    var legacy = Package("repair-transfer", "1.4.0", [Strings("english", english), Strings("czech", czech)]);
    var extraLabels = Package("extra-labels", "1.5.0", [Strings("english", english, key: "EXTRA_LABEL"), Strings("czech", czech, key: "EXTRA_LABEL")]);
    var overrideLabel = Package("update-label", "1.5.0", [Strings("english", english, before: "text // inside quotes", text: "Changed", key: "OTHER_WITH_COMMENT")]);
    var (merge, mergeGame) = Fixture("unrelated-syntax", unrelatedRows, unrelatedRows);
    var merged = await merge.CheckAsync(mergeGame, [legacy, extraLabels, overrideLabel]);
    Require(merged.CanApply && merged.Report.Mods.All(m => m.Status == "Matches"), "Unrelated syntax must not block either language.");
    await merge.ApplyAsync(mergeGame, [legacy, extraLabels, overrideLabel]);
    const string appended = "\r\nEXTRA_LABEL\t\"Caption\"\r\nMOD_CAPTION\t\"Caption\"\r\n";
    Require(Read(mergeGame, czech) == unrelatedRows + appended, "Two mods must merge into the same Czech file.");
    Require(Read(mergeGame, english) == unrelatedRows.Replace("text // inside quotes", "Changed", StringComparison.Ordinal) + appended,
        "Required English edits must merge without changing comments, other rows or CRLF.");
    await merge.ApplyAsync(mergeGame, []);
    Require(Read(mergeGame, english) == unrelatedRows && Read(mergeGame, czech) == unrelatedRows, "Rollback must preserve the manual edits.");

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

    var (duplicate, duplicateGame) = Fixture("duplicate", original, "MOD_CAPTION \"one\"\r\"MOD_\\u0043APTION\" \"two\"\r");
    var duplicateCheck = await duplicate.CheckAsync(duplicateGame, [legacy]);
    Require(!duplicateCheck.CanApply && duplicateCheck.Report.Mods[0].Detail.Contains("more than one definition", StringComparison.Ordinal),
        "Quoted and escaped forms of a required key must be checked for duplicates, including with CR line endings.");

    var (invalidValue, invalidValueGame) = Fixture("invalid-value", original, "MOD_CAPTION \"unsupported \\q escape\"\n");
    var invalidValueCheck = await invalidValue.CheckAsync(invalidValueGame, [legacy]);
    Require(!invalidValueCheck.CanApply && invalidValueCheck.Report.Mods[0].Detail.Contains("MOD_CAPTION", StringComparison.Ordinal),
        "An opaque value on the required key itself must still block.");

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

    Console.WriteLine("Translation merge and compatibility regression checks passed.");

    var (auto, autoGame) = Fixture("automatic-selection", original);
    var (_, otherGame) = Fixture("other-installation", original);
    await AutomaticSelectionChecks.RunAsync(directory.FullName, auto, autoGame, otherGame, legacy, extraLabels, otherEnglish, english);
}
finally
{
    directory.Delete(recursive: true);
}

(PatchInstaller Installer, GameInstallation Game) Fixture(string name, string czechText, string englishText = original)
{
    var game = new GameInstallation(Path.Combine(directory.FullName, name));
    Directory.CreateDirectory(Path.GetDirectoryName(game.InitialPakPath)!);
    using var zip = ZipFile.Open(game.InitialPakPath, ZipArchiveMode.Create);
    Write(zip, english, englishText);
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

object Strings(string id, string member, bool? optional = null, string text = "Caption", string? before = null, string key = "MOD_CAPTION")
{
    var operation = new Dictionary<string, object>
    {
        ["id"] = id,
        ["type"] = "strings.edit",
        ["target"] = new { file = pak, members = new[] { new { format = "zip", name = member } } },
        ["edits"] = new[] { new { key, before = before is null ? (object)new { state = "absent" } : new { state = "present", value = before }, value = text } }
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
