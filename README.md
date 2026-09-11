# Steam Achievement Manager

Steam Achievement Manager (SAM) is a lightweight, portable application used to manage achievements and statistics in the popular PC gaming platform Steam. This application requires the [Steam client](https://store.steampowered.com/about/), a Steam account and network access. Steam must be running and the user must be logged in.

This is a fork of [gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager). The original closed-source version was released in 2008, saw its last major release in 2011, and was last updated in 2013 (a hotfix). The code was later opened so that those interested can do as they like with it.

[Download latest release](https://github.com/yartat/SteamAchievementManager/releases/latest).

[![Build status](https://ci.appveyor.com/api/projects/status/00vic6jliar6j0ol/branch/master?svg=true)](https://ci.appveyor.com/project/gibbed/steamachievementmanager/branch/master)

## Components

| Component | Description |
|---|---|
| `SAM.Picker.exe` | Lists the games on your account and launches the manager for one of them. Start here. The toolbar's view button switches between **Tiles** and **Content**; click it to flip, or use its arrow to pick a mode. |
| `SAM.Game.exe` | The achievement and statistics editor for a single game. Takes an app ID as its argument; run without one, it re-launches the picker. |
| `SAM.API.dll` | The interop layer that talks to Steam's `steamclient.dll`. |

## Requirements

- The [Steam client](https://store.steampowered.com/about/), running and logged in
- The [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — unless you are using a self-contained build (see [Building](#building))

### Linux

In addition to the .NET runtime, the UI needs a few system libraries that a minimal install
may not have:

```
sudo apt-get install libfontconfig1 libice6 libsm6
```

## Platform support

SAM loads Steam's own client library into its process, so the two must share a CPU
architecture. Valve ships that library for x86 and x64 only — there is no ARM build of the
Steam client on any platform.

| Download | Status |
|---|---|
| `win-x64` | Supported and tested |
| `win-x86` | Supported and tested |
| `linux-x64` | Supported and tested (Debian 13) |
| `osx-x64` | Should work; not yet tested |
| `win-arm64`, `linux-arm64`, `osx-arm64` | Built, but **cannot talk to Steam** |

**On an Arm machine, download the x64 build.** Windows on Arm and Apple Silicon's Rosetta 2
will run it, and they are already emulating the Steam client itself. The arm64 bundles are
published only so the set is complete; they start up and tell you this rather than failing
in a confusing way.

## Versioning

Current version: **7.0.x** (latest tag `7.0.41`). The 7.0 series marks the open-source release.

## Changes since the last closed-source release

- General code maintenance to bring the code into a more modern state.
- Icons have been replaced with ones from the Fugue Icons set.
- Support for the current `UserGameStatsSchema` format, alongside the older one.
- Achievement unlock times are shown in the manager.
- 64-bit support: `steamclient64.dll` is loaded when running as a 64-bit process, and the projects build for `AnyCPU` as well as `x86`.
- **Migrated from .NET Framework 4.8 to .NET 10.** The default `AnyCPU` build now runs as a 64-bit process and talks to the 64-bit Steam client; build the `x86` configuration if you need a 32-bit process.
- **Migrated from Windows Forms to [Avalonia](https://avaloniaui.net/).** The interop layer is unchanged; only the UI was rewritten.
- **Two picker view modes.** *Tiles* shows large capsule art with the game name. *Content* shows a row per game with a small icon, the name, release date, when you last played, the Steam review score, earned/total achievements, and your own like/dislike. Sort by any of those from the toolbar or by clicking a column header. This data is read from Steam's local caches, so it is only available for games Steam has already fetched data for; anything else shows `—`.
- **Your own like/dislike** is stored by SAM in `%LOCALAPPDATA%\SteamAchievementManager\ratings.json`. It is *not* your Steam review — Steam does not expose that locally — and nothing is ever sent to Steam.
- Fixed a long-standing bug in the `ISteamClient::GetISteamApps` interop signature, which was missing the `this` pointer. It went unnoticed for years in 32-bit builds but returns a null interface in 64-bit ones.
- **Cross-platform builds.** The interop layer no longer depends on `kernel32` or the Windows registry: it uses `NativeLibrary` and per-OS Steam path discovery, so the projects target plain `net10.0` and publish for Windows, Linux and macOS. See [Platform support](#platform-support) for which targets Steam can actually talk to.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The exact
version is pinned in `global.json`. No Visual Studio installation is required, though
Visual Studio 2022 17.14 or newer will also open and build the solution. NuGet packages
(Avalonia, CommunityToolkit.Mvvm) are restored automatically.

```
dotnet build SAM.sln -c Release
```

Executables are written to `bin\` alongside the Avalonia assemblies and native libraries —
the whole directory is needed to run, not just the executables. For a 32-bit build (output
goes to `bin\x86\`, which must stay separate from the 64-bit output):

```
dotnet build SAM.sln -c Release -p:Platform=x86
```

### Building for another platform

Publish both executables into the same folder — they launch each other by path:

```
dotnet publish SAM.Picker/SAM.Picker.csproj -c Release -r linux-x64 --self-contained false -o publish/linux-x64
dotnet publish SAM.Game/SAM.Game.csproj    -c Release -r linux-x64 --self-contained false -o publish/linux-x64
```

Substitute any of `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`
or `osx-arm64`. Cross-publishing works from any host; you do not need a Linux or Mac to
produce those bundles.

### Self-contained build

A normal build is framework-dependent: it needs the .NET 10 Desktop Runtime installed. To
produce a build that runs on a machine without it — at the cost of roughly 120 MB per
executable — publish self-contained:

```
dotnet publish SAM.Picker/SAM.Picker.csproj -c Release -r win-x64 --self-contained true -o publish
dotnet publish SAM.Game/SAM.Game.csproj -c Release -r win-x64 --self-contained true -o publish
```

## Attribution

Original work by [gibbed](https://github.com/gibbed). Released under the zlib license — see [LICENSE.txt](LICENSE.txt).

Most (if not all) icons are from the [Fugue Icons](https://p.yusukekamiyamane.com/) set.
