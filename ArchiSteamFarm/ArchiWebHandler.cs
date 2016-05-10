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
using Newtonsoft.Json.Linq;
using HtmlAgilityPack;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace ArchiSteamFarm {
	internal sealed class ArchiWebHandler {
		
		private const string SteamCommunity = "steamcommunity.com";

		private static string SteamCommunityURL = "https://" + SteamCommunity;

		private static int Timeout = 30 * 1000;

		private readonly Bot Bot;
		private readonly Dictionary<string, string> Cookie = new Dictionary<string, string>(4);

		private ulong SteamID;

		internal static void Init() {
			Timeout = Program.GlobalConfig.HttpTimeout * 1000;
			SteamCommunityURL = (Program.GlobalConfig.ForceHttp ? "http://" : "https://") + SteamCommunity;
		}

		internal ArchiWebHandler(Bot bot) {
			if (bot == null) {
				return;
			}

			Bot = bot;
		}

		internal async Task<bool> Init(SteamClient steamClient, string webAPIUserNonce, string parentalPin) {
			if (steamClient == null || steamClient.SteamID == null || string.IsNullOrEmpty(webAPIUserNonce)) {
				return false;
			}

			SteamID = steamClient.SteamID;

			string sessionID = Convert.ToBase64String(Encoding.UTF8.GetBytes(SteamID.ToString()));

			// Generate an AES session key
			byte[] sessionKey = CryptoHelper.GenerateRandomBlock(32);

			// RSA encrypt it with the public key for the universe we're on
			byte[] cryptedSessionKey;
			using (RSACrypto rsa = new RSACrypto(KeyDictionary.GetPublicKey(steamClient.ConnectedUniverse))) {
				cryptedSessionKey = rsa.Encrypt(sessionKey);
			}

			// Copy our login key
			byte[] loginKey = new byte[webAPIUserNonce.Length];
			Array.Copy(Encoding.ASCII.GetBytes(webAPIUserNonce), loginKey, webAPIUserNonce.Length);

			// AES encrypt the loginkey with our session key
			byte[] cryptedLoginKey = CryptoHelper.SymmetricEncrypt(loginKey, sessionKey);

			// Do the magic
			Logging.LogGenericInfo("Logging in to ISteamUserAuth...", Bot.BotName);

			KeyValue authResult;
			using (dynamic iSteamUserAuth = WebAPI.GetInterface("ISteamUserAuth")) {
				iSteamUserAuth.Timeout = Timeout;

				try {
					authResult = iSteamUserAuth.AuthenticateUser(
						steamid: SteamID,
						sessionkey: Encoding.ASCII.GetString(WebUtility.UrlEncodeToBytes(cryptedSessionKey, 0, cryptedSessionKey.Length)),
						encrypted_loginkey: Encoding.ASCII.GetString(WebUtility.UrlEncodeToBytes(cryptedLoginKey, 0, cryptedLoginKey.Length)),
						method: WebRequestMethods.Http.Post,
						secure: !Program.GlobalConfig.ForceHttp
					);
				} catch (Exception e) {
					Logging.LogGenericException(e, Bot.BotName);
					return false;
				}
			}

			if (authResult == null) {
				return false;
			}

			Logging.LogGenericInfo("Success!", Bot.BotName);

			string steamLogin = authResult["token"].AsString();
			string steamLoginSecure = authResult["tokensecure"].AsString();

			Cookie["sessionid"] = sessionID;
			Cookie["steamLogin"] = steamLogin;
			Cookie["steamLoginSecure"] = steamLoginSecure;

			// The below is used for display purposes only
			Cookie["webTradeEligibility"] = "{\"allowed\":0,\"reason\":0,\"allowed_at_time\":0,\"steamguard_required_days\":0,\"sales_this_year\":0,\"max_sales_per_year\":0,\"forms_requested\":0}";

			await UnlockParentalAccount(parentalPin).ConfigureAwait(false);
			return true;
		}

		internal async Task<bool?> IsLoggedIn() {
			if (SteamID == 0) {
				return false;
			}

			HtmlDocument htmlDocument = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && htmlDocument == null; i++) {
				htmlDocument = await WebBrowser.UrlGetToHtmlDocument(SteamCommunityURL + "/my/profile", Cookie).ConfigureAwait(false);
			}

			if (htmlDocument == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			HtmlNode htmlNode = htmlDocument.DocumentNode.SelectSingleNode("//span[@id='account_pulldown']");
			return htmlNode != null;
		}

		internal async Task<bool> ReconnectIfNeeded() {
			bool? isLoggedIn = await IsLoggedIn().ConfigureAwait(false);
			if (isLoggedIn.HasValue && !isLoggedIn.Value) {
				Logging.LogGenericInfo("Reconnecting because our sessionID expired!", Bot.BotName);
				Task.Run(async () => await Bot.Restart().ConfigureAwait(false)).Forget();
				return true;
			}

			return false;
		}

		internal async Task<Dictionary<uint, string>> GetOwnedGames() {
			if (SteamID == 0) {
				return null;
			}

			string request = SteamCommunityURL + "/profiles/" + SteamID + "/games/?xml=1";

			XmlDocument response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlGetToXML(request, Cookie).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			XmlNodeList xmlNodeList = response.SelectNodes("gamesList/games/game");
			if (xmlNodeList == null || xmlNodeList.Count == 0) {
				return null;
			}

			Dictionary<uint, string> result = new Dictionary<uint, string>(xmlNodeList.Count);
			foreach (XmlNode xmlNode in xmlNodeList) {
				XmlNode appNode = xmlNode.SelectSingleNode("appID");
				if (appNode == null) {
					continue;
				}

				uint appID;
				if (!uint.TryParse(appNode.InnerText, out appID)) {
					continue;
				}

				XmlNode nameNode = xmlNode.SelectSingleNode("name");
				if (nameNode == null) {
					continue;
				}

				result[appID] = nameNode.InnerText;
			}

			return result;
		}

		internal List<Steam.TradeOffer> GetTradeOffers() {
			if (string.IsNullOrEmpty(Bot.BotDatabase.SteamApiKey)) {
				return null;
			}

			KeyValue response = null;
			using (dynamic iEconService = WebAPI.GetInterface("IEconService", Bot.BotDatabase.SteamApiKey )) {
				iEconService.Timeout = Timeout;

				for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
					try {
						response = iEconService.GetTradeOffers(
							get_received_offers: 1,
							active_only: 1,
							secure: !Program.GlobalConfig.ForceHttp
						);
					} catch (Exception e) {
						Logging.LogGenericException(e, Bot.BotName);
					}
				}
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			List<Steam.TradeOffer> result = new List<Steam.TradeOffer>();
			foreach (KeyValue trade in response["trade_offers_received"].Children) {
				Steam.TradeOffer tradeOffer = new Steam.TradeOffer {
					tradeofferid = trade["tradeofferid"].AsString(),
					accountid_other = (uint) trade["accountid_other"].AsUnsignedLong(), // TODO: Correct this when SK2 with https://github.com/SteamRE/SteamKit/pull/255 gets released
					trade_offer_state = trade["trade_offer_state"].AsEnum<Steam.TradeOffer.ETradeOfferState>()
				};
				foreach (KeyValue item in trade["items_to_give"].Children) {
					tradeOffer.items_to_give.Add(new Steam.Item {
						appid = item["appid"].AsString(),
						contextid = item["contextid"].AsString(),
						assetid = item["assetid"].AsString(),
						classid = item["classid"].AsString(),
						instanceid = item["instanceid"].AsString(),
						amount = item["amount"].AsString(),
					});
				}
				foreach (KeyValue item in trade["items_to_receive"].Children) {
					tradeOffer.items_to_receive.Add(new Steam.Item {
						appid = item["appid"].AsString(),
						contextid = item["contextid"].AsString(),
						assetid = item["assetid"].AsString(),
						classid = item["classid"].AsString(),
						instanceid = item["instanceid"].AsString(),
						amount = item["amount"].AsString(),
					});
				}
				result.Add(tradeOffer);
			}

			return result;
		}
		//TODO perform succes code and error check
        internal async Task<ulong> AcceptGift(ulong gid)
        {
            if (gid == 0)
            {
                return 0;
            }

            string sessionID;
            if (!Cookie.TryGetValue("sessionid", out sessionID))
            {
                return 0;
            }
            if (string.IsNullOrEmpty(sessionID))
            {
                Logging.LogNullError("sessionID");
                return 0;
            }

            string request = SteamCommunityURL + "/gifts/" + gid + "/acceptunpack";
            Dictionary<string, string> data = new Dictionary<string, string>(1) {
                { "sessionid", sessionID }
            };

            HttpResponseMessage response = null;
            for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++)
            {
                response = await WebBrowser.UrlPost(request, data, Cookie).ConfigureAwait(false);
            }

            if (response == null)
            {
                Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
                return 0;
            }          
            JObject objResponse = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (objResponse["gidgiftnew"] != null)
            {
                return UInt64.Parse(objResponse["gidgiftnew"].ToString());
            }
            return 1;
        }
        internal async Task<bool> SendGift(ulong recipientID, ulong gid)
        {
            if (gid == 0)
            {
                return false;
            }

            string sessionID;
            if (!Cookie.TryGetValue("sessionid", out sessionID))
            {
                return false;
            }
            if (string.IsNullOrEmpty(sessionID))
            {
                Logging.LogNullError("sessionID");
                return false;
            }
            var recipientID3 = new SteamID();
            recipientID3.SetFromUInt64(recipientID);
            string request = "https://store.steampowered.com/checkout/sendgiftsubmit/";
            Dictionary<string, string> data = new Dictionary<string, string>(2) {
                {"GifteeAccountID", recipientID3.AccountID.ToString()},
                {"GifteeEmail", "add_to_cart"},
                {"GifteeName", "master"},
                {"GiftMessage", "Sent by ASF"},
                {"GiftSentiment", ""},
                {"GiftSignature", "ASF"},
                {"GiftGID", gid.ToString()},
                {"SessionID", sessionID}
            };

            HttpResponseMessage response = null;
            for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++)
            {
                response = await WebBrowser.UrlPost(request, data, Cookie).ConfigureAwait(false);
            }

            if (response == null)
            {
                Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
                return false;
            }

            return true;
        }
        internal async Task<bool> JoinClan(ulong clanID) {
			if (clanID == 0) {
				return false;
			}

			string sessionID;
			if (!Cookie.TryGetValue("sessionid", out sessionID)) {
				return false;
			}

			string request = SteamCommunityURL + "/gid/" + clanID;

			Dictionary<string, string> data = new Dictionary<string, string>(2) {
				{"sessionID", sessionID},
				{"action", "join"}
			};

			HttpResponseMessage response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlPost(request, data, Cookie).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return false;
			}

			return true;
		}

		internal async Task<bool> AcceptTradeOffer(ulong tradeID) {
			if (tradeID == 0) {
				return false;
			}

			string sessionID;
			if (!Cookie.TryGetValue("sessionid", out sessionID)) {
				return false;
			}

			string referer = SteamCommunityURL + "/tradeoffer/" + tradeID;
			string request = referer + "/accept";

			Dictionary<string, string> data = new Dictionary<string, string>(3) {
				{"sessionid", sessionID},
				{"serverid", "1"},
				{"tradeofferid", tradeID.ToString()}
			};

			HttpResponseMessage response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlPost(request, data, Cookie, referer).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return false;
			}

			return true;
		}

		internal bool DeclineTradeOffer(ulong tradeID) {
			if (tradeID == 0 || string.IsNullOrEmpty( Bot.BotDatabase.SteamApiKey )) {
				return false;
			}

			KeyValue response = null;
			using (dynamic iEconService = WebAPI.GetInterface("IEconService", Bot.BotDatabase.SteamApiKey )) {
				iEconService.Timeout = Timeout;

				for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
					try {
						response = iEconService.DeclineTradeOffer(
							tradeofferid: tradeID.ToString(),
							method: WebRequestMethods.Http.Post,
							secure: !Program.GlobalConfig.ForceHttp
						);
					} catch (Exception e) {
						Logging.LogGenericException(e, Bot.BotName);
					}
				}
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return false;
			}

			return true;
		}

        internal async Task<List<Steam.Item>> GetMyTradableInventory(string inventory)
        {
            List<Steam.Item> result = new List<Steam.Item>();
            
            JObject jObject = null;
            for (byte i = 0; i < WebBrowser.MaxRetries && jObject == null; i++)
            {
                jObject = await WebBrowser.UrlGetToJObject(SteamCommunityURL + "/my/inventory/json/"+ inventory + "/?trading=1", Cookie).ConfigureAwait(false);
            }

            if (jObject == null)
            {
                Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
                return null;
            }

            IEnumerable<JToken> jTokens = jObject.SelectTokens("$.rgInventory.*");
            if (jTokens == null)
            {
                Logging.LogNullError("jTokens", Bot.BotName);
                return null;
            }

            foreach (JToken jToken in jTokens)
            {
                try
                {
                    var item = JsonConvert.DeserializeObject<Steam.Item>(jToken.ToString());
                    item.appid = inventory.Split('/')[0];
                    item.contextid = inventory.Split('/')[1];
                    result.Add(item);
                }
                catch (Exception e)
                {
                    Logging.LogGenericException(e, Bot.BotName);
                }
            }

            return result;
        }
        internal async Task<List<ulong>> GetMyGifts()
        {
            JObject jObject = null;
            for (byte i = 0; i < WebBrowser.MaxRetries && jObject == null; i++)
            {
                jObject = await WebBrowser.UrlGetToJObject(SteamCommunityURL + "/my/inventory/json/753/1", Cookie).ConfigureAwait(false);
            }

            if (jObject == null)
            {
                Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
                return null;
            }

            IEnumerable<JToken> jTokens = jObject.SelectTokens("$.rgInventory.*");
            if (jTokens == null)
            {
                Logging.LogNullError("jTokens", Bot.BotName);
                return null;
            }

            var result = new List<ulong>();
            foreach (JToken jToken in jTokens)
            {
                try
                {
                    result.Add(UInt64.Parse(jToken.SelectToken("id").ToString()));
                }
                catch (Exception e)
                {
                    Logging.LogGenericException(e, Bot.BotName);
                }
            }

            return result;
        }

        internal async Task<int> SendTradeOffer(List<Steam.Item> inventory, ulong partnerID, string token = null) {
			if (inventory == null || inventory.Count == 0 || partnerID == 0) {
				return -1;
			}

			string sessionID;
			if (!Cookie.TryGetValue("sessionid", out sessionID)) {
				return -1;
			}

			List<Steam.TradeOfferRequest> trades = new List<Steam.TradeOfferRequest>(1 + inventory.Count / Trading.MaxItemsPerTrade);
			int itemsCount = 0;
			Steam.TradeOfferRequest singleTrade = null;
			for (ushort i = 0; i < inventory.Count; i++) {
				if (i % Trading.MaxItemsPerTrade == 0) {
					if (trades.Count >= Trading.MaxTradesPerAccount) {
						break;
					}

					singleTrade = new Steam.TradeOfferRequest();
					trades.Add(singleTrade);
				}

				Steam.Item item = inventory[i];
				singleTrade.me.assets.Add(new Steam.Item() {
					appid = item.appid,
					contextid = item.contextid,
					amount = item.amount,
					assetid = item.id
				});
			}

			string referer = SteamCommunityURL + "/tradeoffer/new";
			string request = referer + "/send";

			foreach (Steam.TradeOfferRequest trade in trades) {
				Dictionary<string, string> data = new Dictionary<string, string>(6) {
					{"sessionid", sessionID},
					{"serverid", "1"},
					{"partner", partnerID.ToString()},
					{"tradeoffermessage", "Sent by ASF"},
					{"json_tradeoffer", JsonConvert.SerializeObject(trade)},
					{"trade_offer_create_params", string.IsNullOrEmpty(token) ? "" : $"{{\"trade_offer_access_token\":\"{token}\"}}"}
				};

				HttpResponseMessage response = null;
				for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
					response = await WebBrowser.UrlPost(request, data, Cookie, referer).ConfigureAwait(false);
				}

				if (response == null) {
					Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
					return itemsCount==0?-1:itemsCount;
				}
				itemsCount += trade.me.assets.Count;
				
			}

			return itemsCount;
		}

		internal async Task<HtmlDocument> GetBadgePage(byte page) {
			if (page == 0 || SteamID == 0) {
				return null;
			}

			HtmlDocument htmlDocument = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && htmlDocument == null; i++) {
				htmlDocument = await WebBrowser.UrlGetToHtmlDocument(SteamCommunityURL + "/profiles/" + SteamID + "/badges?l=english&p=" + page, Cookie).ConfigureAwait(false);
			}

			if (htmlDocument == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			return htmlDocument;
		}

		internal async Task<HtmlDocument> GetGameCardsPage(ulong appID) {
			if (appID == 0 || SteamID == 0) {
				return null;
			}

			HtmlDocument htmlDocument = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && htmlDocument == null; i++) {
				htmlDocument = await WebBrowser.UrlGetToHtmlDocument(SteamCommunityURL + "/profiles/" + SteamID + "/gamecards/" + appID + "?l=english", Cookie).ConfigureAwait(false);
			}

			if (htmlDocument == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			return htmlDocument;
		}

		internal async Task<string> GetProfileName( ulong steamID ) {
			if ( steamID == 0 ) {
				return null;
			}

			string request = SteamCommunityURL + "/profiles/" + steamID + "/?xml=1";

			XmlDocument response = null;
			for ( byte i = 0; i < WebBrowser.MaxRetries && response == null; i++ ) {
				response = await WebBrowser.UrlGetToXML( request, Cookie ).ConfigureAwait( false );
			}

			if ( response == null ) {
				Logging.LogGenericWTF( "Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName );
				return null;
			}

			var xmlNode = response.SelectSingleNode("profile/steamID");
			if ( xmlNode == null ) {
				return null;
			}
			return xmlNode.InnerText;
		}
		internal  async Task<List<ulong>> GetMasterClanMembers( ) {
			var result = new List<ulong>();

			if (Bot.BotConfig.SteamMasterClanID == 0) {
				return result;
			}	

			string request = SteamCommunityURL + "/gid/" + Bot.BotConfig.SteamMasterClanID + "/memberslistxml/?xml=1";

			XmlDocument response = null;
			for ( byte i = 0; i < WebBrowser.MaxRetries && response == null; i++ ) {
				response = await WebBrowser.UrlGetToXML( request, Cookie ).ConfigureAwait( false );
			}

			if ( response == null ) {
				Logging.LogGenericWTF( "Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName );
				return result;
			}

			XmlNodeList xmlNodeList = response.SelectNodes("memberList/members/steamID64");
			if ( xmlNodeList == null || xmlNodeList.Count == 0 ) {
				return result;
			}

			
			foreach ( XmlNode xmlNode in xmlNodeList ) {
				string memberNode = xmlNode.InnerText;
				if ( memberNode == null ) {
					continue;
				}

				ulong memberID;
				if ( !ulong.TryParse( memberNode, out memberID ) ) {
					continue;
				}
				
				result.Add( memberID );

			}

			return result;
		}
		internal async Task<Bot.EClanRank> GetProfileRank( ulong profileID64 ) {
			var rank = Bot.EClanRank.None;
			if ( profileID64 == 0 ) {
				return rank;
			}
			SteamID profileID = new SteamID();
			profileID.SetFromUInt64( profileID64 );

			//check if given profile is clan member
			List<ulong> clanMembers = await GetMasterClanMembers().ConfigureAwait(false);
			if (!clanMembers.Contains( profileID64 ) ) {
				return rank;
			}

			//so given profile is clan member. 
			rank = Bot.EClanRank.Member;

			//lets find out his rank:
			//getting his profile name
			string profileName = await GetProfileName(profileID64).ConfigureAwait(false);
			if (profileName == null) {
				return rank;
			}

			//searching in group members			
			var pages = new List<string>();
			pages.Add( SteamCommunityURL + "/gid/" + Bot.BotConfig.SteamMasterClanID + "/members?searchKey=" + WebUtility.UrlEncode( profileName ) + "&l=english" );
			//some strange issue. if profile name was changed recently search wont return it in group memebers. in this case we gonna perform full search
			var membersPerPage = 51;
			var pagesQty = ( clanMembers.Count + membersPerPage + 1 ) / membersPerPage;
			for (int i = 1; i <= pagesQty; i++ ) {
				//http://steamcommunity.com/groups/PUSY_KATS_LOUNGE/members/?p=5
				pages.Add( SteamCommunityURL + "/gid/" + Bot.BotConfig.SteamMasterClanID + "/members/?p=" + i );
			}
			foreach ( var pageUrl in pages ) {
				HtmlDocument htmlDocument = null;
				for ( byte i = 0; i < WebBrowser.MaxRetries && htmlDocument == null; i++ ) {
					htmlDocument = await WebBrowser.UrlGetToHtmlDocument( pageUrl, Cookie ).ConfigureAwait( false );
				}

				if ( htmlDocument == null ) {
					Logging.LogGenericWTF( "Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName );
					return rank;
				}
				//getting rank			
				var memberBlocks = htmlDocument.DocumentNode.SelectSingleNode( "//div[@id='memberList']" );
				foreach ( var memberBlockNode in memberBlocks.ChildNodes ) {
					uint steamID3;
					if ( memberBlockNode.Name != "div"
						|| memberBlockNode.Attributes["data-miniprofile"] == null
						|| !uint.TryParse( memberBlockNode.Attributes["data-miniprofile"].Value, out steamID3 ) ) {
						continue;
					}
					if ( steamID3 != profileID.AccountID ) {
						continue;
					}
					var rankNode = memberBlockNode.SelectSingleNode( "div[@class='rank_icon']" );
					if ( rankNode == null ) {
						return rank;
					}
					string rankTitle = rankNode.Attributes["title"].Value;
					switch ( rankTitle ) {
						case "Group Owner": rank = Bot.EClanRank.Owner; break;
						case "Group Officer": rank = Bot.EClanRank.Officer; break;
						case "Group Moderator": rank = Bot.EClanRank.Moderator; break;
						default: rank = Bot.EClanRank.Member; break;
					}
					return rank;
				}
			}									
			return Bot.EClanRank.Unknown;
		}
		
		private async Task UnlockParentalAccount(string parentalPin) {
			if (string.IsNullOrEmpty(parentalPin) || parentalPin.Equals("0")) {
				return;
			}

			Logging.LogGenericInfo("Unlocking parental account...", Bot.BotName);
			Dictionary<string, string> data = new Dictionary<string, string>(1) {
				{ "pin", parentalPin }
			};

			string referer = SteamCommunityURL;
			string request = referer + "/parental/ajaxunlock";

			HttpResponseMessage response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlPost(request, data, Cookie, referer).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return;
			}

			IEnumerable<string> setCookieValues;
			if (!response.Headers.TryGetValues("Set-Cookie", out setCookieValues)) {
				Logging.LogNullError("setCookieValues", Bot.BotName);
				return;
			}

			foreach (string setCookieValue in setCookieValues) {
				if (!setCookieValue.Contains("steamparental=")) {
					continue;
				}

				string setCookie = setCookieValue.Substring(setCookieValue.IndexOf("steamparental=", StringComparison.Ordinal) + 14);

				int index = setCookie.IndexOf(';');
				if (index > 0) {
					setCookie = setCookie.Substring(0, index);
				}

				Cookie["steamparental"] = setCookie;
				Logging.LogGenericInfo("Success!", Bot.BotName);
				return;
			}

			Logging.LogGenericWarning("Failed to unlock parental account!", Bot.BotName);
		}
	}
}
