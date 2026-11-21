// Virtual DOM-style Task View encapsulation

using System.Text.RegularExpressions;
using VDL.Cli;
using VDL.OS;
using WindowsDesktop;

namespace VDL;

public abstract record TaskViewElement(string Name)
{
    public abstract void Accept(IElementVisitor visitor);
}

// A standalone, non-sequential desktop
public record SimpleDesktop : TaskViewElement
{
    public VirtualDesktop? PhysicalDesktop { get; set; }
    public string NamePrefix { get; init; }
    public int? NameGroupNumber { get; init; }
    public string? PendingWallpaper { get; set; }
    public bool RenamePending { get; set; }
    public int ReservedWindows { get; set; }

    public SimpleDesktop(string name, VirtualDesktop? physical = null) : base(name)
    {
        PhysicalDesktop = physical;
        RenamePending = physical == null;
        var (prefix, number) = NamingEngine.ParseNamingPattern(name);
        NamePrefix = prefix;
        NameGroupNumber = number;
    }

    public override void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

// A logical group containing a sequence of desktops
public record DesktopGroup(string Prefix) : TaskViewElement(Prefix)
{
    public List<SimpleDesktop> Desktops { get; init; } = new();
    public override void Accept(IElementVisitor visitor) => visitor.Visit(this);
}

public record DesktopMetadata(VirtualDesktop Physical, string Prefix, int? Number);

public partial class TaskView
{
    private readonly List<TaskViewElement> _elements = new();
    private readonly Options _options;
    private readonly HashSet<string> _takenNames;
    private readonly List<VirtualDesktop> _sessionCreatedDesktops = new();
    private readonly string _localizedBase;
    private readonly Func<WData?, Guid?> _sourceDesktopId = window => window == null
        ? SystemDesktops.Current.Id : SystemWindows.GetVirtualDesktopForWindow(window.Handle)?.Id;

    // Pure blueprint construction for planning and tests; it never queries Windows.
    internal TaskView(Options options, IEnumerable<TaskViewElement> elements, string localizedBase = "Desktop")
    {
        _options = options;
        _localizedBase = localizedBase;
        _stringComparer = GetComparer();
        _elements.AddRange(elements);
        _takenNames = Flatten().Select(d => d.Name).ToHashSet(_stringComparer);
        _sourceDesktopId = _ => null;
    }

    public IReadOnlyList<VirtualDesktop> SessionCreatedDesktops => _sessionCreatedDesktops;

    private readonly StringComparer _stringComparer;

    public IEnumerable<SimpleDesktop> Flatten()
        =>
        _elements.SelectMany(e => e switch
        {
            SimpleDesktop s => [s],
            DesktopGroup g => g.Desktops,
            _ => Enumerable.Empty<SimpleDesktop>()
        });

    public TaskView(Options options)
    {
        _options = options;
        _localizedBase = SystemDesktops.GetLocalizedDesktopName();
        _stringComparer = GetComparer();

        // collect desktops and parse their names into prefixes and optional numbers
        SystemDesktops.Refresh();
        var metadata = SystemDesktops.All
            .Select((d, index) =>
            {
                var name = string.IsNullOrEmpty(d.Name) ? $"{_localizedBase} {index + 1}" : d.Name;
                var (prefix, number) = NamingEngine.ParseNamingPattern(name);
                return new DesktopMetadata(d, prefix, number);
            })
            .ToArray();

        // group adjacent desktops that share a prefix and follow a numerical sequence
        ConstructModel(metadata);
        _managedIds.UnionWith(metadata.Select(d => d.Physical.Id));

        // index all names from the finished model for collision detection
        _takenNames = Flatten()
            .Select(d => d.Name)
            .ToHashSet(_stringComparer);
    }

    private void ConstructModel(DesktopMetadata[] metadata)
    {
        int i = 0;
        while (i < metadata.Length)
        {
            // determine if we are starting a sequence (desktop group)
            if (i + 1 < metadata.Length && metadata[i].IsFollowedBy(metadata[i + 1], _stringComparer))
            {
                var group = new DesktopGroup(metadata[i].Prefix);

                // consume all consecutive members
                while (i < metadata.Length)
                {
                    group.Desktops.Add(new SimpleDesktop(string.IsNullOrEmpty(metadata[i].Physical.Name) ? $"{_localizedBase} {i + 1}" : metadata[i].Physical.Name, metadata[i].Physical));

                    if (i + 1 < metadata.Length && metadata[i].IsFollowedBy(metadata[i + 1], _stringComparer))
                    {
                        i++; // move to next member of the group
                    }
                    else
                    {
                        break; // consume as standalone element
                    }
                }
                _elements.Add(group);
            }
            else
            {
                // standalone element
                _elements.Add(new SimpleDesktop(string.IsNullOrEmpty(metadata[i].Physical.Name) ? $"{_localizedBase} {i + 1}" : metadata[i].Physical.Name, metadata[i].Physical));
            }

            i++;
        }
    }

    StringComparer GetComparer()
        =>
        _options.CaseSensitive == true ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}

public static class DesktopMetadataExtensions
{
    public static bool IsFollowedBy(
        this DesktopMetadata current,
        DesktopMetadata next,
        StringComparer comparer)
    {
        if (!comparer.Equals(current.Prefix, next.Prefix))
        {
            return false;
        }

        int n1 = current.Number ?? 1;
        int n2 = next.Number ?? 1;

        return n2 > n1;
    }
}

public static class NamingEngine
{
    private static readonly Regex NameRegex = new(
    @"^(?<prefix>.*?)(\s+)?(?<num>\d+)?$",
    RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public static bool IsFollower(string first, string second)
    {
        var p1 = ParseNamingPattern(first);
        var p2 = ParseNamingPattern(second);

        // Must share the same prefix (e.g., both are "Office")
        if (!string.Equals(p1.Prefix, p2.Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Logic 1b/1c: 
        // 1. "Office" (null) followed by "Office 2" (2)
        if (p1.Number == null && p2.Number != null)
            return p2.Number > 1;

        // 2. Sequential numbers (e.g., "Office 2" followed by "Office 3")
        if (p1.Number != null && p2.Number != null)
            return p2.Number > p1.Number;

        return false;
    }

    public static string GenerateNextName(string prefix, int currentCount)
    {
        // Follows logic: First is "Name", second is "Name 2", third is "Name 3"
        if (currentCount == 0) return prefix;
        return $"{prefix} {currentCount + 1}";
    }

    public static (string Prefix, int? Number) ParseNamingPattern(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (string.Empty, null);
        }

        var match = NameRegex.Match(name.Trim());

        if (match.Success)
        {
            string prefix = match.Groups["prefix"].Value.Trim();
            string numStr = match.Groups["num"].Value;

            if (!string.IsNullOrEmpty(numStr) && int.TryParse(numStr, out int number))
            {
                // If we have a prefix like "Office" and num "2", 
                // the prefix is "Office".
                return (prefix, number);
            }

            // No digits found: the whole string is the prefix (e.g., "Office")
            return (prefix, null);
        }

        return (name, null);
    }
}
