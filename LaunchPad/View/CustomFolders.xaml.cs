using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LaunchPad.View
{
	public partial class CustomFolders : Window
	{
		public ObservableCollection<string> SelectedFolders { get; } = new();

		public bool WantsScan { get; private set; }
		public CustomFolders()
		{
			InitializeComponent();
			FoldersList.ItemsSource = SelectedFolders;
		}
		private void AddFolder_Click(object sender, RoutedEventArgs e)
		{
			var dialog = new Microsoft.Win32.OpenFolderDialog
			{
				Title = "Select a folder to scan for games"
			};

			if (dialog.ShowDialog() == true && !SelectedFolders.Contains(dialog.FolderName))
			{
				SelectedFolders.Add(dialog.FolderName);
			}
		}

		private void RemoveFolder_Click(object sender, RoutedEventArgs e)
		{
			if (FoldersList.SelectedItem is string selected)
				SelectedFolders.Remove(selected);
		}

		private void Skip_Click(object sender, RoutedEventArgs e)
		{
			WantsScan = false;
			DialogResult = true;
		}

		private void Continue_Click(object sender, RoutedEventArgs e)
		{
			WantsScan = true;
			DialogResult = true;
		}
	}
}
