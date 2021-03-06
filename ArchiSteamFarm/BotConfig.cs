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
using System.IO;
using System.Xml;

namespace ArchiSteamFarm {
	internal sealed class BotConfig {

		[JsonProperty]
		internal string SteamLogin { get; set; } = null;

		[JsonProperty]
		internal string SteamPassword { get; set; } = null;

		[JsonProperty]
		internal string PersonaName { get; set; } = null;

		[JsonProperty]
		internal string AvatarFile { get; set; } = null;

		[JsonProperty]
		internal string SteamParentalPIN { get; set; } = "0";

		[JsonProperty( Required = Required.DisallowNull )]
		internal ulong SteamMasterID { get; private set; } = 0;

		[JsonProperty( Required = Required.DisallowNull )]
		internal ulong SteamMasterClanID { get; private set; } = 0;

		[JsonProperty( Required = Required.DisallowNull )]
		internal bool CardDropsRestricted { get; private set; } = false;

		[JsonProperty( Required = Required.DisallowNull )]
		internal bool AcceptGifts { get; private set; } = true;

		[JsonProperty]
		internal string SteamTradeToken { get; private set; } = null;

		[JsonProperty( Required = Required.DisallowNull )]
		internal HashSet<string> LootableInventories { get; private set; } = new HashSet<string>( new string[] { 
				//Steam
				"753/1",	//Gifts
				//"753/3",	//Coupons
				"753/6",	//Community
				//"753/7",	//Item Rewards
				//CS:GO
				//"730/2",	//Backpack
				//Team Fortress 2
				//"440/2",	//Backpack
		} );
		internal static BotConfig Load(string path) {
			if (!File.Exists(path)) {
				return null;
			}

			BotConfig botConfig;
			try {
				botConfig = JsonConvert.DeserializeObject<BotConfig>(File.ReadAllText(path));
			} catch (Exception e) {
				Logging.LogGenericException(e);
				return null;
			}

			return botConfig;
		}
		
		// This constructor is used only by deserializer
		private BotConfig() { }
	}
}
