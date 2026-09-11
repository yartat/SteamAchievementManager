# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

Steam Achievement Manager (SAM) — a portable desktop tool that reads and writes
achievements and stats for games in a Steam library. It works by loading Steam's own
`steamclient.dll` in-process and calling its **C++ COM-like vtable interfaces** directly
through hand-rolled P/Invoke. There is no Steamworks SDK dependency: the entire native
surface is reimplemented in `SAM.API`, which has no package references at all.

The UI is **Avalonia 12** (migrated from Windows Forms). `SAM.API` was not touched by that
migration and stays free of any UI dependency.

This repository is **`yartat/SteamAchievementManager`**, a fork of the original
`gibbed/SteamAchievementManager` (both remotes are configured: `origin` and `upstream`).
Licensed zlib — see `LICENSE.txt`. Attribution to the original author must be preserved
in file headers.

## Projects

| Project | Output | Role |
|---|---|---|
| `SAM.API` | `SAM.API.dll` (class library) | All native interop. Loads `steamclient.dll`, resolves vtables, marshals calls, pumps callbacks. Also owns the Valve KeyValues parsers (`KeyValue` binary, `TextKeyValue` text), which both apps use. |
| `SAM.Picker` | `bin/SAM.Picker.exe` (WinExe) | Entry point. Downloads the app-ID list, filters to games you own, launches `SAM.Game`. Two view modes: tiles and content. |
| `SAM.Game` | `bin/SAM.Game.exe` (WinExe) | Per-game editor. Takes an app ID argv, reads the stats schema, edits achievements/stats. |

`SAM.Game.exe` with no arguments re-launches `SAM.Picker.exe`. Both executables refuse to
run from the Steam install directory (see `Program.cs` in each).

All three target **`net10.0`** — plain, not `net10.0-windows`. The SDK version is pinned in
`global.json`; do not remove it, or builds drift onto whatever preview SDK is installed.

## Platform support

The managed code is portable. What is not portable is **Steam's own client library**, which
SAM loads into its own process — so the process architecture and the library architecture
must match, and Valve only ships x86/x64 builds.

| Target | Steam client library | Works? |
|---|---|---|
| `win-x64` | `steamclient64.dll` | **Verified** |
| `win-x86` | `steamclient.dll` | **Verified** |
| `linux-x64` | `linux64/steamclient.so` | **Builds and runs** (Debian 13, WSL2); Steam interop untested |
| `osx-x64` | `steamclient.dylib` | Builds; **untested** |
| `win-arm64` | none — Valve ships no ARM client | Builds, cannot load Steam |
| `linux-arm64` | none | Builds, cannot load Steam |
| `osx-arm64` | none | Builds, cannot load Steam |

The ARM64 bundles exist so the build matrix is complete, and
`SteamPlatform.IsArchitectureSupported` makes them fail at startup with an explanation
rather than a misleading "Steam is not running". On Arm hardware the **x64** bundle is the
one to run — Windows on Arm and Rosetta 2 emulate it, and they are emulating the Steam
client itself anyway. Do not "fix" the ARM builds by loosening that check; the limitation
is Valve's, not SAM's.

Only the Windows paths have been exercised against a real Steam client. On Linux the build,
the launch and `SteamPlatform`'s discovery logic are verified (Debian 13 under WSL2), but
no `steamclient.so` was ever loaded, so the interop itself is still unproven there. The
macOS lists are written from Steam's documented layouts and have never been run.

### Linux runtime prerequisites

A framework-dependent build needs more than the .NET runtime: Avalonia's Skia and X11
backends dlopen system libraries that a minimal Debian does not ship. Found empirically by
launching the app on a bare Debian 13 and fixing one `DllNotFoundException` at a time:

```bash
apt-get install -y libfontconfig1 libice6 libsm6
```

`libfontconfig1` is needed before `libSkiaSharp` will load at all; `libice6`/`libsm6` are
needed by `Avalonia.X11`'s session management. With those three the app starts cleanly.
Package these as documented dependencies for any Linux release.

## Build

```bash
dotnet build SAM.sln -c Release
```

Executables land in `bin/` (64-bit). `-p:Platform=x86` builds 32-bit into **`bin/x86/`**.

`RuntimeIdentifier` defaults to `$(NETCoreSdkPortableRuntimeIdentifier)` — the *host's* RID
— so a plain build on Linux or macOS produces an apphost for that machine. Do not hardcode
`win-x64` as the default: an earlier revision did, and `dotnet build` on Debian happily
emitted a `SAM.Picker.exe` PE32+ binary with Windows `.dll` natives.

**The two platforms must not share an output directory.** With a pinned RID the native
Skia/HarfBuzz libraries are copied flat next to the executable rather than into
`runtimes/<rid>/native/`, so an x86 build and an x64 build writing to the same folder
silently overwrite each other's natives — and the survivor dies at startup with
`The version of the native libSkiaSharp library (88.1) is incompatible`, which is really an
architecture mismatch wearing a version-number disguise. That is why `OutputPath` is
conditioned on `$(Platform)`.

To produce a bundle for a specific target, publish rather than build — both executables must
land in the same folder because they launch each other by path:

```bash
dotnet publish SAM.Picker/SAM.Picker.csproj -c Release -r linux-x64 --self-contained false -o publish/linux-x64
dotnet publish SAM.Game/SAM.Game.csproj    -c Release -r linux-x64 --self-contained false -o publish/linux-x64
```

A framework-dependent build needs the .NET 10 Desktop Runtime present to run. `bin/` must
therefore ship `.exe`, `.dll`, `.runtimeconfig.json`, `.deps.json` and the native
`libSkiaSharp` / `libHarfBuzzSharp` / `av_libglesv2` DLLs. Shipping only the `.exe` files
produces something that will not start.

**Both exe projects pin a `RuntimeIdentifier`** (`win-x64`, or `win-x86` on the x86
platform) with `AppendRuntimeIdentifierToOutputPath=false`. Do not remove this. Without a
RID, a framework-dependent build copies the native assets for *every* RID that Avalonia and
Skia ship — Linux, musl, macOS, Android, riscv, loongarch — which came to **562 MB** in
`bin/`, made the release artifact unusable, and caused the two projects to race each other
copying identical files into the shared output directory. With the RID pinned it is 128 MB,
of which ~100 MB is native `.pdb` symbols that the CI zip strips (`-x!*.pdb`), leaving a
release around 27 MB. `AppendRuntimeIdentifierToOutputPath=false` matters because the two
executables launch each other by path and must stay in the same flat directory.

There are **no tests** in this repository and no test framework is referenced.

## Running

Requires the Steam client installed, running, and logged in. On Windows SAM reads
`HKLM\Software\Valve\Steam\InstallPath`; elsewhere it walks the candidate directories in
`SteamPlatform.GetUnixInstallCandidates`. Without a live Steam process
`Client.Initialize` throws `ClientInitializeException`. You cannot meaningfully exercise
this code in CI or a sandbox — changes to interop must be tested by hand.

## Architecture

### The interop pattern (`SAM.API`)

This is the part that matters. Everything else is ordinary WinForms.

1. `Steam.Load()` (`SAM.API/Steam.cs`) asks `SteamPlatform` for the install path and the
   candidate library paths for this OS and bitness, then loads the first one that exists
   with `NativeLibrary.TryLoad` and binds three exports via `NativeLibrary.TryGetExport`:
   `CreateInterface`, `Steam_BGetCallback`, `Steam_FreeLastCallback`. There is no
   `kernel32` P/Invoke any more — `NativeLibrary` maps to `LoadLibraryEx`/`dlopen` per OS
   and resolves the library's own dependencies, which is what the old `SetDllDirectory`
   call was doing.

### Bitness

On .NET Framework, `AnyCPU` executables defaulted to `Prefer32Bit`, so SAM always ran
32-bit and always loaded `steamclient.dll`. On .NET 10 that default is gone: `AnyCPU` now
means a 64-bit process loading `steamclient64.dll`. The `Environment.Is64BitProcess` branch
handles it, but it means the default build now talks to a different Steam binary than it
used to. If something works in the `x86` configuration and not in `AnyCPU`, this is why.

Calling conventions are *not* a portability problem here, despite appearances.
`CallingConvention.ThisCall` is only meaningful on **Windows x86**; Microsoft's interop
documentation is explicit that on x64, ARM and ARM64 there is a single calling convention
and the attribute is ignored. Since every wrapper already passes the object pointer as an
explicit first argument, the same declarations are correct on every target.
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

### Three invariants you must not break

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

**3. Every vtable delegate needs `[UnmanagedFunctionPointer(CallingConvention.ThisCall)]`
and an explicit `IntPtr self` first parameter.** These are C++ member functions; the object
pointer is an argument and must be passed as `ObjectAddress`. `GetISteamApps` was missing
both for years. On x86 it went unnoticed, because dropping `this` still left the remaining
arguments at the stack offsets `thiscall` expected. On x64 there is one register-based
convention, so every argument shifted by one, Steam returned a null interface, and
`SetupFunctions` threw a `NullReferenceException` — which is how it finally surfaced, once
the .NET 10 migration made x64 the default. When adding an accessor, copy the shape of
`GetISteamUser`, and test in **both** bitnesses; x86 hides this entire class of bug.

### Callbacks

Steam delivers results by callback, not return value. `Client.RunCallbacks(false)` drains
`Steam_BGetCallback` in a loop and dispatches by numeric `Id` to registered `ICallback`
instances. Both view models poll it from an Avalonia `DispatcherTimer` (`OnTimer`, 200 ms),
so callbacks arrive on the UI thread. `_RunningCallbacks` guards against re-entrancy.
Stopping that timer on window close is what `Shutdown()` is for.

`ManagerViewModel` drives everything from `OnUserStatsReceived`: it loads the schema, then
populates achievements and stats. `RefreshStats` only *requests* — nothing is populated
synchronously.

### The UI layer

Both apps follow the same Avalonia shape, and it is worth knowing before editing either:

- `Program.Main` builds the `AppBuilder` and calls `StartWithClassicDesktopLifetime`.
- `App.OnFrameworkInitializationCompleted` does the Steam handshake and *chooses* the main
  window: the real window on success, or a `MessageWindow` carrying the error on failure.
  This replaces the WinForms pattern of showing a `MessageBox` before `Application.Run`.
  The `API.Client` is owned by `App` and disposed on `ShutdownRequested`.
- `ViewModels/` hold all logic and every Steam call. Views are XAML plus thin code-behind.
- `Views/MessageWindow.cs` is a hand-rolled stand-in for `MessageBox`, which Avalonia has
  no equivalent of. View models cannot show dialogs (no window to parent to), so they raise
  `ErrorRaised` / `MessageRaised` / `ConfirmRequested` and the window handles them.
- Bindings are compiled (`AvaloniaUseCompiledBindingsByDefault`), so every `DataTemplate`
  needs an `x:DataType` and binding typos are build errors rather than silent no-ops.
  **This is not the same as the XAML being verified.** A `Grid.ColumnDefinitions` fed from
  an `x:String` resource compiled cleanly and then threw `InvalidCastException` at window
  construction. A green build does not mean the window opens — run it.
- **Do not bind `ToggleButton.IsChecked` to view-model state.** A `ToggleButton` flips its
  own `IsChecked` on click, which fights the binding and leaves the button showing a state
  that was never saved. The like/dislike buttons are plain `Button`s with
  `Classes.liked="{Binding IsLiked}"` and a style selector doing the colouring, so the view
  model stays the single owner of the value.
- Images are `Avalonia.Media.Imaging.Bitmap`. Unlike `System.Drawing.Bitmap` it copies the
  decoded pixels, so the source stream can be disposed immediately — which is why the old
  "bitmap outlives its MemoryStream" bug does not exist in the ported code.

### Picker view modes and `LibraryStats`

The picker's toolbar has a single `ToggleSplitButton`. Its primary half shows the active
mode's icon and label and flips to the other mode on click (`IsChecked` is bound to
`IsContentView`); its drop-down half is a `MenuFlyout` that selects a mode outright via
`ShowTilesCommand` / `ShowContentCommand`. Tiles is the capsule grid; Content is one row
per game with a small capsule, name, playtime and `earned / total` achievements. Both views
bind the same `FilteredGames` collection; only `IsVisible` differs.

The two mode glyphs are `PathIcon` geometries declared in `Window.Resources`, not bitmaps.
The Fugue set used for the rest of the toolbar has no grid or list glyph, and a `PathIcon`
inherits the theme foreground so it stays legible in dark mode — which the PNG icons do
not. Follow that precedent for any further UI-state icons.

Content columns are: capsule, name, release date, last played, Steam rating, achievements,
and the user's own like/dislike. Headers are buttons that set the sort; clicking the active
field reverses it. The toolbar `SplitButton` does the same and also covers tile view.

Where the numbers come from matters, because the obvious approach does not work:

**`ISteamUserStats` is scoped to the one app id the process was initialised with.** That is
the whole reason SAM launches a separate `SAM.Game` process per game. The picker
(`Initialize(0)`) therefore *cannot* ask Steam for another game's achievements. Do not try
to "just call `RequestUserStats` in a loop" — that is a dead end, not an optimisation
problem.

`SAM.Picker/LibraryStats.cs` reads Steam's own on-disk caches instead:

- **Total achievements** — `appcache/stats/UserGameStatsSchema_<appid>.bin`, the same file
  and parser `ManagerViewModel` already uses.
- **Earned achievements** — `appcache/stats/UserGameStats_<accountid>_<appid>.bin`. Under
  `cache`, each numbered block holds a `data` Int32 **bitfield** plus an
  `AchievementTimes` subkey. Earned is the popcount of `data` masked to the bits the
  schema defines for that block. (Counting `AchievementTimes` entries gives the same
  answer, but the bitfield is the authoritative field.)
- **Playtime and last played** — `userdata/<accountid>/config/localconfig.vdf`, at
  `UserLocalConfigStore/Software/Valve/Steam/apps/<appid>/`, as `Playtime` (in **minutes**)
  and `LastPlayed` (unix seconds).
- **Steam rating and release date** — `SteamApps001.GetAppData`, keys `review_percentage`
  (percent positive), `review_score` (Steam's 1-9 band, used for the tooltip wording) and
  `steam_release_date` (unix seconds). Measured coverage across this library: review data
  ~98%, release date ~91%. These are Steam calls, so they run on the background pass with
  the file reads, not on the UI thread.
- **The user's own like/dislike is *not* from Steam.** Steam keeps review recommendations
  server-side and exposes them only through the Web API, which needs a key SAM does not
  have. `RatingStore` persists SAM's own value to
  `%LOCALAPPDATA%\SteamAchievementManager\ratings.json`. Nothing is ever sent to Steam.
  Do not relabel this as "Steam rating" in the UI — it would be a lie to the user.
  Caveat: the whole file is rewritten on each change, so two picker instances running at
  once will clobber each other's ratings. Acceptable for a single-instance desktop tool;
  worth knowing if that ever changes.

Two things to keep in mind when touching this:

- The file names use the **32-bit account id** (`steamId & 0xFFFFFFFF`), not the 64-bit
  SteamID.
- Coverage is partial. Steam only writes these caches for games it has actually fetched or
  run, so `TryGet` returns `null` for the rest and the UI shows `—`. **Unknown and zero are
  different answers** — do not collapse them.

All of this was validated against the live API (`RequestUserStats` +
`GetAchievementAndUnlockTime`) for several games and matched exactly, including
Civilization V at 64/286. If you change the parsing, re-validate the same way rather than
eyeballing it; a plausible-looking wrong number here is worse than no number.

### The stats schema

`ManagerViewModel.LoadUserGameStatsSchema` parses
`<SteamInstall>/appcache/stats/UserGameStatsSchema_<appid>.bin`, a Valve binary KeyValues
blob, with the hand-written parser in `SAM.Game/KeyValue.cs`. It handles **two schema
shapes**: a newer one where `stat.type` is a string enum name, and an older one where
`stat.type_int` (or `type`) is an integer. Keep both paths — recent upstream commits
(`4f2b037`, `de8b710`) exist specifically to fix regressions here.

### Threading

Downloads are `async`/`HttpClient` on the UI thread's synchronization context, so
continuations come back on the UI thread and no marshalling is needed — the
`BackgroundWorker` + `Invoke` dance the WinForms build required is gone.

The one deliberate exception is the ownership sweep in
`GamePickerViewModel.LoadGamesAsync`, which is wrapped in `Task.Run`. It makes two Steam
calls per app id across the whole published game list, so it cannot run on the UI thread.
The WinForms build ran it on a `BackgroundWorker` for exactly the same reason. Note this
means **Steam client calls do happen off the UI thread there** — that is pre-existing
behaviour, preserved deliberately, not an invitation to add more.

Logo and icon downloads use a single-consumer queue (`ConcurrentQueue` + `SemaphoreSlim`),
one download at a time, matching the old single-`BackgroundWorker` behaviour.

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

One trap: `using static InvariantShorthand` puts a method named `_` in scope, so `_` is
**not** available as a discard in files that import it. Name the variable instead.

View models use CommunityToolkit.Mvvm. `[ObservableProperty]` on a `_PascalCase` field
generates the matching `PascalCase` property, so the source generator and the existing
field-naming convention agree. Properties whose setter needs to trigger a re-filter are
written out by hand rather than generated.

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
verified against a live, logged-in Steam client:

| Step | Result |
|---|---|
| `Steam.GetInstallPath()` from the registry | OK (identical in the 32- and 64-bit registry views) |
| `Steam.Load()` — `LoadLibraryEx` + 3 exports | OK — `steamclient64.dll` on x64, `steamclient.dll` on x86 |
| `CreateInterface("SteamClient018")` + vtable marshal | OK in both bitnesses |
| `CreateSteamPipe()` / `ConnectToGlobalUser()` | OK |
| `GetSteamApps001().GetAppData(480, "name")` | OK — returns `Spacewar` |
| `GetSteamApps008().IsSubscribedApp(480)` | OK |
| `GetSteamUser012().IsLoggedIn()` | OK |
| Schema load + achievement/stat read (Terraria, 105600) | OK — 137 achievements, 10 statistics |

Getting there required fixing the `GetISteamApps` signature described in invariant #3.
What remains unverified is the **write** path — `SetAchievement`, `SetStatValue`,
`StoreStats` and `ResetAllStats`. Those mutate a real Steam account, so they were left
alone deliberately; exercise them by hand on a throwaway title before trusting a release.

If the interop ever does misbehave, the modern replacement is C# function pointers
(`delegate* unmanaged[Thiscall]<...>`), which Microsoft documents as "more efficient,
easier to use correctly, and supported in all environments," and which also drops the
per-call boxing that `DynamicInvoke` costs.

Do not pursue trimming or NativeAOT: WinForms plus this much reflection-based marshalling
is not a candidate, and `Marshal.GetDelegateForFunctionPointer` is annotated
`RequiresDynamicCode`.

## Known issues

A clean build is **0 warnings, 0 errors** on both `AnyCPU` and `x86`. Keep it that way.

- **`SAM.API/Wrappers/SteamClient018.cs`** — `GetSteamUtils004` requests `"SteamUtils004"`
  but still marshals the result as `ISteamUtils005`. Upstream requests `"SteamUtils005"`.
  This bends invariant #2 and predates both migrations. It does not crash — the interface
  comes back non-null and `GetAppId()` returns — so it has been left alone, but it is
  still worth confirming as intentional.
- **The logo queue is eager.** The WinForms build only downloaded capsule art for items
  whose `ListViewItem.Bounds` intersected the visible list, using WinForms' virtual mode.
  The Avalonia `ListBox` uses a `WrapPanel`, which does not virtualize, so every filtered
  game's logo is queued instead. That is bounded by games *owned*, not by the full
  published list, so it is a few hundred at worst — but it is more network traffic than
  before. Moving to `ItemsRepeater` + `UniformGridLayout` would restore
  virtualization and visible-only loading, at the cost of hand-rolled selection handling.
- **Write path is unverified** — see the interop table above.
- **The picker toolbar wraps rather than clips.** It is a `WrapPanel`, not a `StackPanel`:
  when the view-mode control was added, a single fixed row ran off the right edge on a
  narrow window and the last button became unreachable. If you add more toolbar items,
  check the window at its `MinWidth`.

## Skills index

Prefer retrieval-led reasoning over recall for .NET work here: consult the skill by name
before implementing, then make the smallest change that fits the surrounding style.

**Routing**

- Interop, vtables, marshalling, calling conventions → `dotnet-skills:ilspy-decompile`
  (to inspect what Steam or a .NET assembly actually does), plus the Microsoft Learn
  native-interop docs. This is the core of the codebase; verify against docs, never guess.
- C# style and language level → `dotnet-skills:csharp-coding-standards`
- Threading, async download queues, UI-thread marshalling, callback pumping →
  `dotnet-skills:csharp-concurrency-patterns`
- Avalonia, XAML, MVVM, compiled bindings, `DataGrid`, styling → no packaged skill covers
  this; use the Avalonia docs at <https://docs.avaloniaui.net>. Do not reach for the
  WinForms or WPF skills, and do not assume WPF XAML translates one-to-one — Avalonia's
  styling system is selector-based, not `Trigger`-based.
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
relevant. `playwright-blazor` in particular is not a way to test this UI — it is a desktop
app, not a web one.

## CodeGraph

This repository is indexed (`.codegraph/`). Reach for it **before** grep or reading files:

```bash
codegraph explore "<symbol names or question>"
```

The MCP tool `codegraph_explore` does the same in one call and returns verbatim
line-numbered source plus call paths and blast radius. It is the cheapest way to answer
"what calls this" — which matters here, because a change to one vtable struct can reach
every wrapper. Re-run `codegraph init` after large refactors to refresh the index.
