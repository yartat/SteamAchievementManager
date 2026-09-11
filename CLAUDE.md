# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

Steam Achievement Manager (SAM) — a portable Windows Forms tool that reads and writes
achievements and stats for games in a Steam library. It works by loading Steam's own
`steamclient.dll` in-process and calling its **C++ COM-like vtable interfaces** directly
through hand-rolled P/Invoke. There is no Steamworks SDK dependency and no NuGet package
of any kind: the entire native surface is reimplemented in `SAM.API`.

This repository is **`yartat/SteamAchievementManager`**, a fork of the original
`gibbed/SteamAchievementManager` (both remotes are configured: `origin` and `upstream`).
Licensed zlib — see `LICENSE.txt`. Attribution to the original author must be preserved
in file headers.

## Projects

| Project | Output | Role |
|---|---|---|
| `SAM.API` | `SAM.API.dll` (class library) | All native interop. Loads `steamclient.dll`, resolves vtables, marshals calls, pumps callbacks. |
| `SAM.Picker` | `bin/SAM.Picker.exe` (WinExe) | Entry point. Downloads the app-ID list, filters to games you own, launches `SAM.Game`. |
| `SAM.Game` | `bin/SAM.Game.exe` (WinExe) | Per-game editor. Takes an app ID argv, reads the stats schema, edits achievements/stats. |

`SAM.Game.exe` with no arguments re-launches `SAM.Picker.exe`. Both executables refuse to
run from the Steam install directory (see `Program.cs` in each).

All three target **`net10.0-windows`**, `Platforms=x86;AnyCPU`. The SDK version is pinned
in `global.json` — do not remove it, or builds drift onto whatever preview SDK is
installed.

## Build

```bash
dotnet build SAM.sln -c Release
```

Executables land in `bin/`, for both Debug and Release. The `x86` platform
(`-p:Platform=x86`) sets `RuntimeIdentifier=win-x86` and produces a genuine 32-bit
apphost; the default `AnyCPU` build produces a 64-bit one. That distinction is load-bearing
— see [Bitness](#bitness).

A framework-dependent build needs the .NET 10 Desktop Runtime present to run. `bin/` must
therefore ship `.exe`, `.dll`, `.runtimeconfig.json` **and** `.deps.json`; dropping the
last two produces an executable that will not start.

There are **no tests** in this repository and no test framework is referenced.

## Running

Requires the Steam client installed, running, and logged in. SAM reads
`HKLM\Software\Valve\Steam\InstallPath` to find `steamclient.dll`; without a live Steam
process `Client.Initialize` throws `ClientInitializeException`. You cannot meaningfully
exercise this code in CI or a sandbox — changes to interop must be tested by hand.

## Architecture

### The interop pattern (`SAM.API`)

This is the part that matters. Everything else is ordinary WinForms.

1. `Steam.Load()` (`SAM.API/Steam.cs`) resolves the Steam install path from the registry,
   calls `SetDllDirectory`, then `LoadLibraryEx`s `steamclient64.dll` or `steamclient.dll`
   depending on `Environment.Is64BitProcess`. It then binds three exports:
   `CreateInterface`, `Steam_BGetCallback`, `Steam_FreeLastCallback`.

### Bitness

On .NET Framework, `AnyCPU` executables defaulted to `Prefer32Bit`, so SAM always ran
32-bit and always loaded `steamclient.dll`. On .NET 10 that default is gone: `AnyCPU` now
means a 64-bit process loading `steamclient64.dll`. The `Environment.Is64BitProcess` branch
handles it, but it means the default build now talks to a different Steam binary than it
used to. If something works in the `x86` configuration and not in `AnyCPU`, this is why.
2. `Steam.CreateInterface<T>("SteamClient018")` returns a raw `IntPtr` to a C++ object.
3. `NativeWrapper<TFunctions>.SetupFunctions` (`SAM.API/NativeWrapper.cs`) treats that
   pointer as a `NativeClass` (one field: `VirtualTable`), then
   `Marshal.PtrToStructure`s the vtable into a `struct` of `IntPtr` fields — one field per
   virtual method, **in declaration order**.
4. Calls go through `Call<TReturn, TDelegate>(Functions.SomeMethod, ...)`, which
   `Marshal.GetDelegateForFunctionPointer`s the slot (cached in `_FunctionCache`) and
   `DynamicInvoke`s it. Instance methods use
   `[UnmanagedFunctionPointer(CallingConvention.ThisCall)]` with the object pointer passed
   explicitly as the first argument.

### Two invariants you must not break

**1. Vtable struct field order is an ABI contract.** The structs in `SAM.API/Interfaces/`
(`ISteamClient018`, `ISteamUserStats013`, `ISteamUtils005`, …) mirror the exact layout of
Valve's C++ interfaces. Every field is `IntPtr` and the *order* is the only thing carrying
meaning. Inserting, removing, or reordering a field silently calls the wrong function —
usually a crash, sometimes corruption. Never reorder these. Deprecated slots are kept as
`DEPRECATED_*` placeholders precisely for this reason.

**2. Interface version strings must match the struct they are marshalled into.** The
string passed to `CreateInterface` / `GetISteam*` selects which vtable Steam hands back.
Requesting one version and interpreting it as another is the same bug as reordering
fields. Keep `GetSteamUserStats013` ↔ `"STEAMUSERSTATS_INTERFACE_VERSION013"`,
`GetSteamApps001` ↔ `"STEAMAPPS_INTERFACE_VERSION001"`, and so on.

### Callbacks

Steam delivers results by callback, not return value. `Client.RunCallbacks(false)` drains
`Steam_BGetCallback` in a loop and dispatches by numeric `Id` to registered `ICallback`
instances. Both forms poll it from a WinForms `Timer` (`OnTimer`), so callbacks arrive on
the UI thread. `_RunningCallbacks` guards against re-entrancy.

`SAM.Game/Manager.cs` drives everything from `OnUserStatsReceived`: it loads the schema,
then populates achievements and stats. `RefreshStats` only *requests* — nothing is
populated synchronously.

### The stats schema

`SAM.Game/Manager.cs:LoadUserGameStatsSchema` parses
`<SteamInstall>/appcache/stats/UserGameStatsSchema_<appid>.bin`, a Valve binary KeyValues
blob, with the hand-written parser in `SAM.Game/KeyValue.cs`. It handles **two schema
shapes**: a newer one where `stat.type` is a string enum name, and an older one where
`stat.type_int` (or `type`) is an integer. Keep both paths — recent upstream commits
(`4f2b037`, `de8b710`) exist specifically to fix regressions here.

### Threading

`SAM.Picker` uses `BackgroundWorker` for the game-list and logo downloads. WinForms
controls may only be touched on the UI thread — `GamePicker.ChangePickerLabelText`
exists to marshal via `Invoke`. Anything running inside a `DoWork` handler must go
through it.

## Conventions

Match the surrounding style; it is consistent and deliberate even where it is unfashionable.

- Explicit `this.` on instance members.
- Explicit comparisons: `if (x == false)`, `if (s == true)` — not `if (!x)`.
- Private fields are `_PascalCase`.
- Target-typed `new()` is used freely (`List<T> foo = new();`).
- `#region` blocks wrap each native method group in the wrappers.
- The zlib copyright header goes at the top of every source file.
- `_($"...")` (`InvariantShorthand.cs`) is the invariant-culture interpolation shorthand.
- Suppressions live in per-project `GlobalSuppressions.cs`, not inline.

`LangVersion` is `latest` in all three projects.

## .NET Core semantics that bit this codebase

The `net48` → `net10.0-windows` migration changed four behaviours silently — no compiler
error, just different runtime results. They are fixed; the notes are here so they are not
reintroduced.

- **`Application.StartupPath` now ends with a directory separator** (verified on .NET 10);
  on .NET Framework it did not. Both `Program.cs` files compared it directly against the
  registry's `InstallPath` to refuse to run from the Steam directory, so that guard
  silently became dead. Replaced with `IsRunningFromSteamDirectory()`, which normalises
  both sides through `Path.GetFullPath` + `Path.TrimEndingDirectorySeparator` and compares
  `OrdinalIgnoreCase`. Do not reintroduce a raw `==` on paths.
- **`Process.Start` defaults to `UseShellExecute=false`** on .NET Core, so a bare
  `"SAM.Picker.exe"` resolves against the *current working directory*, not the app
  directory. Both launch sites now pass `Path.Combine(AppContext.BaseDirectory, …)`.
- **`IntPtr.ToInt32()` overflows on 64-bit pointers.** `NativeWrapper.ToString()` used it;
  harmless while everything was 32-bit, live now that `AnyCPU` means x64. Uses `ToInt64()`.
- **High-DPI settings moved out of the manifest.** `dpiAware` in `app.manifest` now warns
  (`WFO0003`); the setting lives in `<ApplicationHighDpiMode>SystemAware</ApplicationHighDpiMode>`
  in both exe projects. `SystemAware` preserves the previous behaviour — do not "upgrade"
  it to `PerMonitorV2` without checking the fixed-size form layouts.

The interop layer itself was **not** rewritten.
`[UnmanagedFunctionPointer(CallingConvention.ThisCall)]` plus `Delegate.DynamicInvoke` over
raw vtable slots still compiles and is still the mechanism — and it **works on .NET 10**,
verified against the real `steamclient.dll` in both bitnesses:

| Step | x64 | x86 |
|---|---|---|
| `Steam.GetInstallPath()` from the registry | OK | OK |
| `Steam.Load()` — `LoadLibraryEx` + 3 exports | OK (`steamclient64.dll`) | OK (`steamclient.dll`) |
| `CreateInterface("SteamClient018")` + vtable marshal | OK | OK |
| `CreateSteamPipe()` — real `thiscall` vtable dispatch | OK | OK |

So the calling convention, the `NativeClass`/vtable `PtrToStructure` marshalling and the
delegate cache are all sound on .NET 10. What is **still unverified** is everything past
`CreateSteamPipe`, because that needs a Steam client that is running and logged in:
`ConnectToGlobalUser`, the `GetISteam*` accessors, the callback pump, schema loading, and
achievement/stat writes. Those are the interfaces where a wrong version string or a
misordered vtable struct would show up — exercise them against a live client before
trusting a release.

If the interop ever does misbehave, the modern replacement is C# function pointers
(`delegate* unmanaged[Thiscall]<...>`), which Microsoft documents as "more efficient,
easier to use correctly, and supported in all environments," and which also drops the
per-call boxing that `DynamicInvoke` costs.

Do not pursue trimming or NativeAOT: WinForms plus this much reflection-based marshalling
is not a candidate, and `Marshal.GetDelegateForFunctionPointer` is annotated
`RequiresDynamicCode`.

## Known issues

- **`SAM.API/Wrappers/SteamClient018.cs`** — `GetSteamUtils004` requests `"SteamUtils004"`
  but still marshals the result as `ISteamUtils005`. Upstream requests `"SteamUtils005"`.
  This violates invariant #2 above and predates the .NET 10 migration; confirm it is
  intentional.
- **`SAM.Picker/GamePicker.cs`** — in `DoDownloadList`, `_PickerStatusLabel.Text =
  "Checking game ownership..."` runs on the `BackgroundWorker` thread. The
  `ChangePickerLabelText` helper above it was applied to the first assignment only.
- **`WebClient` is obsolete** (`SYSLIB0014`) at `SAM.Game/Manager.cs:42` and
  `SAM.Picker/GamePicker.cs:116` and `:270`. It still works on .NET 10. The two
  `GamePicker` uses are synchronous calls inside `DoWork` and convert to `HttpClient`
  easily; `Manager._IconDownloader` is event-based (`DownloadDataAsync` /
  `DownloadDataCompleted`, with the icon queue keyed off `IsBusy` and `UserState`) and
  needs more care.
- **`SAM.Game/Manager.cs:219`** — `warning CS0168`, `e` declared but never used.
- **Pre-existing, unrelated to the migration:** `OnIconDownload` and `DoDownloadLogo` both
  construct a `Bitmap` from a `MemoryStream` inside a `using` and use it after the stream
  is disposed. GDI+ wants that stream alive for the bitmap's lifetime.

These four warnings are the entire build output; a clean build is 4 warnings, 0 errors.

## Skills index

Prefer retrieval-led reasoning over recall for .NET work here: consult the skill by name
before implementing, then make the smallest change that fits the surrounding style.

**Routing**

- Interop, vtables, marshalling, calling conventions → `dotnet-skills:ilspy-decompile`
  (to inspect what Steam or a .NET assembly actually does), plus the Microsoft Learn
  native-interop docs. This is the core of the codebase; verify against docs, never guess.
- C# style and language level → `dotnet-skills:csharp-coding-standards`
- Threading, `BackgroundWorker`, UI-thread marshalling, callback pumping →
  `dotnet-skills:csharp-concurrency-patterns`
- TFM changes, `Directory.Build.props`, `global.json`, `.slnx`, version management →
  `dotnet-skills:project-structure`
- Replacing the obsolete `WebClient` usages with `HttpClient` →
  `dotnet-skills:csharp-concurrency-patterns` (the icon downloader is event-based; the
  conversion is an async-pattern change, not a find-and-replace)
- Adding any package, or introducing Central Package Management →
  `dotnet-skills:package-management` (use `dotnet add`/`remove`, never hand-edit the XML)
- Public surface of `SAM.API` if it is ever versioned → `dotnet-skills:csharp-api-design`
- Enabling nullable reference types → `dotnet-skills:csharp-nullable-reference-types`
- Struct layout and allocation in the hot marshalling path →
  `dotnet-skills:csharp-type-design-performance`
- Replacing the KeyValues parser or adding any serialization →
  `dotnet-skills:serialization`

**Quality gates**

- `dotnet-skills:slopwatch` — after substantial new or refactored code, to catch
  suppressed warnings, disabled checks, and empty catch blocks. Note this repo already has
  a legitimate `GlobalSuppressions.cs` convention; judge additions against it.
- `dotnet-skills:crap-analysis` — only meaningful once tests exist. There are none today.

**Specialist agents**

- `dotnet-skills:dotnet-concurrency-specialist` — race conditions in the download workers
  or callback pump.
- `dotnet-skills:dotnet-performance-analyst` — if the `DynamicInvoke` path is ever profiled.
- `voltagent-lang:dotnet-core-expert` — .NET 10 runtime, SDK, publish and deployment questions.
- `voltagent-lang:dotnet-framework-4.8-expert` — only for archaeology on pre-migration
  behaviour; the projects no longer target `net48`.

**Not applicable here** — this project has no web, database, DI container, configuration
system, cloud, or container surface. Skip the Aspire, EF Core, ASP.NET, Akka, Blazor,
TestContainers, OpenTelemetry, and email skills; nothing in this repository will make them
relevant.

## CodeGraph

This repository is indexed (`.codegraph/`). Reach for it **before** grep or reading files:

```bash
codegraph explore "<symbol names or question>"
```

The MCP tool `codegraph_explore` does the same in one call and returns verbatim
line-numbered source plus call paths and blast radius. It is the cheapest way to answer
"what calls this" — which matters here, because a change to one vtable struct can reach
every wrapper. Re-run `codegraph init` after large refactors to refresh the index.
