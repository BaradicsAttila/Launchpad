using System;
using System.IO;

namespace LaunchPad.Helpers
{
	public static class PathHelper
	{
		/// <summary>Teljes, normalizált fájlútvonal, vagy null, ha érvénytelen.</summary>
		public static string? NormalizeFile(string? path)
		{
			if (string.IsNullOrWhiteSpace(path)) return null;
			try { return Path.GetFullPath(path.Trim()); }
			catch { return null; }
		}

		/// <summary>Teljes mappaútvonal, MINDIG záró backslash-sel (prefix-összehasonlításhoz).</summary>
		public static string? NormalizeDirectory(string? path)
		{
			if (string.IsNullOrWhiteSpace(path)) return null;
			try
			{
				var full = Path.GetFullPath(path.Trim());
				return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
			}
			catch { return null; }
		}

		/// <summary>
		/// Mint a NormalizeDirectory, de a meghajtó gyökerét (pl. "D:\") elutasítja:
		/// az InstallRoot-ként minden játékot "futónak" mutatna azon a lemezen.
		/// </summary>
		public static string? NormalizeInstallRoot(string? path)
		{
			var dir = NormalizeDirectory(path);
			if (dir == null) return null;

			var root = Path.GetPathRoot(dir);
			if (string.Equals(root, dir, StringComparison.OrdinalIgnoreCase)) return null;

			return dir;
		}

		public static bool IsUnderDirectory(string? directory, string? file)
		{
			var dir = NormalizeDirectory(directory);
			var f = NormalizeFile(file);
			return dir != null && f != null && f.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
		}
	}
}
