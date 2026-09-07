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

        protected override void OnStartup(StartupEventArgs e)
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

            // ViewModels — fresh instance per page
            services.AddTransient<HomeViewModel>();
            services.AddTransient<LibraryViewModel>();
            services.AddTransient<SettingsViewModel>();

            _serviceProvider = services.BuildServiceProvider();
            ServiceProvider = _serviceProvider; // <-- ez hianyzott, ezert volt null a statikus property

            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
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
            base.OnExit(e);
        }
    }
}