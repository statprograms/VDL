using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Polly;
using VDL.Cli;
using VDL.OS;
using WindowsDesktop;
using Polly.Retry;
using System.Collections.Concurrent;
using Polly.Timeout;

namespace VDL;

/// <summary>Receiver for application commands: coordinates discovery, planning and physical execution.</summary>
internal static class DesktopCommandReceiver
{
    // Capture before COM initialization, delays, or --switch-first changes the focus.
    internal static IntPtr InvokingTerminalWindow { get; set; }
    private const int SystemWindowsPollPeriod = 25;
    internal static int TotalItems;
    internal static int ProcessedItems;
    public static int CleanEmptyDesktops(Options options, CancellationToken ct)
    {
        var taskView = new TaskView(options);
        var empty = taskView.Flatten().Reverse().Where(d => SystemWindows.GetWindowsOnDesktop(d.PhysicalDesktop!).Count == 0).ToList();
        int removed = 0;
        foreach (var desktop in empty)
        {
            ct.ThrowIfCancellationRequested();
            if (taskView.Flatten().Count() == 1) break;
            taskView.Execute(new RemoveDesktopCommand(desktop));
            removed++;
        }
        taskView.ApplyAsync(ct).GetAwaiter().GetResult();
        Console.WriteLine($"[Cleanup] Removed {removed} empty desktop(s).");
        return 0;
    }

    public static int ListDesktops(Options options, CancellationToken ct)
    {
        var desktops = VirtualDesktop.GetDesktops();
        var currentId = VirtualDesktop.Current.Id;

        Console.WriteLine("Virtual Desktops:");

        for (int i = 0; i < desktops.Length; i++)
        {
            var d = desktops[i];
            bool isCurrent = d.Id == currentId;

            string pointer = isCurrent ? " ->" : "   ";
            string name = string.IsNullOrWhiteSpace(d.Name) ? "(No Name)" : d.Name;
            string currentLabel = isCurrent ? " (Current)" : string.Empty;

            string output = $"{pointer} {i + 1,2}: {name,-20} {d.Id}{currentLabel}";

            if (options.Verbose == true)
            {
                var windowCount = SystemWindows.GetWindowsOnDesktop(d)?.Count ?? 0;

                string statusTag = windowCount > 0
                   ? $"[Windows: {windowCount,2}]"
                   : "[Empty]";

                output = $"{pointer} {i + 1,2}: {name,-20} {statusTag,-14} {d.Id}{currentLabel}";
            }

            Console.WriteLine(output);
        }

        if (options.Verbose == true)
        {
            Console.WriteLine($"\nTotal: {desktops.Length} desktop(s).");
        }

        return 0;
    }

    public static int CountDesktops(Options options, CancellationToken ct)
    {
        int count = VirtualDesktop.GetDesktops().Length;

        if (options.Verbose == true)
        {
            Console.WriteLine($"Total Virtual Desktops: {count}");
        }
        else
        {
            Console.WriteLine(count);
        }

        return 0;
    }

    public static async Task<int> CreateDesktops(Options options, CancellationToken ct)
    {
        int createNum = options.Steps > 0 ? options.Steps : 1;
        string template = options.DesktopTarget ?? SystemDesktops.GetLocalizedDesktopName();

        if (options.Verbose == true)
        {
            Console.WriteLine($"[Verbose] Planning {createNum} desktops using template: \"{template}\"");
        }

        // 1. Initialize the tree-based TaskView
        var taskView = new TaskView(options);

        // 2. Perform in-memory mutation
        // Naming and creation are deferred until ApplyAsync.
        taskView.Execute(new CreateCommand(createNum, template,
            StartNumber: options.DesktopTarget == null ? taskView.Flatten().Count() + 1 : null,
            NumericNames: options.UseNumericNames == true));
        TotalItems = createNum;

        if (options.Verbose == true)
        {
            taskView.PrintPreview();
        }

        // 3. Commit to OS
        try
        {
            await taskView.ApplyAsync(ct);
            ProcessedItems = createNum;
            return 0;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                Console.Error.WriteLine($"[Error] Failed to apply desktop changes: {ex.Message}");

            taskView.RollbackEmptyCreatedDesktops();

            return 1;
        }
    }

    // Program.cs - Logic for 'vdl profile load/save'
    public static async Task<int> HandleProfileCommand(Options options, CancellationToken ct)
    {
        var taskView = new TaskView(options);

        //string filePath = result.GetValueForArgument(options.DesktopsJsonFile);
        var file = options.DesktopsJsonFile;
        if (file == null)
        {
            Console.Error.WriteLine($"[Error] No profile file");
            return 1;
        }

        var filePath = file.FullName;

        if (options.CliCommand == CliCommands.ProfileSave)
        {
            string json = taskView.CreateSnapshotJson();
            await File.WriteAllTextAsync(filePath, json, ct);
            Console.WriteLine($"[Success] Profile saved to {filePath}");
            return 0;
        }

        if (options.CliCommand == CliCommands.ProfileLoad)
        {
            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"[Error] Profile not found: {filePath}");
                return 1;
            }

            string json = await File.ReadAllTextAsync(filePath, ct);

            // 1. Mutate the in-memory tree (Construction)
            taskView.Execute(new RestoreProfileCommand(json));

            // 2. Preview (Dry Run)
            if (options.Verbose == true)
            {
                taskView.PrintPreview();
            }

            // 3. Commit to OS (The 'Apply' operation)
            Console.WriteLine("[Apply] Synchronizing OS Task View...");
            await taskView.ApplyAsync(ct);

            return 0;
        }

        return 1;
    }

    internal static async Task<int> StartOnDesktopAsync(Options options, CancellationToken ct)
    {
        if (options.Verbose == true && !string.IsNullOrEmpty(options.ApplicationPath))
        {
            Console.WriteLine($"[Run] Executable: {options.ApplicationPath}");

            if (!string.IsNullOrEmpty(options.ApplicationArguments))
            {
                Console.WriteLine($"[Run] Arguments:  {options.ApplicationArguments}");
            }
            else
            {
                Console.WriteLine("[Run] Arguments:  (None)");
            }
        }

        var taskView = new TaskView(options);
        var target = taskView.PlanTarget(null);
        var original = VirtualDesktop.Current;
        try
        {
            if (options.SwitchFirst == true)
            {
                await taskView.ApplyAsync(ct);
                new SwitchDesktopCommand(target.PhysicalDesktop!).Execute();
            }
            ct.ThrowIfCancellationRequested();
            using var process = new LaunchProcessCommand(CreateStartInfo(options)).Execute();
            // Shell activation can reuse an existing process and return no Process object.
            int result = await MoveToDesktopAsync(options, ct, taskView, target);
            if (result != 0) taskView.RollbackEmptyCreatedDesktops();
            return result;
        }
        catch
        {
            taskView.RollbackEmptyCreatedDesktops();
            throw;
        }
        finally
        {
            if (options.SwitchFirst == true && options.SwitchToDesktop != true) new SwitchDesktopCommand(original).Execute();
        }
    }

    internal static int PrintProcessTree(Options _, CancellationToken ct)
    {
        new ProcessTree(fetchModulePaths: true).Print();
        return 0;
    }

    internal static async Task<int> MoveToDesktopAsync(Options options, CancellationToken ct, TaskView? preparedView = null, SimpleDesktop? preparedTarget = null)
    {
        var allDesktops = SystemDesktops.All;
        if (allDesktops.Length == 0)
        {
            return 1;
        }

        int res = 0;

        // define a policy for transient Win32/COM issues
        var retryPolicy = Policy
            .Handle<Win32Exception>()
            .Or<COMException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromMilliseconds(100 * retryAttempt),
                (ex, timeSpan, retryCount, context) =>
                {
                    if (options.Verbose == true)
                    {
                        string errorType = ex is COMException ? "COM" : "Win32";
                        Console.WriteLine($"[{errorType} Retry {retryCount}] Waiting {timeSpan.TotalMilliseconds}ms: {ex.Message}");
                    }
                });

        var taskView = preparedView ?? new TaskView(options);
        var postMoveTasks = new List<Task>();

        if (options.MoveDelay is int delay && delay > 0)
        {
            Console.WriteLine($"* Waiting {delay}ms delay before starting operation...");
            await Task.Delay(delay, ct);
        }

        int currentDesktopIndex = Array.FindIndex(allDesktops, d => d.Id == VirtualDesktop.Current.Id);

        CreateFilters(options, out var processFilters, out var windowFilters);

        var hasMainFilter =
               processFilters.IdFilter != null
            || processFilters.NameFilter != null
            || processFilters.ModuleNameFilter != null
            || processFilters.ModulePathFilter != null
            || windowFilters.TitleFilter != null
            || options.AllInstances == true || options.FilterOnlyMain == true
            || options.FilterOnlyCloakedWindows == true || options.IncludeInvisible == true;

        bool IsOnCurrentDesktop(WData window)
        {
            var appDesktop = SystemWindows.GetVirtualDesktopForWindow(window.Handle);

            return appDesktop != null && allDesktops[Math.Max(0, currentDesktopIndex)].Id == appDesktop.Id;
        }

        WData[] targetWindows;
        string message = String.Empty;
        if (hasMainFilter)
        {
            var stableWindows = await FindStableWindowsProcAsync(options, processFilters, windowFilters, ct);

            var query = stableWindows
                .SelectMany(w => w.BestCandidates)
                .Where(w => options.IncludeInvisible == true || w.IsVisible)
                .OrderByDescending(IsOnCurrentDesktop)
                .ThenByDescending(w => w.GetScore());

            targetWindows = options.AllInstances == true
                ? query.ToArray()
                : query.Take(1).ToArray();

            if (targetWindows.Length == 0)
            {
                return 1;
            }

            if (options.Verbose == true)
            {
                PrintMatches(stableWindows, processFilters, windowFilters);
            }
            message = "\nMoving best matching window(s):";
        }
        else
        {
            // Handle focus race conditions: hotkey manager utilities can cause GetForegroundWindow()
            // to return null or zero value momentarily during context switching.

            // define a retry policy that polls at a small interval if the result is null
            var focusPolicy = Policy<WData?>
                .HandleResult(result => result == null)
                .WaitAndRetryAsync(3, _ => TimeSpan.FromMilliseconds(50));

            WData? fw = await focusPolicy.ExecuteAsync(async () => SystemWindows.GetForegroundWindowData());
            if (fw == null)
            {
                Console.Error.WriteLine("No active window detected in the foreground... (Desktop may be locked or the focus is transitioning).");
                return 1;
            }

            targetWindows = [fw];
            message = "\nMoving the active window:";
        }

        try
        {
            var plannedMoves = new List<(WData Window, SimpleDesktop Target)>();

            foreach (var window in targetWindows)
            {
                var target = preparedTarget ?? taskView.PlanTarget(window);
                if (target != null)
                {
                    plannedMoves.Add((window, target));
                }
            }

            await taskView.ApplyAsync(ct);

            // at the moment we have all necessary windows and target desktops for them

            var moves = plannedMoves.Select(m => (m.Window, m.Target.PhysicalDesktop!)).ToList();
            res = await ExecuteWindowMovesAsync(moves, options, retryPolicy, message, postMoveTasks, ct);
            if (res != 0) taskView.RollbackEmptyCreatedDesktops();

            // Cleanup runs after the moves, when source desktops may have become empty.
            if (options.CleanAfter == true)
            {
                res |= CleanEmptyDesktops(options, ct);
            }

            if (postMoveTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(postMoveTasks).WaitAsync(TimeSpan.FromMilliseconds(500), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Ignore timeout or minor UI errors */ }
            }
        }
        catch (Exception ex)
        {
            taskView.RollbackEmptyCreatedDesktops();
            Program.OnException(ex);
            return 1;
        }

        Console.WriteLine("Done.");
        return res;
    }

    private static async Task<int> ExecuteWindowMovesAsync(
        List<(WData Window, VirtualDesktop Target)> moves,
        Options options,
        AsyncRetryPolicy retryPolicy,
        string message,
        List<Task> postMoveTasks,
        CancellationToken ct)
    {
        int res = 0;
        TotalItems = moves.Count;
        ProcessedItems = 0;

        Console.WriteLine(message);

        foreach (var (window, targetDesktop) in moves)
        {
            ct.ThrowIfCancellationRequested();
            var originalDesktop = VirtualDesktop.Current;
            bool switched = false;
            bool switchBack = false;

            try
            {
                var appDesktop = SystemWindows.GetVirtualDesktopForWindow(window.Handle);
                PrintWindowDetails(window, null, null, null, targetDesktop);

                // Pre-move actions
                if (options.SwitchFirst == true &&
                    originalDesktop.Id != targetDesktop.Id)
                {
                    new SwitchDesktopCommand(targetDesktop).Execute();
                    switched = true;
                    if (options.SwitchToDesktop != true) switchBack = true;
                }

                await retryPolicy.ExecuteAsync(async (ct) =>
                {
                    if (!Win32Helpers.IsWindow(window.Handle))
                    {
                        if (options.Verbose == true)
                            Console.WriteLine($"[Skipped] The window {window.Handle} no longer exists.");
                        throw new InvalidOperationException("The selected window has closed.");
                    }
                    new MoveWindowCommand(window.Handle, targetDesktop).Execute();
                }, ct);

                if (options.SwitchToDesktop == true) new SwitchDesktopCommand(targetDesktop).Execute();

                // post-move actions
                var postTask = Task.Run(async () =>
                {
                    try
                    {
                        // wait for DWM to register the move
                        await Task.Delay(100, ct);

                        // check window existence after the delay
                        if (!Win32Helpers.IsWindow(window.Handle)) return;

                        if (options.Maximize == true) new SetWindowStateCommand(window.Handle, true).Execute();
                        if (options.Minimize == true) new SetWindowStateCommand(window.Handle, false).Execute();
                    }
                    catch (OperationCanceledException) { /* exit quietly on Ctrl+C */}
                });

                postMoveTasks.Add(postTask);
                ProcessedItems++;
            }
            catch (OperationCanceledException) { if (switched) switchBack = true; throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Error] Failed to move {window.Handle}: {ex.Message}");

                // Hint for Windows 24H2 invisible window restrictions
                const int TYPE_E_ELEMENTNOTFOUND = unchecked((int)0x8002802B);
                if (!window.IsVisible && (ex is COMException || ex.HResult == TYPE_E_ELEMENTNOTFOUND))
                {
                    Console.Error.WriteLine("Hint: Moving invisible windows is restricted on Windows 24H2+.");
                }

                if (switched && options.SwitchToDesktop == true)
                {
                    switchBack = true;
                    Console.WriteLine($"[Rollback] Move failed; returned to {originalDesktop.Name ?? "Original Desktop"}.");
                }

                if (options.Verbose == true) Program.OnException(ex);

                res = 1;
            }

            finally
            {
                if (switchBack) new SwitchDesktopCommand(originalDesktop).Execute();
            }
        }

        return res;
    }

    private static void PrintMatches(
        IEnumerable<SearchItem> foundWindows,
        ProcessFilters procFilters,
        WindowFilters winFilters)
    {
        if (!foundWindows.Any())
        {
            Console.WriteLine("No windows found matching the criteria.");
            return;
        }

        Console.WriteLine("Matching processes/windows:");

        foreach (var (process, bestCandidates, _, _) in foundWindows)
        {
            Console.WriteLine($"\nPID {process.Id}");

            foreach (var window in bestCandidates)
            {
                if (!window.IsVisible && winFilters.IncludeInvisible == false)
                {
                    continue;
                }

                PrintWindowDetails(window, process, procFilters, winFilters);
            }
        }
    }

    private static void PrintWindowDetails(
        WData w,
        PData? p,
        ProcessFilters? procFilters,
        WindowFilters? winFilters,
        VirtualDesktop? target = null)
    {
        string title = !string.IsNullOrWhiteSpace(w.Title) ? w.Title : "(empty title)";
        string flags = $"{(w.IsVisible ? " [visible]" : "")}{(w.IsCloaked ? " [cloaked]" : "")}";

        if (target != null)
        {
            string targetName = !string.IsNullOrEmpty(target.Name)
                ? $"\"{target.Name}\""
                : $"Desktop {Array.FindIndex(SystemDesktops.All, d => d.Id == target.Id) + 1}";

            Console.WriteLine($"Window {w.Handle} \"{title}\"{flags} ->");
            Console.WriteLine($"                            {targetName}");
        }
        else
        {
            Console.WriteLine(GetMatchStr(winFilters?.TitleFilter, $"Window {w.Handle}:", title, !string.IsNullOrWhiteSpace(w.Title)) + flags);
        }

        if (p != null)
        {
            Console.WriteLine(GetMatchStr(procFilters?.NameFilter, "Process:", p.Name));
            Console.WriteLine(GetMatchStr(procFilters?.ModuleNameFilter, "Module:", Path.GetFileName(p.Path)));
            Console.WriteLine(GetMatchStr(procFilters?.ModulePathFilter, "Path:", p.Path) + "\n");
        }
    }

    private static string GetMatchStr(Func<string, bool>? filter, string name, string value, bool quotes = true)
    {
        if (quotes)
        {
            value = $"\"{value}\"";
        }
        string prefix = filter?.Invoke(value) ?? false ? "[MATCH]" : String.Empty;
        return $"{prefix,10} {name,15} {value}";
    }

    private static ProcessStartInfo CreateStartInfo(Options options)
    {
        bool useShell = options.NoShellExecute != true;  // if null, we default to ShellExecute = true

        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = options.ApplicationPath!,
            Arguments = options.ApplicationArguments,
            UseShellExecute = useShell,
            CreateNoWindow = false,
        };

        if (!useShell)
        {
            try
            {
                // only set WorkingDirectory if the user provided a full path
                string? dir = Path.GetDirectoryName(options.ApplicationPath);

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    info.WorkingDirectory = dir;
                }
            }
            catch
            {
                // nothing
            }
        }

        return info;
    }

    internal static void CreateFilters(Options options, out ProcessFilters processFilters, out WindowFilters windowFilters, IntPtr? invokingConsole = null, IntPtr? invokingTerminal = null)
    {
        var ignoreCase = !options.CaseSensitive ?? true;

        Func<string?, bool>? Bind(string? pattern) =>
            pattern is null ? null : s => MatchByString(s, pattern, ignoreCase);

        processFilters = new ProcessFilters
        {
            IdFilter = options.FilterProcessId is uint pid ? p => p == pid : null,
            NameFilter = Bind(options.FilterProcessName),
            ModuleNameFilter = Bind(options.FilterProcessModuleName),
            ModulePathFilter = Bind(options.FilterProcessModulePath),
        };

        var titleFilterLogicalOr = new List<Func<string?, bool>>();
        var titleFilterLogicalAnd = new List<Func<string?, bool>>();

        if (options.FilterOnlyEmptyTitles == true)
        {
            titleFilterLogicalAnd.Add(s => string.IsNullOrWhiteSpace(s));
        }

        if (options.FilterWindowTitle != null)
        {
            titleFilterLogicalAnd.Add(s => MatchByString(s, options.FilterWindowTitle, ignoreCase));
        }

        if (options.FilterWindowTitleStart != null)
        {
            titleFilterLogicalAnd.Add(s => s?.StartsWith(options.FilterWindowTitleStart,
                ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture) ?? false);
        }

        if (options.FilterWindowTitleRegex != null)
        {
            titleFilterLogicalAnd.Add(s => MatchByRegex(s, options.FilterWindowTitleRegex, ignoreCase));
        }

        var hasOrFilters = titleFilterLogicalOr.Any();
        var hasAndFilters = titleFilterLogicalAnd.Any();
        var hasFilters = hasOrFilters || hasAndFilters;

        Func<string?, bool>? titleFilter = s =>
        {
            bool passesOr = !hasOrFilters || titleFilterLogicalOr.Any(f => f(s));
            bool passesAnd = titleFilterLogicalAnd.All(f => f(s));

            return passesOr && passesAnd;
        };

        bool hasProcessFilter = processFilters.IdFilter != null || processFilters.NameFilter != null
            || processFilters.ModuleNameFilter != null || processFilters.ModulePathFilter != null;

        windowFilters = new WindowFilters
        {
            // Exclude only our console, not other command prompts. An explicit process
            // selector permits deliberately targeting this console as well.
            ExcludedHandle = hasProcessFilter ? IntPtr.Zero : invokingConsole ?? Win32Helpers.GetConsoleWindow(),
            ExcludedTerminalHandle = hasProcessFilter ? IntPtr.Zero : invokingTerminal ?? InvokingTerminalWindow,
            TitleFilter = !hasFilters ? null : titleFilter,
            IncludeInvisible = options.IncludeInvisible == true,
            OnlyCloaked = options.FilterOnlyCloakedWindows,
            OnlyEmptyTitles = options.FilterOnlyEmptyTitles,
            OnlyMain = options.FilterOnlyMain,
        };
    }

    private static bool MatchByString(string? str, string subStr, bool ignoreCase)
        =>
        str?.Contains(subStr, ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture) ?? false;

    private static bool MatchByRegex(string? str, string regex, bool ignoreCase)
    {
        if (str == null)
        {
            return false;
        }

        try
        {
            var options = RegexOptions.None;
            if (ignoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            var timeout = TimeSpan.FromMilliseconds(AppDefaults.MaxRegexDelay);

            return Regex.IsMatch(str, regex, options, timeout);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Error: The provided regex pattern is invalid.");
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            Console.Error.WriteLine("Error: The regex took too long to execute.");
            return false;
        }
    }

    internal static async Task<IEnumerable<SearchItem>> FindStableWindowsProcAsync(Options options, ProcessFilters processFilters, WindowFilters windowFilters, CancellationToken ct)
    {
        var searchRes = await FindStableWindowsAsync(options, processFilters, windowFilters, options!.StabilityTime, options.MaxTimeout, ct);

        if (options.Verbose == true)
        {
            Console.WriteLine($"[Window cache] Adaptive reuse interval: {SystemWindows.CurrentSnapshotFreshness.TotalMilliseconds:F1}ms " +
                $"(bounds {SystemWindows.SnapshotFreshness.TotalMilliseconds:F0}-{SystemWindows.MaximumSnapshotFreshness.TotalMilliseconds:F0}ms; " +
                "stricter freshness requested near stability deadlines).");
        }

        var stable = searchRes.Where(r => r.Stable).ToList();

        if (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("Operation was cancelled.");
            return [];
        }

        int windowCount = stable.Sum(s => s.BestCandidates.Count());
        if (options.SingleInstance == true && windowCount > 1)
        {
            Console.Error.WriteLine($"Error: Multiple windows ({windowCount}) match the search criteria, but --single was specified.");
            return [];
        }
        if (windowCount == 0) Console.Error.WriteLine("No stable application windows were found matching your search criteria.");
        return stable;
    }

    /*
		Searches for processes matching the filters and waits for their window configurations to stabilize.

		Uses a resilience pipeline with exponential backoff to poll for matches.
		Execution is capped by maxTimeout, which covers both the search and stabilization phases.

		Returns the first matching process found unless --all-instances is specified.
		Returns an empty collection if the timeout is reached or no matches are found.
	*/
    private async static Task<IEnumerable<SearchItem>> FindStableWindowsAsync(
            Options options,
            ProcessFilters processFilters,
            WindowFilters windowsFilters,
            int windowStabilityTime,
            int maxTimeout,
            CancellationToken ct)
    {

        // define the search and stability policy
        var retryOptions = new RetryStrategyOptions<IEnumerable<SearchItem>>
        {
            ShouldHandle = new PredicateBuilder<IEnumerable<SearchItem>>().HandleResult(result => !result.Any()),
            BackoffType = DelayBackoffType.Exponential,
            MaxRetryAttempts = int.MaxValue, // Polly timeout will stop it
            Delay = TimeSpan.FromMilliseconds(2), // start with 2ms polling
            MaxDelay = TimeSpan.FromSeconds(1),
            OnRetry = args =>
            {
                if (options.Verbose == true && args.RetryDelay > TimeSpan.FromMilliseconds(100))
                {
                    Console.WriteLine($"[Search Attempt {args.AttemptNumber + 1}] No matching windows found yet. Next check in {args.RetryDelay.TotalMilliseconds:F0}ms...");
                }
                return default;
            }
        };

        var pipeline = new ResiliencePipelineBuilder<IEnumerable<SearchItem>>()
            .AddTimeout(TimeSpan.FromMilliseconds(maxTimeout)) // global timeout (total duration): outer strategy, will stop inner
            .AddRetry(retryOptions) // inner strategy: individual attempts
            .Build();

        var collected = new ConcurrentDictionary<uint, SearchItem>();
        try
        {
            return await pipeline.ExecuteAsync<IEnumerable<SearchItem>>(async pollyToken =>
            {
                var discoverySnapshot = await SystemWindows.GetSnapshotAsync(pollyToken, SystemWindows.SnapshotFreshness);
                var eligiblePids = discoverySnapshot.Windows.Where(w => windowsFilters.IsMatch(w)).Select(w => w.PID).ToHashSet();
                var processes = SystemProcesses.FindProcesses(processFilters).Where(p => eligiblePids.Contains(p.Id));

                using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(pollyToken);

                // limit the number of concurrent operations
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = innerCts.Token
                };

                var results = new ConcurrentBag<SearchItem>();
                try
                {
                    await Parallel.ForEachAsync(processes, parallelOptions, async (process, cycleToken) =>
                    {
                        // each task respects the same cancellation token
                        var item = await WaitProcessWindowsCountStabilizedAsync(
                            process, windowsFilters, SystemWindowsPollPeriod, windowStabilityTime, cycleToken);

                        if (item is { Stable: true })
                        {
                            results.Add(item);
                            collected[process.Id] = item;

                            bool shouldExit = options.AllInstances != true && options.SingleInstance != true && options.SearchMode switch
                            {
                                SearchMode.Fast => true,
                                SearchMode.Balanced => item.HasMain,
                                _ => false
                            };

                            if (shouldExit)
                            {
                                innerCts.Cancel();
                            }
                        }
                    });
                }
                catch (OperationCanceledException) when (innerCts.IsCancellationRequested && !pollyToken.IsCancellationRequested)
                {
                    // successful early exit
                }

                return results;

            }, ct);
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or OperationCanceledException)
        {
            // Polly's timeout / user cancel via ct
            ct.ThrowIfCancellationRequested();
            return collected.Values.ToArray();
        }
    }

    /*
		Waits for a process to finish spawning its windows and reach a "stable" state.
		Polls until the window count remains constant for the specified duration.

		Returns the process window data; null if the process exits; 
		Stable=false if cancelled or timed out.
	 */
    private static async Task<SearchItem?> WaitProcessWindowsCountStabilizedAsync(
        PData proc,
        WindowFilters windowsFilters,
        int pollMilliseconds,
        int stabilityMilliseconds,
        CancellationToken ct)
    {
        List<WData> allWindows = new();
        List<WData> bestMatching = new();
        var stability = new WindowStabilityTracker();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!SystemProcesses.IsProcessStillAlive(proc))
                {
                    return null;
                }

                var snapshot = await SystemWindows.GetSnapshotAsync(ct, stability.MaximumSnapshotAge(stabilityMilliseconds));
                allWindows = snapshot.ForProcess(proc.Id).Where(w => windowsFilters.IsMatch(w)).ToList();
                bestMatching = allWindows.OrderByDescending(w => w.GetScore()).ToList();

                if (stability.Observe(snapshot, bestMatching, stabilityMilliseconds))
                {
                    return new(proc, bestMatching, allWindows, true);
                }

                await Task.Delay(pollMilliseconds, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // timeout
        }

        return new(proc, bestMatching, allWindows, false);
    }
}

public static class WDataExtensions
{
    public static (bool IsMain, bool HasTitle, bool NotCloaked, bool HasSystemMenu, bool NotPopup, int Len) GetScore(this WData? w)
    {
        if (w == null) return (false, false, false, false, false, -1);

        return
        (
            w.IsMain,
            w.IsVisible && !string.IsNullOrWhiteSpace(w.Title),
            !w.IsCloaked,
            !w.IsPopup,
            w.HasSystemMenu,
            w.Title?.Length ?? 0
        );
    }
}
