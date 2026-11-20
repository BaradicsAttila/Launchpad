using System;
using System.Collections.Generic;
using System.IO;

namespace LaunchPad.Helpers
{
	public static class PathHelper
	{
		// Ezek soha nem lehetnek egy játék InstallRoot-ja: alattuk a rendszer / más programok
		// állandóan futó processzei is vannak, így a játék "sosem állna le".
		private static readonly HashSet<string> TooBroadRoots = BuildTooBroadRoots();

		private static HashSet<string> BuildTooBroadRoots()
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void Add(string? path)
			{
				var normalized = NormalizeDirectory(path);
				if (normalized != null) set.Add(normalized);
			}

			Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
			Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
			Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
			Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); // C:\ProgramData

			var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			Add(profile);                                                                    // C:\Users\<nev>
			if (!string.IsNullOrEmpty(profile))
				Add(Path.GetDirectoryName(profile.TrimEnd(Path.DirectorySeparatorChar)));  // C:\Users

			return set;
		}

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
		/// Mint a NormalizeDirectory, de a meghajtó gyökerét (pl. "D:\") és a túl tág rendszermappákat
		/// elutasítja: az InstallRoot-ként minden játékot "futónak" mutatna.
		/// </summary>
		public static string? NormalizeInstallRoot(string? path)
		{
			var dir = NormalizeDirectory(path);
			if (dir == null) return null;

			var root = Path.GetPathRoot(dir);
			if (string.Equals(root, dir, StringComparison.OrdinalIgnoreCase)) return null;
			if (TooBroadRoots.Contains(dir)) return null;

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
