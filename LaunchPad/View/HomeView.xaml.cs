using LaunchPad.Model;
using LaunchPad.Services;
using LaunchPad.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace LaunchPad.View
{
    /// <summary>
    /// Interaction logic for HomeView.xaml
    /// </summary>
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<HomeViewModel>();
        }
        private void TestThemeSwap_Click(object sender, RoutedEventArgs e)
        {
            var settingsService = App.ServiceProvider.GetRequiredService<SettingsService>();

            var testSettings = new AppSettings
            {
                FontFamily = "Consolas",
                FontColorPrimary = "#00FF00",
                FontColorSecondary = "#FF9900",
                MenuBackgroundPrimary = "#330000",
                MenuBackgroundSecondary = "#440000",
                BackgroundPrimary = "#000033",
                BackgroundSecondary = "#000044",
                TitlebarBackground = "#660000",
                SelectedMenuItemBackground = "#40FF9900"
            };

            settingsService.Apply(testSettings);
        }
        private void TestThemeSwap_Clickback(object sender, RoutedEventArgs e)
        {
            var settingsService = App.ServiceProvider.GetRequiredService<SettingsService>();

            var testSettings = new AppSettings
            {
                FontFamily = "Segoe UI",
                FontColorPrimary = "#CCCCCC",
                FontColorSecondary = "#8A6BE2",
                MenuBackgroundPrimary = "#111119",
                MenuBackgroundSecondary = "#15151C",
                BackgroundPrimary = "#1F1F1F",
                BackgroundSecondary = "#1A1A1A",
                TitlebarBackground = "#444444",
                SelectedMenuItemBackground = "#408A2BE2"
            };

            settingsService.Apply(testSettings);
        }
    }
}
