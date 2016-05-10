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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiSteamFarm {
	internal static class Program {
		internal enum EUserInputType : byte {
			Unknown,
			DeviceID,
			Login,
			Password,
			PhoneNumber,
			SMS,
			SteamGuard,
			SteamParentalPIN,
			RevocationCode,
			TwoFactorAuthentication,
		}

		internal enum EMode : byte {
			Unknown,
			Normal, // Standard most common usage
			Client, // WCF client only
			Server // Normal + WCF server
		}

		internal const string ASF = "ASF";
		internal const string ConfigDirectory = "config";
		internal const string DebugDirectory = "debug";
		internal const string LogFile = "log.txt";
		internal const string GlobalConfigFile = ASF + ".json";
		internal const string GlobalDatabaseFile = ASF + ".db";

		private static readonly Assembly Assembly = Assembly.GetExecutingAssembly();
		internal static readonly Version Version = Assembly.GetName().Version;

		private static readonly object ConsoleLock = new object();
		private static readonly SemaphoreSlim SteamSemaphore = new SemaphoreSlim(1);
		private static readonly ManualResetEvent ShutdownResetEvent = new ManualResetEvent(false);
		private static readonly string ExecutableFile = Assembly.Location;
		private static readonly string ExecutableName = Path.GetFileName(ExecutableFile);
		private static readonly string ExecutableDirectory = Path.GetDirectoryName(ExecutableFile);
		private static readonly WCF WCF = new WCF();

		internal static GlobalConfig GlobalConfig { get; private set; }
		internal static GlobalDatabase GlobalDatabase { get; private set; }
		internal static bool ConsoleIsBusy { get; private set; } = false;
		
		private static EMode Mode = EMode.Normal;

		internal static void Exit(int exitCode = 0) {
			Environment.Exit(exitCode);
		}

		internal static bool Restart() {
			try {
				if (Process.Start(ExecutableFile, string.Join(" ", Environment.GetCommandLineArgs().Skip(1))) != null) {
					Exit();
					return true;
				} else {
					return false;
				}
			} catch (Exception e) {
				Logging.LogGenericException(e);
				return false;
			}
		}

		internal static async Task LimitSteamRequestsAsync() {
			await SteamSemaphore.WaitAsync().ConfigureAwait(false);
			Task.Run(async () => {
				await Utilities.SleepAsync(GlobalConfig.LoginLimiterDelay * 1000).ConfigureAwait(false);
				SteamSemaphore.Release();
			}).Forget();
		}

		internal static string GetUserInput(string botLogin, EUserInputType userInputType, string extraInformation = null) {
			if (userInputType == EUserInputType.Unknown) {
				return null;
			}

			string result;
			lock (ConsoleLock) {
				ConsoleIsBusy = true;
				switch (userInputType) {
					case EUserInputType.DeviceID:
						Console.Write("<" + botLogin + "> Please enter your Device ID (including \"android:\"): ");
						break;
					case EUserInputType.Login:
						Console.Write("<" + botLogin + "> Please enter your login: ");
						break;
					case EUserInputType.Password:
						Console.Write("<" + botLogin + "> Please enter your password: ");
						break;
					case EUserInputType.PhoneNumber:
						Console.Write("<" + botLogin + "> Please enter your full phone number (e.g. +1234567890): ");
						break;
					case EUserInputType.SMS:
						Console.Write("<" + botLogin + "> Please enter SMS code sent on your mobile: ");
						break;
					case EUserInputType.SteamGuard:
						Console.Write("<" + botLogin + "> Please enter the auth code sent to your email: ");
						break;
					case EUserInputType.SteamParentalPIN:
						Console.Write("<" + botLogin + "> Please enter steam parental PIN: ");
						break;
					case EUserInputType.RevocationCode:
						Console.WriteLine("<" + botLogin + "> PLEASE WRITE DOWN YOUR REVOCATION CODE: " + extraInformation);
						Console.WriteLine("<" + botLogin + "> THIS IS THE ONLY WAY TO NOT GET LOCKED OUT OF YOUR ACCOUNT!");
						Console.Write("<" + botLogin + "> Hit enter once ready...");
						break;
					case EUserInputType.TwoFactorAuthentication:
						Console.Write("<" + botLogin + "> Please enter your 2 factor auth code from your authenticator app: ");
						break;
					default:
						Console.Write("<" + botLogin + "> Please enter not documented yet value of \"" + userInputType + "\": ");
						break;
				}
				result = Console.ReadLine();
				Console.Clear(); // For security purposes
				ConsoleIsBusy = false;
			}

			return string.IsNullOrEmpty(result) ? null : result.Trim();
		}

		internal static void OnBotShutdown() {
			foreach (Bot bot in Bot.Bots.Values) {
				if (bot.KeepRunning) {
					return;
				}
			}

			if (WCF.IsServerRunning()) {
				return;
			}

			Logging.LogGenericInfo("No bots are running, exiting");
			ShutdownResetEvent.Set();
		}

		private static void InitServices() {
			GlobalConfig = GlobalConfig.Load();
			if (GlobalConfig == null) {
				Logging.LogGenericError("Global config could not be loaded, please make sure that ASF.json exists and is valid!");
				Thread.Sleep(5000);
				Exit(1);
			}

			GlobalDatabase = GlobalDatabase.Load();
			if (GlobalDatabase == null) {
				Logging.LogGenericError("Global database could not be loaded!");
				Thread.Sleep(5000);
				Exit(1);
			}

			ArchiWebHandler.Init();
			WebBrowser.Init();
			WCF.Init();
		}

		private static void ParseArgs(string[] args) {
			foreach (string arg in args) {
				switch (arg) {
					case "--client":
						Mode = EMode.Client;
						break;
					case "--server":
						Mode = EMode.Server;
						WCF.StartServer();
						break;
					default:
						if (arg.StartsWith("--")) {
							Logging.LogGenericWarning("Unrecognized parameter: " + arg);
							continue;
						}

						if (Mode != EMode.Client) {
							Logging.LogGenericWarning("Ignoring command because --client wasn't specified: " + arg);
							continue;
						}

						Logging.LogGenericInfo("Command sent: " + arg);

						// We intentionally execute this async block synchronously
						Logging.LogGenericInfo("Response received: " + WCF.SendCommand(arg));
						/*
						Task.Run(async () => {
							Logging.LogGenericNotice("WCF", "Response received: " + await WCF.SendCommand(arg).ConfigureAwait(false));
						}).Wait();
						*/
						break;
				}
			}
		}

		private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs args) {
			if (sender == null || args == null) {
				return;
			}

			Logging.LogGenericException((Exception) args.ExceptionObject);
		}

		private static void UnobservedTaskExceptionHandler(object sender, UnobservedTaskExceptionEventArgs args) {
			if (sender == null || args == null) {
				return;
			}

			Logging.LogGenericException(args.Exception);
		}

		private static void Main(string[] args) {
			AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
			TaskScheduler.UnobservedTaskException += UnobservedTaskExceptionHandler;

			Logging.LogGenericInfo("Archi's Steam Farm, version " + Version);
			Directory.SetCurrentDirectory(ExecutableDirectory);
			InitServices();

			// Allow loading configs from source tree if it's a debug build
			if (Debugging.IsDebugBuild) {

				// Common structure is bin/(x64/)Debug/ArchiSteamFarm.exe, so we allow up to 4 directories up
				for (var i = 0; i < 4; i++) {
					Directory.SetCurrentDirectory("..");
					if (Directory.Exists(ConfigDirectory)) {
						break;
					}
				}

				// If config directory doesn't exist after our adjustment, abort all of that
				if (!Directory.Exists(ConfigDirectory)) {
					Directory.SetCurrentDirectory(ExecutableDirectory);
				}
			}

			// If debugging is on, we prepare debug directory prior to running
			if (GlobalConfig.Debug) {
				if (Directory.Exists(DebugDirectory)) {
					Directory.Delete(DebugDirectory, true);
					Thread.Sleep(1000); // Dirty workaround giving Windows some time to sync
				}
				Directory.CreateDirectory(DebugDirectory);

				SteamKit2.DebugLog.AddListener(new Debugging.DebugListener(Path.Combine(Program.DebugDirectory, "debug.txt")));
				SteamKit2.DebugLog.Enabled = true;
			}

			// Parse args
			ParseArgs(args);

			// If we ran ASF as a client, we're done by now
			if (Mode == EMode.Client) {
				return;
			}

			// From now on it's server mode
			Logging.Init();

			if (!Directory.Exists(ConfigDirectory)) {
				Logging.LogGenericError("Config directory doesn't exist!");
				Thread.Sleep(5000);
				Exit(1);
			}

			// Before attempting to connect, initialize our list of CMs
			Bot.RefreshCMs(GlobalDatabase.CellID).Wait();

			foreach (var configFile in Directory.EnumerateFiles(ConfigDirectory, "*.json")) {
				string botName = Path.GetFileNameWithoutExtension(configFile);
				switch ( botName ) {
					case ASF:
					case "example":
					case "minimal":
						continue;
				}

				Bot bot = new Bot(botName);				
			}			

			// Check if we got any bots running
			OnBotShutdown();

			// Wait for signal to shutdown
			ShutdownResetEvent.WaitOne();

			// We got a signal to shutdown, consider giving user some time to read the message
			Thread.Sleep(5000);

			// This is over, cleanup only now
			WCF.StopServer();
		}
	}
}
