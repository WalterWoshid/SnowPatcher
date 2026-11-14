namespace SnowPatcher.Core.Updates;

internal static class UpdateTransaction
{
    internal static void Install(string root, UpdatePlan plan, UpdateManifest manifest)
    {
        var previous = UpdateFiles.Manifest(plan.InstallationDirectory);
        if (previous.Platform != manifest.Platform || SnowPatcherVersion.Compare(manifest.Version, previous.Version) <= 0)
        {
            throw new IOException("The installed version changed while the update was downloading. Check for updates again.");
        }

        var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(backup);
        var files = manifest.Files.Select(f => f.Path).Concat(previous.Files.Select(f => f.Path)).Append(UpdateManifest.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var preimages = new List<UpdatePreimage>();
        foreach (var name in files)
        {
            var source = UpdateFiles.In(plan.InstallationDirectory, name);
            if (Directory.Exists(source))
            {
                throw new IOException("An update file would replace a directory: " + name);
            }

            var hash = File.Exists(source) ? UpdateFiles.Hash(source) : null;
            if (hash is not null)
            {
                UpdateFiles.Copy(source, UpdateFiles.In(backup, name));
                if (UpdateFiles.Hash(UpdateFiles.In(backup, name)) != hash)
                {
                    throw new IOException("An application backup could not be verified.");
                }
            }

            preimages.Add(new UpdatePreimage(name, hash));
        }

        UpdateFiles.Write(Path.Combine(root, "journal.json"), new UpdateJournal(preimages.ToArray()));
        foreach (var file in manifest.Files)
        {
            UpdateFiles.Copy(UpdateFiles.In(plan.StagedDirectory, file.Path), UpdateFiles.In(plan.InstallationDirectory, file.Path));
        }

        var required = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in previous.Files.Where(f => !required.Contains(f.Path)))
        {
            File.Delete(UpdateFiles.In(plan.InstallationDirectory, stale.Path));
        }

        UpdateFiles.Copy(UpdateFiles.In(plan.StagedDirectory, UpdateManifest.FileName), UpdateFiles.In(plan.InstallationDirectory, UpdateManifest.FileName));
        UpdateFiles.Verify(plan.InstallationDirectory, manifest, CancellationToken.None);
    }

    internal static void Rollback(string root, string installation)
    {
        var journal = UpdateFiles.Read<UpdateJournal>(Path.Combine(root, "journal.json"));
        foreach (var file in journal.Files.Reverse())
        {
            var target = UpdateFiles.In(installation, file.Path);
            if (file.Sha256 is null)
            {
                File.Delete(target);
                continue;
            }

            if (File.Exists(target) && UpdateFiles.Hash(target) == file.Sha256)
            {
                continue;
            }

            var source = UpdateFiles.In(Path.Combine(root, "backup"), file.Path);
            if (UpdateFiles.Hash(source) != file.Sha256)
            {
                throw new IOException("An application backup changed: " + file.Path);
            }

            UpdateFiles.Copy(source, target);
        }
    }
}
