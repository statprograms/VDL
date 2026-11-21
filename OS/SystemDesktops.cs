using System.Runtime.InteropServices;
using System.Text;
using Common.Utils.VDL.Data;
using WindowsDesktop;

namespace VDL.OS;

internal record DesktopSnapshot(VirtualDesktop[] All, VirtualDesktop Current);

// OS desktops data cached for a short time period
internal static class SystemDesktops
{
    private static readonly TimeSpan _staleTime = TimeSpan.FromMilliseconds(100);

    private static readonly SimpleThresholdCache<DesktopSnapshot> _cache = new(UpdateDesktopsData, _staleTime);

    //public static List<WData>? GetWindowsOnDesktop(VirtualDesktop targetDesktop)
    //{
    //	var allWindowsByPid = _cache.GetValueAsync().GetAwaiter().GetResult().WindowsByPID;

    //	// flatten the dictionary and filter by the desktop location
    //	return allWindowsByPid.Values
    //		.SelectMany(list => list)
    //		.Where(w => GetVirtualDesktopForWindow(w.Handle)?.Id == targetDesktop.Id)
    //		.ToList();
    //}

    public static VirtualDesktop? GetVirtualDesktopForWindow(IntPtr hWnd)
    {
        try
        {
            return VirtualDesktop.FromHwnd(hWnd);
        }
        catch
        {
            // window have closed or is a system window that can't be queried
            return null;
        }
    }

    public static VirtualDesktop[] All => _cache.GetValueAsync().GetAwaiter().GetResult().All;
    public static VirtualDesktop Current => _cache.GetValueAsync().GetAwaiter().GetResult().Current;

    public static void Refresh()
    {
        try { _cache.RefreshAsync().GetAwaiter().GetResult(); }
        finally { SystemWindows.InvalidateSnapshot(); }
    }

    // Fetches the OS-localized name for "Desktop" from system resources.
    // Examples: "Desktop" (EN), "Рабочий стол" (RU), "Bureau" (FR).
    public static string GetLocalizedDesktopName()
    {
        const string fallback = "Desktop";
        // { "Desktop" / "Virtual Desktop" }
        int[] resourceIds = { -21769, -21828 };

        try
        {
            StringBuilder sb = new StringBuilder(256);

            foreach (int id in resourceIds)
            {
                // resource ID -21769 is the standard "Desktop" string in shell32.dll
                string source = $"@shell32.dll,{id}";

                if (Win32Helpers.SHLoadIndirectString(source, sb, (uint)sb.Capacity, IntPtr.Zero) == 0)
                {
                    string raw = sb.ToString();

                    // "&Desktop" -> "Desktop"
                    return raw.Replace("&&", "<<AMP>>")
                              .Replace("&", "")
                              .Replace("<<AMP>>", "&");
                }
            }
        }
        catch
        {
            // silently catch any P/Invoke or buffer issues and return fallback
        }

        return fallback;
    }

    private static Task<DesktopSnapshot> UpdateDesktopsData(CancellationToken token = default)
    {
        var all = VirtualDesktop.GetDesktops();
        var current = VirtualDesktop.Current;
        return Task.FromResult<DesktopSnapshot>(new DesktopSnapshot(all, current));
    }
}
