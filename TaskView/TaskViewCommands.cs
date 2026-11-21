namespace VDL;

/// <summary>A deferred mutation of the desktop blueprint, committed by TaskView.ApplyAsync.</summary>
public interface ITaskViewCommand
{
    void Execute(List<TaskViewElement> blueprint);
    void Accept(IElementVisitor visitor);
}

public record RenameCommand(TaskViewElement Target, string NewName) : ITaskViewCommand
{
    public void Execute(List<TaskViewElement> blueprint)
    {
        var desktop = Target as SimpleDesktop ?? throw new ArgumentException("Rename requires a desktop.");
        var renamed = new SimpleDesktop(NewName, desktop.PhysicalDesktop)
        { PendingWallpaper = desktop.PendingWallpaper, ReservedWindows = desktop.ReservedWindows, RenamePending = true };
        var list = Blueprint.FindContainer(blueprint, desktop);
        list.Replace(desktop, renamed);
    }
    public void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

public record CreateCommand(int Count, string Prefix, int? StartNumber = null, bool NumericNames = false) : ITaskViewCommand
{
    public void Execute(List<TaskViewElement> blueprint)
    {
        if (Count < 1) throw new ArgumentOutOfRangeException(nameof(Count));
        var names = Blueprint.Flatten(blueprint).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (prefix, suffix) = NamingEngine.ParseNamingPattern(Prefix);
        long next = StartNumber ?? suffix ?? 1;
        if (string.IsNullOrWhiteSpace(prefix) && !NumericNames) prefix = "Desktop";
        var additions = new List<SimpleDesktop>();
        for (int i = 0; i < Count; i++)
        {
            string name;
            do
            {
                if (next > int.MaxValue) throw new InvalidOperationException("Desktop numbering exceeds the supported range.");
                name = NumericNames ? next.ToString() : next == 1 ? prefix : $"{prefix} {next}";
                next++;
            } while (!names.Add(name));
            if (name.Length > 64) throw new InvalidOperationException("Generated desktop name exceeds 64 characters.");
            additions.Add(new SimpleDesktop(name));
        }
        foreach (var addition in additions)
        {
            var group = blueprint.OfType<DesktopGroup>().FirstOrDefault(g => string.Equals(g.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
            if (group != null && !NumericNames)
            {
                int index = group.Desktops.FindIndex(d => (d.NameGroupNumber ?? 1) > (addition.NameGroupNumber ?? 1));
                group.Desktops.Insert(index < 0 ? group.Desktops.Count : index, addition);
            }
            else
            {
                int index = blueprint.FindIndex(e => e is SimpleDesktop d &&
                    string.Equals(d.NamePrefix, addition.NamePrefix, StringComparison.OrdinalIgnoreCase) &&
                    (d.NameGroupNumber ?? 1) > (addition.NameGroupNumber ?? 1));
                if (index < 0)
                {
                    int last = blueprint.FindLastIndex(e => e is SimpleDesktop d && string.Equals(d.NamePrefix, addition.NamePrefix, StringComparison.OrdinalIgnoreCase));
                    index = last < 0 ? blueprint.Count : last + 1;
                }
                blueprint.Insert(index, addition);
            }
        }
    }
    public void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

public record RemoveDesktopCommand(SimpleDesktop Target) : ITaskViewCommand
{
    public void Execute(List<TaskViewElement> blueprint)
    {
        if (Blueprint.Flatten(blueprint).Count() <= 1) throw new InvalidOperationException("At least one desktop must remain.");
        var group = blueprint.OfType<DesktopGroup>().FirstOrDefault(g => g.Desktops.Any(d => ReferenceEquals(d, Target)));
        if (group != null) { group.Desktops.Remove(Target); if (group.Desktops.Count == 0) blueprint.Remove(group); }
        else if (!blueprint.Remove(Target)) throw new ArgumentException("Desktop is not in the blueprint.");
    }
    public void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

public record ReorderDesktopCommand(SimpleDesktop Target, int Index) : ITaskViewCommand
{
    public void Execute(List<TaskViewElement> blueprint)
    {
        var all = Blueprint.Flatten(blueprint).ToList();
        if (Index < 0 || Index >= all.Count) throw new ArgumentOutOfRangeException(nameof(Index));
        if (!all.Any(d => ReferenceEquals(d, Target))) throw new ArgumentException("Desktop is not in the blueprint.");
        all.Remove(Target); all.Insert(Index, Target);
        blueprint.Clear(); blueprint.AddRange(all);
    }
    public void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

public record SetWallpaperCommand(SimpleDesktop Target, string Path) : ITaskViewCommand
{
    public void Execute(List<TaskViewElement> blueprint)
    {
        if (!Blueprint.Flatten(blueprint).Any(d => ReferenceEquals(d, Target))) throw new ArgumentException("Desktop is not in the blueprint.");
        Target.PendingWallpaper = Path;
    }
    public void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

public record RestoreProfileCommand(string Json) : ITaskViewCommand
{
    public void Execute(List<TaskViewElement> blueprint) => TaskView.RestoreBlueprint(blueprint, Json);
    public void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

internal static class Blueprint
{
    public static IEnumerable<SimpleDesktop> Flatten(IEnumerable<TaskViewElement> elements)
        => elements.SelectMany(e => e is DesktopGroup g ? g.Desktops : e is SimpleDesktop d ? new[] { d } : Enumerable.Empty<SimpleDesktop>());

    public static DesktopContainer FindContainer(List<TaskViewElement> elements, SimpleDesktop desktop)
    {
        var group = elements.OfType<DesktopGroup>().FirstOrDefault(g => g.Desktops.Any(d => ReferenceEquals(d, desktop)));
        return new DesktopContainer(elements, group);
    }
    internal record DesktopContainer(List<TaskViewElement> Elements, DesktopGroup? Group)
    {
        public void Replace(SimpleDesktop oldValue, SimpleDesktop newValue)
        {
            if (Group != null) Group.Desktops[Group.Desktops.FindIndex(d => ReferenceEquals(d, oldValue))] = newValue;
            else
            {
                int index = Elements.FindIndex(d => ReferenceEquals(d, oldValue));
                if (index < 0) throw new ArgumentException("Desktop is not in the blueprint.");
                Elements[index] = newValue;
            }
        }
    }
}
