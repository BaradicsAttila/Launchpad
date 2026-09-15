using System.Linq;
using System.Windows;
using LaunchPad;
using LaunchPad.Services;
using LaunchPad.ViewModel;
using Microsoft.Extensions.DependencyInjection;

namespace LaunchPad
{
	public partial class App : Application
	{
		private ServiceProvider _serviceProvider;
		public static ServiceProvider ServiceProvider { get; private set; }

		protected override async void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			var services = new ServiceCollection();

			// Storage
			services.AddSingleton<GameStorage>();
			services.AddSingleton<SettingsStorage>();

			// Services — singletons so the whole app shares one instance
			services.AddSingleton<GameService>();
			services.AddSingleton<SettingsService>();
			services.AddSingleton<GameScanner>();
			services.AddSingleton<GameInstallWatcher>();

			// ViewModels — fresh instance per page
			services.AddTransient<HomeViewModel>();
			services.AddTransient<LibraryViewModel>();
			services.AddTransient<SettingsViewModel>();

			_serviceProvider = services.BuildServiceProvider();
			ServiceProvider = _serviceProvider;

			var settingsService = _serviceProvider.GetRequiredService<SettingsService>();

			var gameService = _serviceProvider.GetRequiredService<GameService>();

			// Ha a konyvtar meg ures MIELOTT barmilyen scan lefutna, ez az elso
			// (vagy ures) inditas jele - ekkor kerdezzuk meg a mely keresest.
			bool isLibraryEmptyBeforeScan = gameService.Games.Count == 0;

			// A gyors scannerek MINDIG lefutnak, minden inditaskor.
			await gameService.ScanAndMergeAsync();

			if (isLibraryEmptyBeforeScan)
			{
				var wantsDeepScan = MessageBox.Show(
					"Szeretnél egy mélyebb keresést is futtatni? Ez a megadott mappá(ka)t fájlonként átvizsgálja " +
					"(pl. portable vagy nem Steam/Epic/GOG-os játékokhoz), de tovább tarthat, mint a gyors keresés.",
					"Első indítás - Mély keresés",
					MessageBoxButton.YesNo,
					MessageBoxImage.Question);

				if (wantsDeepScan == MessageBoxResult.Yes)
				{
					var folderDialog = new Microsoft.Win32.OpenFolderDialog
					{
						Title = "Válaszd ki a mappát a mélykereséshez"
					};

					if (folderDialog.ShowDialog() == true)
					{
						await gameService.DeepScanAndMergeAsync(new[] { folderDialog.FolderName });
					}
				}
			}

			// Ettől kezdve a GameInstallWatcher figyeli a háttérben, ha ÚJ játék
			// települ fel MENET KÖZBEN. Ez is CSAK a gyors scannereket hasznalja,
			// sosem a deep scan-t.
			var installWatcher = _serviceProvider.GetRequiredService<GameInstallWatcher>();
			installWatcher.Start();

			var mainWindow = new MainWindow();
			mainWindow.Show();
		}

		protected override void OnExit(ExitEventArgs e)
		{
			// End any active sessions cleanly before the app closes
			var gameService = _serviceProvider.GetRequiredService<GameService>();

			foreach (var game in gameService.Games.Where(g => g.ActiveSession != null))
				game.EndSession();

			gameService.Save();

			var installWatcher = _serviceProvider.GetRequiredService<GameInstallWatcher>();
			installWatcher.Dispose();

			base.OnExit(e);
		}
	}
}