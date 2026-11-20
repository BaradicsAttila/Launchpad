using System;
using System.ComponentModel;
using System.Windows;

namespace LaunchPad.View
{
	public partial class ScanProgressWindow : Window, IProgress<string>
	{
		private bool _allowClose;

		public ScanProgressWindow()
		{
			InitializeComponent();
		}

		public void Report(string value)
		{
			// BeginInvoke: a háttérszál (szkennelés) ne várakozzon minden egyes "Found: ..." sorra
			// a UI szálon (a blokkoló Invoke jelentősen lassította a mélyszkennelést).
			Dispatcher.BeginInvoke(new Action(() => StatusText.Text = value));
		}

		/// <summary>
		/// Az ablakot a program zárja be, nem a felhasználó: ha a felhasználó az X-szel bezárná
		/// szkennelés közben, a későbbi Close()/Show() hívások hibát dobnának.
		/// </summary>
		public new void Close()
		{
			_allowClose = true;
			base.Close();
		}

		protected override void OnClosing(CancelEventArgs e)
		{
			if (!_allowClose)
			{
				e.Cancel = true;
				return;
			}
			base.OnClosing(e);
		}
	}
}
