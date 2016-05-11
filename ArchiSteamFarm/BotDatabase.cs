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
using System;
using System.IO;

namespace ArchiSteamFarm {
	internal sealed class BotDatabase
	{
		internal string LoginKey {
			get {
				return _LoginKey;
			}
			set {
				if ( _LoginKey == value ) {
					return;
				}

				_LoginKey = value;
				Save();
			}
		}
		internal string SteamApiKey {
			get {
				return _SteamApiKey;
			}
			set {
				if ( _SteamApiKey == value ) {
					return;
				}

				_SteamApiKey = value;
				Save();
			}
		}

		internal ulong SteamID64 {
			get {
				return _SteamID64;
			}
			set {
				if ( _SteamID64 == value ) {
					return;
				}

				_SteamID64 = value;
				Save();
			}
		}
		internal uint AccountID {
			get {
				return _AccountID;
			}
			set {
				if ( _AccountID == value ) {
					return;
				}

				_AccountID = value;
				Save();
			}
		}
		internal SteamGuardAccount SteamGuardAccount {
			get {
				return _SteamGuardAccount;
			}
			set {
				if ( _SteamGuardAccount == value ) {
					return;
				}

				_SteamGuardAccount = value;
				Save();
			}
		}

		[JsonProperty(Required = Required.AllowNull)]
		private string _LoginKey;

		[JsonProperty]
		private string _SteamApiKey;

		[JsonProperty]
		private ulong _SteamID64 = 0;

		[JsonProperty]
		private uint _AccountID = 0;

		[JsonProperty(Required = Required.AllowNull)]
		private SteamGuardAccount _SteamGuardAccount;

		private string FilePath;

		internal static BotDatabase Load(string filePath) {
			if (!File.Exists(filePath)) {
				return new BotDatabase(filePath);
			}

			BotDatabase botDatabase;
			try {
				botDatabase = JsonConvert.DeserializeObject<BotDatabase>(File.ReadAllText(filePath));
			} catch (Exception e) {
				Logging.LogGenericException(e);
				return null;
			}

			botDatabase.FilePath = filePath;
			return botDatabase;
		}

		// This constructor is used when creating new database
		private BotDatabase(string filePath) {
			FilePath = filePath;
			Save();
		}

		// This constructor is used only by deserializer
		private BotDatabase() { }

		internal void Save() {
			lock (FilePath) {
				try {
					File.WriteAllText(FilePath, JsonConvert.SerializeObject(this));
				} catch (Exception e) {
					Logging.LogGenericException(e);
				}
			}
		}
	}
}
