using System;
using System.Collections.Generic;
using System.Diagnostics;
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
		private static readonly string CorruptPath = Path.Combine(DataFolder, "Settings.corrupt.json");

		private const string DefaultTemplateResourceName = "LaunchPad.Resources.Data.Settings.json";

		private static readonly object FileLock = new();

		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};

		public AppSettings LoadSettings()
		{
			lock (FileLock)
			{
				if (!File.Exists(FilePath))
				{
					SeedFromDefaultTemplate();
				}

				if (!File.Exists(FilePath))
				{
					return new AppSettings();
				}

				try
				{
					string json = File.ReadAllText(FilePath);

					if (string.IsNullOrWhiteSpace(json))
					{
						return new AppSettings();
					}

					return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
				}
				catch (Exception ex)
				{
					// Sérült Settings.json: az app ne omoljon össze indításkor. A hibás fájlt
					// félretesszük (hogy ne vesszen el), és alapbeállításokkal indulunk.
					Debug.WriteLine($"[SettingsStorage] Settings.json nem olvasható: {ex.Message}");
					try { File.Copy(FilePath, CorruptPath, overwrite: true); } catch { }
					return new AppSettings();
				}
			}
		}

		/// <summary>Atomi írás (temp fájl + csere): kilövés / áramszünet nem hagyhat félig írt fájlt.</summary>
		public void SaveSettings(AppSettings settings)
		{
			string json = JsonSerializer.Serialize(settings, Options);

			lock (FileLock)
			{
				Directory.CreateDirectory(DataFolder);

				string tmp = FilePath + ".tmp";
				File.WriteAllText(tmp, json);

				if (File.Exists(FilePath))
					File.Replace(tmp, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
				else
					File.Move(tmp, FilePath);
			}
		}

		public AppSettings ResetToDefault()
		{
			lock (FileLock)
			{
				Directory.CreateDirectory(DataFolder);

				string? templateJson = ReadEmbeddedTemplate();

				if (templateJson != null)
				{
					File.WriteAllText(FilePath, templateJson);
				}
				else
				{
					File.WriteAllText(FilePath, JsonSerializer.Serialize(new AppSettings(), Options));
				}
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
