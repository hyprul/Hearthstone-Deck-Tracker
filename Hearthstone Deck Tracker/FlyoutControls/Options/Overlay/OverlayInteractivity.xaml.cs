using System.Windows;

namespace Hearthstone_Deck_Tracker.FlyoutControls.Options.Overlay
{
	public partial class OverlayInteractivity
	{
		private bool _initialized;

		public OverlayInteractivity()
		{
			InitializeComponent();
		}

		public void Load()
		{
			CheckBoxFriendslist.IsChecked = Config.Instance.ExtraFeaturesFriendslist;
			_initialized = true;
		}

		private void CheckBoxFriendslist_OnChecked(object sender, RoutedEventArgs e)
		{
			if(!_initialized)
				return;
			Config.Instance.ExtraFeaturesFriendslist = true;
			Config.Save();
		}

		private void CheckBoxFriendslist_OnUnchecked(object sender, RoutedEventArgs e)
		{
			if(!_initialized)
				return;
			Config.Instance.ExtraFeaturesFriendslist = false;
			Config.Save();
		}
	}
}
