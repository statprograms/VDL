using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Common.Utils.VDL.OS;

public class WData
{
    public uint PID { get; init; }
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = String.Empty;
    public bool IsVisible { get; init; }
    public bool IsCloaked { get; init; }
    public bool IsMain { get; init; }
    public bool IsPopup { get; init; }
    public bool HasSystemMenu { get; init; }
    public bool IsMinimized { get; init; }
    public string ClassName { get; init; } = string.Empty;

    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public readonly struct WindowFilters
{
    // A console can echo VDL's search arguments into its own title.
    public IntPtr ExcludedHandle { get; init; }
    public IntPtr ExcludedTerminalHandle { get; init; }
    public Func<string?, bool>? TitleFilter { get; init; }
    public bool? IncludeInvisible { get; init; }
    public bool? OnlyCloaked { get; init; }
    public bool? OnlyEmptyTitles { get; init; }
    public bool? OnlyMain { get; init; }

    public bool IsMatch(WData w)
    {
        if ((ExcludedHandle != IntPtr.Zero && w.Handle == ExcludedHandle)
            || (ExcludedTerminalHandle != IntPtr.Zero && w.Handle == ExcludedTerminalHandle))
        {
            return false;
        }

        if (TitleFilter?.Invoke(w.Title) == false)
        {
            return false;
        }

        if (IncludeInvisible == false && !w.IsVisible)
        {
            return false;
        }

        if (OnlyCloaked == true && !w.IsCloaked)
        {
            return false;
        }

        if (OnlyEmptyTitles == true && !String.IsNullOrWhiteSpace(w.Title))
        {
            return false;
        }

        if (OnlyMain == true && !w.IsMain)
        {
            return false;
        }

        return true;
    }
}

// OS windows data cached for a short time period
internal static class SystemWindows
{
    public static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMilliseconds(25);
    public static readonly TimeSpan MaximumSnapshotFreshness = TimeSpan.FromMilliseconds(100);
    private static readonly WindowSnapshotCache _snapshots = new(FetchWindowsDataProc, SnapshotFreshness,
        freshnessPolicy: new WindowSnapshotFreshnessPolicy(SnapshotFreshness, MaximumSnapshotFreshness));
    public static TimeSpan CurrentSnapshotFreshness => _snapshots.CurrentFreshness;

    public static WindowSnapshot GetSnapshot(TimeSpan? maximumAge = null) => _snapshots.GetSnapshot(maximumAge);
    public static ValueTask<WindowSnapshot> GetSnapshotAsync(CancellationToken ct = default, TimeSpan? maximumAge = null)
        => _snapshots.GetSnapshotAsync(ct, maximumAge);
    public static void InvalidateSnapshot() => _snapshots.Invalidate();

    public static WindowsDesktop.VirtualDesktop? GetVirtualDesktopForWindow(IntPtr handle)
        => global::VDL.OS.SystemDesktops.GetVirtualDesktopForWindow(handle);

    public static List<WData> GetWindowsOnDesktop(WindowsDesktop.VirtualDesktop desktop)
        => GetAllWindowsData().Where(w => w.IsVisible && GetVirtualDesktopForWindow(w.Handle)?.Id == desktop.Id).ToList();

    public static IEnumerable<WData> GetAllWindowsData() => GetSnapshot().Windows;

    public static IEnumerable<WData> GetWindowsData(uint pid) => GetSnapshot().ForProcess(pid);

    public static IEnumerable<WData> FindWindows(uint pid, WindowFilters filters)
        =>
        GetWindowsData(pid).Where(w => filters.IsMatch(w));

    public static WData? GetForegroundWindowData()
        =>
        GetWindowData(Win32Helpers.GetForegroundWindow());

    public static WData? GetWindowData(IntPtr hWnd)
        =>
        GetSnapshot(SnapshotFreshness).WindowsByHandle.TryGetValue(hWnd, out var w) ? w : null;

    private static IEnumerable<WData> FetchWindowsDataProc()
    {
        var snapshot = new List<WData>();

        GCHandle handle = GCHandle.Alloc(snapshot);
        try
        {
            Win32Helpers.EnumWindows((hWnd, lParam) =>
            {
                uint pid;
                Win32Helpers.GetWindowThreadProcessId(hWnd, out pid);

                uint style = (uint)Win32Helpers.GetWindowLongPtr(hWnd, Win32Helpers.GWL_STYLE);

                int left = 0, top = 0, width = 0, height = 0;
                if (Win32Helpers.GetWindowRect(hWnd, out Win32Helpers.RECT rect))
                {
                    left = rect.Left;
                    top = rect.Top;
                    width = rect.Right - rect.Left;
                    height = rect.Bottom - rect.Top;
                }

                var w = new WData
                {
                    PID = pid,
                    Handle = hWnd,
                    IsVisible = Win32Helpers.IsWindowVisible(hWnd),
                    IsCloaked = Win32Helpers.IsWindowCloaked(hWnd),
                    Title = Win32Helpers.GetWindowTitle(hWnd),
                    IsMain = Win32Helpers.IsMainWindow(hWnd, style),
                    IsPopup = Win32Helpers.IsPopup(style),
                    HasSystemMenu = Win32Helpers.HasSystemMenu(style),
                    IsMinimized = Win32Helpers.IsIconic(hWnd),
                    ClassName = Win32Helpers.GetClassNameStr(hWnd),

                    Left = left,
                    Top = top,
                    Width = width,
                    Height = height
                };

                var data = (List<WData>)GCHandle.FromIntPtr(lParam).Target!;
                data.Add(w);

                return true;
            }, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return snapshot;
    }
}
