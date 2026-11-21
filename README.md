# VDL — Virtual Desktop Launcher

English | [Русский](README.ru.md)

VDL (Virtual Desktop Launcher) is a Windows console utility for moving application windows between virtual desktops, launching applications on selected desktops, creating and cleaning desktops, and saving desktop layouts.

The Windows virtual-desktop integration uses [Slion's VirtualDesktop library](https://github.com/Slion/VirtualDesktop), available as the `Slions.VirtualDesktop` NuGet package. Thanks to its authors and maintainers for making this functionality available.

## Releases

[Download the latest VDL.exe for Windows x64](https://github.com/statprograms/VDL/releases/latest/download/VDL.exe)

A portable, self-contained executable: no separate .NET installation is required. Download `VDL.exe` from the release assets and run it from a terminal.

[All releases and release notes](https://github.com/statprograms/VDL/releases)

[Release v1.0.0](https://github.com/statprograms/VDL/releases/tag/v1.0.0) is available. The latest-download link stays the same when subsequent releases retain the asset name `VDL.exe`. To build your own executable, follow the [build instructions](#build-and-verification).

## Contents

- [Releases](#releases)
- [Quick start](#quick-start)
- [Important: terminal titles can match your search](#important-terminal-titles-can-match-your-search)
- [Command syntax and window selection](#command-syntax-and-window-selection)
- [Command reference](#command-reference)
- [Switch reference](#switch-reference)
- [Application arguments and quoting](#application-arguments-and-quoting)
- [Profiles](#profiles)
- [Optional startup setup with Windows Task Scheduler](#optional-startup-setup-with-windows-task-scheduler)
- [Troubleshooting](#troubleshooting)
- [Build and verification](#build-and-verification)
- [Command architecture](#command-architecture)
- [License and third-party notices](#license-and-third-party-notices)
- [Donations](#donations)

## Quick start

These examples assume `VDL.exe` is on `PATH` or that the current directory contains it. In PowerShell, use `./VDL.exe` if it is not on `PATH`. Desktop-changing examples perform real operations when executed. Replace application paths, process IDs, and filenames with values from your machine.

```cmd
VDL list --verbose
VDL move-left --title-start "Total Commander"
VDL move Work --process notepad --create --switch
VDL move-all Notes --process notepad --create
VDL run-sole notepad.exe
VDL profile save workspace.json
VDL --help
```

The application path comes **before** the optional desktop target in launch commands:

```cmd
VDL run notepad.exe Work --create
```

## Important: terminal titles can match your search

A window title is live text. It can change when an application opens a document, changes directories, connects to a server, or runs a command. A terminal can therefore accidentally become a candidate for a search that was meant to select another application.

For example, suppose Total Commander is on Desktop 2 and you enter this in CMD hosted by Windows Terminal:

```cmd
VDL move-left --title Commander
```

While VDL runs, the visible terminal title may become:

```text
C:\WINDOWS\system32\cmd.exe - vdl move-left --title Commander
```

Both that title and `Total Commander (x64) 11.58` contain `Commander`. `--title` performs a substring search, so both satisfy the text filter. Even searching for the longer text has the same ambiguity:

```cmd
VDL move-left --title "Total Commander"
```

CMD can echo `"Total Commander"` into its title too. Quoting keeps the search phrase together; it does not make the match exact or restrict it to the beginning of a title.

The default Balanced search can return when a matching main window stabilizes. Among returned candidates, VDL prefers windows on the current desktop before considering its window score. A matching terminal may therefore be chosen ahead of the intended application on another desktop. `--all` can include both matches, and `--single` can reject the search if both are found. Neither option expresses which application you intended.

The report identifies the window VDL selected:

```text
Window 1116198 "C:\WINDOWS\system32\cmd.exe - vdl move-left --title Commander" [visible] ->
                            Desktop 1
```

In this example, the terminal was selected. `Done` describes completion for that selected window; it does not mean that Total Commander was moved. After VDL exits, CMD's title may return to `C:\WINDOWS\system32\cmd.exe`, hiding the temporary match you saw during execution.

Use a title prefix when the application's title begins with a known phrase:

```cmd
VDL move-left --title-start "Total Commander"
```

This accepts `Total Commander (x64) 11.58` and rejects the echoed terminal title, which starts with `C:\WINDOWS\system32\cmd.exe`. It does not depend on successfully identifying and excluding the invoking terminal.

Use a process selector when application identity matters more than the current title:

```cmd
VDL move-left --process TOTALCMD64
VDL move-left --process TOTALCMD64 --title-start "Total Commander"
VDL move-left --pid 29184
```

The PID above is illustrative; find the current one with `VDL processes`. Process-name matching is a substring search too. A PID identifies one process, which can still own several windows.

An anchored regular expression is another way to express a prefix:

```cmd
VDL move-left --title-regex "^Total Commander\b"
```

VDL attempts to exclude its invoking console and, when identifiable at startup, its visible Windows Terminal host from automatic searches. Terminal hosting arrangements and focus changes can prevent reliable identification. Do not rely on that exclusion to make an ambiguous title search unique. Explicit process/PID selectors allow intentionally targeting the terminal.

The same ambiguity applies to Notepad, browsers, editors, and any other application name typed into the command. Prefer `--title-start` or a process selector for scripts. A title prefix must match the application's actual title: an editor whose title begins with a document name may require a different prefix or a process filter.

## Command syntax and window selection

```text
VDL <command> [arguments] [switches]
VDL profile <save|load> <file> [switches]
```

- Desktop positions are **one-based**: `1` is the first desktop. An all-digit target is interpreted as a position, even if a desktop also has that name.
- Other explicit targets match desktop names. Use quotes for names containing spaces, such as `"Office 2"`.
- String matching ignores case by default. Run and move commands accept `--case`.
- Without window-selection filters, ordinary move commands use the foreground window. When entered in a focused terminal, that may be the terminal itself.
- Filters search eligible windows across desktops. Windows on another desktop can be cloaked while retaining the visible window style.
- By default, a filtered move selects one candidate. `--all` selects all matches; `move-all` implies it.
- Process filters and title filters are combined with logical AND. Specifying `--title`, `--title-start`, and `--title-regex` together requires all three to match.
- Run commands normally derive an executable filter from `ApplicationPath`. Supplying an explicit filter replaces that default, so make the filter specific enough to identify the intended application.
- A launched application can reuse an existing process or window. VDL searches matching windows; it does not guarantee that the selected window was newly created by this invocation.

For `move` and `run`, supply a destination for predictable results. If omitted, the implementation tries the localized desktop-name base as its target/template. This is not shorthand for the current desktop. For out/sole commands, an optional name is a preference/template, not an exact destination requirement.

## Command reference

### `run <ApplicationPath> [DesktopTarget]`

Launch an application, find its matching window, and move it to a desktop by position or name. Add `--create` if the destination might not exist.

```cmd
VDL run notepad.exe 2
VDL run notepad.exe Work --create --switch
VDL run "C:\Path\App.exe" "Office 2" --create --switch-first
```

The first example requires Desktop 2 to exist. If the target cannot be resolved, VDL reports an error rather than intentionally launching onto an unresolved destination.

### `run-out <ApplicationPath> [DesktopTarget]`

Launch onto the least crowded desktop other than the desktop current when the destination is planned. Occupancy is the primary criterion; a name preference breaks ties. `--create` permits creating a destination if no other desktop is available.

```cmd
VDL run-out notepad.exe
VDL run-out notepad.exe Work --create --switch
```

### `run-sole <ApplicationPath> [DesktopTarget]`

Launch onto an empty desktop other than the original current desktop. If none is available, create a uniquely named desktop within the creation limit. No `--create` is needed for this fallback.

```cmd
VDL run-sole notepad.exe
VDL run-sole notepad.exe Notes --switch
```

An optional name prefers matching empty desktops and supplies a template if creation is needed. It does not force creation when a usable empty desktop already exists.

### `move [DesktopTarget]`

Move the foreground window, or one matching window when filters are supplied, to an explicit destination.

```cmd
VDL move 2
VDL move Work --process notepad --create
VDL move "Office 2" --title-start "Total Commander" --create --switch
```

With `--all` and no creation, matching windows share the requested destination. With `--all --create`, VDL reserves sequential destinations as it plans the moves.

```cmd
VDL move Work --process notepad --all
```

### `move-all [DesktopTarget]`

Move every matching window. Without `--create`, they share the explicit target. With `--create`, each window gets a separate sequential destination; an existing target can be used first, followed by newly named destinations such as `Notes 2` and `Notes 3`.

```cmd
VDL move-all Work --process notepad
VDL move-all Notes --process notepad --create --verbose
```

Avoid unfiltered bulk moves unless you intend to select windows from many applications. `--single` is incompatible with `move-all`.

### `move-left` and `move-right`

Move relative to the desktop containing each selected window, not simply relative to the desktop on which VDL was invoked. `--steps` defaults to 1.

With no window-selection filters, these commands move the active (foreground) window by one desktop:

```cmd
VDL move-right
VDL move-left
```

When typed in a focused CMD or Windows Terminal window, the terminal itself is the active window, so moving it is intentional. For example, running `VDL move-right` while the terminal is on Desktop 1 moves that terminal to Desktop 2, if Desktop 2 exists. Running `VDL move-left` with that terminal active on Desktop 2 moves it back to Desktop 1. This differs from a filtered search accidentally matching the command text in the terminal title: no title matching is performed for these bare commands.

To explicitly switch to the destination as well, use:

```cmd
VDL move-right --switch
VDL move-left --switch
```

To move another application while typing in the terminal, supply a process or title filter, as in the following examples.

```cmd
VDL move-left --title-start "Total Commander"
VDL move-left --process notepad --steps 2 --wrap
VDL move-right --process notepad --steps 3 --create
VDL move-right --process notepad --all --steps 1
```

Without `--wrap` or creation, movement stops at the first or last desktop. `--wrap` cycles between the ends. `move-right --create` extends the desktop sequence as needed. `move-left` does not support creation. Neither command accepts a positional desktop target or `--numeric-names`.

### `move-out [DesktopTarget]`

Move to the least crowded desktop other than the window's source desktop. In bulk operations, planned reservations count toward occupancy so successive selections can be distributed across destinations.

```cmd
VDL move-out --process chrome --all
VDL move-out Work --process notepad --create
```

The optional name prefers matching desktops when occupancy is equal. If no other desktop is available, `--create` permits a new destination.

### `move-sole [DesktopTarget]`

Move to an empty desktop other than the window's source desktop. In bulk operations, each selected window reserves a distinct empty destination. Create desktops if necessary, within the creation limit.

```cmd
VDL move-sole --title-start "Total Commander"
VDL move-sole Notes --process notepad --all
```

Like `run-sole`, this command can create its fallback destination without `--create`.

### `create [DesktopTarget]`

Create one or more desktops. Here the positional argument supplies a naming template; it does not select an existing desktop to move a window onto. `--steps` is the number to create.

```cmd
VDL create
VDL create Office --steps 3
VDL create "Office 4" --steps 2
VDL create --steps 2 --numeric-names
VDL create Work --steps 5 --max-desktops 100 --verbose
```

With no naming template, names continue from the current desktop count using the localized desktop-name base. A named sequence starts with `Office`, then `Office 2`, `Office 3`, and so on, skipping collisions. `--numeric-names` uses numbers without a prefix. Creating desktops does not itself switch focus to them.

### `clean`

Remove desktops without counted visible windows, always retaining at least one desktop. Cloaked windows with the visible style still contribute to occupancy. A desktop containing only windows without that style may be treated as empty.

```cmd
VDL clean
VDL clean --verbose
```

Cleanup can remove the current desktop if it is empty. Removed desktops fall back to the first retained desktop. This command does not accept process or window filters. `--clean` is the separate post-move switch described below.

### `list`

List desktop positions, names, IDs, and the current-desktop marker. Verbose output includes window counts.

```cmd
VDL list
VDL list --verbose
```

### `count`

Print the desktop count. Without verbose output, the result is a bare number.

```cmd
VDL count
VDL count --verbose
```

### `processes`

Print the process tree and associated windows. Use it to discover process names, PIDs, window handles, and titles before constructing filters.

```cmd
VDL processes
```

This command does not accept `--verbose` or window-selection filters.

### `profile save <file>` and `profile load <file>`

Save or restore desktop names, order, and wallpapers. See [Profiles](#profiles) for file details and restoration behavior.

```cmd
VDL profile --help
VDL profile save workspace.json
VDL profile load workspace.json --verbose
VDL profile load workspace.json --max-desktops 100
```

`profile` is a command group; select `save` or `load` to perform an operation. `save` does not accept `--verbose` or `--max-desktops`. `load` accepts both. The load file must already exist.

## Switch reference

Examples use long switch names. Slash aliases are listed so that existing scripts can use the same options. All run/move switches below are available on `run`, `run-out`, `run-sole`, `move`, `move-all`, `move-left`, `move-right`, `move-out`, and `move-sole`, except where a narrower scope is stated.

### Process and window filters

| Switch | Aliases | Matching rule | Example |
| --- | --- | --- | --- |
| `--pid <id>` | `/ProcessId`, `/pid` | One positive process ID; its windows must still satisfy other filters. | `VDL move-left --pid 29184` |
| `--process <text>` | `/ProcessName`, `/p` | Substring of the executable's process name. | `VDL move-left --process TOTALCMD64` |
| `--module <text>` | `/ModuleName`, `/mn` | Substring of the module filename. | `VDL move 2 --module notepad.exe` |
| `--path <text>` | `/ModulePath`, `/mp` | Substring of the executable's filesystem path; wildcards are not supported. Supplying the full path makes it more specific. | `VDL move Work --path "C:\Tools\TOTALCMD64.EXE" --create` |
| `--title <text>` | `/WindowTitle`, `/t` | Substring anywhere in the current title. See the terminal warning above. | `VDL move 2 --process notepad --title report` |
| `--title-start <text>` | `/WindowTitleStart`, `/ts` | Text at the beginning of the title. | `VDL move-left --title-start "Total Commander"` |
| `--title-regex <pattern>` | `/WindowTitleRegex`, `/tr` | .NET regular expression against the title. Prefix with `^` to anchor its start. | `VDL move-left --title-regex "^Total Commander\b"` |
| `--empty` | `/Empty`, `/e` | Only windows with empty or whitespace-only titles. | `VDL move 2 --process MyApp --empty --all` |
| `--cloaked` | `/Cloaked`, `/cl` | Only windows marked cloaked by DWM, such as windows on another desktop. | `VDL move 1 --process notepad --cloaked --all` |
| `--main` | `/Main`, `/mo` | Only windows classified as main: visible-style, top-level, and unowned. | `VDL move 2 --process notepad --main` |
| `--invisible` | `/IncludeInvisible`, `/i` | Include windows without the visible style, in addition to ordinarily eligible windows. | `VDL move 2 --process MyApp --invisible --all` |
| `--case` | `/CaseSensitive`, `/cs` | Enable case-sensitive process, title, and desktop-name matching. | `VDL move-left --title-start "Total Commander" --case` |

Process name, module name, path, and title substring filters are not exact equality tests. Combine selectors to narrow a search. If both module filename and module path are specified, the CLI checks that their filenames agree. Windows can refuse moves of invisible or restricted windows; including them in the search does not guarantee they can be moved.

### Selecting one or several windows

| Switch | Aliases | Behavior | Example |
| --- | --- | --- | --- |
| `--all` | `/All`, `/all` | Select all matching windows; implied by `move-all`. | `VDL move-out --process chrome --all` |
| `--single` | `/SingleInstance`, `/si` | Reject multiple matching windows instead of choosing one. | `VDL move-left --process TOTALCMD64 --single` |

`--all` and `--single` are mutually exclusive. `--single` is also rejected on `move-all`. Without either option, filtered moves select one returned candidate. Bulk searches and uniqueness checks wait for matching processes rather than using the ordinary first-match early exit.

### Desktop creation and relative movement

| Switch | Aliases | Availability and behavior | Example |
| --- | --- | --- | --- |
| `--create` | `/Create`, `/c` | Run/move commands except `move-left`; permit missing destinations. Sole commands already have automatic fallback creation. | `VDL move Work --process notepad --create` |
| `--fill-gaps` | `/FillGaps`, `/fg` | Same commands as `--create`; requires `--create`. Fill missing members before a missing named destination. | `VDL move "Office 4" --process notepad --create --fill-gaps` |
| `--numeric-names` | `/NumericNames`, `/nn` | `create` and run/move commands with a positional target argument; on run/move commands requires `--create`. Use numeric desktop names. | `VDL move 6 --process notepad --create --numeric-names` |
| `--steps <n>` | `/Steps`, `/ss` | `move-left`, `move-right`, `create`; positive distance or creation count. Default 1. | `VDL move-right --process notepad --steps 2` |
| `--wrap` | `/Wrap`, `/w` | `move-left` and `move-right`; cycle at boundaries. Cannot combine with `--create`. | `VDL move-left --process notepad --steps 2 --wrap` |
| `--max-desktops <n>` | `/MaxDesktops`, `/md` | Run/move commands, `create`, `profile load`; creation limit from 1 to 250. Default 50. | `VDL create Work --steps 3 --max-desktops 100` |

A numeric missing destination creates every required physical position: if there are three desktops, `move 6 --create` needs three more. An explicit name such as `"Office 4"` ordinarily creates that missing name; adding `--fill-gaps` also creates missing earlier names such as `Office`, `Office 2`, and `Office 3`. Existing targets are used directly; gap filling is part of resolving a missing target.

`--max-desktops` limits the resulting total, not merely the number added by the command. Increasing it does not create desktops by itself. Naming conventions do not change how all-digit positional targets are interpreted.

### Focus and post-move actions

| Switch | Aliases | Behavior | Example |
| --- | --- | --- | --- |
| `--switch-first` | `/SwitchFirst`, `/swf` | Focus the target before launch or movement. Return to the original desktop unless `--switch` is also requested. | `VDL run notepad.exe Work --create --switch-first` |
| `--switch` | `/Switch`, `/sw` | Leave the target focused after movement. | `VDL move Work --process notepad --create --switch` |
| `--maximize` | `/Maximize`, `/mx` | Request full-size window presentation after moving; application behavior may vary. | `VDL move 2 --process notepad --maximize` |
| `--minimize` | `/Minimize`, `/mi` | Minimize the selected window after moving. | `VDL move 2 --process notepad --minimize` |
| `--clean` | `/Clean` | Remove empty desktops after movement. | `VDL move 2 --process notepad --all --clean` |

These switches are for run/move operations. `--maximize` and `--minimize` are mutually exclusive. `--switch-first --switch` is valid and leaves the destination focused. For several destinations, the final focus can follow the last processed target. Cleanup can change desktop positions and remove an emptied current desktop.

### Search mode and timing

When VDL searches for windows to move, an application may still be starting, opening its main window, or replacing a splash screen. These options control how long VDL looks for a match and how it decides that the matching windows have settled. All times are in milliseconds: `1000` means one second.

**Timeout — the total search budget.** `--timeout` (default `5000`, or five seconds) limits the search, including retries when no window matches and the time spent checking stability. It is a maximum budget, so a successful search can finish sooner. If the budget expires, VDL can use matches that have already become stable; if none have, there is no stable window to move. Increasing this value helps applications whose windows appear slowly. It does not terminate the launched application or impose a time limit on the entire VDL command: launch, the initial delay, and the move itself are outside this search budget.

**Stability — how long a candidate must remain unchanged.** `--stability` (default `500`) gives each candidate process time to finish creating or replacing windows. On fresh window snapshots, VDL checks that the best matching window has the same handle and that the number of matching windows for that process stays the same. A change restarts that process's stability interval; losing all matches clears it. A main-window candidate needs the configured interval, while a non-main candidate needs 50% longer (`750` ms with the default). Increasing stability helps avoid selecting a short-lived startup window, but also makes discovery slower. This checks window identity and count, so it does not establish that a document has finished loading or that a title will never change. Even `--stability 0` needs a later fresh observation to confirm the candidate. Checks occur periodically, so completion is not timed to the exact millisecond.

**Delay — a pause before searching.** `--delay` (default `0`) waits once before VDL starts selecting windows. For a `run` command, the application is launched first, giving it this extra time to start. The pause does not count toward the timeout and does not count as evidence of stability. Use it when an application predictably needs a head start; use timeout when you want VDL to start looking immediately and wait as needed.

For example, `--delay 2000 --timeout 10000 --stability 500` means: wait two seconds, then search for up to ten seconds, checking stability within those ten seconds. It does not add another half-second after the timeout. A match can complete the search sooner, and launching and moving can add to the total command duration.

**Search modes — when enough stable results have been found.** `--mode` chooses the completion rule; all modes still apply your filters and the stability check:

- **Fast** allows the search to finish as soon as any candidate process has stable matching windows. Use it when the filter is specific and quick completion matters.
- **Balanced** (the default) allows early completion when a candidate process has a stable matching main window. It gives main-window candidates a chance before stopping for another kind of window, but can also finish when all candidate processes have completed their checks. It does not require the `--main` filter; add `--main` if only main windows are acceptable.
- **Thorough** does not stop early because one candidate is ready. It waits for the discovered candidate processes to finish their stability checks, or for the search budget to expire. It can finish before the timeout if all candidates complete; it does not guarantee finding windows that appear later.

`--all` and `--single` disable the early completion rules of Fast and Balanced so the search can collect results across candidate processes. Modes control when discovery finishes; they do not make an ambiguous filter more precise. For example, a terminal title containing your command line can still match `--title Commander`; use a more specific filter such as `--title-start "Total Commander"`.

Timeout, stability, and mode apply to the matching-window search used by run/move commands. A plain `VDL move-left` or `VDL move-right` selects the active window directly and does not perform that search. Delay still applies before selecting the active window.

| Switch | Aliases | Default and limits | Example |
| --- | --- | --- | --- |
| `--mode <Fast\|Balanced\|Thorough>` | `/Mode`, `/m` | Balanced. Controls when discovery may return stable matches. | `VDL move-left --title-start "Total Commander" --mode Thorough` |
| `--timeout <ms>` | `/MaxTimeout`, `/mt` | 5000; accepted range 100–300000. Overall discovery/stability search budget. | `VDL run "C:\Path\SlowApp.exe" Work --create --timeout 15000` |
| `--stability <ms>` | `/StabilityTime`, `/st` | 500; accepted range 0–30000, no greater than timeout. | `VDL move 2 --process MyApp --stability 1000 --timeout 10000` |
| `--delay <ms>` | `/Delay`, `/d` | 0; accepted range 0–300000. Wait before starting the window search. | `VDL run "C:\Path\SlowApp.exe" Work --create --delay 2000` |

Examples of selecting each mode:

```cmd
VDL move-left --title-start "Total Commander" --mode Fast
VDL move-left --title-start "Total Commander" --mode Balanced
VDL move-left --title-start "Total Commander" --mode Thorough
```

### Launch arguments and execution

| Switch | Aliases | Availability and behavior | Example |
| --- | --- | --- | --- |
| `--args <arguments...>` | `/Arguments`, `/a` | Run commands only; pass arguments to the launched application. | `VDL run notepad.exe Work --create --args "C:\Notes\todo.txt"` |
| `--no-shell` | `/NoShell`, `/ns` | Run commands only; disable `UseShellExecute` and launch directly. | `VDL run "C:\Path\App.exe" Work --create --no-shell` |

Shell execution is enabled by default. `--no-shell` can be useful with executable paths and when avoiding shell aliases, but VDL does not provide switches for redirecting application input/output. When launching directly with a directory in the application path, VDL uses that directory as the working directory when it exists.

### Diagnostics, help, and version

| Switch | Aliases | Availability and behavior | Example |
| --- | --- | --- | --- |
| `--verbose` | `/Verbose`, `/v` | Run/move commands, `create`, `clean`, `list`, `count`, `profile load`; detailed output appropriate to the operation. | `VDL move-left --title-start "Total Commander" --verbose` |
| `--help` | Built-in `-h`, `-?`; see command help for additional aliases. | Show root or command help. | `VDL move-left --help` |
| `--version` | Built-in root switch. | Print the version reported by the executable. | `VDL --version` |

```cmd
VDL --help
VDL run --help
VDL profile load --help
VDL list /v
VDL move-left /ts "Total Commander" /cs
```

## Application arguments and quoting

For launch commands, use `--` to separate VDL options from application arguments, especially when the application takes switches of its own:

```cmd
VDL run "C:\Path\App.exe" Work --create -- --new-window "C:\Notes\todo.txt"
```

Everything after `--` belongs to the application. Put VDL switches before it. `--args` is an alternative:

```cmd
VDL run notepad.exe Work --create --args "C:\Notes\todo.txt"
```

Prefer one argument-passing style per invocation. If `--args` supplies values, those are used in preference to unmatched trailing tokens. A run command may accept unmatched tokens as application arguments; a misspelled VDL switch can therefore be forwarded to the application instead of producing the error you expected. Check `--verbose` output when diagnosing launch arguments.

Quote paths, desktop names, and filter values containing spaces. Windows argument parsing can be surprising when a quoted argument ends with a backslash: the backslash can escape the closing quote. When passing a literal trailing backslash through the Windows argument parser, double it:

```cmd
VDL run "C:\Path\App.exe" Work --create -- "C:\Folder With Spaces\\"
```

Quoting rules also depend on the shell. The examples use ordinary CMD-compatible double quotes; PowerShell applies its own parsing before VDL receives arguments. Use the verbose argument report to inspect the reconstructed launch arguments.

## Profiles

Profiles store the desktop layout: names, order, and wallpaper paths. They do not store application sessions, launch commands, window positions, or window-to-desktop assignments.

```cmd
VDL profile save workspace.json
VDL profile load workspace.json --verbose
```

A minimal profile looks like this:

```json
[
  { "Name": "Work", "WallpaperPath": null, "Children": [] },
  { "Name": "Notes", "WallpaperPath": null, "Children": [] }
]
```

The reader also accepts older nested `Children` layouts and flattens them in order. Files must contain at least one desktop and must not contain duplicate names. Default desktops without stored names are saved with effective localized names such as `Desktop 1`.

Restoration first reuses desktops with matching names, then reuses remaining desktops by position, and creates additional desktops if needed. Excess managed desktops are removed and their windows fall back to the first retained desktop. Thus a profile load can move windows indirectly when it removes desktops, even though it does not restore application assignments.

The destination directory must exist when saving. Saving to an existing file overwrites it. Wallpaper paths refer to files available on the machine that loads the profile; saving a profile does not copy wallpaper images. A `null` wallpaper does not request a wallpaper update.

## Optional startup setup with Windows Task Scheduler

You can optionally use Windows Task Scheduler to launch applications and place their windows on selected desktops whenever you sign in. Configure each action to run **VDL.exe directly**, with its command and arguments stored in the task's properties. This setup does not use an external `.cmd` or `.bat` file or a command-shell wrapper.

For example, to launch Notepad on a desktop named `Work`, open **Task Scheduler**, choose **Create Task**, and configure:

1. **General:** choose your Windows account and **Run only when user is logged on**.
2. **Triggers:** add **At log on** for that account. An optional delay of about 30 seconds gives the desktop shell time to initialize.
3. **Actions:** add **Start a program** and enter these values, adjusting the paths for your machine:

| Action property | Value |
| --- | --- |
| **Program/script** | `C:\Tools\VDL\VDL.exe` |
| **Start in** | `C:\Tools\VDL` |

In **Add arguments**, enter only the VDL command and its arguments:

```text
run "C:\Windows\System32\notepad.exe" Work --create --process notepad --main --timeout 15000
```

Save the task and use **Run** to test it while signed in. VDL launches Notepad and moves its matching main window to `Work`, creating that desktop if needed.

To arrange more applications, add further **Start a program** actions to the same task. Each action points directly to `VDL.exe` and has its own **Add arguments** value. For example, a second action can launch Firefox on `Browsing`:

```text
run "C:\Program Files\Mozilla Firefox\firefox.exe" Browsing --create --process firefox --main --timeout 15000 -- --new-window "https://example.com"
```

Replace Firefox with an application installed on your machine. Keep the executable path in **Program/script** and the arguments in **Add arguments**. Task Scheduler executes multiple actions in a task sequentially; use their listed order for your startup setup. See Microsoft's [task actions documentation](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-actions).

Use an **At log on** trigger for this GUI workflow. A task configured to run without a logged-on user can execute outside the interactive desktop and will not reliably arrange the windows you see. See Microsoft's [Task Scheduler security-context documentation](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks).

Applications can reuse existing processes or windows, so a repeated task run is not guaranteed to create another independent window or an identical workspace. Adjust application arguments and window filters as needed. Desktop profiles restore desktop layout only; the task actions supply the application launches. VDL does not restore application sessions or saved window geometry.

## Troubleshooting

| Symptom | Explanation and next step |
| --- | --- |
| The terminal moves instead of the application. | Read the reported window title. The search text may occur in CMD's echoed invocation. Use `--title-start` or `--process`; quoting a substring alone does not remove this ambiguity. |
| A different application window is selected. | Several windows may satisfy a substring filter. Combine a process selector with a title filter, or use a current PID. Add `--single` to reject multiple matches. |
| No matching window is found. | Check current titles and process names with `VDL processes`. A prefix must match the start exactly, apart from default case folding. Review `--main`, `--empty`, and visibility constraints. |
| A slow application has no stable window before timeout. | Increase `--timeout`, consider `--delay`, or try Fast/Balanced mode after making the selector specific. |
| A window on another desktop is cloaked. | Cloaking is expected for many off-desktop windows. `--cloaked` restricts to these; `--invisible` refers to a different property, the visible window style. |
| Relative movement appears to do nothing. | The window may already be at the first/last desktop. Use `--wrap`, or `move-right --create` to extend the workspace. Also confirm the selected handle/title. |
| Target cannot be resolved. | Check `VDL list`, supply an explicit destination, and add `--create` if a missing target should be created. |
| Creation is rejected. | Check the total desktop count and `--max-desktops`; filling gaps may require several new desktops. |
| Access denied or a window disappears during a move. | The target can close, change handles, or be restricted. Check permissions and select the application's current window. |
| A profile does not reopen applications. | Profiles restore desktop layout only. Use separate run commands to start applications. |

The move report prints the selected handle and captured title before attempting the physical move. An error may follow it; the selection line by itself is not confirmation of success. `Done` and summaries describe processed selections, not an independent verification that the selected title was the one you intended.

There is no public dry-run switch or general undo command. Planning failures leave Windows unchanged, but a failure after synchronization begins can leave completed moves, names, and wallpaper changes in place. Cleanup only removes newly created desktops when they are empty. Native desktop behavior depends on the Windows version and the virtual-desktop integration available to the application.

## Build and verification

The project targets .NET 10 for Windows. It uses `Slions.VirtualDesktop`, `System.CommandLine`, and Polly through NuGet package references. Dependency licenses and notices are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), with original notice files retained under [`licenses/`](licenses/).

```powershell
dotnet build VDL.slnx
dotnet build VDL.slnx -c Release
dotnet publish VDL.csproj -c Release -p:PublishProfile=Standalone
dotnet run --project Tests/VDL.Tests.csproj
dotnet run --no-launch-profile -- --help
```

Use `--no-launch-profile` when running the CLI from the repository: the existing IDE launch profile supplies its own arguments. Binaries are written below `bin/Debug/net10.0-windows10.0.19041.0/` or the corresponding Release directory.

The regression harness checks parsing, dispatch, and in-memory planning without moving live windows. An optional read-only integration probe searches actual windows through production discovery and stability code:

```powershell
dotnet run --project Tests/VDL.Tests.csproj -- --inspect-title-start "Total Commander"
```

## Command architecture


The invocation path is:

```text
CommandLine parsing and validation
  -> Options
  -> ApplicationCommandFactory
  -> IApplicationCommand.ExecuteAsync
  -> DesktopCommandReceiver
  -> TaskView planning / ITaskViewCommand mutations
  -> TaskView.ApplyAsync / physical system commands
```

- `TaskView/ApplicationCommands.cs` contains application commands for running, moving, creating, cleaning, querying, and profile operations. Movement variants capture their `MoveMode` in `Options`. It also contains physical commands for launching processes, moving windows, switching desktops, and changing window state.
- `TaskView/TaskViewCommands.cs` contains deferred blueprint mutations: create, rename, remove, reorder, wallpaper, and profile restoration. `TaskView.Execute` validates each mutation before executing it in memory.
- `TaskView/TaskView.cs` represents individual desktops and desktop groups. Group labels do not represent additional physical desktops.
- `TaskView/TaskViewExecution.cs` resolves and reserves destinations, serializes profiles, and synchronizes the intended layout with Windows. Window moves occur after all planned destination desktops have been materialized.
- `TaskView/DesktopCommandReceiver.cs` coordinates window discovery, stability polling, launch options, movement retries, post-move actions, and output. `Program.cs` handles process entry, command wiring, timing, and top-level errors.

Planning failures do not change Windows. Synchronization tracks the desktops owned by its initial snapshot and leaves concurrently added desktops outside that snapshot intact. Failure cleanup removes newly created desktops only when they are empty; already completed window moves, renames, and wallpaper updates are not automatically undone.


### Shared window snapshots and polling

`SystemWindows` collects window information on demand and shares an immutable snapshot. All-window, per-process, and per-handle queries use this cache. When a snapshot expires, one caller performs collection; concurrent callers await that refresh and recheck the cache before collecting. There is no background refresh timer, so no window scans run solely to maintain the cache when no caller needs data.

The cache starts with a 25 ms reuse interval. Its adaptive policy measures successful collection and snapshot-indexing wall time, smooths it with a 25% exponential moving average, and selects three times that cost, bounded to 25-100 ms. Cheap collection retains the original 25 ms behavior; expensive collection permits more reuse. This is a workload throttle, not a CPU-utilization measurement. Adding readers does not itself shorten the interval.

Each snapshot has an increasing version, a monotonic capture timestamp, and read-only indexes by PID and window handle. Per-process stabilization loops still wait 25 ms between checks. Repeated reads of the same snapshot cannot make a window appear stable: stability advances only with a newer observation and uses its capture timestamp.

Callers can require a fresher snapshot than the adaptive interval would otherwise permit. Discovery and individual-handle lookups request at most 25 ms-old data. A process's first stability observation does too. While waiting for stability, its freshness cap follows the remaining interval, with 25-100 ms bounds, tightening back to 25 ms near the deadline. Zero and short stability settings retain the original freshness resolution. The best-handle/count rules, main/non-main stability periods, search modes, and outer process-discovery backoff remain in place.

Window moves, desktop switches, launch attempts, window-state commands, and explicit desktop refreshes invalidate the snapshot. The next request then collects fresh data regardless of the adaptive interval. A collection invalidated while running is repeated before its result can be used. Changes made outside VDL are observed on a subsequent refresh; collection is an observation pass, not an atomic freeze of Windows. Native enumeration remains synchronous and cannot be interrupted halfway through by a cancellation token.

`--verbose` reports the adaptive reuse interval after a filtered search. The old unused `AdaptiveThreshold` helper remains removed; the dedicated `WindowSnapshotFreshnessPolicy` implements this bounded policy. `SimpleThresholdCache` continues to serve the separate desktop-information cache.

### Read-only polling measurements

Run the measurement harness in Release mode:

```powershell
dotnet run --project Tests/VDL.Tests.csproj -c Release -- --measure-polling output/measurements/polling.json
```

It measures native window/process collection, allocation and cache-hit overhead, independent versus shared native collection with 1/8/16 readers, and fixed versus adaptive reuse during synthetic stability searches. A final search uses the production discovery path without moving a window. The JSON report contains machine details, sample counts, per-operation timings, scan/request counts, and elapsed search times.

Synthetic cases inject blocking collector delays of 5/20/50 ms. Actual delays can be longer because of Windows scheduling and are reflected in the measured collector wall time. These cases model expensive observation latency; they do not reproduce a complete slow computer or measure its CPU usage. Use the same harness on older computers for representative results. Short pilot runs illustrate tradeoffs rather than establish statistically reliable performance guarantees.

## License and third-party notices

Virtual Desktop Launcher is licensed under the [MIT License](LICENSE), copyright 2026 statprograms. Donations are optional and do not change the license terms.

Dependencies retain their own licenses. [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) contains the collected license and attribution texts for direct dependencies, Roslyn, the build-time analyzer dependency, and the bundled .NET runtime. Original notice files are retained in [`licenses/`](licenses/).

The application license and consolidated third-party notices are embedded as resources in `VDL.exe`, including in the single-file distribution. They are also available in this repository for reading. Keep the notices with redistributed source and preserve them when building distributions. When upgrading dependencies or the self-contained .NET runtime, review and update the notice collection for the versions being distributed.

## Donations

If you find Virtual Desktop Launcher useful and would like to say thanks, you can make a donation using the details below.

- **Currency:** USDT (Tether)
- **Network:** Tron (TRC20)

**Donation address:**

```text
TKvRzDo7wJGjH6jXpq1k4VzG9uB8iHNEXd
```

Please send **only USDT on the Tron (TRC20) network** to this address. Select **Tron / TRC20** as the withdrawal network in your wallet or exchange.

Thanks!
