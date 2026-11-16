using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LaunchPad.Services
{
	/// <summary>
	/// Egy futó processz. A Path null, ha nem sikerült lekérdezni (pl. hozzáférés megtagadva) -
	/// ilyenkor csak a fájlnév (ExeName) áll rendelkezésre.
	/// </summary>
	internal readonly record struct RunningProcess(int Pid, string ExeName, string? Path, DateTime? StartUtc);

	/// <summary>
	/// A futó processzek listája Toolhelp32 snapshotból + QueryFullProcessImageName.
	/// Ellentétben a Process.MainModule-lal, ez emelt jogú (admin) processzekre is működik,
	/// és nem foglal le Process objektumokat minden körben.
	/// Az útvonalat/indulási időt (PID + exe név) alapján cache-eli.
	/// </summary>
	internal sealed class ProcessScanner
	{
		private const uint TH32CS_SNAPPROCESS = 0x00000002;
		private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
		private const int ERROR_INSUFFICIENT_BUFFER = 122;

		// Egy működő Windowson mindig van ennél több processz. Ha mégsem, a pillanatkép
		// hibás/részleges, és NEM szabad "semmi nem fut"-ként értelmezni (különben
		// félrevezetően lezárnánk a futó játékok sessionjeit).
		private const int MinPlausibleProcessCount = 10;

		private static readonly IntPtr InvalidHandle = new(-1);

		private sealed record CacheEntry(string ExeName, string Path, DateTime? StartUtc);

		private readonly object _lock = new();
		private readonly Dictionary<int, CacheEntry> _cache = new();

		/// <summary>
		/// Kivételt dob, ha a pillanatkép nem megbízható. A hívó ilyenkor kihagyja a kört
		/// és NEM módosít semmilyen sessiont.
		/// </summary>
		public List<RunningProcess> Snapshot()
		{
			lock (_lock)
			{
				IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
				if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
					throw new Win32Exception(Marshal.GetLastWin32Error());

				var result = new List<RunningProcess>(256);
				var alive = new HashSet<int>();

				try
				{
					var entry = new PROCESSENTRY32W
					{
						dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>()
					};

					if (!Process32FirstW(snapshot, ref entry))
						throw new Win32Exception(Marshal.GetLastWin32Error());

					do
					{
						int pid = (int)entry.th32ProcessID;
						if (pid <= 4) continue; // Idle (0) és System (4)

						string exeName = entry.szExeFile ?? string.Empty;
						alive.Add(pid);

						if (_cache.TryGetValue(pid, out var cached) &&
							string.Equals(cached.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
						{
							result.Add(new RunningProcess(pid, exeName, cached.Path, cached.StartUtc));
							continue;
						}

						var (path, start) = QueryProcess(pid);

						if (path != null)
							_cache[pid] = new CacheEntry(exeName, path, start);
						else
							_cache.Remove(pid);

						result.Add(new RunningProcess(pid, exeName, path, start));
					}
					while (Process32NextW(snapshot, ref entry));
				}
				finally
				{
					CloseHandle(snapshot);
				}

				if (result.Count < MinPlausibleProcessCount)
					throw new InvalidOperationException($"Gyanúsan kevés processz a pillanatképben: {result.Count}");

				// A már megszűnt processzek törlése a cache-ből (PID újrahasznosítás ellen is).
				foreach (var pid in _cache.Keys.Where(k => !alive.Contains(k)).ToList())
					_cache.Remove(pid);

				return result;
			}
		}

		private static (string? Path, DateTime? StartUtc) QueryProcess(int pid)
		{
			IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
			if (handle == IntPtr.Zero) return (null, null);

			try
			{
				string? path = null;

				var sb = new StringBuilder(1024);
				int size = sb.Capacity;
				if (QueryFullProcessImageNameW(handle, 0, sb, ref size))
				{
					path = sb.ToString(0, size);
				}
				else if (Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
				{
					sb = new StringBuilder(32768);
					size = sb.Capacity;
					if (QueryFullProcessImageNameW(handle, 0, sb, ref size))
						path = sb.ToString(0, size);
				}

				DateTime? start = null;
				if (GetProcessTimes(handle, out long creation, out _, out _, out _) && creation > 0)
				{
					try { start = DateTime.FromFileTimeUtc(creation); }
					catch (ArgumentOutOfRangeException) { }
				}

				return (path, start);
			}
			finally
			{
				CloseHandle(handle);
			}
		}

		// -------------------------------------------------------------------
		// P/Invoke
		// -------------------------------------------------------------------

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct PROCESSENTRY32W
		{
			public uint dwSize;
			public uint cntUsage;
			public uint th32ProcessID;
			public UIntPtr th32DefaultHeapID;
			public uint th32ModuleID;
			public uint cntThreads;
			public uint th32ParentProcessID;
			public int pcPriClassBase;
			public uint dwFlags;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
			public string szExeFile;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(
			IntPtr hProcess,
			out long lpCreationTime,
			out long lpExitTime,
			out long lpKernelTime,
			out long lpUserTime);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CloseHandle(IntPtr hObject);
	}
}
