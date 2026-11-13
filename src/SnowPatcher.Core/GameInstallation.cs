namespace SnowPatcher.Core;

public sealed record GameInstallation(string RootDirectory)
{
    public string InitialPakPath => Path.Combine(RootDirectory, "preload", "paks", "client", "initial.pak");

    public bool HasInitialPak => File.Exists(InitialPakPath);
}
