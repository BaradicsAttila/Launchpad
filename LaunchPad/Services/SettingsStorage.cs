using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	public class SettingsStorage
	{
		private const string FilePath = @"Resources\Data\Settings.json";
		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};
		public AppSettings LoadSettings()
		{
			if (!File.Exists(FilePath))
			{
				return new AppSettings();
			}
			string json = File.ReadAllText(FilePath);
			if (string.IsNullOrWhiteSpace(json))
			{
				return new AppSettings();
			}
			return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
		}
		public void SaveSettings (AppSettings settings)
		{
			string json = JsonSerializer.Serialize(settings, Options);
			File.WriteAllText(FilePath, json);
		}

	}
}
