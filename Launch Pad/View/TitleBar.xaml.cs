using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Launch_Pad.View
{
    public partial class TitleBar : UserControl
	{
		public TitleBar()
		{
			InitializeComponent();
		}
		private Window mainwindow(){
			return Window.GetWindow(this);
		}
		
		private void clsbtn_Click(object sender, RoutedEventArgs e)
		{
			Environment.Exit(0);
		}


		private void maximbtn_Click(object sender, RoutedEventArgs e)
        {
			if (mainwindow().WindowState == WindowState.Normal)
			{
				maximbtn.Content = "";
				mainwindow().WindowState = WindowState.Maximized;

			}
			else
			{
				maximbtn.Content = "";
				mainwindow().WindowState = WindowState.Normal;

			}
		}

		private void minimbtn_Click(object sender, RoutedEventArgs e)
        {
			mainwindow().WindowState = WindowState.Minimized;
        }


	}
}
