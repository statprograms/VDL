using VDL.Cli;
using WindowsDesktop;

namespace VDL;

/// <summary>An invocable CLI operation. Options are captured when the command is created.</summary>
public interface IApplicationCommand
{
    Task<int> ExecuteAsync(CancellationToken ct = default);
}

public abstract record ApplicationCommand(Options Options) : IApplicationCommand
{
    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!VirtualDesktop.IsSupported)
            throw new PlatformNotSupportedException("Virtual Desktops are not supported on this system.");
        return await ExecuteCoreAsync(ct);
    }

    protected abstract Task<int> ExecuteCoreAsync(CancellationToken ct);
}

public sealed record RunApplicationCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => DesktopCommandReceiver.StartOnDesktopAsync(Options, ct);
}
public sealed record MoveWindowsCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => DesktopCommandReceiver.MoveToDesktopAsync(Options, ct);
}
public sealed record CreateDesktopsCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => DesktopCommandReceiver.CreateDesktops(Options, ct);
}
public sealed record CleanDesktopsCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => Task.FromResult(DesktopCommandReceiver.CleanEmptyDesktops(Options, ct));
}
public sealed record ListDesktopsCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => Task.FromResult(DesktopCommandReceiver.ListDesktops(Options, ct));
}
public sealed record CountDesktopsCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => Task.FromResult(DesktopCommandReceiver.CountDesktops(Options, ct));
}
public sealed record ListProcessesCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => Task.FromResult(DesktopCommandReceiver.PrintProcessTree(Options, ct));
}
public sealed record SaveProfileCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => DesktopCommandReceiver.HandleProfileCommand(Options, ct);
}
public sealed record LoadProfileCommand(Options Options) : ApplicationCommand(Options)
{
    protected override Task<int> ExecuteCoreAsync(CancellationToken ct) => DesktopCommandReceiver.HandleProfileCommand(Options, ct);
}

public static class ApplicationCommandFactory
{
    public static IApplicationCommand Create(Options options)
    {
        var command = options.CliCommand;
        if (CliCommands.RunCommands.Contains(command)) return new RunApplicationCommand(options);
        if (CliCommands.MoveCommands.Contains(command)) return new MoveWindowsCommand(options);
        if (command == CliCommands.Create) return new CreateDesktopsCommand(options);
        if (command == CliCommands.Clean) return new CleanDesktopsCommand(options);
        if (command == CliCommands.List) return new ListDesktopsCommand(options);
        if (command == CliCommands.Count) return new CountDesktopsCommand(options);
        if (command == CliCommands.Processes) return new ListProcessesCommand(options);
        if (command == CliCommands.ProfileSave) return new SaveProfileCommand(options);
        if (command == CliCommands.ProfileLoad) return new LoadProfileCommand(options);
        throw new ArgumentException($"No executable operation for '{command.Name}'.", nameof(options));
    }
}

/// <summary>Physical actions used by the execution receiver after destinations are materialized.</summary>
public interface ISystemCommand { void Execute(); }
public sealed record MoveWindowCommand(IntPtr Handle, VirtualDesktop Target) : ISystemCommand
{
    public void Execute()
    {
        try { VirtualDesktop.MoveToDesktop(Handle, Target); }
        finally { SystemWindows.InvalidateSnapshot(); }
    }
}
public sealed record SwitchDesktopCommand(VirtualDesktop Target) : ISystemCommand
{
    public void Execute()
    {
        try { Target.Switch(); }
        finally { SystemWindows.InvalidateSnapshot(); }
    }
}
public sealed record SetWindowStateCommand(IntPtr Handle, bool Maximize) : ISystemCommand
{
    public void Execute()
    {
        try
        {
            if (Maximize) Win32Helpers.MaximizeNoActivate(Handle);
            else Win32Helpers.Minimize(Handle);
        }
        finally { SystemWindows.InvalidateSnapshot(); }
    }
}
public sealed record LaunchProcessCommand(System.Diagnostics.ProcessStartInfo StartInfo)
{
    public System.Diagnostics.Process? Execute()
    {
        try { return System.Diagnostics.Process.Start(StartInfo); }
        finally { SystemWindows.InvalidateSnapshot(); }
    }
}
