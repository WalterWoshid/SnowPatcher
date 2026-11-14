using System.Text.Json;
using System.Text.Json.Serialization;
using SnowPatcher.Core.Patching;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.Core.Settings;

public sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(UpdateFiles.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter<GameUpdateBehavior>() }
    };
    private readonly string _path;

    public PreferencesStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowPatcher");
        _path = UpdateFiles.In(root, "preferences.json");
    }

    public AppPreferences Load()
    {
        PatchStorage.RejectLinks(_path);
        if (!File.Exists(_path))
        {
            return new AppPreferences();
        }

        try
        {
            var preferences = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllBytes(_path), JsonOptions);
            if (preferences is null || preferences.FormatVersion != 1 || !Enum.IsDefined(preferences.GameUpdates) ||
                (preferences.GameFolder is not null && !Path.IsPathFullyQualified(preferences.GameFolder)))
            {
                throw new InvalidDataException("The saved preferences are not supported by this version.");
            }

            return preferences.Normalize();
        }
        catch (JsonException error) { throw new InvalidDataException("The saved preferences could not be read.", error); }
    }

    public void Save(AppPreferences preferences)
    {
        PatchStorage.RejectLinks(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PatchStorage.SaveNew(temporary, JsonSerializer.SerializeToUtf8Bytes(preferences.Normalize(), JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
