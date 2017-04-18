﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using log4net;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;

namespace Titan.Core.Accounts
{
    public class Account
    {
        public readonly ILog Log;

        private readonly string _username;
        private readonly string _password;

        private readonly uint _target;
        private readonly ulong _matchId;

        // TODO: Steam Guard handleing
        public readonly DirectoryInfo SentryDirectory;
        public readonly FileInfo SentryFile;

        public SteamClient SteamClient { get; }
        public SteamUser SteamUser { get; }
        public SteamGameCoordinator GameCoordinator { get; }
        public CallbackManager Callbacks { get; }

        public bool IsRunning { get; private set; }

        public Account(string username, string password, uint target, ulong matchId)
        {
            _username = username;
            _password = password;

            _target = target;
            _matchId = matchId;

            SentryDirectory = new DirectoryInfo(Environment.CurrentDirectory + Path.DirectorySeparatorChar + "sentries");
            SentryFile = new FileInfo(Path.Combine(SentryDirectory.ToString(), username + ".sentry.bin"));

            Log = LogManager.GetLogger("Account - " + username);

            SteamClient = new SteamClient();
            Callbacks = new CallbackManager(SteamClient);
            SteamUser = SteamClient.GetHandler<SteamUser>();
            GameCoordinator = SteamClient.GetHandler<SteamGameCoordinator>();
        }

        public void ConnectAndReport()
        {
            Log.Debug("Connecting to Steam");

            Callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            Callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            Callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            Callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            Callbacks.Subscribe<SteamGameCoordinator.MessageCallback>(OnGcMessage);

            IsRunning = true;
            SteamClient.Connect();

            while(IsRunning)
            {
                Callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        // ==========================================
        // CALLBACKS
        // ==========================================

        public void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if(callback.Result == EResult.OK)
            {
                Log.Debug("Successfully connected to Steam. Logging in...");

                SteamUser.LogOn(new SteamUser.LogOnDetails
                {
                    Username = _username,
                    Password = _password
                });
            }
            else
            {
                Log.ErrorFormat("Unable to connect to Steam: {0}", callback.Result);

                IsRunning = false;
            }
        }

        public void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Log.Debug("Successfully disconncected from Steam.");
            IsRunning = false;
        }

        public void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if(callback.Result == EResult.OK)
            {
                // Success
                Log.Debug("Successfully logged in.");

                var playGames = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
                playGames.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                {
                    game_id = 730
                });
                SteamClient.Send(playGames);

                Thread.Sleep(5000);

                var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>((uint) EGCBaseClientMsg.k_EMsgGCClientHello);
                GameCoordinator.Send(clientHello, 730);
            }
            else if(callback.Result == EResult.AccountLogonDenied)
            {
                Log.Warn("Account has Steam Guard enabled.");
                // Steam Guard enabled
            }
            else
            {
                Log.ErrorFormat("Unable to logon to account: {0}: {1}", callback.Result, callback.ExtendedResult);
                IsRunning = false;
            }
        }

        public void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log.DebugFormat("Successfully logged off from Steam: {0}", callback.Result);
        }

        public void OnGcMessage(SteamGameCoordinator.MessageCallback callback)
        {
            var map = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint) EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientReportResponse, OnReportResponse }
            };

            Action<IPacketGCMsg> func;
            if(map.TryGetValue(callback.EMsg, out func))
            {
                func(callback.Message);
            }
        }

        public void OnClientWelcome(IPacketGCMsg msg)
        {
            Log.Debug("Successfully received \"Hello!\" from CS:GO. Sending report...");

            var sendReport = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientReportPlayer>((uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientReportPlayer);
            sendReport.Body.account_id = _target;
            sendReport.Body.match_id = _matchId;
            sendReport.Body.rpt_aimbot = 2;
            sendReport.Body.rpt_wallhack = 3;
            sendReport.Body.rpt_speedhack = 4;
            sendReport.Body.rpt_teamharm = 5;
            sendReport.Body.rpt_textabuse = 6;
            sendReport.Body.rpt_voiceabuse = 7;
            GameCoordinator.Send(sendReport, 730);
        }

        public void OnReportResponse(IPacketGCMsg msg)
        {
            var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientReportResponse>(msg);

            Log.DebugFormat("Successfully reported. Confirmation ID: {0}", response.Body.confirmation_id);

            SteamUser.LogOff();
            SteamClient.Disconnect();
        }

    }
}