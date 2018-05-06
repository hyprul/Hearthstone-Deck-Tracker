using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Controls.Error;
using Hearthstone_Deck_Tracker.Controls.Stats;
using Hearthstone_Deck_Tracker.HsReplay;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility;
using Hearthstone_Deck_Tracker.Utility.Analytics;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using Hearthstone_Deck_Tracker.Utility.HotKeys;
using Hearthstone_Deck_Tracker.Utility.Toasts;
using Hearthstone_Deck_Tracker.Windows;
using MahApps.Metro.Controls.Dialogs;
using Hearthstone_Deck_Tracker.Utility.Updating;
using HearthSim.Core;
using HearthSim.Core.Hearthstone;
using HearthSim.Core.Hearthstone.Enums;
using HearthSim.Core.Hearthstone.GameStateModifiers;
using HearthSim.Core.HSReplay;
using HearthSim.Core.LogReading;
using HearthSim.UI.Themes;
using HearthSim.Util;
using WPFLocalizeExtension.Engine;
using ImageCache = HearthSim.UI.Util.ImageCache;
using Log = HearthSim.Util.Logging.Log;

namespace Hearthstone_Deck_Tracker
{
	public static class Core
	{
		internal const int UpdateDelay = 100;
		private static TrayIcon _trayIcon;
		private static OverlayWindow _overlay;
		private static Overview _statsOverview;
		private static int _updateRequestsPlayer;
		private static int _updateRequestsOpponent;
		private static DateTime _startUpTime;
		public static Version Version { get; set; }
		public static MainWindow MainWindow { get; set; }

		internal static HSReplayNet HSReplay => Manager?.HSReplayNet;
		internal static HearthSim.Core.Hearthstone.Game Hearthstone => Manager?.Game;
		internal static ILogInput LogReader => Manager?.LogReader;
		internal static Manager Manager { get; private set; }

		public static Overview StatsOverview => _statsOverview ?? (_statsOverview = new Overview());

		public static bool Initialized { get; private set; }

		public static TrayIcon TrayIcon => _trayIcon ?? (_trayIcon = new TrayIcon());

		public static OverlayWindow Overlay => _overlay ?? (_overlay = new OverlayWindow(Hearthstone));

		internal static bool UpdateOverlay { get; set; } = true;
		internal static bool Update { get; set; }
		internal static bool CanShutdown { get; set; }

		internal static HSReplayNetConfig HSConfig { get; private set; }

#pragma warning disable 1998
		public static async void Initialize()
#pragma warning restore 1998
		{
			LocalizeDictionary.Instance.Culture = CultureInfo.GetCultureInfo("en-US");
			_startUpTime = DateTime.UtcNow;
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
			Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
			Config.Load();
			HSConfig = new HSReplayNetConfig(Config.Instance.DataDir, "089b2bc6-3c26-4aab-adbe-bcfd5bb48671",
				"jIpNwuUWLFI6S3oeQkO3xlW6UCnfogw1IpAbFXqq", Helper.GetUserAgent(), GameTypeHelper.GetFromConfig(),
				Config.Instance.HsReplayUploadSpectator, Config.Instance.HsReplayUploadPacks ?? false,
				Config.Instance.SelectedTwitchUser);
			Manager = new Manager(HSConfig);
			ThemeManager.Load(new ThemeConfig() {Theme = "Dark"});
			Manager.Start();

			ImageCache.Initialize(Config.Instance.DataDir);

			//TODO: Figure out where to put these
			Manager.Game.FriendlyChallenge += () =>
			{
				if(Config.Instance.FlashHsOnFriendlyChallenge)
					HearthstoneWindow.Flash();
			};
			Manager.Game.DungeonRunMatchStarted += args =>
			{
				Manager.Game.CurrentGame.LocalPlayer.Deck = Manager.Game.SelectedDeck; 
				DeckManager.DungeonRunMatchStarted(args.IsNew, args.Deck); 
			};
			Manager.Game.DungeonRunDeckUpdated += args =>
			{
				DeckManager.UpdateDungeonRunDeck(args.Deck); 
			};
			Manager.Game.Arena.DraftComplete += args =>
			{
				DeckManager.ImportDeck(new Deck(HearthSim.Core.Hearthstone.Enums.DeckType.Arena, args.Info.Deck));
			};
			Manager.Game.Collection.DecksChanged += args => DeckManager.ImportDecks(args.Items); 
			Manager.Game.HearthstoneInstallationNotFound += () =>
			{
				ErrorManager.AddError("Could not find Hearthstone installation",
					"Please set Hearthstone installation path via 'options > tracker > settings > set hearthstone path'.");
			};
			Manager.Game.GameStateEvents.GameStateChanged += args =>
			{
				if(args.Modifier is TagChange t && t.Tag == GameTag.ZONE)
				{
					UpdatePlayerCards();
					UpdateOpponentCards();
					Helper.UpdateEverything(Manager.Game);

					if(t.Value == (int)GameTag.ZONE || t.PreviousValue == (int)GameTag.ZONE)
						Overlay.UpdateSecrets(args.State.OpposingPlayer.Secrets);
				}
			Manager.Game.SolvedSecretsChanged += () =>
			{
				Overlay.UpdateSecrets(Hearthstone.CurrentGame.OpposingPlayer.Secrets.ToList());
			};
			Manager.Game.GameCreated += args =>
			{
				Manager.Game.CurrentGame.LocalPlayer.Deck = Manager.Game.SelectedDeck; 
				UpdatePlayerCards(true);
				UpdateOpponentCards(true);
				Helper.UpdateEverything(Manager.Game);
			};
			Manager.Game.GameEnded += args =>
			{
				var game = DeckManager.HandleMatchResults(args);
				if(game != null)
				{
					var deck = DeckList.Instance.Decks.FirstOrDefault(x => x.DeckId == game.DeckId);
					ToastManager.ShowGameResultToast(deck?.Name, game);
				}
			};
			Manager.Game.LogConfigError += args => MainWindow.ShowLogConfigUpdateFailedMessage().Forget();
			Manager.Game.HearthstoneRestartRequired += () =>
			{
				MainWindow.ShowMessageAsync("Hearthstone restart required",
					"The log.config file has been updated. "
					+ "HDT may not work properly until Hearthstone has been restarted.").Forget();
				Overlay.ShowRestartRequiredWarning();
			};
			Manager.Game.HearthstoneStarted += BackupManager.Run;
			Manager.Game.ModeChanged += args =>
			{
				if(args.CurrentMode == Mode.GAMEPLAY || args.PreviousMode == Mode.GAMEPLAY)
				{
					UpdatePlayerCards(true);
					UpdateOpponentCards(true);
					Helper.UpdateEverything(Manager.Game);
				}
			};
			Manager.Game.ActivePlayerDeckChanged += args =>
			{
				UpdatePlayerCards(true);
				UpdateOpponentCards(true);
				Helper.UpdateEverything(Manager.Game);
			};
			HSReplayNetHelper.Initialize();

			HearthstoneRunner.StartingHearthstone += state =>
			{
				if(state == HearthstoneRunner.State.Error)
				{
					ErrorManager.AddError("Could not start Battle.net Launcher",
						"Starting the Battle.net launcher failed or was too slow. "
						+ "Please try again once it started or run Hearthstone manually.", true);
				}
			};

			Log.Info($"HDT: {Helper.GetCurrentVersion()}, Operating System: {Helper.GetWindowsVersion()}, .NET Framework: {Helper.GetInstalledDotNetVersion()}");
			var splashScreenWindow = new SplashScreenWindow();
#if(SQUIRREL)
			if(Config.Instance.CheckForUpdates)
			{
				var updateCheck = Updater.StartupUpdateCheck(splashScreenWindow);
				while(!updateCheck.IsCompleted)
				{
					await Task.Delay(500);
					if(splashScreenWindow.SkipUpdate)
						break;
				}
			}
#endif
			splashScreenWindow.ShowConditional();
			Log.Initialize(Path.Combine(Config.Instance.DataDir, "Logs"), "hdt_log");
			ConfigManager.Run();
			LocUtil.UpdateCultureInfo();
			var newUser = ConfigManager.PreviousVersion == null;
			Manager.UpdateLogConfig().Forget();
			UITheme.InitializeTheme();
			ResourceMonitor.Run();
			MainWindow = new MainWindow();
			MainWindow.LoadConfigSettings();
			MainWindow.Show();
			splashScreenWindow.Close();

			if(Config.Instance.DisplayHsReplayNoteLive && ConfigManager.PreviousVersion != null && ConfigManager.PreviousVersion < new Version(1, 1, 0))
				MainWindow.FlyoutHsReplayNote.IsOpen = true;

			if(ConfigManager.UpdatedVersion != null)
			{
#if(!SQUIRREL)
				Updater.Cleanup();
#endif
				MainWindow.FlyoutUpdateNotes.IsOpen = true;
				MainWindow.UpdateNotesControl.SetHighlight(ConfigManager.PreviousVersion);
#if(SQUIRREL && !DEV)
				if(Config.Instance.CheckForDevUpdates && !Config.Instance.AllowDevUpdates.HasValue)
					MainWindow.ShowDevUpdatesMessage();
#endif
			}
			DataIssueResolver.Run();

#if(!SQUIRREL)
			Helper.CopyReplayFiles();
#endif
			BackupManager.Run();

			if(Config.Instance.PlayerWindowOnStart)
				Windows.PlayerWindow.Show();
			if(Config.Instance.OpponentWindowOnStart)
				Windows.OpponentWindow.Show();
			if(Config.Instance.TimerWindowOnStartup)
				Windows.TimerWindow.Show();

			PluginManager.Instance.LoadPluginsFromDefaultPath();
			MainWindow.Options.OptionsTrackerPlugins.Load();
			PluginManager.Instance.StartUpdateAsync();

			UpdateOverlayAsync();

			if(Config.Instance.ShowCapturableOverlay)
			{
				Windows.CapturableOverlay = new CapturableOverlayWindow();
				Windows.CapturableOverlay.Show();
			}

			RemoteConfig.Instance.Load();
			HotKeyManager.Load();

			if(Helper.HearthstoneDirExists && Config.Instance.StartHearthstoneWithHDT && !Hearthstone.IsRunning)
				HearthstoneRunner.StartHearthstone().Forget();

			Initialized = true;

			Influx.OnAppStart(
				Helper.GetCurrentVersion(),
				newUser,
				HSReplay.OAuth.IsFullyAuthenticated,
				HSReplay.OAuth.AccountData?.IsPremium?.Equals("true", StringComparison.InvariantCultureIgnoreCase) ?? false,
				(int)(DateTime.UtcNow - _startUpTime).TotalSeconds,
				PluginManager.Instance.Plugins.Count
			);
		}

		private static async void UpdateOverlayAsync()
		{
#if(!SQUIRREL)
			if(Config.Instance.CheckForUpdates)
				Updater.CheckForUpdates(true);
#endif
			var hsForegroundChanged = false;
			while(UpdateOverlay)
			{
				if(Config.Instance.CheckForUpdates)
					Updater.CheckForUpdates();
				if(HearthstoneWindow.Exists())
				{
					Overlay.UpdatePosition();

					if(!Hearthstone.IsRunning)
					{
						Overlay.Update(true);
						Windows.CapturableOverlay?.UpdateContentVisibility();
					}

					TrayIcon.MenuItemStartHearthstone.Visible = false;

					Helper.GameWindowState = HearthstoneWindow.GetState();
					Windows.CapturableOverlay?.Update();
					if(HearthstoneWindow.IsInForeground() && Helper.GameWindowState != WindowState.Minimized)
					{
						if(hsForegroundChanged)
						{
							Overlay.Update(true);
							if(Config.Instance.WindowsTopmostIfHsForeground && Config.Instance.WindowsTopmost)
							{
								//if player topmost is set to true before opponent:
								//clicking on the playerwindow and back to hs causes the playerwindow to be behind hs.
								//other way around it works for both windows... what?
								Windows.OpponentWindow.Topmost = true;
								Windows.PlayerWindow.Topmost = true;
								Windows.TimerWindow.Topmost = true;
							}
							hsForegroundChanged = false;
						}
					}
					else if(!hsForegroundChanged)
					{
						if(Config.Instance.WindowsTopmostIfHsForeground && Config.Instance.WindowsTopmost)
						{
							Windows.PlayerWindow.Topmost = false;
							Windows.OpponentWindow.Topmost = false;
							Windows.TimerWindow.Topmost = false;
						}
						hsForegroundChanged = true;
					}
				}
				else if(Hearthstone.IsRunning)
				{
					Overlay.ShowOverlay(false);
					if(Windows.CapturableOverlay != null)
					{
						Windows.CapturableOverlay.UpdateContentVisibility();
						await Task.Delay(100);
						Windows.CapturableOverlay.ForcedWindowState = WindowState.Minimized;
						Windows.CapturableOverlay.WindowState = WindowState.Minimized;
					}
					Overlay.HideRestartRequiredWarning();
					Helper.ClearCachedHearthstoneBuild();
					TurnTimer.Instance.Stop();

					TrayIcon.MenuItemStartHearthstone.Visible = true;

					if(Config.Instance.CloseWithHearthstone)
						MainWindow.Close();
				}

				await Task.Delay(UpdateDelay);
			}
			CanShutdown = true;
		}

		internal static async void UpdatePlayerCards(bool reset = false)
		{
			_updateRequestsPlayer++;
			await Task.Delay(100);
			_updateRequestsPlayer--;
			if(_updateRequestsPlayer > 0)
				return;
			var cards = (Hearthstone.CurrentGame != null && !Hearthstone.IsInMenu
							? Hearthstone.CurrentGame.LocalPlayer?.GetRemainingCards().ToList()
							: Hearthstone.SelectedDeck?.Cards.ToList()) ?? new List<Card>();
			Overlay.UpdatePlayerCards(cards, reset);
			if(Windows.PlayerWindow.IsVisible)
				Windows.PlayerWindow.UpdatePlayerCards(cards, reset);
		}

		internal static async void UpdateOpponentCards(bool reset = false)
		{
			_updateRequestsOpponent++;
			await Task.Delay(100);
			_updateRequestsOpponent--;
			if(_updateRequestsOpponent > 0)
				return;
			var cards = (Hearthstone.CurrentGame != null && !Hearthstone.IsInMenu
							? Hearthstone.CurrentGame.OpposingPlayer?.GetRemainingCards().ToList()
							: null) ?? new List<Card>();
			Overlay.UpdateOpponentCards(cards, reset);
			if(Windows.OpponentWindow.IsVisible)
				Windows.OpponentWindow.UpdateOpponentCards(cards, reset);
		}

		public static class Windows
		{
			private static PlayerWindow _playerWindow;
			private static OpponentWindow _opponentWindow;
			private static TimerWindow _timerWindow;
			private static StatsWindow _statsWindow;

			//TODO
			public static PlayerWindow PlayerWindow => _playerWindow ?? (_playerWindow = new PlayerWindow(Hearthstone));
			public static OpponentWindow OpponentWindow => _opponentWindow ?? (_opponentWindow = new OpponentWindow(Hearthstone));

			public static TimerWindow TimerWindow => _timerWindow ?? (_timerWindow = new TimerWindow(Config.Instance));
			public static StatsWindow StatsWindow => _statsWindow ?? (_statsWindow = new StatsWindow());
			public static CapturableOverlayWindow CapturableOverlay;
		}

		internal static bool StatsOverviewInitialized => _statsOverview != null;
		public static List<long> IgnoredArenaDecks { get; } = new List<long>();
	}
}
