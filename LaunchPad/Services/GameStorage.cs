using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	public class GameStorage
	{
		private static readonly string DataFolder = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"LaunchPad");
		private static readonly string FilePath = Path.Combine(DataFolder, "Games.json");
		private static readonly string BackupPath = Path.Combine(DataFolder, "Games.json.bak");
		private static readonly string CorruptPath = Path.Combine(DataFolder, "Games.corrupt.json");

		// Mentés közben soha ne olvashasson/írhasson két szál egyszerre.
		private static readonly object FileLock = new();

		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};

		public List<Game> LoadGames()
		{
			lock (FileLock)
			{
				if (!File.Exists(FilePath))
					return new List<Game>();

				try
				{
					return Parse(File.ReadAllText(FilePath));
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[GameStorage] Games.json nem olvasható: {ex.Message}");

					// A sérült fájlt NEM írjuk felül észrevétlenül: elmentjük egy mellé.
					try { File.Copy(FilePath, CorruptPath, overwrite: true); } catch { }

					// Az előző jó mentés (a File.Replace által készített .bak) a tartalék.
					if (File.Exists(BackupPath))
					{
						try { return Parse(File.ReadAllText(BackupPath)); }
						catch (Exception ex2)
						{
							Debug.WriteLine($"[GameStorage] A .bak sem olvasható: {ex2.Message}");
						}
					}

					return new List<Game>();
				}
			}
		}

		private static List<Game> Parse(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
				return new List<Game>();

			return JsonSerializer.Deserialize<List<Game>>(json, Options) ?? new List<Game>();
		}

		/// <summary>
		/// A szerializálást a hívó szálon végzi (a GameService a UI szálról hívja, így a
		/// Sessions listák közben nem módosulhatnak), az írás pedig atomi:
		/// előbb temp fájl, aztán csere. Egy kilövés/áramszünet nem hagyhat félig írt Games.json-t.
		/// </summary>
		public void SaveGames(List<Game> games)
		{
			string json = JsonSerializer.Serialize(games, Options);

			lock (FileLock)
			{
				Directory.CreateDirectory(DataFolder);

				string tmp = FilePath + ".tmp";
				File.WriteAllText(tmp, json);

				if (File.Exists(FilePath))
					File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true);
				else
					File.Move(tmp, FilePath);
			}
		}
	}
}
