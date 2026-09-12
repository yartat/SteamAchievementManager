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

`Shared/` is not a project. It is a handful of files, namespace `SAM.Shared`, linked
into **both** executables with `<Compile Include="..\Shared\*.cs">` (and
`<AvaloniaResource Include="..\Shared\Icons.axaml" Link="Resources\Icons.axaml" />`,
which is what makes the avares path identical in both assemblies):

| File | Why it is shared |
|---|---|
| `AppSettings.cs` | both processes read the same `~/.sam/settings.json` |
| `IconCache.cs` | both write into the same icon directory |
| `ProgressViewModel.cs` | one status-bar progress indicator, behaving the same in both windows |
| `Icons.axaml` | one icon set and one style sizing it |
| `Theme.axaml` | the Library palette, in a Light and a Dark form |

They are linked rather than moved into `SAM.API` because that project stays
package-free and SQLite is a package. Both assemblies get their own copy of the
types, which is harmless — they never meet in one process.

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
| `linux-x64` | `linux64/steamclient.so` | **Verified** against live Steam (Debian 13, WSL2) |
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

Windows and Linux have both been exercised end to end against a live, logged-in Steam
client — native build, install-path discovery, `steamclient.so`/`.dll` load, vtable
dispatch, `LibraryStats`, and the picker showing the real library. Playtime read on Linux
matched Windows to the minute for every game checked. The macOS candidate lists are
written from Steam's documented layouts and have never been run.

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

There are **no tests** in this repository and no test framework is referenced. That does
not mean nothing can be checked without Steam — see "Verifying without Steam" under
[Running](#running).

## Running

Requires the Steam client installed, running, and logged in. On Windows SAM reads
`HKLM\Software\Valve\Steam\InstallPath`; elsewhere it walks the candidate directories in
`SteamPlatform.GetUnixInstallCandidates`. Without a live Steam process
`Client.Initialize` throws `ClientInitializeException`. You cannot meaningfully exercise
this code in CI or a sandbox — changes to interop must be tested by hand.

### Verifying without Steam

Plenty of this codebase *can* be exercised on a machine with no Steam at all, and should be
before anything is called done. There are no tests in the repository, so this means a
throwaway console project that links the files under test:

- **The SQLite layer, `IconCache`, `RatingStore`, `ProgressViewModel`** — plain classes.
  Link the `.cs` files into a scratch project and drive them. `AppSettings.HomeDirectory`
  is a static path with no seam, so anything touching it runs against the real `~/.sam`:
  check the directory does not already exist and restore the machine afterwards, or do not
  run it at all.
- **Windows, resources and styles** — `Avalonia.Headless` plus `Avalonia.Skia`, with
  `UseHeadlessDrawing = false`. `AppBuilder.Configure<App>().UseSkia().UseHeadless(...)
  .SetupWithoutStarting()` runs `App.Initialize()` without a lifetime, so
  `OnFrameworkInitializationCompleted` skips the Steam handshake. A window can then be
  constructed, measured, arranged and rendered with `CaptureRenderedFrame()`. This catches
  everything in the right-hand column of the table under
  [The UI layer](#the-ui-layer), and the PNG it hands back can be looked at.
- **Icon geometries** — render them and measure the ink. Do **not** use `Geometry.Bounds`
  for this: for an arc it reports the endpoint extents rather than the swept extents, so
  `RefreshIcon` measures 8.2 wide on a 16 grid and renders 15.

What this cannot reach is anything behind `API.Client`: the vtable dispatch, the schema
parse, `LibraryStats`, and every number the windows actually display.

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

`CallingConvention.ThisCall` itself is not a portability problem.
`ThisCall` is only meaningful on **Windows x86**; on x64, ARM and ARM64 there is a single
calling convention and the attribute is ignored. Since every wrapper passes the object
pointer as an explicit first argument, those declarations are correct on every target.

**Struct returns are a different story — see invariant #4.** "One calling convention on
x64" covers how *arguments* are passed; it does not cover how a C++ member function
*returns a struct by value*, and Windows and System V disagree there.
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

### Four invariants you must not break

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

**4. A C++ method that returns a struct *by value* needs a per-platform declaration.**
Windows and System V AMD64 disagree about how that works, and the difference is silent:

- **MSVC x86/x64** returns it through a hidden pointer passed as an extra argument, so the
  delegate is `void Native(IntPtr self, out T value)`.
- **System V AMD64** (Linux, macOS) returns a small trivially-copyable struct in RAX, so
  the delegate is `T Native(IntPtr self)`.

`ISteamUser::GetSteamID()` returns `CSteamID`, a 64-bit POD, and is the one place this
arises today. With the Windows form used on Linux it does not throw — it reads a slot
nobody wrote and returns **0**, which then silently poisons everything keyed off the
account id (`LibraryStats`' playtime and achievement lookups, and
`RequestUserStats(steamId)` in `SAM.Game`). `SteamUser012.GetSteamId` therefore branches on
`OperatingSystem.IsWindows()` and caches its delegate locally, because
`NativeWrapper`'s cache is keyed on the function pointer alone and two delegate types
sharing one vtable slot would collide.

An audit of the other `out`/`ref` delegates found no further cases: the parameters in
`CreateLocalUser`, `GetStat`, and `GetImageSize` are genuine C++ pointer arguments, not
hidden struct returns. If you add a wrapper for a method whose C++ signature returns a
`struct`/`class` by value, handle both ABIs and verify on both.

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
  **That is the only part of the XAML the build checks.** Know which side of the line you
  are on:

  | Caught at build | Only caught when the window opens |
  |---|---|
  | `{Binding Foo}` where `Foo` is not on the `x:DataType` | `{StaticResource Foo}` where `Foo` does not exist |
  | `{CompiledBinding}` paths | `<StyleInclude Source="...">` pointing nowhere |
  | unknown control or property names | a property fed a resource of the wrong type |

  Two of those three have already bitten this repository: a `Grid.ColumnDefinitions` fed
  from an `x:String` resource compiled cleanly and threw `InvalidCastException` at
  construction, and renaming a key in `Shared/Icons.axaml` still builds with zero warnings.
  **A green build does not mean the window opens** — see
  [Verifying without Steam](#verifying-without-steam) for how to check without a Steam
  client.
- **Do not bind `ToggleButton.IsChecked` to view-model state.** A `ToggleButton` flips its
  own `IsChecked` on click, which fights the binding and leaves the button showing a state
  that was never saved. The like/dislike buttons are plain `Button`s with
  `Classes.liked="{Binding IsLiked}"` and a style selector doing the colouring, so the view
  model stays the single owner of the value.
- Images are `Avalonia.Media.Imaging.Bitmap`. Unlike `System.Drawing.Bitmap` it copies the
  decoded pixels, so the source stream can be disposed immediately — which is why the old
  "bitmap outlives its MemoryStream" bug does not exist in the ported code.

### The Library layout

Both windows follow the Library design (`SAM 8.0 Library`). The organising idea is
**completion**: how far through a game's achievements you are is the thing the picker is
built to show, not a column you can sort by if you think to.

**The picker** is a left rail plus a main area. The rail carries two kinds of thing and
they must not be made to look alike:

- **Collections** (`GameCollection`) are mutually exclusive — All games, Recently played,
  Has achievements, Perfect games, Liked. They are buttons with a `Classes.on` binding.
- **Also show** are the pre-existing `ShowGames` / `ShowDemos` / `ShowMods` / `ShowJunk`
  toggles, which are inclusive and combine. They are `CheckBox`es, which is the whole
  reason they look different.

Both narrow the same pass in `RefreshGames`, collection first, then type.

Rail counts are computed over the **whole library**, not the filtered view, so a count
never changes just because another collection is open. `RefreshCollectionCounts` has to be
called after anything that moves the library or its stats — the cached load, the ownership
merge, the stats pass, and a rating change. Completion only becomes known in the stats
pass, so that one also re-runs `RefreshGames` when a collection or sort depends on it.

The tile grid carries the meter. **The meter is absent, not empty, when Steam has never
cached a schema** (`HasCompletion`): an empty track would read as "none earned", and
unknown and zero are different answers. Both views still bind the same `FilteredGames`;
only `IsVisible` differs.

**The editor** is a game header, filter chips, a list, and a detail pane. The detail pane
exists because the description had nowhere to go in the old three-column grid.

Content columns are: capsule, name, release date, last played, Steam rating, achievements
(count plus meter), and the user's own like/dislike. Headers are buttons that set the sort.

### The palette

`Shared/Theme.axaml` is a `ResourceDictionary` with `ThemeDictionaries` for Light and Dark,
merged into each app's `Application.Resources`. **Every `Sam*` brush must be referenced with
`{DynamicResource}`.** The app is `RequestedThemeVariant="Default"`, so it follows the
system and the variant can change while a window is open; a `{StaticResource}` resolves
once and then lies.

Gold is the accent because the subject is achievements, and it is deliberately **not the
same value in both variants**: `#DDA63A` reads well on near-black and manages about 2:1 on
white, so the light variant darkens it to `#8A6612`. Do not "unify" them.

`RatingLikeBrush` and `RatingDislikeBrush` sit outside the theme dictionaries on purpose —
they are the app's existing semantic tokens and mean the same thing in either variant.

### Icons

Every icon in both applications is a `StreamGeometry` in `Shared/Icons.axaml`, rendered
through a `PathIcon`. There are no icon bitmaps left; the Fugue PNGs the toolbars used to
carry were removed because a `PathIcon` inherits the theme foreground and stays legible in
dark mode, which they did not, and because two icon mechanisms cannot be sized or coloured
from one place. **Do not put an `<Image>` in a button.**

Two things about that file are load-bearing:

- **One grid.** Every geometry is drawn on a 16x16 grid with its content spanning roughly
  1..15. `PathIcon` stretches the geometry's *own bounds* uniformly into `Width` x
  `Height`, so a glyph drawn on a different grid is silently scaled to a different weight
  beside its neighbours. `Geometry.Bounds` will not tell you when this has happened —
  for an arc it reports the endpoint extents, not the swept extents, so `RefreshIcon`
  measures 8.2 wide and renders 15. Measure the rendered pixels instead.
- **One style.** `Shared/Icons.axaml` also carries the bare `PathIcon` selector that sizes
  everything to 14x14, plus `PathIcon.small` at 10 for a glyph that qualifies another
  rather than standing alone. Add a class there rather than a `Width`/`Height` on the
  usage, or the toolbars drift apart one button at a time.

It is included by each app as `<StyleInclude Source="/Resources/Icons.axaml" />`. That
path resolves at **runtime**, not build time: renaming a key or mistyping the source still
compiles cleanly and only fails when the window opens.

### The status bar

Both windows derive their view model from `Shared/ProgressViewModel.cs` and show the same
indicator: an icon, a line of text and a `ProgressBar`.

Phases with a known total count out of it; those without — the game-list download, and
`SAM.Game` waiting on the `RequestUserStats` callback — show the running bar instead.
Loops that run off the UI thread report through an `IProgress<int>` **created on the UI
thread**, which is what marshals the callback back; `ProgressStep` throttles them to about
200 updates so a sweep over tens of thousands of published app ids does not post a message
per item.

`BeginProgress` and `EndProgress` count jobs rather than flipping a flag, and this matters:
the picker's startup runs the cached-icon decode, the library refresh and the logo queue at
once, all reporting into the one indicator. With a flag, the first to finish would take the
bar down while the other two were still working. Every `BeginProgress` needs a matching
`EndProgress` on every path, failures included.

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
  have. `RatingStore` persists SAM's own value to `~/.sam/ratings.json`. Nothing is ever
  sent to Steam.
  Do not relabel this as "Steam rating" in the UI — it would be a lie to the user.
  Caveat: the whole file is rewritten on each change, so two picker instances running at
  once will clobber each other's ratings. Acceptable for a single-instance desktop tool;
  worth knowing if that ever changes.
  Ratings used to live under %LOCALAPPDATA%\SteamAchievementManager\.
  `RatingStore.Migrate` moves that file across on first run and is the only thing that
  looks there; if the move fails it reads them where they are and writes to the new path
  next time, so a locked or read-only profile loses nothing. Keep it until it is safe to
  assume nobody is upgrading from a pre-`~/.sam` build.

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

### The cache (`~/.sam`)

Neither window starts empty. One SQLite database and one icon directory serve both
applications; both default under `~/.sam` and are relocatable from the Settings dialog.

| Store | Owner | Holds |
|---|---|---|
| `games` table | `SAM.Picker/GameCache.cs` | the owned game list and its stats |
| `achievements` table | `SAM.Game/AchievementCache.cs` | one game's achievement list and its unlock state |
| `<appid>.img` | `Shared/IconCache.cs` | capsule art, one file per app id |
| `<appid>_<icon>` | `Shared/IconCache.cs` | achievement icons, one file per app id and icon name |

The two processes hold the same database file open at once. That works because the schema
is created with `journal_mode=WAL` — concurrent readers alongside one writer — and
`AchievementCache` sets `Default Timeout` so the moment they overlap is a wait rather than
a `SQLITE_BUSY`. Do not "simplify" either by dropping WAL.

**The achievement icon naming is load-bearing.** `IconCache.Prune` walks the whole
directory and deletes what belongs to a game that is no longer owned, recovering the app id
from the file name: the whole stem for a capsule, the part before the first underscore for
an achievement icon. Name an achievement icon anything else and the picker's next refresh
silently eats it. Files whose name yields no app id are left alone entirely — the directory
is user-chosen and may not be ours.

Startup order matters and is deliberate:

1. `LoadFromCache()` runs **synchronously** in the constructor. It is pure database reads,
   so the window is painted with games before any network or Steam work starts.
2. `HydrateCachedIconsAsync()` decodes the on-disk capsules on a background thread.
3. `RefreshLibraryAsync()` re-downloads the published list, re-checks ownership, refreshes
   stats, and then `SaveCacheAsync()` writes it back.

Do not turn step 1 into an `async` call — the whole point is that it completes before the
first frame.

`LoadGamesAsync` **merges** into `_Games` rather than clearing and rebuilding. The cached
entries already on screen carry decoded bitmaps and the user's own rating; replacing them
wholesale blanks the window and re-downloads every capsule. `GameCache.Sync` is the
counterpart on the database side: one transaction that upserts what is owned and deletes
what is not, so a crash mid-sync cannot leave a half-written library.

Two details worth keeping:

- **Dispose the cache before moving it.** SQLite's WAL leaves `-wal` and `-shm` sidecars
  open; `GameCache.MoveTo` closes the connection, calls `SqliteConnection.ClearAllPools()`,
  moves all three files, and reopens. Skipping the pool clear leaves the file locked on
  Windows.
- **`SQLitePCLRaw.bundle_e_sqlite3` is pinned to 2.1.13** in `SAM.Picker.csproj` *and*
  `SAM.Game.csproj`. `Microsoft.Data.Sqlite` 10.0.1 otherwise resolves 2.1.11, which
  carries GHSA-2m69-gcr7-jv3q. Do not drop either pin to tidy the files.

Settings live at a fixed `~/.sam/settings.json` — they cannot live under the configurable
database directory, because that is the path they would have to be read to find. The file
is only written once something changes; its absence means defaults. `~/.sam/ratings.json`
sits beside it for the same reason: it is per-user data, and it must not move when the
cache directory does.

So `~/.sam` holds, by default, `settings.json`, `ratings.json`, `games.db` (plus its WAL
sidecars) and `icons/`. Only the last two are relocatable; the first two are found by
fixed path or nothing could be found at all.

### The achievement cache (`SAM.Game`)

`SAM.Game` used to show an empty window until Steam answered `RequestUserStats`, then
download every achievement icon from the CDN one at a time. Both now have a warm path.

Constructor order is deliberate and mirrors the picker:

1. `RefreshStats()` asks Steam. It only *asks* — the answer arrives later, on the callback
   timer.
2. `LoadFromCache()` then runs **synchronously**, painting the achievement list from the
   `achievements` table. It has to come after `RefreshStats()`, which clears the
   collections.
3. `OnUserStatsReceived` rebuilds the list from live data and calls `SaveCache()`.

`IsInputEnabled` stays false across that window, so nothing cached can be committed to
Steam. The cached `IsAchieved` is display only; `OriginalValue` is overwritten from Steam
before Commit is ever reachable.

Three things that are easy to get wrong here:

- **`SaveCache` builds its rows from `_AchievementDefinitions`, not from `Achievements`.**
  The latter is whatever the search box and the locked/unlocked toggles last left on
  screen. Persisting it would write a filtered subset over the whole list.
- **Rows are keyed by account id *and* language.** Achievement state belongs to whoever was
  logged in, and the cached name and description are the *localized* strings — so
  `_Language` is read once in the constructor and `LoadUserGameStatsSchema` uses that same
  field rather than calling `GetCurrentGameLanguage()` again. A language switch must miss
  the cache, not show the previous language's text.
- **Writes are chained through `_SaveTask`, not fired off with `Task.Run`.** A
  `SqliteConnection` cannot serve two commands at once, and pressing Refresh twice produces
  two callbacks.

Only the achievement list is cached. Stat definitions still come from the schema file on
every start; it is a local file and the parse is cheap.

### Pending changes (`SAM.Game`)

`_PendingStates` holds the user's uncommitted toggles by achievement id, and it is the
model — `Achievements` is only what the filters last left on screen. This is not
bookkeeping for the Uncommitted bar; it fixes two real bugs the bar would otherwise have
made obvious:

- Changing a filter rebuilt the list from Steam and **silently discarded every pending
  toggle**.
- `StoreAchievements` read the changed rows off `Achievements`, so a change made before
  switching filters **was never committed**.

Three rules keep it honest:

- `_SteamStates` caches what Steam last said, read once per callback by
  `RefreshSteamStates`. The list can then be rebuilt for a filter or search change without
  asking Steam again, and `EffectiveState` is "the override if there is one, else Steam's".
- A toggle back to Steam's value **removes** the entry rather than storing a no-op, so the
  count is the number of real changes.
- `RefreshSteamStates` clears pending. Anything toggled while the *cached* list was on
  screen was toggled against unconfirmed state, so it is dropped rather than replayed onto
  the real values.

Bulk operations set `_IsBulkUpdating` so the summary is recomputed once rather than once
per achievement — on Civilization V that is the difference between 10 and 2,860 property
notifications.

The filter is one `AchievementFilter` enum, replacing a pair of independent
"show only locked" / "show only unlocked" booleans that could be set to two different
combinations meaning the same thing. It filters on the state **on screen**, pending edits
included, so an achievement does not vanish from Locked the instant it is ticked.

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
verified against a live, logged-in Steam client on all three supported targets:

| Step | win-x64 | win-x86 | linux-x64 |
|---|---|---|---|
| Install-path discovery | OK | OK | OK (`~/.local/share/Steam`) |
| `Steam.Load()` + 3 exports | `steamclient64.dll` | `steamclient.dll` | `linux64/steamclient.so` |
| `CreateInterface` + vtable marshal | OK | OK | OK |
| `CreateSteamPipe()` / `ConnectToGlobalUser()` | OK | OK | OK |
| `GetAppData(480/105600, "name")` | `Spacewar` / `Terraria` | same | same |
| `IsSubscribedApp(480)` / language | True / `english` | same | same |
| `IsLoggedIn()` | True | True | True |
| `GetSteamId()` | 765611980067…409 | same | same (after invariant #4 fix) |
| `LibraryStats` playtime | 6614 / 1356 / 5847 min | identical | identical |
| SAM.Game on Terraria | 137 achievements, 10 stats | identical | — |

Getting there required fixing the `GetISteamApps` signature (invariant #3) and the
`GetSteamID` struct-return ABI (invariant #4). Playtime read on Linux matched Windows to
the minute, and the achievement counts matched wherever both machines had the schema
cached.

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
- **Orphaned `achievements` rows are never pruned.** `GameCache.Sync` prunes the `games`
  table and `IconCache.Prune` drops the matching icons, including achievement icons, but
  nothing deletes achievement *rows* for a game that is no longer owned. They are a few
  hundred bytes each and a re-purchase makes them current again, so this is noted rather
  than fixed.
- **Moving the cache while a game window is open.** `GameCache.MoveTo` and
  `IconCache.MoveTo` assume they are the only holder. `SAM.Game` keeps its own connection
  to the same database, so a move performed from the picker's Settings dialog while a game
  window is open can fail on Windows. Pre-existing in shape, wider in reach now that a
  second process opens the file.
- **`SAM.Game/Resources/` still holds two unreferenced PNGs** (`poop-smiley-sad.png` and
  `poop-smiley-sad-enlarged.png`). They predate the Avalonia migration and nothing in the
  XAML points at them; left in place rather than deleted on a guess.
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
