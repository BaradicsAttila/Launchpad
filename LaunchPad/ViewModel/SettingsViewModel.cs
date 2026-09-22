using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaunchPad.Services;
using LaunchPad.View;

namespace LaunchPad.ViewModel
{
	public partial class SettingsViewModel : ObservableObject
	{
		private readonly SettingsService _settingsService;
		private readonly GameService _gameService;

		public ObservableCollection<string> CustomGameFolders { get; }

		[ObservableProperty]
		private string? selectedFolder;

		public SettingsViewModel(SettingsService settingsService, GameService gameService)
		{
			_settingsService = settingsService;
			_gameService = gameService;

			CustomGameFolders = new ObservableCollection<string>(_settingsService.Current.CustomGameFolders);
		}

		[RelayCommand]
		private async Task AddFolder()
		{
			var dialog = new Microsoft.Win32.OpenFolderDialog
			{
				Title = "Select a folder to scan for games"
			};

			if (dialog.ShowDialog() != true) return;
			if (CustomGameFolders.Contains(dialog.FolderName)) return;

			CustomGameFolders.Add(dialog.FolderName);
			PersistFolders();

			var progressWindow = new ScanProgressWindow();
			progressWindow.Show();
			await _gameService.DeepScanAndMergeAsync(new[] { dialog.FolderName }, progressWindow);
			progressWindow.Close();

			_settingsService.Current.CustomFolderLastScanUtc[dialog.FolderName] = DateTime.UtcNow;
			_settingsService.Save();
		}

		[RelayCommand]
		private void RemoveFolder()
		{
			if (SelectedFolder == null) return;

			CustomGameFolders.Remove(SelectedFolder);
			PersistFolders();
		}

		[RelayCommand]
		private async Task DeepScanWholePc()
		{
			var drives = DriveInfo.GetDrives()
				.Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
				.Select(d => d.RootDirectory.FullName)
				.ToList();

			var progressWindow = new ScanProgressWindow();
			progressWindow.Show();
			await _gameService.DeepScanAndMergeAsync(drives, progressWindow);
			progressWindow.Close();
		}

		private void PersistFolders()
		{
			_settingsService.Current.CustomGameFolders = CustomGameFolders.ToList();
			_settingsService.Save();
		}
	}
}