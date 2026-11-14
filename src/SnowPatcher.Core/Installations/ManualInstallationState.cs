using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace SnowPatcher.Core.Installations;

internal static class ManualInstallationState
{
    public static GameInstallState Read(GameInstallation installation)
    {
        if (!Directory.Exists(Path.GetDirectoryName(installation.InitialPakPath)))
        {
            throw new InvalidDataException("Choose the SnowRunner folder containing preload/paks/client.");
        }

        var executable = Path.Combine(installation.RootDirectory, "Sources", "Bin", "SnowRunner.exe");
        var version = "Unknown";
        var identity = "unknown";
        if (File.Exists(executable))
        {
            try
            {
                using var stream = File.OpenRead(executable);
                using var reader = new PEReader(stream);
                var headers = reader.PEHeaders;
                if (headers.PEHeader is { } pe)
                {
                    // Patches can change code and file length. Neither belongs in the build identity.
                    identity = FormattableString.Invariant($"{headers.CoffHeader.Machine}:{headers.CoffHeader.TimeDateStamp}:{pe.SizeOfImage}");
                    var recorded = FileVersionInfo.GetVersionInfo(executable).FileVersion;
                    if (!string.IsNullOrWhiteSpace(recorded) && !recorded.Any(char.IsControl))
                    {
                        version = recorded;
                    }
                }
            }
            catch (BadImageFormatException) { }
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"manual\n{version}\n{identity}")));
        return new(GameStore.Manual, version, fingerprint, executable);
    }
}
