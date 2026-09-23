using System;
using System.Linq;
using System.Windows;
using LaunchPad;
using LaunchPad.Services;
using LaunchPad.View;
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

            // Amig a vegleges MainWindow meg nem jelenik, ne alljon le az app csak
            // azert, mert kozben (pl. a progressWindow.Close() es a mainWindow.Show()
            // kozott) pillanatnyilag nincs nyitva ablak. Alapertelmezesben
            // (ShutdownMode.OnLastWindowClose) ez azonnali kilepest okozna.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var services = new ServiceCollection();

            services.AddSingleton<GameStorage>();
            services.AddSingleton<SettingsStorage>();

            services.AddSingleton<GameService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameScanner>();
            services.AddSingleton<GameInstallWatcher>();

            services.AddTransient<HomeViewModel>();
            services.AddTransient<LibraryViewModel>();
            services.AddTransient<SettingsViewModel>();

            _serviceProvider = services.BuildServiceProvider();
            ServiceProvider = _serviceProvider;

            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            var gameService = _serviceProvider.GetRequiredService<GameService>();

            bool isFirstRun = gameService.Games.Count == 0;

            var progressWindow = new ScanProgressWindow();
            progressWindow.Show();

            // A gyors scannerek MINDIG lefutnak, minden inditaskor.
            await gameService.ScanAndMergeAsync(progressWindow);

            // Eltunt jatekok jelolese - olcso, csak File.Exists ellenorzes.
            gameService.MarkMissingGamesAsDeleted();

            if (isFirstRun)
            {
                progressWindow.Hide();

                var setupWindow = new CustomFolders();
                var result = setupWindow.ShowDialog();

                if (result == true && setupWindow.WantsScan && setupWindow.SelectedFolders.Count > 0)
                {
                    var folders = setupWindow.SelectedFolders.ToList();

                    settingsService.Current.CustomGameFolders = folders;
                    foreach (var folder in folders)
                        settingsService.Current.CustomFolderLastScanUtc[folder] = DateTime.UtcNow;
                    settingsService.Save();

                    progressWindow.Show();
                    await gameService.DeepScanAndMergeAsync(folders, progressWindow);
                }
            }
            else if (settingsService.Current.CustomGameFolders.Count > 0)
            {
                // Nem elso inditas: a mar megadott custom foldereket NEM
                // scanneljuk ujra automatikusan - csak ha valtozast jeleznek.
                var changedFolders = gameService.GetChangedCustomFolders(
                    settingsService.Current.CustomGameFolders,
                    settingsService.Current.CustomFolderLastScanUtc);

                if (changedFolders.Count > 0)
                {
                    progressWindow.Show();
                    await gameService.DeepScanAndMergeAsync(changedFolders, progressWindow);

                    foreach (var folder in changedFolders)
                        settingsService.Current.CustomFolderLastScanUtc[folder] = DateTime.UtcNow;
                    settingsService.Save();
                }
            }

            progressWindow.Close();

            var installWatcher = _serviceProvider.GetRequiredService<GameInstallWatcher>();
            installWatcher.Start();

            var mainWindow = new MainWindow();
            Application.Current.MainWindow = mainWindow;   
                                                           
            mainWindow.Show();

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            var gameService = _serviceProvider.GetRequiredService<GameService>();

            foreach (var game in gameService.Games.Where(g => g.ActiveSession != null))
                game.EndSession();

            gameService.Save();

            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            settingsService.Save();

            var installWatcher = _serviceProvider.GetRequiredService<GameInstallWatcher>();
            installWatcher.Dispose();

            base.OnExit(e);
        }
    }
}