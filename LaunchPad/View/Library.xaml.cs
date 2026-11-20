using LaunchPad.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows;
using System.Windows.Controls;

namespace LaunchPad.View
{
    /// <summary>
    /// Interaction logic for Library.xaml
    /// </summary>
    public partial class Library : UserControl
    {
        public Library()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<LibraryViewModel>();

            // A nézet eltűnésekor a VM leiratkozik a játék-eseményekről.
            Unloaded += (_, _) => (DataContext as IDisposable)?.Dispose();
        }

		private void Button_Click(object sender, RoutedEventArgs e)
		{
			this.SearchBar.Text = string.Empty;
		}
	}
}
