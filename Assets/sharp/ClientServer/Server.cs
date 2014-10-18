﻿using System;
using ServerClient.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

namespace ServerClient
{
    class DelayedAction
    {
        public OverlayEndpoint ep;
        public Action a;
    }

    class Server
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("server");

        Random r = new Random();

        GameInfo gameInfo;
        List<IPEndPoint> validatorPool = new List<IPEndPoint>();
        List<Point> spawnWorlds = new List<Point>();

        OverlayHost myHost;

        int playerCounter = 1;
        static string PlayerNameMap(int value)
        {
            char[] baseChars = new char[] { '0','1','2','3','4','5','6','7','8','9',
            'A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
            'a','b','c','d','e','f','g','h','i','j','k','l','m','n','o','p','q','r','s','t','u','v','w','x','y','z'};

            MyAssert.Assert(value >= 0);

            string result = string.Empty;
            int targetBase = baseChars.Length;

            do
            {
                result = baseChars[value % targetBase] + result;
                value = value / targetBase;
            }
            while (value > 0);

            return result;
        }
        Dictionary<Guid, DelayedAction> delayedActions = new Dictionary<Guid, DelayedAction>();

        public OverlayEndpoint Address { get { return myHost.Address; } }

        public Server(GlobalHost globalHost)
        {
            myHost = globalHost.NewHost(Server.hostName, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;

            Action<ForwardFunctionCall> onChange = (ffc) => myHost.BroadcastGroup(Client.hostName, MessageType.GAME_INFO_CHANGE, ffc.Serialize());
            gameInfo = new ForwardProxy<GameInfo>(new GameInfo(), onChange).Get();
        }

        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return ProcessClientMessage;

            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
                return ProcessWorldMessage;

            if (role == NodeRole.PLAYER)
            {
                PlayerInfo inf = gameInfo.GetPlayerByHost(n.info.remote);
                return (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf);
            }

            throw new InvalidOperationException("Server.AssignProcessor unexpected connection " + n.info.remote + " " + role);
        }
        void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.NEW_PLAYER_REQUEST)
            {
                Guid player = Serializer.Deserialize<Guid>(stm);
                OnNewPlayerRequest(player, n.info.remote);
            }
            else if (mt == MessageType.NEW_WORLD_REQUEST)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                OnNewWorldRequest(worldPos);
            }
            else if (mt == MessageType.NEW_VALIDATOR)
            {
                OnNewValidator(n.info.remote.addr);
            }
            else if (mt == MessageType.ACCEPT)
            {
                Guid actionId = Serializer.Deserialize<Guid>(stm);
                OnAccept(actionId, n.info.remote);
            }
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessWorldMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.NEW_WORLD_REQUEST)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                OnNewWorldRequest(worldPos);
            }
            else
                throw new Exception("Client.ProcessWorldMessage bad message type " + mt.ToString());
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
                OnSpawnRequest(inf);
            else
                throw new Exception(Log.StDump("unexpected", mt));
        }

        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
        }
        void OnNewClient(Node n)
        {
            n.SendMessage(MessageType.GAME_INFO, gameInfo.Serialize());
        }

        void OnNewPlayerRequest(Guid playerId, OverlayEndpoint playerHost)
        {
            Guid validatorId = Guid.NewGuid();
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName(validatorId.ToString()));

            OverlayEndpoint playerNewHost = new OverlayEndpoint(playerHost.addr, new OverlayHostName(playerId.ToString()));
            PlayerInfo info = new PlayerInfo(playerId, playerNewHost, validatorHost, PlayerNameMap(playerCounter++));

            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);
            myHost.SendMessage(validatorClient, MessageType.PLAYER_VALIDATOR_ASSIGN, validatorId, info);

            DelayedAction da = new DelayedAction()
            {
                ep = validatorClient,
                a = () =>
                {
                    gameInfo.AddPlayer(info);
                    //myHost.BroadcastGroup(Client.hostName, MessageType.NEW_PLAYER, info);
                }
            };

            delayedActions.Add(validatorId, da);
        }
        void OnNewValidator(IPEndPoint ip)
        {
            MyAssert.Assert(!validatorPool.Where((valip) => valip == ip).Any());
            validatorPool.Add(ip);
        }
        
        void OnNewWorldRequest(Point worldPos)
        {
            if (gameInfo.TryGetWorldByPos(worldPos) != null)
                return;
            
            Guid validatorId = Guid.NewGuid();
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName(validatorId.ToString()));

            WorldInfo info = new WorldInfo(worldPos, validatorHost);
            WorldInitializer init = new WorldInitializer(r.Next());

            if (worldPos == new Point(0, 0))
            //if ((worldPos.x % 2 == 0) && (worldPos.y % 2 == 0))
                init.hasSpawn = true;

            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);
            myHost.SendMessage(validatorClient, MessageType.WORLD_VALIDATOR_ASSIGN, validatorId, info, init);

            DelayedAction da = new DelayedAction()
            {
                ep = validatorClient,
                a = () =>
                {
                    if (init.hasSpawn == true)
                        spawnWorlds.Add(worldPos);

                    gameInfo.AddWorld(info);
                    //myHost.BroadcastGroup(Client.hostName, MessageType.NEW_WORLD, info);
                }
            };

            delayedActions.Add(validatorId, da);

        }

        void OnSpawnRequest(PlayerInfo inf)
        {
            if (!spawnWorlds.Any())
            {
                Log.Dump("No spawn worlds", inf);
                return;
            }

            Point spawnWorldPos = spawnWorlds.Random(n => r.Next(n));

            WorldInfo spawnWorld = gameInfo.GetWorldByPos(spawnWorldPos);

            myHost.ConnectSendMessage(spawnWorld.host, MessageType.SPAWN_REQUEST, inf.id);
        }

        void OnAccept(Guid id, OverlayEndpoint remote)
        {
            DelayedAction da = delayedActions.GetValue(id);
            delayedActions.Remove(id);

            MyAssert.Assert(da.ep == remote);

            da.a();
        }
    }
}
