﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using Newtonsoft.Json;
using SteamAuth;
using SteamKit2;
using SteamKit2.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace ArchiSteamFarm {
	internal sealed class Bot {
		private const ushort CallbackSleep = 500; // In miliseconds

		internal static readonly Dictionary<string, Bot> Bots = new Dictionary<string, Bot>();

		private static readonly uint LoginID = MsgClientLogon.ObfuscationMask; // This must be the same for all ASF bots and all ASF processes

		private readonly string SentryFile;
		private readonly Timer AcceptConfirmationsTimer;
		private readonly Timer SendItemsTimer;
		private readonly Timer DistributeGiftsTimer;

		internal readonly string BotName;
		internal readonly ArchiHandler ArchiHandler;
		internal readonly ArchiWebHandler ArchiWebHandler;
		internal readonly BotConfig BotConfig;
		internal readonly BotDatabase BotDatabase;
		internal readonly SteamClient SteamClient;

		private readonly CallbackManager CallbackManager;
		private readonly CardsFarmer CardsFarmer;
		private readonly SteamApps SteamApps;
		private readonly SteamFriends SteamFriends;
		private readonly SteamUser SteamUser;
		private readonly Trading Trading;

		internal bool KeepRunning { get; private set; } = false;

		private bool InvalidPassword = false;
		private bool LoggedInElsewhere = false;
		private string AuthCode, TwoFactorAuth;

		private EClanRank rank;

		internal enum EClanRank
		{
			Owner = 4,
			Officer = 3,
			Moderator = 2,
			Member = 1,
			None = 0,
			Unknown = -1
		}

		internal static async Task RefreshCMs(uint cellID) {			
			bool initialized = false;
			for (byte i = 0; i < 3 && !initialized; i++) {
				try {
					Logging.LogGenericInfo("Refreshing list of CMs...");
					await SteamDirectory.Initialize(cellID).ConfigureAwait(false);
					initialized = true;
				} catch (Exception e) {
					Logging.LogGenericException(e);
					await Utilities.SleepAsync(1000).ConfigureAwait(false);
				}
			}

			if (initialized) {
				Logging.LogGenericInfo("Success!");
			} else {
				Logging.LogGenericWarning("Failed to initialize list of CMs after 3 tries, ASF will use built-in SK2 list, it may take a while to connect");
			}
		}

		private static bool IsOwner(ulong steamID) {
			if (steamID == 0) {
				return false;
			}

			return steamID == Program.GlobalConfig.SteamOwnerID;
		}

		private static bool IsValidCdKey(string key) {
			if (string.IsNullOrEmpty(key)) {
				return false;
			}

			// Steam keys are offered in many formats: https://support.steampowered.com/kb_article.php?ref=7480-WUSF-3601
			// It's pointless to implement them all, so we'll just do a simple check if key is supposed to be valid
			// Every valid key, apart from Prey one has at least two dashes
			return Utilities.GetCharCountInString(key, '-') >= 2;
		}

		internal Bot(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return;
			}

			BotName = botName;

			string botPath = Path.Combine(Program.ConfigDirectory, botName);
			BotConfig = BotConfig.Load( botPath + ".json" );
			if ( BotConfig == null ) {
				Logging.LogGenericError( "Your config for this bot instance is invalid, it won't run!", botName );
				return;
			}

			bool alreadyExists;
			lock (Bots) {
				alreadyExists = Bots.ContainsKey(botName);
				if (!alreadyExists) {
					Bots[botName] = this;
				}
			}

			if (alreadyExists) {
				return;
			}

			BotDatabase = BotDatabase.Load(botPath + ".db");
			SentryFile = botPath + ".bin";

			// Support and convert SDA files
			string maFilePath = botPath + ".maFile";
			if (BotDatabase.SteamGuardAccount == null && File.Exists(maFilePath)) {
				ImportAuthenticator(maFilePath);
			}

			// Initialize
			SteamClient = new SteamClient();

			if (Program.GlobalConfig.Debug && !Debugging.NetHookAlreadyInitialized && Directory.Exists(Program.DebugDirectory)) {
				try {
					SteamClient.DebugNetworkListener = new NetHookNetworkListener(Program.DebugDirectory);
					Debugging.NetHookAlreadyInitialized = true;
				} catch (Exception e) {
					Logging.LogGenericException(e, botName);
				}
			}

			ArchiHandler = new ArchiHandler();
			SteamClient.AddHandler(ArchiHandler);

			CallbackManager = new CallbackManager(SteamClient);
			CallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
			CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

			SteamApps = SteamClient.GetHandler<SteamApps>();
			CallbackManager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicense);
            CallbackManager.Subscribe<SteamApps.GuestPassListCallback>(OnGuestPassList);

            SteamFriends = SteamClient.GetHandler<SteamFriends>();
			CallbackManager.Subscribe<SteamFriends.ChatInviteCallback>(OnChatInvite);
			CallbackManager.Subscribe<SteamFriends.ChatMsgCallback>(OnChatMsg);
			CallbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
			CallbackManager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMsg);
			CallbackManager.Subscribe<SteamFriends.FriendMsgHistoryCallback>(OnFriendMsgHistory);

			SteamUser = SteamClient.GetHandler<SteamUser>();
			CallbackManager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
			CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
			CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
			CallbackManager.Subscribe<SteamUser.LoginKeyCallback>(OnLoginKey);
			CallbackManager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

			CallbackManager.Subscribe<ArchiHandler.NotificationsCallback>(OnNotifications);
			CallbackManager.Subscribe<ArchiHandler.PurchaseResponseCallback>(OnPurchaseResponse);

			ArchiWebHandler = new ArchiWebHandler(this);
			CardsFarmer = new CardsFarmer(this);
			Trading = new Trading(this);

			if ( BotConfig.AcceptConfirmationsPeriod > 0 && AcceptConfirmationsTimer == null ) {
				AcceptConfirmationsTimer = new Timer(
					async e => await AcceptConfirmations().ConfigureAwait( false ),
					null,
					TimeSpan.FromMinutes( BotConfig.AcceptConfirmationsPeriod ), // Delay
					TimeSpan.FromMinutes( BotConfig.AcceptConfirmationsPeriod ) // Period
				);
			}

			if ( BotConfig.SendTradePeriod > 0 && SendItemsTimer == null ) {
				SendItemsTimer = new Timer(
					async e => await ResponseLoot( BotConfig.SteamMasterID ).ConfigureAwait( false ),
					null,
					TimeSpan.FromHours( BotConfig.SendTradePeriod ), // Delay
					TimeSpan.FromHours( BotConfig.SendTradePeriod ) // Period
				);
			}

			if ( BotConfig.DistributeGiftsPeriod > 0 && DistributeGiftsTimer == null ) {
				DistributeGiftsTimer = new Timer(
					async e => await DistributeGifts().ConfigureAwait( false ),
					null,
					TimeSpan.FromHours( BotConfig.SendTradePeriod ), // Delay
					TimeSpan.FromHours( BotConfig.SendTradePeriod ) // Period
				);
			}

			Start().Wait();
		}

		public bool IsMaster( ulong steamID ) {
			if ( steamID == 0 ) {
				return false;
			}

			return steamID == BotConfig.SteamMasterID;
		}
		public bool IsSlave( ulong steamID ) {
			if ( steamID == 0 ) {
				return false;
			}
			foreach ( var botPair in Bots ) {
				var bot = botPair.Value;
				
				if ( bot.IsMaster( this.SteamUser.SteamID.ConvertToUInt64() ) ) {
					return true;
				}
			}
			return false;
		}
		internal async Task<string> DistributeGifts() {
			List<uint> allowedIDs = new List<uint>();
			foreach (var bot in Bots.Values) {
				if ( bot == null || bot.BotDatabase.SteamID64 == 0 || this.BotDatabase.SteamID64 == 0
					|| bot.BotDatabase.AccountID == 0 || this.BotDatabase.AccountID == 0) {
					continue;
				}
				if ( bot.IsMaster( this.BotDatabase.SteamID64 )) {
					allowedIDs.Add( bot.BotDatabase.AccountID );
				}
			}
			await ArchiWebHandler.DistributeGiftsTo( allowedIDs ).ConfigureAwait( false );
			return "Sent!";
		}
		internal async Task AcceptConfirmations(Confirmation.ConfirmationType allowedConfirmationType = Confirmation.ConfirmationType.Unknown) {
			if (BotDatabase.SteamGuardAccount == null) {
				return;
			}

			try {
				if (!await BotDatabase.SteamGuardAccount.RefreshSessionAsync().ConfigureAwait(false)) {
					return;
				}

				Confirmation[] confirmations = await BotDatabase.SteamGuardAccount.FetchConfirmationsAsync().ConfigureAwait(false);
				if (confirmations == null) {
					return;
				}

				foreach (Confirmation confirmation in confirmations) {
					if (allowedConfirmationType != Confirmation.ConfirmationType.Unknown && confirmation.ConfType != allowedConfirmationType) {
						continue;
					}

					BotDatabase.SteamGuardAccount.AcceptConfirmation(confirmation);
				}
			} catch (SteamGuardAccount.WGTokenInvalidException) {
				Logging.LogGenericWarning("Accepting confirmation: Failed!", BotName);
				Logging.LogGenericWarning("Confirmation could not be accepted because of invalid token exception", BotName);
				Logging.LogGenericWarning("If issue persists, consider removing and readding ASF 2FA", BotName);
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
				return;
			}
		}

		[Obsolete]
		internal void ResetGamesPlayed() {
			
		}

		internal async Task Restart() {
			await Stop().ConfigureAwait(false);
			await Start().ConfigureAwait(false);
		}

		internal async Task OnFarmingFinished(bool farmedSomething) {
			if (farmedSomething && BotConfig.SendOnFarmingFinished) {
				await ResponseLoot(BotConfig.SteamMasterID).ConfigureAwait(false);
			}
		}

		internal async Task<string> Response(ulong steamID, string message) {
            if (!IsMaster(steamID) || string.IsNullOrEmpty(message)) {
				return null;
			}

			if (!message.StartsWith("!")) {
				return await ResponseRedeem(steamID, BotName, message, true).ConfigureAwait(false);
			}

			if (!message.Contains(" ")) {
				switch (message) {
					case "!2fa":
						return Response2FA( steamID );
					case "!2faoff":
						return Response2FAOff(steamID);
					case "!2faok":
						return await Response2FAOK( steamID ).ConfigureAwait( false );
					case "!sendgifts":
						return await DistributeGifts( ).ConfigureAwait( false );
					case "!exit":
						return ResponseExit(steamID);
					case "!farm":
						return await ResponseFarm(steamID).ConfigureAwait(false);
					case "!loot":
						return await ResponseLoot(steamID).ConfigureAwait(false);
					case "!rejoinchat":
						return ResponseRejoinChat(steamID);
					case "!restart":
						return ResponseRestart(steamID);
					case "!status":
						return ResponseStatus(steamID);
					case "!rank":
						return ResponseRank( steamID );
					case "!statusall":
						return ResponseStatusAll(steamID);
					case "!stop":
						return await ResponseStop(steamID).ConfigureAwait(false);
					default:
						return "Unrecognized command: " + message;
				}
			} else {
				string[] args = message.Split(' ');
				switch (args[0]) {
					case "!2fa":
						return Response2FA(steamID, args[1]);
					case "!rank":
						return await ResponseRank( steamID, args[1] ).ConfigureAwait(false);
					case "!2faoff":
						return Response2FAOff(steamID, args[1]);
					case "!2faok":
						return await Response2FAOK(steamID, args[1]).ConfigureAwait(false);
					case "!addlicense":
						if (args.Length > 2) {
							return await ResponseAddLicense(steamID, args[1], args[2]).ConfigureAwait(false);
						} else {
							return await ResponseAddLicense(steamID, BotName, args[1]).ConfigureAwait(false);
						}
					case "!farm":
						return await ResponseFarm(steamID, args[1]).ConfigureAwait(false);
					case "!loot":
						return await ResponseLoot(steamID, args[1]).ConfigureAwait(false);
					case "!owns":
						if (args.Length > 2) {
							return await ResponseOwns(steamID, args[1], args[2]).ConfigureAwait(false);
						} else {
							return await ResponseOwns(steamID, BotName, args[1]).ConfigureAwait(false);
						}
					case "!play":
						if (args.Length > 2) {
							return await ResponsePlay(steamID, args[1], args[2]).ConfigureAwait(false);
						} else {
							return await ResponsePlay(steamID, BotName, args[1]).ConfigureAwait(false);
						}
					case "!redeem":
						if (args.Length > 2) {
							return await ResponseRedeem(steamID, args[1], args[2], false).ConfigureAwait(false);
						} else {
							return await ResponseRedeem(steamID, BotName, args[1], false).ConfigureAwait(false);
						}
					case "!start":
						return await ResponseStart(steamID, args[1]).ConfigureAwait(false);
					case "!status":
						return ResponseStatus(steamID, args[1]);
					case "!stop":
						return await ResponseStop(steamID, args[1]).ConfigureAwait(false);
					default:
						return "Unrecognized command: " + args[0];
				}
			}
		}

		private async Task Start() {
			if (SteamClient.IsConnected) {
				return;
			}

			if (!KeepRunning) {
				KeepRunning = true;
				Task.Run(() => HandleCallbacks()).Forget();
			}

			Logging.LogGenericInfo("Starting...", BotName);

			// 2FA tokens are expiring soon, use limiter only when we don't have any pending
			if (TwoFactorAuth == null) {
				await Program.LimitSteamRequestsAsync().ConfigureAwait(false);
			}

			SteamClient.Connect();
		}

		private async Task Stop() {
			if (!SteamClient.IsConnected) {
				return;
			}

			Logging.LogGenericInfo("Stopping...", BotName);

			for (byte i = 0; i < WebBrowser.MaxRetries && SteamClient.IsConnected; i++) {
				SteamClient.Disconnect();
				await Utilities.SleepAsync(1000).ConfigureAwait(false);
			}

			if (SteamClient.IsConnected) {
				Logging.LogGenericWarning("Could not stop this bot instance!", BotName);
			} else {
				Logging.LogGenericInfo("Stopped!", BotName);
			}
		}

		private async Task Shutdown() {
			KeepRunning = false;
			await Stop().ConfigureAwait(false);
			Program.OnBotShutdown();
		}

		private void ImportAuthenticator(string maFilePath) {
			if (BotDatabase.SteamGuardAccount != null || !File.Exists(maFilePath)) {
				return;
			}

			Logging.LogGenericInfo("Converting SDA .maFile into ASF format...", BotName);
			try {
				BotDatabase.SteamGuardAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(File.ReadAllText(maFilePath));
				File.Delete(maFilePath);
				Logging.LogGenericInfo("Success!", BotName);
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
				return;
			}

			// If this is SDA file, then we should already have everything ready
			if (BotDatabase.SteamGuardAccount.Session != null) {
				Logging.LogGenericInfo("Successfully finished importing mobile authenticator!", BotName);
				return;
			}

			// But here we're dealing with WinAuth authenticator
			Logging.LogGenericInfo("ASF requires a few more steps to complete authenticator import...", BotName);

			InitializeLoginAndPassword();

			UserLogin userLogin = new UserLogin(BotConfig.SteamLogin, BotConfig.SteamPassword);
			LoginResult loginResult;
			while ((loginResult = userLogin.DoLogin()) != LoginResult.LoginOkay) {
				switch (loginResult) {
					case LoginResult.Need2FA:
						userLogin.TwoFactorCode = Program.GetUserInput(BotName, Program.EUserInputType.TwoFactorAuthentication);
						break;
					default:
						Logging.LogGenericError("Unhandled situation: " + loginResult, BotName);
						return;
				}
			}

			if (userLogin.Session == null) {
				BotDatabase.SteamGuardAccount = null;
				Logging.LogGenericError("Session is invalid, linking can't be completed!", BotName);
				return;
			}

			BotDatabase.SteamGuardAccount.FullyEnrolled = true;
			BotDatabase.SteamGuardAccount.Session = userLogin.Session;

			if (string.IsNullOrEmpty(BotDatabase.SteamGuardAccount.DeviceID)) {
				BotDatabase.SteamGuardAccount.DeviceID = Program.GetUserInput(BotName, Program.EUserInputType.DeviceID);
			}

			BotDatabase.Save();
			Logging.LogGenericInfo("Successfully finished importing mobile authenticator!", BotName);
		}

		private string ResponseStatus(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			if (CardsFarmer.CurrentGamesFarming.Count > 0) {
				return "Bot " + BotName + " is currently farming appIDs: " + string.Join(", ", CardsFarmer.CurrentGamesFarming) + " and has a total of " + CardsFarmer.GamesToFarm.Count + " games left to farm.";
			} else {
				return "Bot " + BotName + " is currently not farming anything.";
			}
		}
		private async Task<string> ResponseRank( ulong steamID, string profileID ) {
			if ( steamID == 0 ) {
				return null;
			}
			EClanRank rank = await ArchiWebHandler.GetProfileRank( UInt64.Parse(profileID) ).ConfigureAwait( false );

			return "Rank: " + rank.ToString( "g" );
		}
		private string ResponseRank( ulong steamID ) {
			if ( steamID == 0 ) {
				return null;
			}
			return "Bot's rank: " + rank.ToString( "g" );
		}

		private static string ResponseStatus(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return bot.ResponseStatus(steamID);
		}

		private static string ResponseStatusAll(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsOwner(steamID)) {
				return "ERROR: Not authorized!";
			}

			StringBuilder result = new StringBuilder(Environment.NewLine);

			int totalBotsCount = Bots.Count;
			int runningBotsCount = 0;

			foreach (Bot bot in Bots.Values) {
				result.Append(bot.ResponseStatus(steamID) + Environment.NewLine);
				if (bot.KeepRunning) {
					runningBotsCount++;
				}
			}

			result.Append("There are " + totalBotsCount + " bots initialized and " + runningBotsCount + " of them are currently running.");
			return result.ToString();
		}

        private async Task<string> ResponseLoot(ulong steamID) {
            if (steamID == 0) {
                return null;
            }

            if (!IsMaster(steamID)) {
                return "ERROR: Not authorized!";
            }

            if (BotConfig.SteamMasterID == 0) {
                return "Trade couldn't be send because SteamMasterID is not defined!";
            }

            await Trading.LimitInventoryRequestsAsync().ConfigureAwait(false);
			var itemsCount = 0;
			List<Steam.Item> inventoryItems = new List<Steam.Item>();
			List<ulong> gifts = new List<ulong>();

			foreach (string inventory in BotConfig.LootableInventories) {
				if (inventory == "753/1" ) {
					gifts = await ArchiWebHandler.GetMyGifts().ConfigureAwait(false);
					if ( gifts != null && gifts.Count != 0 ) {
						foreach ( var gid in gifts ) {
							await ArchiWebHandler.SendGift( BotConfig.SteamMasterID, gid ).ConfigureAwait( false );
							itemsCount++;
						}

					}
				} else {
					inventoryItems.AddRange(await ArchiWebHandler.GetMyTradableInventory(inventory).ConfigureAwait(false));					
				}
			}
			int tradeResult = await ArchiWebHandler.SendTradeOffer( inventoryItems, BotConfig.SteamMasterID, BotConfig.SteamTradeToken ).ConfigureAwait( false );
			if ( tradeResult > -1 ) {
				await AcceptConfirmations( Confirmation.ConfirmationType.Trade ).ConfigureAwait( false );
				itemsCount += tradeResult;
			}
			return itemsCount + " items out of " + (inventoryItems.Count + gifts.Count) + " were sent!";
        }

		private static async Task<string> ResponseLoot(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseLoot(steamID).ConfigureAwait(false);
		}

		private string Response2FA(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			if (BotDatabase.SteamGuardAccount == null) {
				return "That bot doesn't have ASF 2FA enabled!";
			}

			long timeLeft = 30 - TimeAligner.GetSteamTime() % 30;
			return "2FA Token: " + BotDatabase.SteamGuardAccount.GenerateSteamGuardCode() + " (expires in " + timeLeft + " seconds)";
		}

		private static string Response2FA(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return bot.Response2FA(steamID);
		}

		private string Response2FAOff(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			if (BotDatabase.SteamGuardAccount == null) {
				return "That bot doesn't have ASF 2FA enabled!";
			}

			if (DelinkMobileAuthenticator()) {
				return "Done! Bot is no longer using ASF 2FA";
			} else {
				return "Something went wrong during delinking mobile authenticator!";
			}
		}

		private static string Response2FAOff(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return bot.Response2FAOff(steamID);
		}

		private async Task<string> Response2FAOK(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			if (BotDatabase.SteamGuardAccount == null) {
				return "That bot doesn't have ASF 2FA enabled!";
			}

			await AcceptConfirmations().ConfigureAwait(false);
			return "Done!";
		}

		private static async Task<string> Response2FAOK(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.Response2FAOK(steamID).ConfigureAwait(false);
		}

		private static string ResponseExit(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsOwner(steamID)) {
				return "ERROR: Not authorized!";
			}

			Program.Exit();
			return null;
		}

		private async Task<string> ResponseFarm(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			await CardsFarmer.RestartFarming().ConfigureAwait(false);
			return "Done!";
		}

		private static async Task<string> ResponseFarm(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseFarm(steamID).ConfigureAwait(false);
		}
		//TODO clean up
		private async Task<string> ResponseRedeem(ulong steamID, string message, bool validate) {
			if (steamID == 0 || string.IsNullOrEmpty(message)) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			StringBuilder response = new StringBuilder();
			using (StringReader reader = new StringReader(message)) {
				string key = reader.ReadLine();
				IEnumerator<Bot> iterator = Bots.Values.GetEnumerator();
				Bot currentBot = this;
				while (key != null) {
					if (currentBot == null) {
						break;
					}

					if (validate && !IsValidCdKey(key)) {
						key = reader.ReadLine();
						continue;
					}

					ArchiHandler.PurchaseResponseCallback result;
					try {
						result = await currentBot.ArchiHandler.RedeemKey(key).ConfigureAwait(false);
					} catch (Exception e) {
						Logging.LogGenericException(e, currentBot.BotName);
						break;
					}

					if (result == null) {
						break;
					}

					var purchaseResult = result.PurchaseResult;
					var items = result.Items;

					switch (purchaseResult) {
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.AlreadyOwned:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.BaseGameRequired:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OnCooldown:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.RegionLocked:
							response.Append(Environment.NewLine + "<" + currentBot.BotName + "> Key: " + key + " | Status: " + purchaseResult + " | Items: " + string.Join("", items));
							
							key = reader.ReadLine();
							break;
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK:
							response.Append(Environment.NewLine + "<" + currentBot.BotName + "> Key: " + key + " | Status: " + purchaseResult + " | Items: " + string.Join("", items));							
							key = reader.ReadLine();
							break;
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.DuplicatedKey:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.InvalidKey:
							response.Append(Environment.NewLine + "<" + currentBot.BotName + "> Key: " + key + " | Status: " + purchaseResult + " | Items: " + string.Join("", items));							
							key = reader.ReadLine();
							break;
					}
				}
			}

			if (response.Length == 0) {
				return null;
			}

			return response.ToString();
		}

		private static async Task<string> ResponseRedeem(ulong steamID, string botName, string message, bool validate) {
			if (steamID == 0 || string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(message)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseRedeem(steamID, message, validate).ConfigureAwait(false);
		}

		private static string ResponseRejoinChat(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsOwner(steamID)) {
				return "ERROR: Not authorized!";
			}

			foreach (Bot bot in Bots.Values) {
				bot.JoinMasterChat();
			}

			return "Done!";
		}

		private static string ResponseRestart(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsOwner(steamID)) {
				return "ERROR: Not authorized!";
			}

			Program.Restart();
			return null;
		}

		private async Task<string> ResponseAddLicense(ulong steamID, HashSet<uint> gameIDs) {
			if (steamID == 0 || gameIDs == null || gameIDs.Count == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			StringBuilder result = new StringBuilder();
			foreach (uint gameID in gameIDs) {
				SteamApps.FreeLicenseCallback callback;
				try {
					callback = await SteamApps.RequestFreeLicense(gameID);
				} catch (Exception e) {
					Logging.LogGenericException(e, BotName);
					continue;
				}

				result.AppendLine("Result: " + callback.Result + " | Granted apps: " + string.Join(", ", callback.GrantedApps) + " " + string.Join(", ", callback.GrantedPackages));
			}

			return result.ToString();
		}

		private static async Task<string> ResponseAddLicense(ulong steamID, string botName, string games) {
			if (steamID == 0 || string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(games)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			string[] gameIDs = games.Split(',');

			HashSet<uint> gamesToRedeem = new HashSet<uint>();
			foreach (string game in gameIDs) {
				uint gameID;
				if (!uint.TryParse(game, out gameID)) {
					continue;
				}
				gamesToRedeem.Add(gameID);
			}

			if (gamesToRedeem.Count == 0) {
				return "Couldn't parse any games given!";
			}

			return await bot.ResponseAddLicense(steamID, gamesToRedeem).ConfigureAwait(false);
		}

		private async Task<string> ResponseOwns(ulong steamID, string games) {
			if (steamID == 0 || string.IsNullOrEmpty(games)) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			Dictionary<uint, string> ownedGames = await ArchiWebHandler.GetOwnedGames().ConfigureAwait(false);
			if (ownedGames == null || ownedGames.Count == 0) {
				return "List of owned games is empty!";
			}

			// Check if this is uint
			uint appID;
			if (uint.TryParse(games, out appID)) {
				string ownedName;
				if (ownedGames.TryGetValue(appID, out ownedName)) {
					return "Owned already: " + appID + " | " + ownedName;
				} else {
					return "Not owned yet: " + appID;
				}
			}

			StringBuilder response = new StringBuilder();

			// This is a string
			foreach (KeyValuePair<uint, string> game in ownedGames) {
				if (game.Value.IndexOf(games, StringComparison.OrdinalIgnoreCase) < 0) {
					continue;
				}

				response.AppendLine(Environment.NewLine + "Owned already: " + game.Key + " | " + game.Value);
			}

			if (response.Length > 0) {
				return response.ToString();
			} else {
				return "Not owned yet: " + games;
			}
		}

		private static async Task<string> ResponseOwns(ulong steamID, string botName, string games) {
			if (steamID == 0 || string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(games)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseOwns(steamID, games).ConfigureAwait(false);
		}

		private async Task<string> ResponsePlay(ulong steamID, HashSet<uint> gameIDs) {
			if (steamID == 0 || gameIDs == null || gameIDs.Count == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			if (gameIDs.Contains(0)) {
				if (await CardsFarmer.SwitchToManualMode(false).ConfigureAwait(false)) {
					ResetGamesPlayed();
				}
			} else {
				await CardsFarmer.SwitchToManualMode(true).ConfigureAwait(false);
				ArchiHandler.PlayGames(gameIDs);
			}

			return "Done!";
		}

		private static async Task<string> ResponsePlay(ulong steamID, string botName, string games) {
			if (steamID == 0 || string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(games)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			string[] gameIDs = games.Split(',');

			HashSet<uint> gamesToPlay = new HashSet<uint>();
			foreach (string game in gameIDs) {
				uint gameID;
				if (!uint.TryParse(game, out gameID)) {
					continue;
				}
				gamesToPlay.Add(gameID);
			}

			if (gamesToPlay.Count == 0) {
				return "Couldn't parse any games given!";
			}

			return await bot.ResponsePlay(steamID, gamesToPlay).ConfigureAwait(false);
		}

		private async Task<string> ResponseStart(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			if (KeepRunning) {
				return "That bot instance is already running!";
			}

			await Start().ConfigureAwait(false);
			return "Done!";
		}

		private static async Task<string> ResponseStart(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseStart(steamID).ConfigureAwait(false);
		}

		private async Task<string> ResponseStop(ulong steamID) {
			if (steamID == 0) {
				return null;
			}

			if (!IsMaster(steamID)) {
				return "ERROR: Not authorized!";
			}

			if (!KeepRunning) {
				return "That bot instance is already inactive!";
			}

			await Shutdown().ConfigureAwait(false);
			return "Done!";
		}

		private static async Task<string> ResponseStop(ulong steamID, string botName) {
			if (steamID == 0 || string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseStop(steamID).ConfigureAwait(false);
		}
        
		private void HandleCallbacks() {
			TimeSpan timeSpan = TimeSpan.FromMilliseconds(CallbackSleep);
			while (KeepRunning || SteamClient.IsConnected) {
				CallbackManager.RunWaitCallbacks(timeSpan);
			}
		}

		private async Task HandleMessage(ulong steamID, ulong senderID, string message) {
			if (steamID == 0 || string.IsNullOrEmpty(message)) {
				return;
			}

			SendMessage(steamID, await Response(senderID, message).ConfigureAwait(false));
		}

		private void SendMessage(ulong steamID, string message) {
			if (steamID == 0 || string.IsNullOrEmpty(message)) {
				return;
			}

			if (new SteamID(steamID).IsChatAccount) {
				SteamFriends.SendChatRoomMessage(steamID, EChatEntryType.ChatMsg, message);
			} else {
				SteamFriends.SendChatMessage(steamID, EChatEntryType.ChatMsg, message);
			}
		}

		private void LinkMobileAuthenticator() {
			if (BotDatabase.SteamGuardAccount != null) {
				return;
			}

			Logging.LogGenericInfo("Linking new ASF MobileAuthenticator...", BotName);

			InitializeLoginAndPassword();

			UserLogin userLogin = new UserLogin(BotConfig.SteamLogin, BotConfig.SteamPassword);
			LoginResult loginResult;
			while ((loginResult = userLogin.DoLogin()) != LoginResult.LoginOkay) {
				switch (loginResult) {
					case LoginResult.NeedEmail:
						userLogin.EmailCode = Program.GetUserInput(BotName, Program.EUserInputType.SteamGuard);
						break;
					default:
						Logging.LogGenericError("Unhandled situation: " + loginResult, BotName);
						return;
				}
			}

			AuthenticatorLinker authenticatorLinker = new AuthenticatorLinker(userLogin.Session);

			AuthenticatorLinker.LinkResult linkResult;
			while ((linkResult = authenticatorLinker.AddAuthenticator()) != AuthenticatorLinker.LinkResult.AwaitingFinalization) {
				switch (linkResult) {
					case AuthenticatorLinker.LinkResult.MustProvidePhoneNumber:
						authenticatorLinker.PhoneNumber = Program.GetUserInput(BotName, Program.EUserInputType.PhoneNumber);
						break;
					default:
						Logging.LogGenericError("Unhandled situation: " + linkResult, BotName);
						return;
				}
			}

			BotDatabase.SteamGuardAccount = authenticatorLinker.LinkedAccount;

			AuthenticatorLinker.FinalizeResult finalizeResult = authenticatorLinker.FinalizeAddAuthenticator(Program.GetUserInput(BotName, Program.EUserInputType.SMS));
			if (finalizeResult != AuthenticatorLinker.FinalizeResult.Success) {
				Logging.LogGenericError("Unhandled situation: " + finalizeResult, BotName);
				DelinkMobileAuthenticator();
				return;
			}

			Logging.LogGenericInfo("Successfully linked ASF as new mobile authenticator for this account!", BotName);
			Program.GetUserInput(BotName, Program.EUserInputType.RevocationCode, BotDatabase.SteamGuardAccount.RevocationCode);
		}

		private bool DelinkMobileAuthenticator() {
			if (BotDatabase.SteamGuardAccount == null) {
				return false;
			}

			bool result = BotDatabase.SteamGuardAccount.DeactivateAuthenticator();

			if (result) {
				BotDatabase.SteamGuardAccount = null;
			}

			return result;
		}

		private void JoinMasterChat() {
			if (BotConfig.SteamMasterClanID == 0) {
				return;
			}

			SteamFriends.JoinChat(BotConfig.SteamMasterClanID);
		}

		private void InitializeLoginAndPassword() {
			if (string.IsNullOrEmpty(BotConfig.SteamLogin)) {
				BotConfig.SteamLogin = Program.GetUserInput(BotName, Program.EUserInputType.Login);
			}

			if (string.IsNullOrEmpty(BotConfig.SteamPassword) && string.IsNullOrEmpty(BotDatabase.LoginKey)) {
				BotConfig.SteamPassword = Program.GetUserInput(BotName, Program.EUserInputType.Password);
			}
		}

		private void OnConnected(SteamClient.ConnectedCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.Result != EResult.OK) {
				Logging.LogGenericError("Unable to connect to Steam: " + callback.Result, BotName);
				return;
			}

			Logging.LogGenericInfo("Connected to Steam!", BotName);

			if (!KeepRunning) {
				Logging.LogGenericInfo("Disconnecting...", BotName);
				SteamClient.Disconnect();
				return;
			}

			byte[] sentryHash = null;
			if (File.Exists(SentryFile)) {
				try {
					byte[] sentryFileContent = File.ReadAllBytes(SentryFile);
					sentryHash = CryptoHelper.SHAHash(sentryFileContent);
				} catch (Exception e) {
					Logging.LogGenericException(e, BotName);
				}
			}

			InitializeLoginAndPassword();

			Logging.LogGenericInfo("Logging in...", BotName);

			// TODO: Please remove me immediately after https://github.com/SteamRE/SteamKit/issues/254 gets fixed
			if (Program.GlobalConfig.HackIgnoreMachineID) {
				Logging.LogGenericWarning("Using workaround for broken GenerateMachineID()!", BotName);
				ArchiHandler.HackedLogOn(new SteamUser.LogOnDetails {
					Username = BotConfig.SteamLogin,
					Password = BotConfig.SteamPassword,
					AuthCode = AuthCode,
					LoginID = LoginID,
					LoginKey = BotDatabase.LoginKey,
					TwoFactorCode = TwoFactorAuth,
					SentryFileHash = sentryHash,
					ShouldRememberPassword = true,
                    CellID = Program.GlobalDatabase.CellID
				});
				return;
			}

			SteamUser.LogOn(new SteamUser.LogOnDetails {
				Username = BotConfig.SteamLogin,
				Password = BotConfig.SteamPassword,
				AuthCode = AuthCode,
				LoginID = LoginID,
				LoginKey = BotDatabase.LoginKey,
				TwoFactorCode = TwoFactorAuth,
				SentryFileHash = sentryHash,
				ShouldRememberPassword = true,
                CellID = Program.GlobalDatabase.CellID
            });
		}

		private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo("Disconnected from Steam!", BotName);
			await CardsFarmer.StopFarming().ConfigureAwait(false);

			if (!KeepRunning) {
				return;
			}

			// If we initiated disconnect, do not attempt to reconnect
			if (callback.UserInitiated) {
				return;
			}

			if (InvalidPassword) {
				InvalidPassword = false;
				if (!string.IsNullOrEmpty(BotDatabase.LoginKey)) { // InvalidPassword means usually that login key has expired, if we used it
					BotDatabase.LoginKey = null;
					Logging.LogGenericInfo("Removed expired login key", BotName);
				} else { // If we didn't use login key, InvalidPassword usually means we got captcha or other network-based throttling
					Logging.LogGenericInfo("Will retry after 25 minutes...", BotName);
					await Utilities.SleepAsync(25 * 60 * 1000).ConfigureAwait(false); // Captcha disappears after around 20 minutes, so we make it 25
				}
			} else if (LoggedInElsewhere) {
				LoggedInElsewhere = false;
				Logging.LogGenericInfo("Account is being used elsewhere, ASF will try to resume farming in " + Program.GlobalConfig.AccountPlayingDelay + " minutes...", BotName);
				await Utilities.SleepAsync(Program.GlobalConfig.AccountPlayingDelay * 60 * 1000).ConfigureAwait(false);
			}

			Logging.LogGenericInfo("Reconnecting...", BotName);

			// 2FA tokens are expiring soon, use limiter only when we don't have any pending
			if (TwoFactorAuth == null) {
				await Program.LimitSteamRequestsAsync().ConfigureAwait(false);
			}

			SteamClient.Connect();
		}

		private void OnFreeLicense(SteamApps.FreeLicenseCallback callback) {
			if (callback == null) {
				return;
			}
		}

        private async void OnGuestPassList(SteamApps.GuestPassListCallback callback)
        {
            if (callback == null || callback.Result != EResult.OK || callback.CountGuestPassesToRedeem == 0 || callback.GuestPasses.Count == 0 || !BotConfig.AcceptGifts)
            {
                return;
            }

            bool acceptedSomething = false;
            foreach (KeyValue guestPass in callback.GuestPasses)
            {
                ulong gid = guestPass["gid"].AsUnsignedLong();
                if (gid == 0)
                {
                    continue;
                }

                Logging.LogGenericInfo("Accepting gift: " + gid + "...", BotName);
                var acceptResult = await ArchiWebHandler.AcceptGift(gid).ConfigureAwait(false);
                if (acceptResult > 0)
                {                                    
                    if (acceptResult > 1 && BotConfig.SteamMasterID != 0)
                    {
                        Logging.LogGenericInfo("Gift added in invenetory "+ acceptResult + "!", BotName);
                    } else
                    {
                        acceptedSomething = true;
                        Logging.LogGenericInfo("Success!", BotName);
                    }
                }
                else {
                    Logging.LogGenericInfo("Failed!", BotName);
                }
            }

            if (acceptedSomething)
            {
                CardsFarmer.RestartFarming().Forget();
            }
        }

        private void OnChatInvite(SteamFriends.ChatInviteCallback callback) {
			if (callback == null) {
				return;
			}

			if (!IsMaster(callback.PatronID)) {
				return;
			}

			SteamFriends.JoinChat(callback.ChatRoomID);
		}

		private async void OnChatMsg(SteamFriends.ChatMsgCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.ChatMsgType != EChatEntryType.ChatMsg) {
				return;
			}

			if (!IsMaster(callback.ChatterID)) {
				return;
			}

			switch (callback.Message) {
				case "!leave":
					SteamFriends.LeaveChat(callback.ChatRoomID);
					break;
				default:
					await HandleMessage(callback.ChatRoomID, callback.ChatterID, callback.Message).ConfigureAwait(false);
					break;
			}
		}

		private void OnFriendsList(SteamFriends.FriendsListCallback callback) {
			if (callback == null) {
				return;
			}
			bool isMasterInFriendList = false;
				 
			foreach (var friend in callback.FriendList) {	
				if (IsMaster( friend.SteamID.ConvertToUInt64())) {
					isMasterInFriendList = true;
				}			
				if (friend.Relationship != EFriendRelationship.RequestRecipient) {
					continue;
				}

				switch (friend.SteamID.AccountType) {
					case EAccountType.Clan:
						// TODO: Accept clan invites from master?
						break;
					default:
						if ( IsMaster(friend.SteamID) ) {
							SteamFriends.AddFriend( friend.SteamID );
							break;
						}
						if ( BotConfig.AddSlaves && IsSlave( friend.SteamID ) ) {
							SteamFriends.AddFriend( friend.SteamID );
							break;
						}
						break;
						
				}
			}

			if (BotConfig.SteamMasterID != 0 && !isMasterInFriendList) {
				foreach (var bot in Bots.Values ) {
					if ( bot.SteamUser.SteamID.ConvertToUInt64() == this.BotConfig.SteamMasterID && bot.BotConfig.AddSlaves) {
						bot.SteamFriends.AddFriend( this.SteamUser.SteamID );
						break;
					}
				}
				//In the case if master dont use bot
				if ( BotConfig.AddMaster ) {
					SteamFriends.AddFriend( BotConfig.SteamMasterID );
				}
			}
		}

		private async void OnFriendMsg(SteamFriends.FriendMsgCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.EntryType != EChatEntryType.ChatMsg) {
				return;
			}

			if (!IsMaster(callback.Sender)) {
				return;
			}

			await HandleMessage(callback.Sender, callback.Sender, callback.Message).ConfigureAwait(false);
		}

		private async void OnFriendMsgHistory(SteamFriends.FriendMsgHistoryCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.Result != EResult.OK) {
				return;
			}

			if (!IsMaster(callback.SteamID)) {
				return;
			}

			if (callback.Messages.Count == 0) {
				return;
			}

			// Get last message
			var lastMessage = callback.Messages[callback.Messages.Count - 1];

			// If message is read already, return
			if (!lastMessage.Unread) {
				return;
			}

			// If message is too old, return
			if (DateTime.UtcNow.Subtract(lastMessage.Timestamp).TotalMinutes > 1) {
				return;
			}

			// Handle the message
			await HandleMessage(callback.SteamID, callback.SteamID, lastMessage.Message).ConfigureAwait(false);
		}

		private void OnAccountInfo(SteamUser.AccountInfoCallback callback) {
			if (callback == null) {
				return;
			}
			if (BotConfig.PersonaName != null && !callback.PersonaName.Equals(BotConfig.PersonaName)) {
				SteamFriends.SetPersonaName( BotConfig.PersonaName );
			}
			SteamFriends.SetPersonaState(EPersonaState.Online);
		}

		private void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo("Logged off of Steam: " + callback.Result, BotName);

			switch (callback.Result) {
				case EResult.AlreadyLoggedInElsewhere:
				case EResult.LoggedInElsewhere:
				case EResult.LogonSessionReplaced:
					LoggedInElsewhere = true;
					break;
			}
		}

		private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
			if (callback == null) {
				return;
			}

			switch (callback.Result) {
				case EResult.AccountLogonDenied:
					AuthCode = Program.GetUserInput(BotConfig.SteamLogin, Program.EUserInputType.SteamGuard);
					break;
				case EResult.AccountLoginDeniedNeedTwoFactor:
					if (BotDatabase.SteamGuardAccount == null) {
						TwoFactorAuth = Program.GetUserInput(BotConfig.SteamLogin, Program.EUserInputType.TwoFactorAuthentication);
					} else {
						TwoFactorAuth = BotDatabase.SteamGuardAccount.GenerateSteamGuardCode();
					}
					break;
				case EResult.InvalidPassword:
					InvalidPassword = true;
					Logging.LogGenericWarning("Unable to login to Steam: " + callback.Result, BotName);
					break;
				case EResult.OK:
					Logging.LogGenericInfo("Successfully logged on!", BotName);

					if (callback.CellID != 0) {
						Program.GlobalDatabase.CellID = callback.CellID;
					}
					if ( callback.ClientSteamID.ConvertToUInt64() != BotDatabase.SteamID64 ) {
						BotDatabase.SteamID64 = callback.ClientSteamID.ConvertToUInt64();
					}
					if ( callback.ClientSteamID.AccountID != BotDatabase.AccountID ) {
						BotDatabase.AccountID = callback.ClientSteamID.AccountID;
					}
					// Support and convert SDA files
					string maFilePath = Path.Combine(Program.ConfigDirectory, callback.ClientSteamID.ConvertToUInt64() + ".maFile");
					if (BotDatabase.SteamGuardAccount == null && File.Exists(maFilePath)) {
						ImportAuthenticator(maFilePath);
					}
					
					// Reset one-time-only access tokens
					AuthCode = null;
					TwoFactorAuth = null;

					ResetGamesPlayed();

					if (string.IsNullOrEmpty(BotConfig.SteamParentalPIN)) {
						BotConfig.SteamParentalPIN = Program.GetUserInput(BotName, Program.EUserInputType.SteamParentalPIN);
					}

					if (!await ArchiWebHandler.Init(SteamClient, callback.WebAPIUserNonce, BotConfig.SteamParentalPIN).ConfigureAwait(false)) {
						await Restart().ConfigureAwait(false);
						return;
					}
					
					if (BotConfig.SteamMasterClanID != 0) {
						ulong steamID64 = callback.ClientSteamID.ConvertToUInt64();
						rank = await ArchiWebHandler.GetProfileRank( steamID64 ).ConfigureAwait( false );
						await ArchiWebHandler.JoinClan(BotConfig.SteamMasterClanID).ConfigureAwait(false);
						JoinMasterChat();
					}

					Trading.CheckTrades();

					Task.Run(async () => await CardsFarmer.StartFarming().ConfigureAwait(false)).Forget();
					break;
				case EResult.NoConnection:
				case EResult.ServiceUnavailable:
				case EResult.Timeout:
				case EResult.TryAnotherCM:
					Logging.LogGenericWarning("Unable to login to Steam: " + callback.Result, BotName);
					break;
				default: // Unexpected result, shutdown immediately
					Logging.LogGenericWarning("Unable to login to Steam: " + callback.Result, BotName);
					await Shutdown().ConfigureAwait(false);
					break;
			}
		}

		private void OnLoginKey(SteamUser.LoginKeyCallback callback) {
			if (callback == null) {
				return;
			}

			BotDatabase.LoginKey = callback.LoginKey;
			SteamUser.AcceptNewLoginKey(callback);
		}

		private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback) {
			if (callback == null) {
				return;
			}

			try {
				int fileSize;
				byte[] sentryHash;

				using (FileStream fileStream = File.Open(SentryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
					fileStream.Seek(callback.Offset, SeekOrigin.Begin);
					fileStream.Write(callback.Data, 0, callback.BytesToWrite);
					fileSize = (int) fileStream.Length;

					fileStream.Seek(0, SeekOrigin.Begin);
					using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
						sentryHash = sha.ComputeHash(fileStream);
					}
				}

				// Inform the steam servers that we're accepting this sentry file
				SteamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails {
					JobID = callback.JobID,
					FileName = callback.FileName,
					BytesWritten = callback.BytesToWrite,
					FileSize = fileSize,
					Offset = callback.Offset,
					Result = EResult.OK,
					LastError = 0,
					OneTimePassword = callback.OneTimePassword,
					SentryFileHash = sentryHash,
				});
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
			}
		}

		private async void OnNotifications(ArchiHandler.NotificationsCallback callback) {
			if (callback == null || callback.Notifications == null) {
				return;
			}

			bool checkTrades = false;
			foreach (var notification in callback.Notifications) {
				switch (notification.NotificationType) {
					case ArchiHandler.NotificationsCallback.Notification.ENotificationType.Trading:
						checkTrades = true;
						break;
				}
			}

			if (checkTrades) {
				Trading.CheckTrades();
			}
		}

		private void OnPurchaseResponse(ArchiHandler.PurchaseResponseCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.PurchaseResult == ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK) {
				// We will restart CF module to recalculate current status and decide about new optimal approach
				Task.Run(async () => await CardsFarmer.RestartFarming().ConfigureAwait(false)).Forget();
			}
		}
	}
}
