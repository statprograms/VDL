using System.CommandLine;
using VDL;
using VDL.Cli;

if (args.Length > 0 && args[0] == "--measure-polling")
{
    await PollingMeasurements.RunAsync(args.Length > 1 ? args[1] : "output/measurements/polling.json");
    return;
}

// Optional read-only integration probe: uses the production discovery and stability path.
if (args.Length > 0 && (args[0] == "--inspect-title" || args[0] == "--inspect-title-start"))
{
    var probeCli = new CommandLine();
    var probeOptions = probeCli.GetOptions(CliCommands.RootCommand.Parse(["move-left", args[0] == "--inspect-title-start" ? "--title-start" : "--title", args[1]]));
    IntPtr terminal = args.Length > 2 ? new IntPtr(long.Parse(args[2])) : Common.Utils.VDL.OS.Win32Helpers.GetInvokingTerminalWindow();
    DesktopCommandReceiver.CreateFilters(probeOptions, out var processes, out var windows, invokingTerminal: terminal);
    var found = await DesktopCommandReceiver.FindStableWindowsProcAsync(probeOptions, processes, windows, CancellationToken.None);
    foreach (var item in found)
        foreach (var window in item.BestCandidates)
            Console.WriteLine($"PID {item.Process.Id} Window {window.Handle} \"{window.Title}\"");
    return;
}

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}
void Throws(Action action, string name)
{
    try { action(); } catch (Exception) { checks++; return; }
    throw new Exception("FAILED (expected rejection): " + name);
}
var cli = new CommandLine();
Options Parse(params string[] args)
{
    var parsed = CliCommands.RootCommand.Parse(args);
    Check(parsed.Errors.Count == 0, "valid parse: " + string.Join(" ", args) + ": " + string.Join("; ", parsed.Errors.Select(e => e.Message)));
    return cli.GetOptions(parsed);
}
var cases = new (string[] Args, Type Type)[]
{
    (["run", "notepad.exe", "2"], typeof(RunApplicationCommand)),
    (["run-out", "notepad.exe"], typeof(RunApplicationCommand)),
    (["run-sole", "notepad.exe"], typeof(RunApplicationCommand)),
    (["move", "Work"], typeof(MoveWindowsCommand)),
    (["move-left", "--title-start", "Total Commander"], typeof(MoveWindowsCommand)),
    (["run-sole", "notepad.exe", "--title-start", "Untitled"], typeof(RunApplicationCommand)),
    (["move-all", "Notes", "--create"], typeof(MoveWindowsCommand)),
    (["move-left", "--steps", "2", "--wrap"], typeof(MoveWindowsCommand)),
    (["move-right", "--steps", "3", "--create"], typeof(MoveWindowsCommand)),
    (["move-out", "--all", "--process", "chrome"], typeof(MoveWindowsCommand)),
    (["move-sole", "--all"], typeof(MoveWindowsCommand)),
    (["create", "Office", "--steps", "3", "--numeric-names"], typeof(CreateDesktopsCommand)),
    (["clean"], typeof(CleanDesktopsCommand)),
    (["list", "--verbose"], typeof(ListDesktopsCommand)),
    (["count"], typeof(CountDesktopsCommand)),
    (["processes"], typeof(ListProcessesCommand)),
    (["profile", "save", "workspace.json"], typeof(SaveProfileCommand)),
    (["profile", "load", "README.md", "--max-desktops", "100"], typeof(LoadProfileCommand))
};
foreach (var entry in cases)
{
    var options = Parse(entry.Args);
    Check(ApplicationCommandFactory.Create(options).GetType() == entry.Type, "factory " + string.Join(" ", entry.Args));
}
var bulk = Parse("move-all", "Notes", "--create");
Check(bulk.MoveMode == MoveMode.MoveExplicit && bulk.AllInstances == true && bulk.CliCommand == CliCommands.MoveAll, "move-all identity and mode");
var run = Parse("run", "notepad", "Work", "--", "path with spaces", "trailing\\");
Check(run.ApplicationPath == "notepad" && run.FilterProcessModuleName == "notepad.exe" && run.ApplicationArguments == "\"path with spaces\" trailing\\", "launch args and default filter");
Check(Parse("move", "1", "--pid", "4294967295", "--delay", "10").FilterProcessId == uint.MaxValue, "uint PID and delay");
foreach (string[] invalid in new string[][]
{
    ["move-right", "--wrap", "--create"], ["move", "1", "--single", "--all"],
    ["move-all", "1", "--single"], ["move", "0"], ["move", "1", "--pid", "0"],
    ["create", "--steps", "0"], ["create", "--max-desktops", "0"],
    ["move", "1", "--maximize", "--minimize"], ["move", "1", "--delay", "-1"],
    ["count", "--unknown"], ["move", "1", "--title-regex", "["]
}) Check(CliCommands.RootCommand.Parse(invalid).Errors.Count > 0, "invalid CLI rejected: " + string.Join(" ", invalid));

// Exercise actual parsing -> command creation -> invocation without changing the live workspace.
foreach (var entry in cases)
{
    IApplicationCommand? captured = null;
    bool executed = false;
    var invocation = new CommandLine().MapCommands(options =>
    {
        captured = ApplicationCommandFactory.Create(options);
        return new ProbeCommand(() => executed = true, 37);
    });
    int exit = await invocation.RunCommand(entry.Args, ex => throw new Exception("Invocation failed", ex));
    Check(executed && exit == 37 && captured?.GetType() == entry.Type, "end-to-end dispatch " + string.Join(" ", entry.Args));
}

var model = new TaskView(bulk, new TaskViewElement[] { new SimpleDesktop("Notes") });
Check(model.PlanTarget(null).Name == "Notes", "bulk first destination");
Check(model.PlanTarget(null).Name == "Notes 2", "bulk second destination");
Check(model.PlanTarget(null).Name == "Notes 3", "bulk third destination");
var numbered = new TaskView(Parse("move", "4", "--create", "--numeric-names"), new TaskViewElement[] { new SimpleDesktop("1") });
Check(numbered.PlanTarget(null).Name == "4" && numbered.Flatten().Count() == 4, "numeric target creates missing positions");
var gaps = new TaskView(Parse("move", "Work 4", "--create", "--fill-gaps"), new TaskViewElement[] { new SimpleDesktop("Work 2") });
Check(gaps.PlanTarget(null).Name == "Work 4", "named gap destination");
Check(gaps.Flatten().Select(d => d.Name).SequenceEqual(new[] { "Work", "Work 2", "Work 3", "Work 4" }), "named gaps preserve order");
var sole = new TaskView(Parse("move-sole", "--all"), new TaskViewElement[] { new SimpleDesktop("Desktop") });
Check(!ReferenceEquals(sole.PlanTarget(null), sole.PlanTarget(null)), "sole reserves unique destinations");
var capped = new TaskView(bulk with { AutoCreateDesktopsMax = 1 }, new TaskViewElement[] { new SimpleDesktop("Notes") });
capped.PlanTarget(null);
Throws(() => capped.PlanTarget(null), "total desktop cap");
Check(capped.Flatten().Count() == 1, "limit rejection leaves model intact");

List<TaskViewElement> blueprint = [new SimpleDesktop("Office")];
new CreateCommand(2, "Office").Execute(blueprint);
Check(Blueprint.Flatten(blueprint).Select(d => d.Name).SequenceEqual(new[] { "Office", "Office 2", "Office 3" }), "sequential naming");
var second = Blueprint.Flatten(blueprint).ElementAt(1);
new RenameCommand(second, "Work").Execute(blueprint);
Check(Blueprint.Flatten(blueprint).ElementAt(1).Name == "Work", "rename");
var last = Blueprint.Flatten(blueprint).Last();
new ReorderDesktopCommand(last, 0).Execute(blueprint);
Check(ReferenceEquals(Blueprint.Flatten(blueprint).First(), last), "reorder");
new RemoveDesktopCommand(last).Execute(blueprint);
Check(Blueprint.Flatten(blueprint).Count() == 2, "remove");
var group = new DesktopGroup("Office") { Desktops = [new SimpleDesktop("Office"), new SimpleDesktop("Office 2")] };
var validation = new ValidationVisitor(); group.Accept(validation);
Check(validation.IsValid, "group label does not collide with first desktop");
new RestoreProfileCommand("[{\"Name\":\"Office\",\"Children\":[{\"Name\":\"Office 2\"}]}]").Execute(blueprint);
Check(Blueprint.Flatten(blueprint).Select(d => d.Name).SequenceEqual(new[] { "Office", "Office 2" }), "legacy nested profile");
Throws(() => new RestoreProfileCommand("[]").Execute(blueprint), "empty profile");
Throws(() => new RestoreProfileCommand("[{\"Name\":\"X\"},{\"Name\":\"x\"}]").Execute(blueprint), "duplicate profile");
Check(Blueprint.Flatten(blueprint).Count() == 2, "invalid profile leaves model intact");
var roundTrip = new TaskView(Parse("create"), new TaskViewElement[] { new SimpleDesktop("Work") { PendingWallpaper = "wallpaper.jpg" } });
var profile = roundTrip.CreateSnapshotJson();
List<TaskViewElement> restored = [];
new RestoreProfileCommand(profile).Execute(restored);
Check(Blueprint.Flatten(restored).Single().Name == "Work" && Blueprint.Flatten(restored).Single().PendingWallpaper == "wallpaper.jpg", "snapshot round trip");
var named = new TaskView(Parse("move", "work", "--case"), new TaskViewElement[] { new SimpleDesktop("Work") });
Throws(() => named.PlanTarget(null), "case-sensitive destination");
var insensitive = new TaskView(Parse("move", "work"), new TaskViewElement[] { new SimpleDesktop("Work") });
Check(insensitive.PlanTarget(null).Name == "Work", "case-insensitive destination");
var bounded = new TaskView(Parse("move-right", "--steps", "5"), new TaskViewElement[] { new SimpleDesktop("A"), new SimpleDesktop("B"), new SimpleDesktop("C") });
Check(bounded.PlanTarget(null).Name == "C", "relative right clamps");
var wrap = new TaskView(Parse("move-left", "--steps", "2", "--wrap"), new TaskViewElement[] { new SimpleDesktop("A"), new SimpleDesktop("B"), new SimpleDesktop("C") });
Check(wrap.PlanTarget(null).Name == "B", "relative left wraps");
var rightCreate = new TaskView(Parse("move-right", "--steps", "4", "--create"), new TaskViewElement[] { new SimpleDesktop("Desktop") });
Check(rightCreate.PlanTarget(null).Name == "Desktop 5" && rightCreate.Flatten().Count() == 5, "relative right expansion");
var outModel = new TaskView(Parse("move-out", "--all"), new TaskViewElement[] { new SimpleDesktop("A"), new SimpleDesktop("B") });
Check(outModel.PlanTarget(null).Name == "A" && outModel.PlanTarget(null).Name == "B", "out balances reservations");
var profileCapped = new TaskView(bulk with { AutoCreateDesktopsMax = 1 }, new TaskViewElement[] { new SimpleDesktop("Keep") });
Throws(() => profileCapped.Execute(new RestoreProfileCommand("[{\"Name\":\"A\"},{\"Name\":\"B\"}]")), "profile cap");
Check(profileCapped.Flatten().Single().Name == "Keep", "rejected profile preserves model");
var excessiveGaps = new TaskView(Parse("move", "Work 2147483647", "--create", "--fill-gaps"), new TaskViewElement[] { new SimpleDesktop("Work") });
Throws(() => excessiveGaps.PlanTarget(null), "large gap request rejected before enumerating");
var defaultCreate = new TaskView(Parse("move", "--create"), new TaskViewElement[] { new SimpleDesktop("Desktop 1"), new SimpleDesktop("Desktop 2") });
Check(defaultCreate.PlanTarget(null).Name == "Desktop 3", "default creation continues physical desktop numbering");
var firstDesktop = model.Flatten().First();
Throws(() => model.Execute(new RenameCommand(firstDesktop, "Notes 2")), "rename collision");
Check(firstDesktop.Name == "Notes", "rejected rename preserves desktop");
List<TaskViewElement> onlyDesktop = [new SimpleDesktop("Only")];
Throws(() => new RemoveDesktopCommand((SimpleDesktop)onlyDesktop[0]).Execute(onlyDesktop), "last desktop remains");
// A command prompt echoes the invocation in its title and must not become
// the winner before the requested application on another desktop is found.
var titleSearch = Parse("move-left", "--title", "Commander");
var consoleWindow = new Common.Utils.VDL.OS.WData
{
    Handle = new IntPtr(1116198), PID = 10, IsVisible = true, IsMain = true,
    Title = @"C:\WINDOWS\system32\cmd.exe - vdl move-left --title Commander"
};
var commanderWindow = new Common.Utils.VDL.OS.WData
{
    Handle = new IntPtr(985212), PID = 20, IsVisible = true, IsMain = true, IsCloaked = true,
    Title = "Total Commander (x64) 11.58 - Ivan Ivanov"
};
DesktopCommandReceiver.CreateFilters(titleSearch, out _, out var titleFilters, consoleWindow.Handle);
Check(!titleFilters.IsMatch(consoleWindow), "invoking console cannot self-match title search");
Check(titleFilters.IsMatch(commanderWindow), "Commander remains eligible while cloaked on another desktop");
var eligibleWindows = new[] { consoleWindow, commanderWindow }.Where(titleFilters.IsMatch).ToArray();
Check(eligibleWindows.Length == 1 && eligibleWindows[0].Handle == commanderWindow.Handle, "console is excluded before process discovery and ranking");
DesktopCommandReceiver.CreateFilters(Parse("move-left", "--title-regex", "Commander"), out _, out var regexFilters, consoleWindow.Handle);
Check(!regexFilters.IsMatch(consoleWindow) && regexFilters.IsMatch(commanderWindow), "regex search also excludes invoking console");
DesktopCommandReceiver.CreateFilters(Parse("move-left", "--title", "Commander", "--pid", "10"), out var explicitProcess, out var explicitFilters, consoleWindow.Handle);
Check(explicitProcess.IsMatch(new Common.Utils.VDL.OS.PData { Id = 10 }) && explicitFilters.IsMatch(consoleWindow), "explicit PID can intentionally select invoking console");
var otherConsole = new Common.Utils.VDL.OS.WData { Handle = new IntPtr(30), IsVisible = true, Title = "Commander command prompt" };
Check(titleFilters.IsMatch(otherConsole), "other consoles are not globally excluded");
DesktopCommandReceiver.CreateFilters(titleSearch, out _, out var detachedFilters, IntPtr.Zero);
Check(detachedFilters.IsMatch(commanderWindow), "search works without an attached console");
// Windows Terminal's visible HWND differs from GetConsoleWindow's hidden HWND.
var visibleTerminal = new IntPtr(1116198);
var hiddenConsole = new IntPtr(987654);
var resolvedTerminal = Common.Utils.VDL.OS.Win32Helpers.ResolveInvokingTerminalWindow(
    visibleTerminal, "CASCADIA_HOSTING_WINDOW_CLASS", true, true);
Check(resolvedTerminal == visibleTerminal, "Windows Terminal visible host resolved separately from hidden console");
DesktopCommandReceiver.CreateFilters(titleSearch, out _, out var terminalFilters, hiddenConsole, resolvedTerminal);
Check(!terminalFilters.IsMatch(consoleWindow) && terminalFilters.IsMatch(commanderWindow), "visible Terminal cannot win title search against Commander");
Check(!terminalFilters.IsMatch(new Common.Utils.VDL.OS.WData { Handle = hiddenConsole, Title = "Commander", IsVisible = true }), "hidden console also excluded");
Check(terminalFilters.IsMatch(otherConsole), "unrelated terminal window remains eligible");
DesktopCommandReceiver.CreateFilters(Parse("move-left", "--pid", "10", "--title", "Commander"), out _, out var explicitTerminalFilters, hiddenConsole, resolvedTerminal);
Check(explicitTerminalFilters.IsMatch(consoleWindow), "explicit PID can target visible Terminal");
Check(Common.Utils.VDL.OS.Win32Helpers.ResolveInvokingTerminalWindow(visibleTerminal, "OtherClass", true, true) == IntPtr.Zero, "foreground application is not excluded just because WT_SESSION is inherited");
Check(Common.Utils.VDL.OS.Win32Helpers.ResolveInvokingTerminalWindow(visibleTerminal, "CASCADIA_HOSTING_WINDOW_CLASS", false, false) == IntPtr.Zero, "unrelated foreground Terminal is not excluded without console context");
Check(Common.Utils.VDL.OS.Win32Helpers.ResolveInvokingTerminalWindow(visibleTerminal, "CASCADIA_HOSTING_WINDOW_CLASS", false, true) == visibleTerminal, "hidden pseudoconsole identifies terminal even without WT_SESSION");
var prefixOptions = Parse("move-left", "--title-start", "Total Commander");
Check(prefixOptions.FilterWindowTitleStart == "Total Commander", "title-start parsed into command options");
DesktopCommandReceiver.CreateFilters(prefixOptions, out _, out var prefixFilters, IntPtr.Zero, IntPtr.Zero);
Check(prefixFilters.IsMatch(commanderWindow), "prefix matches Total Commander on another desktop");
var echoedPrefix = new Common.Utils.VDL.OS.WData
{
    Handle = consoleWindow.Handle, IsVisible = true,
    Title = @"C:\WINDOWS\system32\cmd.exe - vdl move-left --title-start ""Total Commander"""
};
Check(!prefixFilters.IsMatch(echoedPrefix), "prefix rejects echoed CMD command without console exclusions");
Check(prefixFilters.TitleFilter!("total commander version 1.5"), "prefix ignores case by default");
Check(!prefixFilters.TitleFilter!("Other Total Commander"), "prefix does not match in the middle");
Check(!prefixFilters.TitleFilter!(null), "prefix rejects absent title");
DesktopCommandReceiver.CreateFilters(Parse("move-left", "--title-start", "Total Commander", "--case"), out _, out var exactPrefixFilters, IntPtr.Zero, IntPtr.Zero);
Check(exactPrefixFilters.TitleFilter!("Total Commander") && !exactPrefixFilters.TitleFilter!("total commander"), "prefix honors --case");
DesktopCommandReceiver.CreateFilters(Parse("move-left", "--title-start", "Total", "--title", "Commander", "--title-regex", "x64"), out _, out var combinedPrefixFilters, IntPtr.Zero, IntPtr.Zero);
Check(combinedPrefixFilters.IsMatch(commanderWindow) && !combinedPrefixFilters.TitleFilter!("Total Commander x86"), "prefix combines with other title filters using AND");
foreach (var command in CliCommands.RunMoveCommands)
{
    string[] prefixArgs = CliCommands.RunCommands.Contains(command)
        ? [command.Name, "notepad.exe", "--title-start", "Total Commander"]
        : [command.Name, "--title-start", "Total Commander"];
    var parsedPrefix = Parse(prefixArgs);
    Check(parsedPrefix.FilterWindowTitleStart == "Total Commander", "prefix available on " + command.Name);
    if (CliCommands.RunCommands.Contains(command))
        Check(parsedPrefix.FilterProcessModuleName == null && parsedPrefix.FilterProcessModulePath == null, "explicit prefix prevents implicit executable filter for " + command.Name);
}
Check(CliCommands.RootCommand.Parse(["move-left", "--title-start"]).Errors.Count > 0, "prefix requires text argument");
checks += await WindowSnapshotTests.RunAsync();
Console.WriteLine($"Passed {checks} checks.");

sealed record ProbeCommand(Action Action, int ExitCode) : IApplicationCommand
{
    public Task<int> ExecuteAsync(CancellationToken ct = default) { Action(); return Task.FromResult(ExitCode); }
}
