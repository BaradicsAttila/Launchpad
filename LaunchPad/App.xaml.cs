using System;
using System.Diagnostics;
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
            services.AddSingleton<GameProcessMonitor>();
            services.AddSingleton<GameLauncher>();

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

            // A session-figyelés a scan UTÁN indul: így a játékok már megkapták az InstallRoot-ot,
            // és a lemezről betöltött nyitott sessionök (összeomlás után) itt kerülnek helyreállításra.
            var processMonitor = _serviceProvider.GetRequiredService<GameProcessMonitor>();
            processMonitor.Start();

            var mainWindow = new MainWindow();
            Application.Current.MainWindow = mainWindow;

            mainWindow.Show();

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Minden lépés külön védve van, hogy egy hiba ne akadályozza a többi mentését.

            // 1) Session-figyelés leállítása. NEM zárja le a futó játék sessionjét (a játék az app
            //    bezárása után is futhat), csak előre tolja az EndedAt-et; a nem futókat lezárja.
            try
            {
                _serviceProvider.GetRequiredService<GameProcessMonitor>().StopAndPersist();
            }
            catch (Exception ex) { Debug.WriteLine($"[OnExit] Monitor: {ex.Message}"); }

            // 2) Játékok mentése
            try
            {
                var gameService = _serviceProvider.GetRequiredService<GameService>();
                gameService.Save();
                gameService.Dispose();
            }
            catch (Exception ex) { Debug.WriteLine($"[OnExit] GameService: {ex.Message}"); }

            // 3) Beállítások mentése
            try
            {
                _serviceProvider.GetRequiredService<SettingsService>().Save();
            }
            catch (Exception ex) { Debug.WriteLine($"[OnExit] Settings: {ex.Message}"); }

            // 4) Watcherek felszabadítása
            try
            {
                _serviceProvider.GetRequiredService<GameInstallWatcher>().Dispose();
            }
            catch (Exception ex) { Debug.WriteLine($"[OnExit] Watcher: {ex.Message}"); }

            base.OnExit(e);
        }
    }
}
