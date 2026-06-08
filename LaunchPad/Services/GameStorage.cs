using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	internal class GameStorage
	{
		private const string FilePath = "TestGames.json"; /*change later*/
		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions{ WriteIndented = true };
		public List<Game> LoadGame()
		{
			if (!File.Exists(FilePath))
			{
				return new List<Game>();
			}
			string json = File.ReadAllText(FilePath);
			return JsonSerializer.Deserialize<List<Game>>(json, Options) ?? new List<Game>();
		}
		public void SaveGames(List<Game> games)
		{
			string json = JsonSerializer.Serialize(games, Options);
			File.WriteAllText(FilePath, json);
		}
	}
}
