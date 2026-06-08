using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows;
using System.Windows.Media;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	internal class SettingsService
	{
		private readonly SettingsStorage _storage;
		public AppSettings Current { get; private set; }
		public SettingsService(SettingsStorage storage)
		{
			_storage = storage; 
			Current = _storage.LoadSettings(); 
			Apply(Current);
		}
		private static SolidColorBrush ToBrush(string hex)
		{
			var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
			return new SolidColorBrush(color);
		}
		public void Apply(AppSettings settings)
		{
			Current = settings;

			var dict = Application.Current.Resources;

			dict["AppFontFamily"] = new System.Drawing.FontFamily(settings.FontFamily);
			dict["AppFontColorPrimary"] = ToBrush(settings.FontColorPrimary);
			dict["AppFontColorSecondary"] = ToBrush(settings.FontColorSecondary);
			dict["AppMenuBackgroundPrimary"] = ToBrush(settings.MenuBackgroundPrimary);
			dict["AppMenuBackgroundSecondary"] = ToBrush(settings.MenuBackgroundSecondary);
			dict["AppBackgroundPrimary"] = ToBrush(settings.BackgroundPrimary);
			dict["AppBackgroundSecondary"] = ToBrush(settings.BackgroundSecondary);
			dict["AppTitlebarBackground"] = ToBrush(settings.TitlebarBackground);
			dict["AppSelectedMenuItemBackground"] = ToBrush(settings.SelectedMenuItemBackground);
		}
	}
	
}
