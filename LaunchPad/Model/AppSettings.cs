using System;
using System.Collections.Generic;
using System.Text;

namespace LaunchPad.Model
{
	internal class AppSettings
	{
		public string FontFamily { get; set; } = "Segoe UI";
		public string FontColorPrimary { get; set; } = "#CCCCCC";
		public string FontColorSecondary { get; set; } = "#8A6BE2";
		public string MenuBackgroundPrimary { get; set; } = "#111119";
		public string MenuBackgroundSecondary { get; set; } = "#15151C";
		public string BackgroundPrimary { get; set; } = "#1F1F1F";
		public string BackgroundSecondary { get; set; } = "#1A1A1A";
		public string TitlebarBackground { get; set; } = "#444444";
		public string SelectedMenuItemBackground { get; set; } = "#408A2BE2";
	}
}
