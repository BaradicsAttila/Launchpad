using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	public class SettingsStorage
	{
		private static readonly string DataFolder = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"LaunchPad");

		private static readonly string FilePath = Path.Combine(DataFolder, "Settings.json");

		private const string DefaultTemplateResourceName = "LaunchPad.Resources.Data.Settings.json";

		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};

		public AppSettings LoadSettings()
		{
			if (!File.Exists(FilePath))
			{
				SeedFromDefaultTemplate();
			}

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

		public void SaveSettings(AppSettings settings)
		{
			Directory.CreateDirectory(DataFolder);

			string json = JsonSerializer.Serialize(settings, Options);
			File.WriteAllText(FilePath, json);
		}
		public AppSettings ResetToDefault()
		{
			Directory.CreateDirectory(DataFolder);

			string? templateJson = ReadEmbeddedTemplate();

			if (templateJson != null)
			{
				File.WriteAllText(FilePath, templateJson);
			}
			else
			{
				SaveSettings(new AppSettings());
			}

			return LoadSettings();
		}

		private void SeedFromDefaultTemplate()
		{
			Directory.CreateDirectory(DataFolder);

			string? templateJson = ReadEmbeddedTemplate();

			if (templateJson != null)
			{
				File.WriteAllText(FilePath, templateJson);
			}
		}

		private static string? ReadEmbeddedTemplate()
		{
			var assembly = Assembly.GetExecutingAssembly();
			using var stream = assembly.GetManifestResourceStream(DefaultTemplateResourceName);

			if (stream == null) return null;

			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}
	}
}