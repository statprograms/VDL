using VDL.Cli;

namespace VDL;

public interface IElementVisitor
{
    void Visit(SimpleDesktop desktop);
    void Visit(DesktopGroup group);
    void Visit(RenameCommand command);
    void Visit(CreateCommand command);
    void Visit(RemoveDesktopCommand command);
    void Visit(ReorderDesktopCommand command);
    void Visit(SetWallpaperCommand command);
    void Visit(RestoreProfileCommand command);
}

public class ValidationVisitor : IElementVisitor
{
    private readonly HashSet<string> _seenNames;
    private readonly List<string> _errors = new();
    private readonly int _maxDesktops;
    public ValidationVisitor(int maxDesktops = AppDefaults.MaxDesktops, bool caseSensitive = false)
    {
        _maxDesktops = maxDesktops;
        _seenNames = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }
    public IReadOnlyList<string> Errors => _errors;
    public bool IsValid => _errors.Count == 0;
    public void Visit(SimpleDesktop desktop)
    {
        if (!_seenNames.Add(desktop.Name)) _errors.Add($"Duplicate desktop name: '{desktop.Name}'.");
    }
    public void Visit(DesktopGroup group)
    {
        // The prefix is a label, not an additional physical desktop.
        foreach (var desktop in group.Desktops) Visit(desktop);
    }
    public void Visit(RenameCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.NewName) || command.NewName.Length > 64 || command.NewName.Any(char.IsControl)) _errors.Add("Desktop name must contain 1 to 64 characters and no control characters.");
    }
    public void Visit(CreateCommand command)
    {
        if (command.Count < 1 || command.Count > _maxDesktops) _errors.Add($"Creation count must be between 1 and {_maxDesktops}.");
    }
    public void Visit(RemoveDesktopCommand command) { }
    public void Visit(ReorderDesktopCommand command) { }
    public void Visit(SetWallpaperCommand command)
    {
        if (!File.Exists(command.Path)) _errors.Add($"Wallpaper not found: {command.Path}");
    }
    public void Visit(RestoreProfileCommand command) { }
}
