﻿using SpotifyAPI.Web.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Timers;
using System.Web;
using System.Windows;
using System.Windows.Media;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;
using System.Linq;
using Songify_Slim.Models;
using System.Text.RegularExpressions;
using TwitchLib.Api.V5;
using System.Windows.Controls.Primitives;

namespace Songify_Slim
{
    // This class handles everything regarding to twitch.tv
    public static class TwitchHandler
    {
        public static TwitchClient _client;
        public static bool onCooldown = false;
        public static Timer cooldownTimer = new Timer
        {
            Interval = TimeSpan.FromSeconds(Settings.TwSRCooldown).TotalMilliseconds,
        };

        public static void BotConnect()
        {
            try
            {
                // Checks if twitch credentials are present
                if (string.IsNullOrEmpty(Settings.TwAcc) || string.IsNullOrEmpty(Settings.TwOAuth) || string.IsNullOrEmpty(Settings.TwChannel))
                {
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        foreach (Window window in Application.Current.Windows)
                        {
                            if (window.GetType() == typeof(MainWindow))
                            {
                                //(window as MainWindow).icon_Twitch.Foreground = new SolidColorBrush(Colors.Red);
                                (window as MainWindow).LblStatus.Content = "Please fill in Twitch credentials.";
                            }
                        }
                    }));
                    return;
                }

                // creates new connection based on the credentials in settings
                ConnectionCredentials credentials = new ConnectionCredentials(Settings.TwAcc, Settings.TwOAuth);
                ClientOptions clientOptions = new ClientOptions
                {
                    MessagesAllowedInPeriod = 750,
                    ThrottlingPeriod = TimeSpan.FromSeconds(30)
                };
                WebSocketClient customClient = new WebSocketClient(clientOptions);
                _client = new TwitchClient(customClient);
                _client.Initialize(credentials, Settings.TwChannel);

                _client.OnMessageReceived += _client_OnMessageReceived;
                _client.OnConnected += _client_OnConnected;
                _client.OnDisconnected += _client_OnDisconnected;

                _client.Connect();

                // subscirbes to the cooldowntimer elapsed event for the command cooldown
                cooldownTimer.Elapsed += CooldownTimer_Elapsed;
            }
            catch (Exception)
            {
                Logger.LogStr("Couldn't connect to Twitch, mabe credentials are wrong?");
            }

        }

        private static void _client_OnDisconnected(object sender, TwitchLib.Communication.Events.OnDisconnectedEventArgs e)
        {
            // Disconnected
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window.GetType() != typeof(MainWindow))
                        continue;

                    //(window as MainWindow).icon_Twitch.Foreground = new SolidColorBrush(Colors.Red);
                    (window as MainWindow).LblStatus.Content = "Disconnected from Twitch";
                    (window as MainWindow).mi_TwitchConnect.IsEnabled = true;
                    (window as MainWindow).mi_TwitchDisconnect.IsEnabled = false;

                }
            }));

            Logger.LogStr("Disconnected from Twitch");
        }

        private static void CooldownTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            // Resets the cooldown for the !ssr command
            onCooldown = false;
            cooldownTimer.Stop();
        }

        private static void _client_OnConnected(object sender, OnConnectedArgs e)
        {
            // Connected
            Application.Current.Dispatcher.Invoke(new Action(() =>
                        {
                            foreach (Window window in Application.Current.Windows)
                            {
                                if (window.GetType() != typeof(MainWindow))
                                    continue;

                                //(window as MainWindow).icon_Twitch.Foreground = new SolidColorBrush(Colors.Green);
                                (window as MainWindow).LblStatus.Content = "Connected to Twitch";
                                (window as MainWindow).mi_TwitchConnect.IsEnabled = false;
                                (window as MainWindow).mi_TwitchDisconnect.IsEnabled = true;
                            }

                        }));
            Logger.LogStr("Connected to Twitch");
        }

        private static void _client_OnMessageReceived(object sender, OnMessageReceivedArgs e)
        {
            if (Settings.MsgLoggingEnabled)
            {
                // If message logging is enabled and the reward was triggered, save it to the settings (if settings window is open, write it to the textbox)
                if (e.ChatMessage.CustomRewardId != null)
                {
                    Settings.TwRewardID = e.ChatMessage.CustomRewardId;

                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        foreach (Window window in Application.Current.Windows)
                        {
                            if (window.GetType() == typeof(Window_Settings))
                            {
                                (window as Window_Settings).txtbx_RewardID.Text = e.ChatMessage.CustomRewardId;
                            }
                        }
                    }));
                }
            }

            // if the reward is the same with the desired reward for the requests 
            if (Settings.TwSRReward && e.ChatMessage.CustomRewardId == Settings.TwRewardID)
            {
                if (IsUserBlocked(e.ChatMessage.DisplayName))
                {
                    _client.SendWhisper(e.ChatMessage.Username, "You are blocked from making Songrequests");
                    return;
                }

                if (APIHandler.spotify == null)
                {
                    _client.SendMessage(e.ChatMessage.Channel, "It seems that Spotify is not connected right now.");
                    return;
                }

                // if Spotify is connected and working manipulate the string and call methods to get the song info accordingly
                if (e.ChatMessage.Message.StartsWith("spotify:track:"))
                {
                    // search for a track with the id
                    string TrackID = e.ChatMessage.Message.Replace("spotify:track:", "");

                    // add the track to the spotify queue and pass the OnMessageReceivedArgs (contains user who requested the song etc)
                    AddSong(TrackID, e);
                }

                else if (e.ChatMessage.Message.StartsWith("https://open.spotify.com/track/"))
                {
                    string trackid = e.ChatMessage.Message.Replace("https://open.spotify.com/track/", "");
                    trackid = trackid.Split('?')[0];
                    AddSong(trackid, e);
                }

                else
                {
                    // search for a track with a search string from chat
                    SearchItem searchItem = APIHandler.FindTrack(e.ChatMessage.Message);
                    if (searchItem.Tracks.Items.Count > 0)
                    {
                        // if a track was found convert the object to FullTrack (easier use than searchItem)
                        FullTrack fullTrack = searchItem.Tracks.Items[0];

                        // add the track to the spotify queue and pass the OnMessageReceivedArgs (contains user who requested the song etc)
                        AddSong(fullTrack.Id, e);
                    }
                    else
                    {
                        string response;
                        // if no track has been found inform the requester
                        response = Settings.Bot_Resp_Error;
                        response = response.Replace("{user}", e.ChatMessage.DisplayName);
                        response = response.Replace("{artist}", "");
                        response = response.Replace("{title}", "");
                        response = response.Replace("{maxreq}", "");
                        response = response.Replace("{errormsg}", "");

                        _client.SendMessage(e.ChatMessage.Channel, response);
                        return;
                    }
                }
                return;
            }

            // Same code from above but it reacts to a command instead of rewards
            if (Settings.TwSRCommand && e.ChatMessage.Message.StartsWith("!ssr"))
            {
                // Do nothing if the user is blocked, don't even reply
                if (IsUserBlocked(e.ChatMessage.DisplayName))
                {
                    _client.SendWhisper(e.ChatMessage.DisplayName, "You are blocked from making Songrequests");
                    return;
                }

                // if onCooldown skip
                if (onCooldown)
                {
                    return;
                }

                if (APIHandler.spotify == null)
                {
                    _client.SendMessage(e.ChatMessage.Channel, "It seems that Spotify is not connected right now.");
                    return;
                }

                // if Spotify is connected and working manipulate the string and call methods to get the song info accordingly
                string[] msgSplit = e.ChatMessage.Message.Split(' ');

                // Prevent crash on command without args
                if (msgSplit.Length <= 1)
                {
                    _client.SendMessage(e.ChatMessage.Channel, "@" + e.ChatMessage.DisplayName + " please specify a song to add to the queue.");
                    StartCooldown();
                    return;
                }
                if (msgSplit[1].StartsWith("spotify:track:"))
                {
                    // search for a track with the id
                    string trackID = msgSplit[1].Replace("spotify:track:", "");
                    // add the track to the spotify queue and pass the OnMessageReceivedArgs (contains user who requested the song etc)
                    AddSong(trackID, e);
                }

                else if (msgSplit[1].StartsWith("https://open.spotify.com/track/"))
                {
                    string trackid = msgSplit[1].Replace("https://open.spotify.com/track/", "");
                    trackid = trackid.Split('?')[0];
                    AddSong(trackid, e);
                }
                else
                {
                    string searchString = e.ChatMessage.Message.Replace("!ssr ", "");
                    // search for a track with a search string from chat
                    SearchItem searchItem = APIHandler.FindTrack(searchString);
                    if (searchItem.Tracks.Items.Count > 0)
                    {
                        // if a track was found convert the object to FullTrack (easier use than searchItem)
                        FullTrack fullTrack = searchItem.Tracks.Items[0];
                        // add the track to the spotify queue and pass the OnMessageReceivedArgs (contains user who requested the song etc)
                        AddSong(fullTrack.Id, e);
                    }
                    else
                    {
                        string response;
                        // if no track has been found inform the requester
                        response = Settings.Bot_Resp_Error;
                        response = response.Replace("{user}", e.ChatMessage.DisplayName);
                        response = response.Replace("{artist}", "");
                        response = response.Replace("{title}", "");
                        response = response.Replace("{maxreq}", "");
                        response = response.Replace("{errormsg}", "");

                        _client.SendMessage(e.ChatMessage.Channel, response);
                        return;
                    }
                }
                // start the command cooldown
                StartCooldown();
                return;
            }

        }

        private static bool IsUserBlocked(string displayName)
        {
            string[] UserBlacklist = Settings.UserBlacklist.Split(new[] { "|||" }, StringSplitOptions.None);

            // checks if one of the artist in the requested song is on the blacklist
            foreach (string s in UserBlacklist)
            {
                if (s == displayName)
                    return true;
            }
            return false;
        }

        private static void StartCooldown()
        {
            // starts the cooldown on the command
            onCooldown = true;
            cooldownTimer.Interval = TimeSpan.FromSeconds(Settings.TwSRCooldown).TotalMilliseconds;
            cooldownTimer.Start();
        }
        private static string CleanFormatString(string currSong)
        {
            RegexOptions options = RegexOptions.None;
            Regex regex = new Regex("[ ]{2,}", options);
            currSong = regex.Replace(currSong, " ");
            currSong = currSong.Trim();
            // Add trailing spaces for better scroll
            return currSong;
        }
        private static void AddSong(string trackID, OnMessageReceivedArgs e)
        {
            // loads the blacklist from settings
            string[] Blacklist = Settings.ArtistBlacklist.Split(new[] { "|||" }, StringSplitOptions.None);
            string response;
            // gets the track information using spotify api
            FullTrack track = APIHandler.GetTrack(trackID);

            // checks if one of the artist in the requested song is on the blacklist
            foreach (string s in Blacklist)
            {
                if (Array.IndexOf(track.Artists.Select(x => x.Name).ToArray(), s) != -1)
                {
                    // if artist is on blacklist, skip and inform requester
                    response = Settings.Bot_Resp_Blacklist;
                    response = response.Replace("{user}", e.ChatMessage.DisplayName);
                    response = response.Replace("{artist}", s);
                    response = response.Replace("{title}", "");
                    response = response.Replace("{maxreq}", "");
                    response = response.Replace("{errormsg}", "");
                    response = CleanFormatString(response);

                    _client.SendMessage(e.ChatMessage.Channel, response);
                    return;
                }
            }

            // checks if song length is longer or equal to 10 minutes
            if (track.DurationMs >= TimeSpan.FromMinutes(10).TotalMilliseconds)
            {
                // if track length exceeds 10 minutes skip and inform requster
                response = Settings.Bot_Resp_Length;
                response = response.Replace("{user}", e.ChatMessage.DisplayName);
                response = response.Replace("{artist}", "");
                response = response.Replace("{title}", "");
                response = response.Replace("{maxreq}", "");
                response = response.Replace("{errormsg}", "");
                response = CleanFormatString(response);

                _client.SendMessage(e.ChatMessage.Channel, response);
                return;
            }

            // checks if the song is already in the queue
            if (isInQueue(track.Id))
            {
                // if the song is already in the queue skip and inform requester
                response = Settings.Bot_Resp_IsInQueue;
                response = response.Replace("{user}", e.ChatMessage.DisplayName);
                response = response.Replace("{artist}", "");
                response = response.Replace("{title}", "");
                response = response.Replace("{maxreq}", "");
                response = response.Replace("{errormsg}", "");
                response = CleanFormatString(response);

                _client.SendMessage(e.ChatMessage.Channel, response);
                return;
            }

            // checks if the user has already the max amount of songs in the queue
            if (MaxQueueItems(e.ChatMessage.DisplayName))
            {
                // if the user reached max requests in the queue skip and inform requester
                response = Settings.Bot_Resp_MaxReq;
                response = response.Replace("{user}", e.ChatMessage.DisplayName);
                response = response.Replace("{artist}", "");
                response = response.Replace("{title}", "");
                response = response.Replace("{maxreq}", Settings.TwSRMaxReq.ToString());
                response = response.Replace("{errormsg}", "");
                response = CleanFormatString(response);
                _client.SendMessage(e.ChatMessage.Channel, response);
                return;
            }

            // generate the spotifyURI using the track id
            string SpotifyURI = "spotify:track:" + trackID;

            // try adding the song to the queue using the URI
            SpotifyAPI.Web.Models.ErrorResponse error = APIHandler.AddToQ(SpotifyURI);
            if (error.Error != null)
            {
                // if an error has been encountered, log it, inform the requester and skip 
                Logger.LogStr(error.Error.Message + "\n" + error.Error.Status);
                response = Settings.Bot_Resp_Error;
                response = response.Replace("{user}", e.ChatMessage.DisplayName);
                response = response.Replace("{artist}", "");
                response = response.Replace("{title}", "");
                response = response.Replace("{maxreq}", "");
                response = response.Replace("{errormsg}", error.Error.Message);

                _client.SendMessage(e.ChatMessage.Channel, response);
                return;
            }

            // if everything workes so far, inform the user that the song has been added to the queue
            response = Settings.Bot_Resp_Success;
            response = response.Replace("{user}", e.ChatMessage.DisplayName);
            response = response.Replace("{artist}", track.Artists[0].Name);
            response = response.Replace("{title}", track.Name);
            response = response.Replace("{maxreq}", "");
            response = response.Replace("{errormsg}", "");
            _client.SendMessage(e.ChatMessage.Channel, response);

            // Upload the track and who requested it to the queue on the server
            UploadToQueue(track, e.ChatMessage.DisplayName);

            // Add the song to the internal queue and update the queue window if its open
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                Window mw = null, qw = null;
                foreach (Window window in Application.Current.Windows)
                {
                    if (window.GetType() == typeof(MainWindow))
                        mw = window;
                    if (window.GetType() == typeof(Window_Queue))
                        qw = window;
                }
                if (mw != null)
                    (mw as MainWindow).ReqList.Add(new RequestObject
                    {
                        Requester = e.ChatMessage.DisplayName,
                        TrackID = track.Id,
                        Title = track.Name,
                        Artists = track.Artists[0].Name,
                        Length = FormattedTime(track.DurationMs)
                    });

                if (qw != null)
                {
                    //(qw as Window_Queue).dgv_Queue.ItemsSource.
                    (qw as Window_Queue).dgv_Queue.Items.Refresh();
                }
            }));
        }

        public static string FormattedTime(int duration)
        {
            // duration in milliseconds gets converted to mm:ss
            string minutes, seconds;

            TimeSpan t = TimeSpan.FromMilliseconds(duration);
            minutes = t.Minutes.ToString();

            if (t.Seconds < 10)
            {
                seconds = "0" + t.Seconds;
            }
            else
            {
                seconds = t.Seconds.ToString();
            }

            return minutes + ":" + seconds;
        }

        private static void UploadToQueue(FullTrack track, string displayName)
        {
            string artists = "";
            int counter = 0;
            // put all artists from the song in one string
            foreach (SimpleArtist artist in track.Artists)
            {
                if (counter <= 3)
                {
                    artists += artist.Name + ", ";
                    counter++;
                }
                else
                {
                    continue;
                }
            }
            // remove the last ", "
            artists = artists.Remove(artists.Length - 2, 2);

            string length = FormattedTime(track.DurationMs);

            // upload tot the queue
            WebHelper.UpdateWebQueue(track.Id, artists, track.Name, length, displayName, "0", "i");
        }

        private static bool isInQueue(string id)
        {
            // Checks if the song ID is already in the internal queue (Mainwindow reqList)
            List<RequestObject> temp = new List<RequestObject>();
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window.GetType() == typeof(MainWindow))
                    {
                        temp = (window as MainWindow).ReqList.FindAll(x => x.TrackID == id);
                    }
                }
            }));

            if (temp.Count > 0)
            {
                return true;
            }

            return false;
        }

        private static bool MaxQueueItems(string requester)
        {
            // Checks if the requester already reached max songrequests
            List<RequestObject> temp = new List<RequestObject>();
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window.GetType() == typeof(MainWindow))
                    {
                        temp = (window as MainWindow).ReqList.FindAll(x => x.Requester == requester);
                    }
                }
            }));

            if (temp.Count < Settings.TwSRMaxReq)
            {
                return false;
            }

            return true;
        }
    }
}
