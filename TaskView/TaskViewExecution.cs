using System.Text.Json;
using VDL.Cli;
using VDL.OS;
using WindowsDesktop;

namespace VDL;

public sealed class TaskViewDesktopSnapshot
{
    public required string Name { get; set; }
    public string? WallpaperPath { get; set; }
    public List<TaskViewDesktopSnapshot> Children { get; set; } = new();
}

public partial class TaskView
{
    private readonly HashSet<Guid> _managedIds = new();

    public void Execute(ITaskViewCommand command)
    {
        var validator = new ValidationVisitor(_options.AutoCreateDesktopsMax, _options.CaseSensitive == true);
        command.Accept(validator);
        if (!validator.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, validator.Errors));
        if (command is CreateCommand create && Flatten().Count() + create.Count > _options.AutoCreateDesktopsMax)
            throw new InvalidOperationException($"Creation would exceed the desktop limit of {_options.AutoCreateDesktopsMax}.");
        if (command is RestoreProfileCommand restore)
        {
            var preview = new List<TaskViewElement>();
            RestoreBlueprint(preview, restore.Json);
            if (Blueprint.Flatten(preview).Count() > _options.AutoCreateDesktopsMax)
                throw new InvalidOperationException($"Profile exceeds the desktop limit of {_options.AutoCreateDesktopsMax}.");
        }
        if (command is RenameCommand rename && Flatten().Any(d => !ReferenceEquals(d, rename.Target) && _stringComparer.Equals(d.Name, rename.NewName)))
            throw new InvalidOperationException($"Desktop '{rename.NewName}' already exists.");
        command.Execute(_elements);
        _takenNames.Clear();
        _takenNames.UnionWith(Flatten().Select(d => d.Name));
    }

    /// <summary>Resolve and reserve a destination without changing Windows.</summary>
    public SimpleDesktop PlanTarget(WData? window)
    {
        var (target, count) = GetTargetIntent(window);
        // Bulk explicit creation gives every selected window its own sequential destination.
        if (_options.MoveMode == MoveMode.MoveExplicit && _options.AllInstances == true &&
            _options.CreateDesktops == true && target?.ReservedWindows > 0)
        {
            target = null; count = 1;
        }
        if (target == null && count > 0)
        {
            string template = _options.DesktopTarget ?? (_options.UseNumericNames == true ? "1" : _localizedBase);
            if (_options.MoveMode == MoveMode.MoveRight || int.TryParse(template, out _))
            {
                // Numeric CLI targets are absolute, one-based desktop positions.
                for (int i = 0; i < count; i++)
                    Execute(new CreateCommand(1, _options.UseNumericNames == true ? "1" : _localizedBase,
                        Flatten().Count() + 1, _options.UseNumericNames == true));
                target = Flatten().Last();
            }
            else if (_options.FillGaps == true && NamingEngine.ParseNamingPattern(template).Number is int number)
            {
                var (prefix, _) = NamingEngine.ParseNamingPattern(template);
                for (int n = 1; n <= number; n++)
                {
                    string name = n == 1 ? prefix : $"{prefix} {n}";
                    if (!Flatten().Any(d => _stringComparer.Equals(d.Name, name)))
                        Execute(new CreateCommand(1, prefix, n));
                }
                target = Flatten().First(d => _stringComparer.Equals(d.Name, template));
            }
            else
            {
                var before = Flatten().ToHashSet(ReferenceEqualityComparer.Instance);
                Execute(new CreateCommand(count, template,
                    StartNumber: _options.DesktopTarget == null ? Flatten().Count() + 1 : null,
                    NumericNames: _options.UseNumericNames == true));
                target = Flatten().Last(d => !before.Contains(d));
            }
        }
        if (target == null)
            throw new InvalidOperationException("Unable to resolve target desktop. Specify an existing desktop or add --create.");
        target.ReservedWindows++;
        return target;
    }

    public (SimpleDesktop? Target, int CreateCount) GetTargetIntent(WData? window)
    {
        var all = Flatten().ToList();
        if (all.Count == 0) throw new InvalidOperationException("No desktops are available.");
        var sourceId = _sourceDesktopId(window);
        int source = all.FindIndex(d => d.PhysicalDesktop?.Id == sourceId);
        if (source < 0) source = 0;
        switch (_options.MoveMode)
        {
            case MoveMode.MoveExplicit:
                string raw = _options.DesktopTarget ?? _localizedBase;
                if (int.TryParse(raw, out int index))
                    return index <= all.Count ? (all[index - 1], 0) : (null, _options.CreateDesktops == true ? index - all.Count : 0);
                var exact = all.FirstOrDefault(d => _stringComparer.Equals(d.Name, raw));
                if (exact != null) return (exact, 0);
                if (_options.CreateDesktops != true) return (null, 0);
                var (prefix, number) = NamingEngine.ParseNamingPattern(raw);
                if (_options.FillGaps == true && number > _options.AutoCreateDesktopsMax)
                    throw new InvalidOperationException($"Filling this sequence would exceed the desktop limit of {_options.AutoCreateDesktopsMax}.");
                int missing = _options.FillGaps == true && number.HasValue
                    ? Enumerable.Range(1, number.Value).Count(n => !all.Any(d => _stringComparer.Equals(d.Name, n == 1 ? prefix : $"{prefix} {n}")))
                    : 1;
                return (null, missing);
            case MoveMode.MoveLeft:
            case MoveMode.MoveRight:
                int destination = source + (_options.MoveMode == MoveMode.MoveRight ? _options.Steps : -_options.Steps);
                if (_options.WrapDesktops == true) return (all[(destination % all.Count + all.Count) % all.Count], 0);
                if (destination >= all.Count && _options.CreateDesktops == true) return (null, destination - all.Count + 1);
                return (all[Math.Clamp(destination, 0, all.Count - 1)], 0);
            case MoveMode.MoveOut:
            case MoveMode.MoveSole:
                var candidates = all.Where(d => d.PhysicalDesktop?.Id != sourceId || d.PhysicalDesktop == null)
                    .OrderByDescending(d => !string.IsNullOrEmpty(_options.DesktopTarget) &&
                        d.Name.Contains(_options.DesktopTarget, _options.CaseSensitive == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
                var chosen = _options.MoveMode == MoveMode.MoveSole
                    ? candidates.FirstOrDefault(d => GetWeight(d) == 0)
                    : candidates.OrderBy(GetWeight).FirstOrDefault();
                // Sole commands promise a new desktop when none are free. Out requires --create.
                return (chosen, chosen == null && (_options.MoveMode == MoveMode.MoveSole || _options.CreateDesktops == true) ? 1 : 0);
            default: throw new InvalidOperationException("No movement mode was supplied.");
        }
    }

    private int GetWeight(SimpleDesktop desktop)
        => (desktop.PhysicalDesktop == null ? 0 : SystemWindows.GetWindowsOnDesktop(desktop.PhysicalDesktop).Count) + desktop.ReservedWindows;

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var intended = Flatten().ToList();
        if (intended.Count == 0) throw new InvalidOperationException("At least one desktop must remain.");
        if (intended.Count > _options.AutoCreateDesktopsMax && intended.Any(d => d.PhysicalDesktop == null))
            throw new InvalidOperationException($"Desktop limit of {_options.AutoCreateDesktopsMax} exceeded.");
        try
        {
            foreach (var desktop in intended)
            {
                ct.ThrowIfCancellationRequested();
                if (desktop.PhysicalDesktop == null)
                {
                    if (VirtualDesktop.GetDesktops().Length >= _options.AutoCreateDesktopsMax)
                        throw new InvalidOperationException("Desktop creation limit reached.");
                    desktop.PhysicalDesktop = VirtualDesktop.Create();
                    _sessionCreatedDesktops.Add(desktop.PhysicalDesktop);
                    _managedIds.Add(desktop.PhysicalDesktop.Id);
                }
                if (desktop.RenamePending)
                {
                    if (desktop.PhysicalDesktop.Name != desktop.Name) desktop.PhysicalDesktop.Name = desktop.Name;
                    desktop.RenamePending = false;
                }
                if (desktop.PendingWallpaper != null)
                {
                    desktop.PhysicalDesktop.WallpaperPath = desktop.PendingWallpaper;
                    desktop.PendingWallpaper = null;
                }
            }
            var ids = intended.Select(d => d.PhysicalDesktop!.Id).ToHashSet();
            var fallback = intended[0].PhysicalDesktop!;
            foreach (var orphan in VirtualDesktop.GetDesktops().Reverse().Where(d => _managedIds.Contains(d.Id) && !ids.Contains(d.Id)))
            {
                ct.ThrowIfCancellationRequested();
                orphan.Remove(fallback);
                _managedIds.Remove(orphan.Id);
            }
            var physical = VirtualDesktop.GetDesktops().ToList();
            for (int i = 0; i < intended.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var desktop = intended[i].PhysicalDesktop!;
                int oldIndex = physical.FindIndex(d => d.Id == desktop.Id);
                if (oldIndex != i)
                {
                    desktop.Move(i);
                    physical.RemoveAt(oldIndex); physical.Insert(i, desktop);
                }
            }
            SystemDesktops.Refresh();
        }
        catch
        {
            RollbackEmptyCreatedDesktops();
            throw;
        }
        await Task.CompletedTask;
    }

    public void RollbackEmptyCreatedDesktops()
    {
        foreach (var desktop in _sessionCreatedDesktops.AsEnumerable().Reverse())
        {
            try
            {
                var remaining = VirtualDesktop.GetDesktops();
                if (remaining.Length > 1 && remaining.Any(d => d.Id == desktop.Id) && SystemWindows.GetWindowsOnDesktop(desktop).Count == 0)
                {
                    desktop.Remove(remaining.First(d => d.Id != desktop.Id));
                    foreach (var node in Flatten().Where(d => d.PhysicalDesktop?.Id == desktop.Id)) node.PhysicalDesktop = null;
                }
            }
            catch { /* Preserve the original failure if Explorer is unavailable. */ }
        }
        SystemDesktops.Refresh();
    }

    public void PrintPreview()
    {
        int index = 1;
        foreach (var desktop in Flatten()) Console.WriteLine($"{index++,3}: {desktop.Name}{(desktop.PhysicalDesktop == null ? " [new]" : "")}");
    }

    public string CreateSnapshotJson()
        => JsonSerializer.Serialize(Flatten().Select(d => new TaskViewDesktopSnapshot
        {
            Name = d.Name,
            WallpaperPath = d.PendingWallpaper ?? d.PhysicalDesktop?.WallpaperPath
        }), new JsonSerializerOptions { WriteIndented = true });

    internal static void RestoreBlueprint(List<TaskViewElement> blueprint, string json)
    {
        var snapshots = JsonSerializer.Deserialize<List<TaskViewDesktopSnapshot>>(json)
            ?? throw new InvalidDataException("Profile cannot be null.");
        var flat = new List<TaskViewDesktopSnapshot>();
        void Walk(IEnumerable<TaskViewDesktopSnapshot> items)
        {
            foreach (var snapshot in items)
            {
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Name)) throw new InvalidDataException("Every profile desktop needs a name.");
                flat.Add(snapshot);
                Walk(snapshot.Children ?? []);
            }
        }
        Walk(snapshots);
        if (flat.Count == 0) throw new InvalidDataException("Profile must contain at least one desktop.");
        if (flat.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != flat.Count)
            throw new InvalidDataException("Profile contains duplicate desktop names.");
        var available = Blueprint.Flatten(blueprint).Where(d => d.PhysicalDesktop != null).ToList();
        var restored = flat.Select(s => new SimpleDesktop(s.Name) { PendingWallpaper = s.WallpaperPath }).ToList();
        // Preserve identities for matching names, then reuse remaining desktops by position.
        foreach (var desktop in restored)
        {
            var match = available.FirstOrDefault(d => string.Equals(d.Name, desktop.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null) { desktop.PhysicalDesktop = match.PhysicalDesktop; available.Remove(match); }
        }
        foreach (var desktop in restored.Where(d => d.PhysicalDesktop == null))
        {
            if (available.Count == 0) break;
            desktop.PhysicalDesktop = available[0].PhysicalDesktop; available.RemoveAt(0);
        }
        blueprint.Clear(); blueprint.AddRange(restored);
    }
}
