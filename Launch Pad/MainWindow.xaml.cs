using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Launch_Pad
{
	public partial class MainWindow : Window
	{
		public MainWindow()
		{
			InitializeComponent();
		}


		private Point mouseDownPos;
		private bool leftmousebtndonw = false;

		// P/Invoke for native caption drag
		[DllImport("user32.dll")]
		private static extern bool ReleaseCapture();

		[DllImport("user32.dll")]
		private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

		private const int WM_NCLBUTTONDOWN = 0x00A1;
		private const int HTCAPTION = 0x02;

		private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{

			// Ignore double-click (handled elsewhere)
			if (e.ClickCount > 1) return;

			if (this.WindowState == WindowState.Maximized)
			{
				mouseDownPos = e.GetPosition(this);
				leftmousebtndonw = true;
				// capture so we reliably receive move/up
				Mouse.Capture(this, CaptureMode.SubTree);
			}
			else
			{
				DragMove();
			}
		}

		private void Window_MouseMove(object sender, MouseEventArgs e)
		{
			if (!leftmousebtndonw || this.WindowState != WindowState.Maximized) return;

				leftmousebtndonw = false;
				// release WPF capture if any
				if (Mouse.Captured == this) Mouse.Capture(null);

				// Use native sequence to start a real caption drag immediately
				ReleaseCapture(); // release any capture so OS can take over
				var hwnd = new WindowInteropHelper(this).Handle;
				SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);

				// After this the OS handles restore + moving; nothing more to do here.
		}																

		private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			leftmousebtndonw = false;
			if (Mouse.Captured == this) Mouse.Capture(null);
		}

		private void TitleBar_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			if (this.WindowState == WindowState.Normal)
			{
				this.WindowState = WindowState.Maximized;
			}
		}
	}
}