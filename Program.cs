using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VDL.Cli;

namespace VDL;

public record SearchItem(
    PData Process,
    IEnumerable<WData> BestCandidates,
    IEnumerable<WData> AllWindows,
    bool Stable)
{
    public WData? TopWindow => BestCandidates.FirstOrDefault();
    public bool HasMain => TopWindow?.IsMain ?? false;
}

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        DesktopCommandReceiver.InvokingTerminalWindow = Win32Helpers.GetInvokingTerminalWindow();
        // Enter through an STA thread for Windows desktop integration.
        return MainAsync(args).GetAwaiter().GetResult();
    }

    static async Task<int> MainAsync(string[] args)
    {
        try
        {

            DesktopCommandReceiver.TotalItems = 0;
            DesktopCommandReceiver.ProcessedItems = 0;
            var sw = Stopwatch.StartNew();

            // register command handlers
            var cli = new CommandLine().MapCommands(ApplicationCommandFactory.Create);

            // parse arguments and invoke the selected command
            var res = await cli.RunCommand(args, OnException);

            sw.Stop();

            if (CliCommands.ModificationCommands.Contains(cli.CommandOptions?.CliCommand))
            {
                if (DesktopCommandReceiver.TotalItems > 1)
                {
                    Console.WriteLine($"\n[Summary] Successfully processed {DesktopCommandReceiver.ProcessedItems}/{DesktopCommandReceiver.TotalItems} items in {sw.ElapsedMilliseconds}ms.");
                }
                else if (DesktopCommandReceiver.ProcessedItems == 1)
                {
                    Console.WriteLine($"[Done] Operation completed in {sw.ElapsedMilliseconds}ms.");
                }
            }

            if (Debugger.IsAttached)
            {
                Console.ReadLine();
            }

            return res;
        }
        catch (Exception ex)
        {
            OnException(ex);
            return 1;
        }
    }

    internal static void OnException(Exception ex)
    {
        var uw = ex.GetBaseException();
        switch (uw)
        {
            case OperationCanceledException:
                Console.WriteLine("\n[Cancelled] Operation aborted by user.");
                break;

            case Exception cliEx when cliEx.Source == "System.CommandLine":
                Console.Error.WriteLine($"CLI error: {cliEx.Message}");
                break;

            case FileNotFoundException:
                Console.Error.WriteLine($"{uw.Message}");
                break;

            case COMException comEx:
                Console.Error.WriteLine($"COM Error (0x{comEx.ErrorCode:X8}): {comEx.Message}");
                break;

            case Win32Exception w32ex when w32ex.NativeErrorCode == (int)Win32Helpers.SystemErrorCodes.ERROR_ACCESS_DENIED:
                Console.Error.WriteLine("Access Denied: Try running the application as Administrator.");
                break;

            case Win32Exception w32ex when w32ex.NativeErrorCode == (int)Win32Helpers.SystemErrorCodes.ERROR_INVALID_HANDLE:
                Console.Error.WriteLine("The window handle is no longer valid. It may have been closed.");
                break;

            case Win32Exception w32ex:
                Console.Error.WriteLine($"System Error ({w32ex.NativeErrorCode}): {w32ex.Message}");
                break;

            default:
                Console.Error.WriteLine($"{uw.GetType().FullName}: {uw.Message}");
                Console.Error.WriteLine(uw.StackTrace);
                break;
        }
    }

}
