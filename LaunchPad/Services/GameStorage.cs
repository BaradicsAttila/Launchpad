using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	public class GameStorage
	{
		private const string FilePath = @"Resources\Data\Games.json";

		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};

		public List<Game> LoadGames()
		{
			if (!File.Exists(FilePath))
			{
				return new List<Game>();
			}

			string json = File.ReadAllText(FilePath);

			if (string.IsNullOrWhiteSpace(json))
			{
				return new List<Game>();
			}

			return JsonSerializer.Deserialize<List<Game>>(json, Options) ?? new List<Game>();
		}
		public void SaveGames(List<Game> games)
		{
			string json = JsonSerializer.Serialize(games, Options);
			File.WriteAllText(FilePath, json);
		}
	}
}