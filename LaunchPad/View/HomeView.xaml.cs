using LaunchPad.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows.Controls;

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

            // A nézet eltűnésekor a VM leiratkozik a játék-eseményekről.
            Unloaded += (_, _) => (DataContext as IDisposable)?.Dispose();
        }
    }
}
