<!--suppress HtmlDeprecatedAttribute -->
<h1 align="center">SnowPatcher</h1>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?labelColor=ebdbb2&amp;style=flat&amp;logo=dotnet&amp;logoColor=512BD4"></a>
  <a href="https://avaloniaui.net/"><img alt="Avalonia" src="https://img.shields.io/badge/UI-Avalonia-8F5BFF?labelColor=ebdbb2&amp;style=flat"></a>
  <img alt="Windows and Linux" src="https://img.shields.io/badge/Platforms-Windows%20%7C%20Linux-458588?labelColor=ebdbb2&amp;style=flat">
</p>

<p align="center">A simple SnowRunner mod manager, built with C# and Avalonia.</p>

[Download](https://github.com/WalterWoshid/SnowPatcher/releases/latest) ·
[mod.io](https://mod.io/g/snowrunner/m/snowpatcher-mod-manager)

Extract the Windows ZIP into its own folder and run `SnowPatcher.exe`.
The Windows build is tested; the Linux build is provided without an in-game test.

Detects your Steam installation, checks mod compatibility and backs up game files
before applying changes. Drop in SnowPatcher ZIPs or add mod.io links, enable the
mods you want and click Apply with SnowRunner closed.

Checks for SnowPatcher updates at startup. Click the update button to download and
restart; mods and backups are kept. Version 1.0.0 needs one manual update first.
If a mod needs a newer version, **Update & install** finishes its installation after restarting.

Drop a new ZIP to update a mod; click its version to choose an older copy. Existing
manual changes are preserved when compatible. Legacy manual ZIPs need conversion
before SnowPatcher can apply them.

## Development

Requires the .NET 10 SDK. Open `SnowPatcher.sln` in Rider or run:

```sh
dotnet run --project src/SnowPatcher.App
dotnet format --verify-no-changes
```

Builds enforce the recommended .NET analyzers and code style.
Version tags build desktop releases and upload the Windows ZIP to mod.io.

[Modding tools](docs/MODDING.md) · [Package format](docs/PACKAGES.md) ·
[App behavior](docs/APPLICATION.md) · [mod.io setup](docs/MODIO.md)

## Show your support

Give a ⭐ if this project helped you!
