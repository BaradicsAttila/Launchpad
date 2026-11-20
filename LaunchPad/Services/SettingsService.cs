using System;
using System.Windows;
using System.Windows.Media;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	public class SettingsService
	{
		private readonly SettingsStorage _storage;
		public AppSettings Current { get; private set; }

		public SettingsService(SettingsStorage storage)
		{
			_storage = storage;
			Current = _storage.LoadSettings();
			Apply(Current);
		}

		private static SolidColorBrush ToBrush(string hex, string fallbackHex)
		{
			Color color;
			try
			{
				color = (Color)ColorConverter.ConvertFromString(hex);
			}
			catch (Exception)
			{
				// Elgépelt / hibás szín a Settings.json-ban ne vigye el az indulást.
				color = (Color)ColorConverter.ConvertFromString(fallbackHex);
			}

			var brush = new SolidColorBrush(color);
			brush.Freeze();
			return brush;
		}

		/// <summary>
		/// CSAK a témát (betűtípus, színek) alkalmazza az erőforrásokra a megadott beállításokból.
		/// A Current-et nem cseréli le: egy részlegesen kitöltött AppSettings (pl. teszt téma)
		/// különben kitörölné a CustomGameFolders / ExcludedGamePaths listákat a következő mentésnél.
		/// </summary>
		public void Apply(AppSettings settings)
		{
			var dict = Application.Current.Resources;
			var defaults = new AppSettings();

			dict["AppFontFamily"] = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? defaults.FontFamily : settings.FontFamily);
			dict["AppFontColorPrimary"] = ToBrush(settings.FontColorPrimary, defaults.FontColorPrimary);
			dict["AppFontColorSecondary"] = ToBrush(settings.FontColorSecondary, defaults.FontColorSecondary);
			dict["AppBorderColorPrimary"] = ToBrush(settings.BorderColorPrimary, defaults.BorderColorPrimary);
			dict["AppBorderColorSecondary"] = ToBrush(settings.BorderColorSecondary, defaults.BorderColorSecondary);
			dict["AppMenuBackgroundPrimary"] = ToBrush(settings.MenuBackgroundPrimary, defaults.MenuBackgroundPrimary);
			dict["AppMenuBackgroundSecondary"] = ToBrush(settings.MenuBackgroundSecondary, defaults.MenuBackgroundSecondary);
			dict["AppBackgroundPrimary"] = ToBrush(settings.BackgroundPrimary, defaults.BackgroundPrimary);
			dict["AppBackgroundSecondary"] = ToBrush(settings.BackgroundSecondary, defaults.BackgroundSecondary);
			dict["AppTitlebarBackground"] = ToBrush(settings.TitlebarBackground, defaults.TitlebarBackground);
			dict["AppSelectedMenuItemBackground"] = ToBrush(settings.SelectedMenuItemBackground, defaults.SelectedMenuItemBackground);
		}

		public void Save()
		{
			_storage.SaveSettings(Current);
		}
	}
}
