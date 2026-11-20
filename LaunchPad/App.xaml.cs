using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LaunchPad;
using LaunchPad.Services;
using LaunchPad.View;
using LaunchPad.ViewModel;
using Microsoft.Extensions.DependencyInjection;

namespace LaunchPad
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        // Statikus mező: a mutex élete az app élete (a GC nem szedheti össze).
        private static Mutex? _singleInstanceMutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Amig a vegleges MainWindow meg nem jelenik, ne alljon le az app csak
            // azert, mert kozben (pl. a progressWindow.Close() es a mainWindow.Show()
            // kozott) pillanatnyilag nincs nyitva ablak. Alapertelmezesben
            // (ShutdownMode.OnLastWindowClose) ez azonnali kilepest okozna.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Egyszerre csak egy példány futhat: két példány egymás Games.json-ját
            // (és a session-adatokat) írná felül.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\LaunchPad.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // Az async void metódusban elszálló kivétel különben "némán" megölné az appot,
            // vagy félig elindulva, láthatatlan ablakokkal, zombi processzként maradna
            // (ShutdownMode.OnExplicitShutdown miatt).
            try
            {
                await StartAsync();
            }
            catch (Exception ex)
            {
                LogError("Indítási hiba", ex);
                MessageBox.Show(
                    $"A LaunchPad indítása sikertelen:\n{ex.Message}",
                    "LaunchPad",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
            }
        }

        private async System.Threading.Tasks.Task StartAsync()
        {
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

            // Az "első indítás" a beállításokban is nyilván van tartva: ha az első scan nem talál
            // semmit, a custom mappák ablak akkor sem jön fel minden indításkor újra.
            bool isFirstRun = !settingsService.Current.FirstRunCompleted && gameService.Games.Count == 0;

            var progressWindow = new ScanProgressWindow();
            progressWindow.Show();

            try
            {
                // A gyors scannerek MINDIG lefutnak, minden inditaskor.
                await gameService.ScanAndMergeAsync(progressWindow);

                // Eltunt jatekok jelolese - olcso, csak File.Exists ellenorzes.
                gameService.MarkMissingGamesAsDeleted();

                if (isFirstRun)
                {
                    progressWindow.Hide();

                    var setupWindow = new CustomFolders();
                    var result = setupWindow.ShowDialog();

                    settingsService.Current.FirstRunCompleted = true;
                    settingsService.Save();

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
            }
            finally
            {
                // Hiba esetén sem maradhat a haladásjelző ablak nyitva.
                progressWindow.Close();
            }

            // A régi mentésekben maradt duplikátumok (ugyanaz az exe két néven) összevonása.
            // A monitor és a watcher indítása ELŐTT, hogy már az egyesített listát figyeljék.
            gameService.RemoveDuplicateGames();

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

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Egy UI-eseménykezelő hibája ne vigye el az egész appot (és a futó session-adatokat).
            LogError("Kezeletlen UI hiba", e.Exception);
            e.Handled = true;
        }

        private static void LogError(string title, Exception ex)
        {
            Debug.WriteLine($"[App] {title}: {ex}");
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LaunchPad");
                Directory.CreateDirectory(folder);
                File.AppendAllText(
                    Path.Combine(folder, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Ha a második példány lépett ki, vagy az indítás hamar elhasalt, nincs mit menteni.
            if (_serviceProvider != null)
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
            }

            base.OnExit(e);
        }
    }
}
