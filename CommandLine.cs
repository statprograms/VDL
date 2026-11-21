using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;

namespace VDL.Cli;

public enum SearchMode
{
    Fast,      // return immediately on any stable match
    Balanced,  // wait for the first "strong" (Main) window to stabilize
    Thorough   // wait for all matching processes to stabilize or timeout
}

public enum MoveMode
{
    MoveExplicit,
    MoveLeft,
    MoveRight,
    MoveOut,
    MoveSole
}

public enum ProfileCommandType
{
    Save,  // save currently existing system desktops to a json file
    Load,   // restore desktop profile from json
    None
}

public static class AppDefaults
{
    public const int MaxDesktops = 50;                                // maximum number of desktops allowed by default
    public const int HardMaxLimit = 250;                              // absolute upper limit for desktop creation to ensure stability
    public const int WindowSearchTimeoutMs = 5000;                    // default maximum time in milliseconds to wait for a window search
    public const int WindowStabilityMs = 500;                         // time in milliseconds a window must remain unchanged to be considered stable
    public const int MaxRegexDelay = 1_000;                           // maximum time allowed for regex parsing
    public const string DefaultDirection = "right";                   // default relative movement direction
    public const SearchMode DefaultSearchMode = SearchMode.Balanced;  // default precision level for window searching
}

public record Options
{
    public bool? AllInstances { get; init; }                       // operate on all matching windows instead of just the first
    public string? ApplicationArguments { get; init; }             // arguments string passed to the application
    public string? ApplicationPath { get; init; }                  // path to the application to launch
    public required int AutoCreateDesktopsMax { get; init; }       // effective limit for automatic desktop creation
    public bool? CaseSensitive { get; init; }                      // perform case-sensitive string matching
    public bool? CleanAfter { get; init; }                         // remove empty desktops after the operation
    public required Command CliCommand { get; init; }              // specific cli command to be executed
    public FileInfo? DesktopsJsonFile { get; init; }       // argument for the profile commands
    public bool? CreateDesktops { get; init; }                     // create new desktops if the target is beyond the current range
    public string? DesktopTarget { get; init; }                    // target desktop name or index
    public bool? FillGaps { get; init; }                           // create all missing desktops that go before the target ('1', 2', '3' if target is '4')
    public bool? FilterOnlyCloakedWindows { get; init; }           // filter: only include cloaked or suspended windows
    public bool? FilterOnlyEmptyTitles { get; init; }              // filter: only include windows without title text
    public bool? FilterOnlyMain { get; init; }                     // filter: include only main windows
    public uint? FilterProcessId { get; init; }                    // filter: unique id of the process
    public string? FilterProcessModuleName { get; init; }          // filter: name of the module
    public string? FilterProcessModulePath { get; init; }          // filter: full filesystem path to the module
    public string? FilterProcessName { get; init; }                // filter: name of the process executable
    public string? FilterWindowTitle { get; init; }                // filter: partial match for the window title
    public string? FilterWindowTitleStart { get; init; }           // filter: match the beginning of the window title
    public string? FilterWindowTitleRegex { get; init; }           // filter: regular expression match for the window title
    public bool? IncludeInvisible { get; init; }                   // include windows with ws_visible set to false
    public int? MaxDesktopsOverride { get; init; }                 // user-defined override for the desktop limit
    public bool? Maximize { get; init; }                           // maximize the window after moving
    public required int MaxTimeout { get; init; }                  // maximum search duration in milliseconds
    public bool? Minimize { get; init; }                           // minimize the window after moving
    public int? MoveDelay { get; init; }                           // delay (ms) before starting the window search
    public MoveMode? MoveMode { get; init; }                       // type of the move operation across desktops
    public bool? NoShellExecute { get; init; }                     // true = direct execution, false = via Windows Shell
    public required SearchMode SearchMode { get; init; }           // search precision and behavior mode
    public bool? SingleInstance { get; init; }                     // abort if more than one window matches
    public required int StabilityTime { get; init; }               // required duration for window structural stability
    public required int Steps { get; init; }                       // number of virtual desktops to jump/create
    public bool? SwitchFirst { get; init; }                        // change focus to the target desktop before operation
    public bool? SwitchToDesktop { get; init; }                    // change focus to the target desktop after operation
    public bool? UseNumericNames { get; init; }                    // If true, creates '4' instead of 'Desktop 4'
    public bool? Verbose { get; init; }                            // show detailed diagnostic and progress information
    public bool? WrapDesktops { get; init; }                       // wrap around to the first/last desktop
}

public static class CliCommands
{
    public const string Name = "VDL";

    //public static readonly RootCommand RootCommand = new RootCommand($"{Name} - A command-line utility to launch applications and move windows to specific virtual desktops");

    public static readonly RootCommand RootCommand = new RootCommand($"{Name} - Virtual Desktop Launcher CLI")
    {
        Description = $@"
Virtual Desktop Launcher ({Name})
Launch applications, move windows, and manage Windows virtual desktops from the command line.

USAGE EXAMPLES:
	Move the active window to a desktop named 'Work', and switch to it (creates if missing):
	{Name} move ""Work"" --switch --create

	Move all Notepad windows to separate new desktops ('Notes', 'Notes 2', etc.):
	{Name} move-all ""Notes"" --process notepad --create

	Move the active window 2 desktops to the right (wrapping around if at the end):
	{Name} move-right --steps 2 --wrap

	Launch an app on the first available empty desktop (creating one if none are free):
	{Name} run-sole ""C:\Path\App.exe""

	Distribute all Chrome windows across the least crowded desktops in the system:
	{Name} move-out --all --process chrome

	Clean up the workspace by removing all desktops that have no windows:
	{Name} clean"
    };

    // Run commands
    public static readonly Command Run = new("run", "Launch an application on a specific desktop. Supports number (e.g. 1) or name (e.g. \"Work\"). Example: run \"C:\\App.exe\" 2");
    public static readonly Command RunOut = new("run-out", "Launch an application on the least crowded desktop (excluding current). Balances load in bulk runs. Example: run-out \"C:\\Path\\App.exe\"");
    public static readonly Command RunSole = new("run-sole", "Launch an application on the first empty desktop, or create a new, uniquely named desktop. Example: run-sole \"C:\\Path\\App.exe\"");

    // Move commands
    public static readonly Command Move = new("move",
        "Move a window to a desktop by index (1, 2...) or name. Combined with --create, " +
        "it fills gaps or creates new desktops sequentially (e.g., 3, 4... or 'Work 2'). Example: " +
        "'move 3 --create' or 'move Work --create'");

    public static readonly Command MoveAll = new("move-all",
        "Move all matching windows. Combined with --create, spreads windows " +
        "across new sequential desktops (e.g., 3, 4... or 'Notes', 'Notes 2'...)");

    public static readonly Command MoveLeft = new("move-left", "Move a window to the previous desktop. Use --steps to jump and --wrap to wrap around to the last desktop. Example: move-left --steps 2");
    public static readonly Command MoveRight = new("move-right", "Move a window to the next desktop. Use --steps to jump and --create to expand the workspace. Example: move-right --steps 3 --create");
    public static readonly Command MoveOut = new("move-out", "Move a window to the least crowded desktop. In bulk moves, windows are balanced across available quiet desktops. Example: move-out --all");
    public static readonly Command MoveSole = new("move-sole", "Move a window to the first empty desktop. In bulk moves, each window is assigned to a unique empty desktop. Example: move-sole --all");

    // Desktops load/save
    public static readonly Command Profile = new Command("profile", "Manage workspace profiles (snapshots)");
    public static readonly Command ProfileLoad = new Command("load", "Restore a workspace layout from a file");
    public static readonly Command ProfileSave = new Command("save", "Save current workspace layout to a file");

    // Other commands
    public static readonly Command Processes = new("processes", "List all active system processes, including their windows.");
    public static readonly Command Clean = new("clean", "Removes all virtual desktops that contain no windows.");
    public static readonly Command List = new("list", "List all currently present virtual desktops, including their names and IDs.");
    public static readonly Command Count = new("count", "Print the total number of virtual desktops in the system.");
    public static readonly Command Create = new("create", "Create one or more new virtual desktops with optional naming.");

    public static readonly Command[] RunCommands =
    [
        Run,
        RunOut,
        RunSole,
    ];

    public static readonly Command[] MoveRelativeCommands =
    [
        MoveLeft,
        MoveRight,
    ];

    public static readonly Command[] MoveCommands =
    [
        Move,
        MoveAll,
        MoveOut,
        MoveSole,
        ..MoveRelativeCommands,
    ];

    public static readonly Command[] RunMoveCommands =
    [
        ..RunCommands,
        ..MoveCommands
    ];

    public static readonly Command[] ModificationCommands =
    [
        ..RunMoveCommands,
        Create,
        Clean,
    ];

    public static readonly Command[] OtherCommands =
    [
        Processes,
        List,
        Count,
        Profile
    ];

    public static readonly Command[] ProfileCommands =
    [
        ProfileLoad,
        ProfileSave,
    ];

    public static readonly Command[] All =
    [
        ..ModificationCommands,
        ..OtherCommands
    ];

    public static readonly Command[] CommandsThatSupportDesktopTargetArgument =
    [
        ..CliCommands.RunMoveCommands.Except(CliCommands.MoveRelativeCommands),
        Create
    ];

    public static readonly Command[] CommandsThatSupportDesktopCreation =
    [
        ..RunMoveCommands.Except([MoveLeft])
    ];

    public static readonly Command[] CommandsThatSupportVerboseOption =
    [
        ..ModificationCommands,
        List,
        Count,
        ProfileLoad
    ];

    public static readonly Command[] CommandsThatSupportStepsOption =
    [
        ..MoveRelativeCommands,
        Create,
    ];

    public static readonly Command[] CommandsSupportingMaxDesktops =
    [
        ..ModificationCommands.Except([Clean]),
        ProfileLoad,
    ];

    public static readonly (Command Cmd, Command[] Subs)[] CommandsSupportingSubcommands =
    [
        (Profile, [ProfileLoad, ProfileSave])
    ];
}

internal static class CliArguments
{
    public static readonly Argument<string?> DesktopTargetArgument = new Argument<string?>("DesktopTarget")
    {
        Description = $"Target desktop by number (1, 2...) or name (e.g., 'Office 2'); if missing, a new desktop is created using this value as a template (up to {AppDefaults.MaxDesktops} total).",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static readonly Argument<string> ApplicationPathArgument = new Argument<string>("ApplicationPath")
    {
        Description = "Path to the application executable."
    };

    public static readonly Argument<FileInfo> ProfileFileArgument = new Argument<FileInfo>("file")
    {
        Description = "The path to the profile JSON file.",
        Arity = ArgumentArity.ExactlyOne
    };
}

internal static class CliOptions
{
    public static readonly Option<string[]?> ApplicationArguments = new Option<string[]?>("/Arguments", "/a", "--args") { Description = "\tArguments to pass to the application.", AllowMultipleArgumentsPerToken = true };

    // Filter Group 
    public static readonly Option<uint?> ProcessIdFilter = new Option<uint?>("/ProcessId", "/pid", "--pid") { Description = "Filter: Match by process Id." };
    public static readonly Option<string?> ProcessNameFilter = new Option<string?>("/ProcessName", "/p", "--process") { Description = "Filter: Match by process name (e.g., 'notepad')." };
    public static readonly Option<string?> ModuleNameFilter = new Option<string?>("/ModuleName", "/mn", "--module") { Description = @"Filter: by module name (e.g., 'notepad.exe')." };
    public static readonly Option<string?> ModulePathFilter = new Option<string?>("/ModulePath", "/mp", "--path") { Description = "Filter: by full module path (e.g., 'c:\\windows\\notepad.exe')." };
    public static readonly Option<string?> WindowTitleFilter = new Option<string?>("/WindowTitle", "/t", "--title") { Description = "Filter: search by window title (partial match)." };
    public static readonly Option<string?> WindowTitleStartFilter = new Option<string?>("/WindowTitleStart", "/ts", "--title-start") { Description = "Filter: match the beginning of the window title. Case-insensitive unless --case is specified." };
    public static readonly Option<string?> WindowTitleRegexFilter = new Option<string?>("/WindowTitleRegex", "/tr", "--title-regex") { Description = "Filter: by window title regex." };
    public static readonly Option<bool?> EmptyOnlyFilter = new Option<bool?>("/Empty", "/e", "--empty") { Description = "Filter: Match only windows that have no title text." };
    public static readonly Option<bool?> CloakedOnlyFilter = new Option<bool?>("/Cloaked", "/cl", "--cloaked") { Description = "Filter: Match only cloaked windows (e.g., those hidden on other desktops or suspended)." };
    public static readonly Option<bool?> OnlyMainFilter = new Option<bool?>("/Main", "/mo", "--main") { Description = "Filter: Match only main windows (excludes others from search)." };

    // Behavior Group
    public static readonly Option<bool?> CaseSensitive = new Option<bool?>("/CaseSensitive", "/cs", "--case") { Description = "Case-sensitive matching." };
    public static readonly Option<bool?> SingleInstance = new Option<bool?>("/SingleInstance", "/si", "--single") { Description = "Enforce a unique match; abort if multiple windows meet the criteria." };
    public static readonly Option<bool?> IncludeInvisible = new Option<bool?>("/IncludeInvisible", "/i", "--invisible") { Description = "Include windows that are hidden or backgrounded (WS_VISIBLE=false)." };
    public static readonly Option<bool?> AllInstances = new Option<bool?>("/All", "/all", "--all") { Description = "Operate on all matches. Combined with 'move-sole', spreads windows across empty desktops." };
    public static readonly Option<bool?> Wrap = new("/Wrap", "/w", "--wrap") { Description = "Cycle between first and last desktops. Mutually exclusive with --create." };
    public static readonly Option<bool?> Create = new("/Create", "/c", "--create") { Description = "Create new desktops if target is missing. For bulk moves, creates 'Name', 'Name 2', etc." };
    public static readonly Option<bool?> SwitchFirst = new Option<bool?>("/SwitchFirst", "/swf", "--switch-first") { Description = "Switch focus to the target desktop before the operation." };
    public static readonly Option<bool?> SwitchTo = new Option<bool?>("/Switch", "/sw", "--switch") { Description = "Switch focus to the target desktop after the operation." };
    public static readonly Option<bool> CleanAfter = new("/Clean", "--clean") { Description = "After the operation, remove any desktops that are now empty." };
    public static readonly Option<bool?> Maximize = new("/Maximize", "/mx", "--maximize") { Description = "Maximize the window to full size after moving (may not work for all apps)." };
    public static readonly Option<bool?> Minimize = new("/Minimize", "/mi", "--minimize") { Description = "Minimize the window after moving." };
    public static readonly Option<bool?> NoShellExecute = new("/NoShell", "/ns", "--no-shell") { Description = "Disable UseShellExecute. Required for I/O redirection or bypassing Windows system aliases." };
    public static readonly Option<bool?> FillGaps = new("/FillGaps", "/fg", "--fill-gaps") { Description = "Create all missing desktops that go before the target (e.g., '1', 2', '3' if target is '4')." };
    public static readonly Option<bool?> UseNumericNames = new("/NumericNames", "/nn", "--numeric-names") { Description = "If true, create '4' instead of 'Desktop 4'" };

    public static readonly Option<SearchMode> SearchMode = new("/Mode", "/m", "--mode")
    {
        Description = "Search precision and behavior mode:\n" +
                  "  Fast     - Return immediately on any stable match.\n" +
                  "  Balanced - Wait for the first 'strong' (main) window to stabilize.\n" +
                  "  Thorough - Wait for all matching processes to stabilize or time out.",
        DefaultValueFactory = _ => AppDefaults.DefaultSearchMode
    };

    public static readonly Option<int> MaxDesktopsOverride = new("/MaxDesktops", "/md", "--max-desktops")
    {
        Description = "Override the default maximum desktop limit (Warning: high values can slow down Task View).",
        DefaultValueFactory = _ => AppDefaults.MaxDesktops
    };

    // Timing
    public static readonly Option<int> MaxTimeout = new Option<int>("/MaxTimeout", "/mt", "--timeout")
    {
        Description = "Timing: Max window search time(ms). Recommended 5000+ for heavy apps.",
        DefaultValueFactory = parseResult => AppDefaults.WindowSearchTimeoutMs,
    };

    public static readonly Option<int> WindowStabilityTime = new Option<int>("/StabilityTime", "/st", "--stability")
    {
        Description = "Timing: The duration the window structure must remain unchanged (no new sub-windows) to be considered stable before moving to a new desktop (ms).",
        DefaultValueFactory = parseResult => AppDefaults.WindowStabilityMs,
    };

    public static readonly Option<int> MoveDelay = new Option<int>("/Delay", "/d", "--delay")
    {
        Description = "Timing: Wait (ms) before starting the window search and move operation (useful for apps with long loading time).",
        DefaultValueFactory = _ => 0,
    };

    public static readonly Option<int> Steps = new("/Steps", "/ss", "--steps")
    {
        Description = "Number of desktops. Example: 'move-right --steps 3' jumps over 2 desktops.",
        DefaultValueFactory = _ => 1
    };

    // Misc
    public static readonly Option<bool?> Verbose = new("/Verbose", "/v", "--verbose") { Description = "Show detailed progress and error information during execution." };

    public static readonly Option[] Filters =
    [
        CloakedOnlyFilter,
        EmptyOnlyFilter,
        OnlyMainFilter,
        ModuleNameFilter,
        ModulePathFilter,
        ProcessIdFilter,
        ProcessNameFilter,
        WindowTitleFilter,
        WindowTitleStartFilter,
        WindowTitleRegexFilter,
    ];

    public static readonly Option[] SearchAndFilteringSpecificOptions =
    [
        IncludeInvisible,
        SearchMode,
        MaxTimeout,
        WindowStabilityTime,
    ];

    public static readonly Option[] RunCommandsOptions =
    [
        NoShellExecute,
    ];

    public static readonly Option[] SearchResultSelectionOptions = [
        AllInstances,
        SingleInstance
    ];

    public static readonly Option[] PreMoveActionOptions = [
        SwitchFirst,
    ];

    public static readonly Option[] PostMoveActionOptions = [
        Maximize,
        Minimize,
        CleanAfter,
        SwitchTo
    ];

    public static readonly Option[] CommonExecutionOptions =
    [
        CaseSensitive,
        Verbose,
    ];

    public static readonly Option[] OptionsForRunMoveCommands =
    [
        ..Filters,
        ..SearchResultSelectionOptions,
        ..SearchAndFilteringSpecificOptions,
        ..PreMoveActionOptions,
        ..PostMoveActionOptions,
        MoveDelay,
        ..CommonExecutionOptions,
    ];

    public static readonly Option[] OptionsForRelativeMoveCommands = [Wrap];

    public static readonly Option[] OptionsForCommandsThatSupportDesktopCreation = [Create, FillGaps];

    public static readonly Option[] OptionsForCommandsThatSupportDesktopTargetArgument = [UseNumericNames];

    public static readonly Option[] OptionsForCommandsThatSupportSteps = [Steps];

    public static readonly Option[] OptionsForCommandsSupportingMaxDesktops = [MaxDesktopsOverride];
}

internal class CommandLine
{
    private static bool _configured;
    private readonly Dictionary<Command, Func<Options, CancellationToken, Task<int>>> _actions = new();
    private Func<Options, CancellationToken, Task<int>>? _globalAction;
    private Action<Exception>? _onException;

    public Options? CommandOptions { get; private set; }

    public CommandLine()
    {
        if (_configured) return;
        _configured = true;
        CliCommands.RootCommand.TreatUnmatchedTokensAsErrors = true;
        foreach (var command in CliCommands.RunCommands) command.TreatUnmatchedTokensAsErrors = false;

        AddArgument(CliArguments.ApplicationPathArgument, CliCommands.RunCommands);
        AddOption(CliOptions.ApplicationArguments, CliCommands.RunCommands);
        AddOptions(CliOptions.RunCommandsOptions, CliCommands.RunCommands);
        AddOptions(CliOptions.OptionsForRelativeMoveCommands, CliCommands.MoveRelativeCommands);

        AddArgument(CliArguments.DesktopTargetArgument, CliCommands.CommandsThatSupportDesktopTargetArgument);
        AddOptions(CliOptions.OptionsForCommandsThatSupportDesktopTargetArgument, CliCommands.CommandsThatSupportDesktopTargetArgument);

        AddArgument(CliArguments.ProfileFileArgument, CliCommands.ProfileCommands);

        AddOption(CliOptions.Verbose, CliCommands.CommandsThatSupportVerboseOption);
        AddOptions(CliOptions.OptionsForCommandsThatSupportDesktopCreation, CliCommands.CommandsThatSupportDesktopCreation);
        AddOptions(CliOptions.OptionsForCommandsThatSupportSteps, CliCommands.CommandsThatSupportStepsOption);
        AddOptions(CliOptions.OptionsForCommandsSupportingMaxDesktops, CliCommands.CommandsSupportingMaxDesktops);

        AddOptions(CliOptions.SearchAndFilteringSpecificOptions, CliCommands.RunMoveCommands);

        AddOptions(CliOptions.OptionsForRunMoveCommands.Except([CliOptions.Create]), CliCommands.RunMoveCommands);
        AddOption(CliOptions.Create, CliCommands.CommandsThatSupportDesktopCreation);

        foreach (var cmdWithSubs in CliCommands.CommandsSupportingSubcommands)
        {
            foreach (var sub in cmdWithSubs.Subs)
            {
                cmdWithSubs.Cmd.Add(sub);
            }
        }

        foreach (var cmd in CliCommands.All)
        {
            CliCommands.RootCommand.Add(cmd);
        }



        AddValidators();
        AddHelpAliases();
    }

    public CommandLine MapCommands(Func<Options, IApplicationCommand> factory)
    {
        foreach (var command in CliCommands.All.Except([CliCommands.Profile]).Concat(CliCommands.ProfileCommands))
            Map(command, (options, ct) => factory(options).ExecuteAsync(ct));
        return this;
    }

    // asynchronous handler setters
    public CommandLine MapAll(Func<Options, CancellationToken, Task<int>> func)
    {
        _globalAction = func;
        return this;
    }

    public CommandLine Map(Command cmd, Func<Options, CancellationToken, Task<int>> func)
    {
        _actions[cmd] = func;
        return this;
    }

    // setters that wrap synchronous handlers into a Task
    public CommandLine MapAll(Func<Options, CancellationToken, int> func)
        =>
        MapAll((o, ct) => Task.FromResult(func(o, ct)));

    public CommandLine Map(Command cmd, Func<Options, CancellationToken, int> func)
        =>
        Map(cmd, (o, ct) => Task.FromResult(func(o, ct)));

    public CommandLine MapGroup(IEnumerable<Command> commands, Func<Options, CancellationToken, Task<int>> func)
    {
        foreach (var cmd in commands)
        {
            Map(cmd, func);
        }
        return this;
    }

    public async Task<int> RunCommand(string[] args, Action<Exception> onException)
    {
        _onException = onException;
        BindActions();

        var parseResult = CliCommands.RootCommand.Parse(args);
        var isHelp = parseResult.Action is HelpAction;
        var noSubCommand = parseResult.CommandResult.Command == CliCommands.RootCommand;

        if (parseResult.Errors.Any() || isHelp)
        {
            if (noSubCommand || parseResult.Errors.Any())
            {
                EscapingErrorCheck(args);
            }

            if (!isHelp)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Use '{CliCommands.Name} <command> --help' for more information.");
                Console.ResetColor();
            }

            // Let the InvokeAsync handle the help display
            return await parseResult.InvokeAsync();
        }

        CommandOptions = GetOptions(parseResult);
        return await parseResult.InvokeAsync();
    }

    private void AddArgument(Argument arg, IEnumerable<Command> commands)
    {
        foreach (var cmd in commands)
        {
            cmd.Add(arg);
        }
    }

    private void AddArguments(IEnumerable<Argument> args, IEnumerable<Command> commands)
    {
        foreach (var cmd in commands)
        {
            foreach (var arg in args)
            {
                cmd.Add(arg);
            }
        }
    }

    private void AddOption(Option opt, IEnumerable<Command> commands)
    {
        foreach (var cmd in commands)
        {
            if (!cmd.Options.Contains(opt)) cmd.Add(opt);
        }
    }

    private void AddOptions(IEnumerable<Option> options, IEnumerable<Command> commands)
    {
        foreach (var cmd in commands)
        {
            foreach (var opt in options)
            {
                if (!cmd.Options.Contains(opt)) cmd.Add(opt);
            }
        }
    }

    private void BindActions()
    {
        foreach (var (cmd, action) in _actions)
        {
            cmd.SetAction(async (parseResult, ct) =>
            {
                try
                {
                    ArgumentNullException.ThrowIfNull(CommandOptions);

                    if (_globalAction != null)
                    {
                        await _globalAction.Invoke(CommandOptions, ct);
                    }

                    if (action == null)
                    {
                        return 1;
                    }

                    return await action.Invoke(CommandOptions, ct);
                }
                catch (Exception ex)
                {
                    _onException?.Invoke(ex);
                    return 1;
                }
            });
        }
    }

    private void EscapingErrorCheck(string[] args)
    {
        // Detect if any argument contains a "misplaced" quote 
        // (a hallmark of the Windows trailing backslash bug)
        bool hasEscapingError = args.Any(a => a.Contains('"'));
        if (hasEscapingError)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("(!) Potential Windows Path Escaping Issue Detected:");
            Console.WriteLine("    If a quoted path ends in a backslash (e.g. \"C:\\Temp\\\"), ");
            Console.WriteLine("    the shell 'swallows' the next argument.");
            Console.ResetColor();

            Console.WriteLine("\nHow the shell parsed your input:");
            for (int i = 0; i < args.Length; i++)
            {
                string marker = args[i].Contains('"') ? " [Contains escaped quote]" : "";
                Console.WriteLine($"  Arg {i}: [{args[i]}]{marker}");
            }
            Console.WriteLine("\nFix: Use a double backslash at the end: \"C:\\Path\\\\\"\n");
        }
    }

    // the current effective limit from the override option
    private int GetCurrentMaxDesktopsLimit(SymbolResult result)
        =>
        result.GetResult(CliOptions.MaxDesktopsOverride)?.GetValueOrDefault<int>()
                ?? AppDefaults.MaxDesktops;

    private void AddValidators()
    {
        // Arguments

        CliArguments.ApplicationPathArgument.Validators.Add(result =>
        {
            var rawValue = result.GetValueOrDefault<string>();
            var sanitizedValue = SanitizePath(rawValue);

            if (string.IsNullOrEmpty(sanitizedValue))
            {
                result.AddError("The application path cannot be empty.");
                return;
            }

            bool isSimpleFileName = !sanitizedValue.Contains(Path.DirectorySeparatorChar) &&
                                    !sanitizedValue.Contains(Path.AltDirectorySeparatorChar);

            if (!isSimpleFileName)
            {
                if (!File.Exists(sanitizedValue))
                {
                    result.AddError($"The application file was not found: '{sanitizedValue}'");
                    return;
                }

                if (File.GetAttributes(sanitizedValue).HasFlag(FileAttributes.Directory))
                {
                    result.AddError("The application path must point to a file, not a directory.");
                    return;
                }
            }
        });

        CliArguments.DesktopTargetArgument.Validators.Add(result =>
        {
            var rawValue = result.GetValueOrDefault<string>();

            // the user provided a number (e.g., "3")
            if (int.TryParse(rawValue, out int value))
            {
                if (value < 1)
                {
                    result.AddError("Desktop number must be 1 or greater.");
                    return;
                }

                var currentMaxLimit = GetCurrentMaxDesktopsLimit(result);
                if (value > currentMaxLimit)
                {
                    string baseError = $"Desktop number {value} exceeds the current limit of {currentMaxLimit}.";
                    if (currentMaxLimit == AppDefaults.MaxDesktops && value <= AppDefaults.HardMaxLimit)
                    {
                        result.AddError($"{baseError} Use '--max-desktops' to increase this limit up to {AppDefaults.HardMaxLimit}.");
                    }
                    else
                    {
                        result.AddError(baseError);
                    }
                }
            }
            // the user provided a name (e.g., "Office")
            else
            {
                ValidateDesktopName(result, rawValue);
            }
        });

        // Option-specific validators

        var fileNameValidators = new Action<OptionResult>[]
        {
            ValidateFileNameChars,
            result =>
            {
                var value = result.GetValueOrDefault<string>();
                if (string.IsNullOrWhiteSpace(value)) return;

                if (value.EndsWith(" ") || value.EndsWith("."))
                    result.AddError($"{result.Option.Name} cannot end with a space or a period.");

                if (value.Length > 255)
                    result.AddError($"{result.Option.Name} is too long (max 255 characters).");
            }
        };

        foreach (var validator in fileNameValidators)
        {
            CliOptions.ProcessNameFilter.Validators.Add(validator);
            CliOptions.ModuleNameFilter.Validators.Add(validator);
        }

        CliOptions.ModulePathFilter.Validators.Add(ValidatePathChars);
        CliOptions.ModulePathFilter.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(value)) return;

            if (value.Contains('*') || value.Contains('?'))
                result.AddError("Wildcards (* or ?) are not supported in the module path filter.");

            if (value.Length >= 260)
                result.AddError("The provided path exceeds the standard Windows limit of 260 characters.");
        });

        CliOptions.MaxDesktopsOverride.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();
            if (value < 1 || value > AppDefaults.HardMaxLimit)
            {
                result.AddError($"Maximum desktop limit cannot exceed {AppDefaults.HardMaxLimit} to ensure system stability.");
            }
        });

        CliOptions.Steps.Validators.Add(result =>
        {
            int steps = result.GetValueOrDefault<int>();
            if (steps < 1)
            {
                result.AddError("The --steps argument must be a positive integer (1 or greater).");
                return;
            }

            var currentMaxLimit = GetCurrentMaxDesktopsLimit(result);
            if (steps > currentMaxLimit)
            {
                result.AddError($"Steps value ({steps}) exceeds the maximum allowed desktops ({currentMaxLimit}).");
            }
        });

        int MaxReasonableDelayMs = 300_000;
        int MaxReasonableTimeoutMs = 300_000;
        int MaxReasonableStabilityMs = 30_000;

        int MaxReasonableDelayMin = (int)(TimeSpan.FromMilliseconds(MaxReasonableDelayMs).TotalSeconds / 60);
        int MaxReasonableTimeoutMin = (int)(TimeSpan.FromMilliseconds(MaxReasonableTimeoutMs).TotalSeconds / 60);
        int MaxReasonableStabilitySec = (int)(TimeSpan.FromMilliseconds(MaxReasonableStabilityMs).TotalSeconds);

        CliOptions.MoveDelay.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();

            if (value < 0)
            {
                result.AddError("The --delay (/md) argument cannot be negative.");
                return;
            }

            if (value > MaxReasonableDelayMs)
            {
                result.AddError($"Move delay of {value}ms is unusually high. Please keep it under {MaxReasonableDelayMs}ms ({MaxReasonableDelayMin}min).");
            }
        });

        CliOptions.MaxTimeout.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();

            if (value < 100)
            {
                result.AddError("The --timeout (/to) must be at least 100ms to allow for process discovery.");
                return;
            }

            if (value > MaxReasonableTimeoutMs)
            {
                result.AddError($"The search timeout of {value}ms is unusually high. Please keep it under {MaxReasonableTimeoutMs}ms ({MaxReasonableTimeoutMin}min).");
            }
        });

        CliOptions.WindowStabilityTime.Validators.Add(result =>
        {
            var stability = result.GetValueOrDefault<int>();
            var timeout = result.GetResult(CliOptions.MaxTimeout)?.GetValueOrDefault<int>()
                          ?? AppDefaults.WindowSearchTimeoutMs;

            if (stability < 0)
            {
                result.AddError("The --stability (/st) argument cannot be negative.");
                return;
            }

            if (stability > timeout)
            {
                result.AddError($"Stability time ({stability}ms) cannot be greater than the max timeout ({timeout}ms). The window would never stabilize in time.");
                return;
            }

            if (stability > MaxReasonableStabilityMs)
            {
                result.AddError($"Stability time of {stability}ms is unusually high. Please keep it under {MaxReasonableStabilityMs}ms ({MaxReasonableStabilitySec}s).");
            }
        });

        CliOptions.ProcessIdFilter.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<uint?>();

            if (value < 0)
            {
                result.AddError("The --pid (/pid) filter cannot be a negative number.");
                return;
            }

            if (value == 0)
            {
                result.AddError("Process ID 0 (System Idle) cannot be targeted as it does not have a movable window.");
            }
        });

        CliOptions.WindowTitleRegexFilter.Validators.Add(result =>
        {
            var pattern = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(pattern)) return;

            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(AppDefaults.MaxRegexDelay));
            }
            catch (ArgumentException ex)
            {
                result.AddError($"Invalid Regular Expression pattern: {ex.Message}");
            }
        });

        // Command-level validators

        var commonValidators = new Action<CommandResult>[]
        {
            ValidateModuleFileNames,
            ValidateSpecificRelativeCommandsOptions
        };

        foreach (var cmd in CliCommands.RunMoveCommands)
        {
            foreach (var validator in commonValidators)
            {
                cmd.Validators.Add(validator);
            }

            cmd.Validators.Add(result => ValidateMutuallyExclusive(result, CliOptions.SingleInstance, CliOptions.AllInstances));
            cmd.Validators.Add(result => ValidateMutuallyExclusive(result, CliOptions.Maximize, CliOptions.Minimize));
        }

        foreach (var cmd in CliCommands.CommandsThatSupportDesktopCreation)
        {
            cmd.Validators.Add(result => ValidateAllowedOptionsForOption(result, CliOptions.FillGaps, CliOptions.Create));
            cmd.Validators.Add(result => ValidateAllowedOptionsForOption(result, CliOptions.UseNumericNames, CliOptions.Create));
        }

        foreach (var cmd in CliCommands.All.Except(CliCommands.CommandsThatSupportDesktopCreation))
        {
            cmd.Validators.Add(result => ValidateAllowedCommandsForOption(result, CliOptions.Create, CliCommands.CommandsThatSupportDesktopCreation));
            cmd.Validators.Add(result => ValidateAllowedCommandsForOption(result, CliOptions.FillGaps, CliCommands.CommandsThatSupportDesktopCreation));
            cmd.Validators.Add(result => ValidateAllowedCommandsForOption(result, CliOptions.UseNumericNames, CliCommands.CommandsThatSupportDesktopCreation.Concat([CliCommands.Create]).ToArray()));
        }

        foreach (var cmd in CliCommands.MoveRelativeCommands)
        {
            cmd.Validators.Add(result => ValidateMutuallyExclusive(result, CliOptions.Wrap, CliOptions.Create));
        }

        CliCommands.MoveAll.Validators.Add(ValidateMoveAllOptions);

        CliCommands.Clean.Validators.Add(result =>
        {
            if (HasAnyFilter(result))
            {
                result.AddError("The 'clean' command does not support window filters.");
            }
        });

        CliCommands.ProfileLoad.Validators.Add(result =>
        {
            FileInfo? file = result.GetValue(CliArguments.ProfileFileArgument);

            if (file == null)
            {
                result.AddError($"Specify the path to the profile json file.");
                return;
            }

            if (!file.Exists)
            {
                result.AddError($"The profile file '{file.FullName}' was not found.");
            }
        });

        CliCommands.ProfileSave.Validators.Add(result =>
        {
            FileInfo? file = result.GetValue(CliArguments.ProfileFileArgument);

            if (file == null)
            {
                result.AddError($"Path to the profile json file must be specified.");
                return;
            }

            if (!IsPathValid(file.FullName))
            {
                result.AddError($"The profile file '{file.FullName}' contains illegal characters (e.g. /, \\, :, *)..");
            }
        });

        //CliArguments.ProfileFileArgument.Validators.Add(result =>
        //{
        //	var fileInfo = result.GetValueOrDefault<FileInfo>();

        //	if (fileInfo != null && !fileInfo.Exists)
        //	{
        //		result.AddError($"The profile file '{fileInfo.FullName}' was not found.");
        //	}
        //});
    }

    private static void ValidateDesktopName(SymbolResult result, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        // Rule: Length (64 is standard for Windows UI labels)
        if (name.Length > 64)
        {
            result.AddError($"Desktop name '{name}' is too long (max 64 characters).");
        }

        // prevent characters that might cause issues if names are ever saved to disk/registry
        var invalidChars = Path.GetInvalidFileNameChars();
        if (name.Any(c => invalidChars.Contains(c)))
        {
            result.AddError($"Desktop name '{name}' contains illegal characters (e.g. /, \\, :, *).");
        }

        // ensure no tabs or newlines are included.
        if (name.Any(char.IsControl))
        {
            result.AddError("Desktop name cannot contain control characters.");
        }
    }

    private void AddHelpAliases()
    {
        foreach (var cmd in CliCommands.All.Concat([CliCommands.RootCommand]))
        {
            var helpOption = cmd.Options.OfType<HelpOption>().FirstOrDefault();
            if (helpOption != null)
            {
                helpOption.Aliases.Add("-?");
                helpOption.Aliases.Add("/?");
                helpOption.Aliases.Add("/Help");
                helpOption.Aliases.Add("/h");
            }
        }
    }

    private void ValidateModuleFileNames(CommandResult result)
    {
        var modNameRes = result.GetResult(CliOptions.ModuleNameFilter);
        var modPathRes = result.GetResult(CliOptions.ModulePathFilter);

        // If both are provided, ensure the filenames match to avoid ambiguity
        if (modNameRes != null && modPathRes != null)
        {
            var name = modNameRes.GetValueOrDefault<string>();
            var path = modPathRes.GetValueOrDefault<string>();

            if (!string.Equals(name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            {
                result.AddError($"Ambiguous options. File names do not match: '{name}' and '{Path.GetFileName(path)}'.");
            }
        }
    }

    private bool HasAnyFilter(CommandResult result)
    {
        return CliOptions.Filters.Any(f => result.GetResult(f) != null);
    }

    private void ValidateFileNameChars(OptionResult result)
    {
        var value = result.GetValueOrDefault<string>();

        if (!IsFileNameValid(value))
        {
            result.AddError($"The name '{value}' contains invalid characters.");
        }
    }

    private void ValidatePathChars(OptionResult result)
    {
        var path = result.GetValueOrDefault<string>();
        if (!IsPathValid(path))
        {
            result.AddError($"{result.Option.Name} contains invalid characters.");
        }
    }

    private bool IsFileNameValid(string fileName)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            if (fileName.Any(c => invalidChars.Contains(c)))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private bool IsPathValid(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var invalidChars = Path.GetInvalidPathChars();

            if (path.Any(c => invalidChars.Contains(c)))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private void ValidateMutuallyExclusive(CommandResult result, Option opt1, Option opt2)
    {
        var res1 = result.GetResult(opt1);
        var res2 = result.GetResult(opt2);

        if (res1 != null && res2 != null)
        {
            result.AddError($"Ambiguous options: '{opt1.GetFirstAlias()}' and '{opt2.GetFirstAlias()}' cannot be used together.");
        }
    }

    private void ValidateSpecificRelativeCommandsOptions(CommandResult result)
    {
        // --wrap is for move-right and move-left
        ValidateAllowedCommandsForOption(result, CliOptions.Wrap, CliCommands.MoveRight, CliCommands.MoveLeft);
    }

    private void ValidateAllowedCommandsForOption(
        CommandResult result,
        Option option,
        params Command[] allowedCommands)
    {
        if (result.GetResult(option) != null && !allowedCommands.Contains(result.Command))
        {
            var allowedCommandNames = allowedCommands.Select(c => c.Name).ToArray();
            var commandsStr = string.Join(", ", allowedCommandNames.Select(n => $"'{n}'"));
            var pluralS = allowedCommandNames.Length > 1 ? "s" : "";

            result.AddError($"The {option.GetFirstAlias()} option can only be used with the {commandsStr} command{pluralS}.");
        }
    }

    private void ValidateAllowedOptionsForOption(CommandResult result, Option opt, params Option[] allowedOptions)
    {
        if (result.GetResult(opt) != null)
        {
            bool hasAllowedOption = allowedOptions.Any(o => result.GetResult(o) != null);
            if (!hasAllowedOption)
            {
                var allowedAliases = allowedOptions.Select(o => o.GetFirstAlias());
                string optionsStr = string.Join(", ", allowedAliases.Select(a => $"'{a}'"));

                result.AddError($"Option '{opt.GetFirstAlias()}' can be used only when {optionsStr} is also specified.");
            }
        }
    }

    private void ValidateMoveAllOptions(CommandResult result)
    {
        if (result.Command == CliCommands.MoveAll
         && result.GetResult(CliOptions.SingleInstance) != null)
        {
            result.AddError("The --single option cannot be used with the 'move-all' command because 'move-all' already implies all instances.");
        }
    }

    // escapes an argument according to standard Windows command-line rules
    private string EscapeArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        // if it doesn't need quotes, return as is
        if (arg.IndexOfAny(new char[] { ' ', '\t', '\n', '\r', '\"' }) == -1)
        {
            return arg;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('\"');
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashCount = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashCount++;
                i++;
            }

            if (i == arg.Length)
            {
                // escape backslashes before the closing quote
                sb.Append('\\', backslashCount * 2);
                break;
            }
            else if (arg[i] == '\"')
            {
                // escape backslashes before an internal quote, plus the quote itself
                sb.Append('\\', backslashCount * 2 + 1);
                sb.Append('\"');
            }
            else
            {
                // normal backslashes
                sb.Append('\\', backslashCount);
                sb.Append(arg[i]);
            }
        }
        sb.Append('\"');
        return sb.ToString();
    }

    private static string? SanitizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return rawPath;

        // 1. Fix the "Windows trailing backslash" bug: "C:\Path\" --args
        // results in rawPath being: C:\Path" --args
        // (remove accidental trailing quotes caused by \" at the end of a path)
        string sanitized = rawPath.TrimEnd('\"');

        // 2. Check if the shell swallowed the next arguments into this string
        if (sanitized.Contains(" --") || sanitized.Contains(" /"))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("(!) Warning: Potential path escaping issue detected.");
            Console.WriteLine($"    The path '{sanitized}' appears to contain CLI flags.");
            Console.WriteLine("    Ensure you use double backslashes at the end of quoted paths (e.g., \"C:\\Path\\\\\").");
            Console.ResetColor();
        }

        return sanitized;
    }

    public Options GetOptions(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;


        string? applicationPath = SanitizePath(parseResult.GetValue(CliArguments.ApplicationPathArgument));
        string? modulePath = SanitizePath(parseResult.GetValue(CliOptions.ModulePathFilter));

        var isRunCommand = CliCommands.RunCommands.Any(c => c == parseResult.CommandResult.Command);
        var hasAnyFilter = HasAnyFilter(parseResult.CommandResult);

        // default for 'run' command: use application path for the module filter if no filters are specified
        string? mpFilter = modulePath;
        if (isRunCommand && !hasAnyFilter)
        {
            mpFilter = applicationPath;
        }

        // conditional ApplicationArguments logic
        string? formattedArgs = null;
        if (isRunCommand)
        {
            // Use either explicit --args, or double-dash tokens + trailing unmatched arguments:
            //
            // VDL <..parameters..> --args [arg1] [arg2] [arg3]
            //   or
            // VDL <..parameters..> -- [arg1] [arg2] [arg3]
            //   or
            // VDL <..parameters..> [arg1] -- [arg2] [arg3]
            //   or
            // VDL <..parameters..> [arg1] [arg2] -- [arg3]
            //   or
            // VDL <..parameters..> [arg1] [arg2] [arg3]
            //

            var argsOptionValue = parseResult.GetValue(CliOptions.ApplicationArguments);
            var rawArgs = (argsOptionValue is { Length: > 0 }) ? argsOptionValue : parseResult.UnmatchedTokens;
            if (rawArgs.Any())
            {
                formattedArgs = string.Join(" ", rawArgs.Select(EscapeArgument));
            }
        }

        var desktopsLimitOverride = parseResult.GetValue(CliOptions.MaxDesktopsOverride);

        MoveMode? moveMode = command switch
        {
            var cmd when cmd == CliCommands.Run || cmd == CliCommands.Move || cmd == CliCommands.MoveAll =>
                MoveMode.MoveExplicit,

            var cmd when cmd == CliCommands.MoveRight =>
                MoveMode.MoveRight,

            var cmd when cmd == CliCommands.MoveLeft =>
                MoveMode.MoveLeft,

            var cmd when cmd == CliCommands.MoveOut || cmd == CliCommands.RunOut =>
                MoveMode.MoveOut,

            var cmd when cmd == CliCommands.MoveSole || cmd == CliCommands.RunSole =>
                MoveMode.MoveSole,

            _ => null
        };

        return new()
        {
            AllInstances = (command == CliCommands.MoveAll) || (parseResult.GetValue(CliOptions.AllInstances) == true),
            ApplicationArguments = formattedArgs,
            ApplicationPath = applicationPath,
            AutoCreateDesktopsMax = desktopsLimitOverride > 0 && desktopsLimitOverride <= AppDefaults.HardMaxLimit ? desktopsLimitOverride : AppDefaults.MaxDesktops,
            CliCommand = command,
            FilterProcessModulePath = isRunCommand && !hasAnyFilter && applicationPath != null && Path.GetFileName(applicationPath) == applicationPath ? null : mpFilter,
            MaxDesktopsOverride = desktopsLimitOverride,
            MoveMode = moveMode,

            // direct mapping
            CaseSensitive = parseResult.GetValue(CliOptions.CaseSensitive),
            CleanAfter = parseResult.GetValue(CliOptions.CleanAfter),
            CreateDesktops = parseResult.GetValue(CliOptions.Create),
            DesktopsJsonFile = parseResult.GetValue(CliArguments.ProfileFileArgument),
            DesktopTarget = parseResult.GetValue(CliArguments.DesktopTargetArgument),
            FillGaps = parseResult.GetValue(CliOptions.FillGaps),
            FilterOnlyCloakedWindows = parseResult.GetValue(CliOptions.CloakedOnlyFilter),
            FilterOnlyEmptyTitles = parseResult.GetValue(CliOptions.EmptyOnlyFilter),
            FilterOnlyMain = parseResult.GetValue(CliOptions.OnlyMainFilter),
            FilterProcessId = parseResult.GetValue(CliOptions.ProcessIdFilter),
            FilterProcessModuleName = isRunCommand && !hasAnyFilter && applicationPath != null && Path.GetFileName(applicationPath) == applicationPath ? (Path.HasExtension(applicationPath) ? applicationPath : applicationPath + ".exe") : parseResult.GetValue(CliOptions.ModuleNameFilter),
            FilterProcessName = parseResult.GetValue(CliOptions.ProcessNameFilter),
            FilterWindowTitle = parseResult.GetValue(CliOptions.WindowTitleFilter),
            FilterWindowTitleStart = parseResult.GetValue(CliOptions.WindowTitleStartFilter),
            FilterWindowTitleRegex = parseResult.GetValue(CliOptions.WindowTitleRegexFilter),
            IncludeInvisible = parseResult.GetValue(CliOptions.IncludeInvisible),
            Maximize = parseResult.GetValue(CliOptions.Maximize),
            MaxTimeout = parseResult.GetValue(CliOptions.MaxTimeout),
            Minimize = parseResult.GetValue(CliOptions.Minimize),
            MoveDelay = parseResult.GetValue(CliOptions.MoveDelay),
            NoShellExecute = parseResult.GetValue(CliOptions.NoShellExecute),
            SearchMode = parseResult.GetValue(CliOptions.SearchMode),
            SingleInstance = parseResult.GetValue(CliOptions.SingleInstance),
            StabilityTime = parseResult.GetValue(CliOptions.WindowStabilityTime),
            Steps = parseResult.GetValue(CliOptions.Steps),
            SwitchFirst = parseResult.GetValue(CliOptions.SwitchFirst),
            SwitchToDesktop = parseResult.GetValue(CliOptions.SwitchTo),
            UseNumericNames = parseResult.GetValue(CliOptions.UseNumericNames),
            Verbose = parseResult.GetValue(CliOptions.Verbose),
            WrapDesktops = parseResult.GetValue(CliOptions.Wrap),
        };
    }
}

internal static class OptionsExtensions
{
    public static string GetFirstAlias(this Option option)
    {
        return option.Aliases.FirstOrDefault(a => a.StartsWith("--")) ?? option.Name;
    }
}
