using System.Runtime.InteropServices;
using System.Text;

namespace Common.Utils.VDL.OS;

public class PData
{
	public uint Id { get; init; }
	public uint ParentId { get; init; }
	public string Name { get; init; } = String.Empty;
	public string Path { get; init; } = String.Empty;
	public string CommandLine { get; init; } = String.Empty;
	public DateTime? ExitTime { get; init; } 
	public DateTime? CreationTime { get; init; }
}

public readonly struct ProcessFilters
{
	public Func<uint, bool>? IdFilter { get; init; }
	public Func<string?, bool>? NameFilter { get; init; }
	public Func<string?, bool>? ModuleNameFilter { get; init; }
	public Func<string?, bool>? ModulePathFilter { get; init; }

	public bool IsMatch(PData p)
	{
		if (IdFilter != null && !IdFilter(p.Id))
		{
			return false;
		}

		if (NameFilter != null && !NameFilter(p.Name))
		{
			return false;
		}

		if (ModulePathFilter != null && !ModulePathFilter(p.Path))
		{
			return false;
		}

		if (ModuleNameFilter != null && !ModuleNameFilter(Path.GetFileName(p.Path)))
		{
			return false;
		}

		return true;
	}
}

public static class SystemProcesses
{
	public static IEnumerable<PData> FindProcesses(ProcessFilters filters)
	{
		var result = new List<PData>();

		var needPathInfo =
			filters.ModulePathFilter != null
		 || filters.ModuleNameFilter != null;

		foreach (var p in GetProcesses(needPathInfo))
		{
			if (filters.IsMatch(p))
			{
				result.Add(p);
			}
		}

		return result.OrderByDescending(pw => pw.CreationTime);
	}

	public static IEnumerable<PData> GetProcesses(bool fetchModulePaths)
	{
		var result = new List<PData>();

		IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
		if (snapshot == IntPtr.Zero) return result;

		try
		{
			PROCESSENTRY32 procEntry = new PROCESSENTRY32();
			procEntry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

			if (Process32First(snapshot, ref procEntry))
			{
				do
				{
					string? modPath = String.Empty;
					if (fetchModulePaths)
					{
						modPath = GetProcessPath(procEntry.th32ProcessID);
					}

					string? cmdLine = GetProcessCommandLine(procEntry.th32ProcessID);

					GetTimes(procEntry.th32ProcessID, out var start, out var end);

					result.Add(
						new PData
						{
							Id = procEntry.th32ProcessID,
							ParentId = procEntry.th32ParentProcessID,
							Name = procEntry.szExeFile,
							Path = modPath ?? String.Empty,
							CommandLine = cmdLine ?? String.Empty,
							CreationTime = start,
							ExitTime = end
						});
				}
				while (Process32Next(snapshot, ref procEntry));
			}
		}
		finally
		{
			CloseHandle(snapshot);
		}

		return result;
	}

	public static string? GetProcessPath(uint pid)
	{
		IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
		if (hProcess == IntPtr.Zero) return null;

		try
		{
			int capacity = 2048; // Max path size
			StringBuilder sb = new StringBuilder(capacity);
			if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
			{
				return sb.ToString();
			}
			return null;
		}
		finally
		{
			CloseHandle(hProcess);
		}
	}

	public static void GetTimes(uint pid, out DateTime? start, out DateTime? end)
	{
		start = end = null;

		// Use LIMITED_INFORMATION to avoid "Access Denied" on many system processes
		IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
		if (hProcess != IntPtr.Zero)
		{
			try
			{
				if (GetProcessTimes(hProcess, out long createTime, out long exitTime, out _, out _))
				{
					if (createTime != 0)
					{
						start = DateTime.FromFileTime(createTime);
					}
					if (exitTime != 0)
					{
						end = DateTime.FromFileTime(exitTime);
					}
				}
			}
			finally
			{
				CloseHandle(hProcess);
			}
		}
	}

	public static bool IsProcessStillAlive(PData anchor)
	{
		GetTimes(anchor.Id, out DateTime? currentStart, out DateTime? currentExit);

		return currentStart.HasValue &&
			   !currentExit.HasValue &&
			   currentStart.Value == anchor.CreationTime;
	}

	public static string? GetProcessCommandLine(uint pid)
	{
		// Must use QUERY_INFORMATION + VM_READ to inspect the PEB memory space
		IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
		if (hProcess == IntPtr.Zero) return null;

		try
		{
			PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
			int returnLength = 0;

			// Step 1: Fetch the Process Basic Info to grab the PebBaseAddress pointer
			if (NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf(pbi), ref returnLength) != 0)
				return null;

			int pointerSize = IntPtr.Size;

			// Step 2: Set the 64-bit structural memory offsets inside the PEB
			int processParametersOffset = pointerSize == 8 ? 0x20 : 0x10;
			int commandLineOffset = pointerSize == 8 ? 0x70 : 0x40;

			// Step 3: Read the ProcessParameters pointer address out of the PEB
			byte[] pebBuffer = new byte[pointerSize];
			int bytesRead = 0;
			if (!ReadProcessMemory(hProcess, pbi.PebBaseAddress + processParametersOffset, pebBuffer, pebBuffer.Length, ref bytesRead))
				return null;

			IntPtr processParametersPtr = (IntPtr)BitConverter.ToInt64(pebBuffer, 0);

			// Step 4: Read the UNICODE_STRING structure metadata for the command line (Length + Buffer pointer)
			byte[] unicodeStringStruct = new byte[pointerSize * 2];
			if (!ReadProcessMemory(hProcess, processParametersPtr + commandLineOffset, unicodeStringStruct, unicodeStringStruct.Length, ref bytesRead))
				return null;

			ushort len = BitConverter.ToUInt16(unicodeStringStruct, 0);
			IntPtr bufferPtr = (IntPtr)BitConverter.ToInt64(unicodeStringStruct, pointerSize);

			if (len == 0 || bufferPtr == IntPtr.Zero) return String.Empty;

			// Step 5: Extract the actual string character array from the character buffer
			byte[] commandLineBuffer = new byte[len];
			if (!ReadProcessMemory(hProcess, bufferPtr, commandLineBuffer, commandLineBuffer.Length, ref bytesRead))
				return null;

			return Encoding.Unicode.GetString(commandLineBuffer);
		}
		catch
		{
			return null;
		}
		finally
		{
			CloseHandle(hProcess);
		}
	}

	// -- WinAPI Interop --
	private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
	private const uint PROCESS_QUERY_INFORMATION = 0x0400;

	private const uint TH32CS_SNAPPROCESS = 0x00000002;
	private const uint PROCESS_VM_READ = 0x0010;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public struct PROCESSENTRY32
	{
		public uint dwSize;
		public uint cntUsage;
		public uint th32ProcessID;
		public IntPtr th32DefaultHeapID;
		public uint th32ModuleID;
		public uint cntThreads;
		public uint th32ParentProcessID;
		public int pcPriClassBase;
		public uint dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_BASIC_INFORMATION
	{
		public IntPtr Reserved1;
		public IntPtr PebBaseAddress;
		public IntPtr Reserved2_0;
		public IntPtr Reserved2_1;
		public IntPtr UniqueProcessId;
		public IntPtr Reserved3;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryFullProcessImageName([In] IntPtr hProcess, [In] int dwFlags, [Out] StringBuilder lpExeName, [In, Out] ref int lpdwSize);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
	[DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
	[DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
	[DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, ref int returnLength);
	[DllImport("kernel32.dll", SetLastError = true)]	private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);
}
