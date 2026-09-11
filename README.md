# Steam Achievement Manager

Steam Achievement Manager (SAM) is a lightweight, portable application used to manage achievements and statistics in the popular PC gaming platform Steam. This application requires the [Steam client](https://store.steampowered.com/about/), a Steam account and network access. Steam must be running and the user must be logged in.

This is a fork of [gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager). The original closed-source version was released in 2008, saw its last major release in 2011, and was last updated in 2013 (a hotfix). The code was later opened so that those interested can do as they like with it.

[Download latest release](https://github.com/yartat/SteamAchievementManager/releases/latest).

[![Build status](https://ci.appveyor.com/api/projects/status/00vic6jliar6j0ol/branch/master?svg=true)](https://ci.appveyor.com/project/gibbed/steamachievementmanager/branch/master)

## Components

| Component | Description |
|---|---|
| `SAM.Picker.exe` | Lists the games on your account and launches the manager for one of them. Start here. |
| `SAM.Game.exe` | The achievement and statistics editor for a single game. Takes an app ID as its argument; run without one, it re-launches the picker. |
| `SAM.API.dll` | The interop layer that talks to Steam's `steamclient.dll`. |

## Requirements

- Windows
- The [Steam client](https://store.steampowered.com/about/), running and logged in
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — unless you are using a self-contained build (see [Building](#building))

## Versioning

Current version: **7.0.x** (latest tag `7.0.41`). The 7.0 series marks the open-source release.

## Changes since the last closed-source release

- General code maintenance to bring the code into a more modern state.
- Icons have been replaced with ones from the Fugue Icons set.
- Support for the current `UserGameStatsSchema` format, alongside the older one.
- Achievement unlock times are shown in the manager.
- 64-bit support: `steamclient64.dll` is loaded when running as a 64-bit process, and the projects build for `AnyCPU` as well as `x86`.
- **Migrated from .NET Framework 4.8 to .NET 10.** The default `AnyCPU` build now runs as a 64-bit process and talks to the 64-bit Steam client; build the `x86` configuration if you need a 32-bit process.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The exact
version is pinned in `global.json`. No Visual Studio installation is required, though
Visual Studio 2022 17.14 or newer will also open and build the solution.

```
dotnet build SAM.sln -c Release
```

Executables are written to `bin\`. For a 32-bit build:

```
dotnet build SAM.sln -c Release -p:Platform=x86
```

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
