using System;
using System.Windows;

namespace LaunchPad.View
{
	public partial class ScanProgressWindow : Window, IProgress<string>
	{
		public ScanProgressWindow()
		{
			InitializeComponent();
		}

		public void Report(string value)
		{
			Dispatcher.Invoke(() => StatusText.Text = value);
		}
	}
}