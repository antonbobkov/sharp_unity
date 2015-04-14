﻿using System;

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

using Tools;
using Network;


namespace ServerClient
{
    class PlayerDatabase
    {
        private Dictionary<Guid, Point> playerPositions = new Dictionary<Guid, Point>();

        public void Set(Guid player, Point pos)
        {
            // track

            if (playerPositions.ContainsKey(player))
                Remove(player);


            playerPositions.Add(player, pos);
        }

        public void Remove(Guid player)
        {
            MyAssert.Assert(playerPositions.ContainsKey(player));
            
            Point prevPos = playerPositions[player];
            
            // untrack

            playerPositions.Remove(player);
        }
    }

    class ClientWorld
    {
        private int tracker = 0;
        private WorldInfo? info = null;
        private World world = null;

        private Point position;

        ClientWorld(Point position)
        {
            this.position = position;
        }

        public void Track()
        {
            // wrong: don't always subscribe
            ++tracker;
            if (info != null)
                Subscribe();
        }

        public void Untrack()
        {
            --tracker;
            MyAssert.Assert(tracker >= 0);

            if (tracker == 0 && info != null)
                Unsubscribe();
        }

        public void SetWorldInfo(WorldInfo newInfo)
        {
            MyAssert.Assert(newInfo.position == position);
            
            // if the same as the current value, do nothing
            if (info.HasValue && info.Value == newInfo)
                return;
            
            // first ever value - initialize
            if (info == null)
            {
                info = newInfo;
                
                if(tracker > 0)
                    Subscribe();

                return;
            }

            // only do something if new information has greater generation
            MyAssert.Assert(newInfo.generation != info.Value.generation);
            if (newInfo.generation < info.Value.generation)
                return; // old info, ignore

            if (tracker > 0)
                Unsubscribe();

            info = newInfo;

            if (tracker > 0)
                Subscribe();
        }

        public void SetWorld(WorldInitializer init)
        {
            MyAssert.Assert(info.HasValue);
            MyAssert.Assert(init.info.position == position);

            if (init.info != info.Value)    // old world information - ignore
            {
                MyAssert.Assert(init.info.generation < info.Value.generation);
                return;
            }

            MyAssert.Assert(world == null);

            world = new World(init, null);  // wrong: should have non-null action
        }

        private void Subscribe();
        private void Unsubscribe(); // set world to null
    }

    class Client
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("client");

        //public GameInfo gameInfo = null;
        public OverlayEndpoint serverHost = null;
        //Node server = null;

        OverlayHost myHost;

        Aggregator all;

        public Dictionary<Point, int> trackedWorlds = new Dictionary<Point, int>();
        public Dictionary<Point, World> knownWorlds = new Dictionary<Point, World>();

        public void TrackWorld(WorldInfo world)
        {
            foreach (Point p in Point.SymmetricRange(Point.One))
            {
                Point q = p + world.position;
                if (!trackedWorlds.ContainsKey(q))
                    trackedWorlds.Add(q, 0);

                trackedWorlds[q]++;
            }

            if (knownWorlds.ContainsKey(world.position))
            {
                World w = knownWorlds[world.position];
                foreach (WorldInfo inf in w.GetKnownNeighbors())
                    OnNeighbor(inf);
            }
            else
                OnNewWorld(world);
        }
        public void UnTrackWorld(Point worldPos)
        {
            foreach (Point p in Point.SymmetricRange(Point.One))
            {
                Point q = p + worldPos;

                MyAssert.Assert(trackedWorlds.ContainsKey(q));
                MyAssert.Assert(trackedWorlds[q] > 0);

                trackedWorlds[q]--;

                if (trackedWorlds[q] == 0)
                    TryRemoveWorld(q);
            }
        }

        bool TryRemoveWorld(Point worldPos)
        {
            World w = knownWorlds.TryGetValue(worldPos);
            if (w == null)
                return false;
            
            //myHost.TryCloseNode(w.Info.host);
            myHost.SendMessage(w.Info.host, MessageType.UNSUBSCRIBE);
            
            knownWorlds.Remove(worldPos);
            onDeleteWorldHook(w);

            return true;
        }

        public HashSet<Guid> myPlayerAgents = new HashSet<Guid>();

        public Action onServerReadyHook = () => { };

        public Action<World> onNewWorldHook = (a) => { };
        public Action<World> onDeleteWorldHook = (a) => { };

        public Action<PlayerInfo> onNewMyPlayerHook = (a) => { };

        public Action<World, PlayerInfo, Point, ActionValidity> onMoveHook = (a, b, c, d) => { };
        public Action<World, PlayerInfo, bool> onPlayerLeaveHook = (a, b, c) => { };
        
        public Client(GlobalHost globalHost, Aggregator all_)
        {
            all = all_;

            myHost = globalHost.NewHost(Client.hostName, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.CLIENT), Aggregator.longInactivityWait);

            myHost.onNewConnectionHook = ProcessNewConnection;
        }

        Game.MessageProcessor AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (role == NodeRole.CLIENT)
                return ProcessClientMessage;

            if (role == NodeRole.SERVER)
            {
                MyAssert.Assert(serverHost == n.info.remote);
                return ProcessServerMessage;
            }

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = Serializer.Deserialize<WorldInfo>(nodeInfo);
                return (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf);
            }

            throw new Exception(Log.StDump(n.info, role, "unexpected"));
        }
        void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.SERVER_ADDRESS)
            {
                OverlayEndpoint host = Serializer.Deserialize<OverlayEndpoint>(stm);
                OnServerAddress(host);
            }
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            //if (mt == MessageType.GAME_INFO_VAR_INIT)
            //{
            //    GameInfoSerialized info = Serializer.Deserialize<GameInfoSerialized>(stm);
            //    OnGameInfo(info);
            //}
            //else if (mt == MessageType.GAME_INFO_VAR_CHANGE)
            //{
            //    MyAssert.Assert(gameInfo != null);

            //    ForwardFunctionCall ffc = ForwardFunctionCall.Deserialize(stm, typeof(GameInfo));
            //    ffc.Apply(gameInfo);
            //}
            if (mt == MessageType.PLAYER_VALIDATOR_ASSIGN)
            {
                Guid actionId = Serializer.Deserialize<Guid>(stm);
                PlayerInfo info = Serializer.Deserialize<PlayerInfo>(stm);
                OnPlayerValidateRequest(actionId, info);
            }
            else if (mt == MessageType.WORLD_VALIDATOR_ASSIGN)
            {
                Guid actionId = Serializer.Deserialize<Guid>(stm);
                WorldInitializer init = Serializer.Deserialize<WorldInitializer>(stm);
                OnWorldValidateRequest(actionId, init);
            }
            else if (mt == MessageType.NEW_PLAYER_REQUEST_SUCCESS)
            {
                PlayerInfo info = Serializer.Deserialize<PlayerInfo>(stm);
                OnNewPlayerRequestApproved(info);
            }
            else
                throw new Exception("Client.ProcessServerMessage bad message type " + mt.ToString());
        }
        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.WORLD_VAR_INIT)
            {
                WorldInitializer wrld = Serializer.Deserialize<WorldInitializer>(stm);
                OnNewWorldVar(wrld);
            }
            else if (mt == MessageType.WORLD_VAR_CHANGE)
            {
                if (knownWorlds.ContainsKey(inf.position))
                {
                    ForwardFunctionCall ffc = ForwardFunctionCall.Deserialize(stm, typeof(World));
                    ffc.Apply(knownWorlds.GetValue(inf.position));
                }
            }
            else
                throw new Exception(Log.StDump(mt, inf, "unexpected"));
        }

        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
        }
        void OnNewClient(Node n)
        {
            if (serverHost != null)
                n.SendMessage(MessageType.SERVER_ADDRESS, serverHost);
            Log.Console("New mesh point: " + n.info.remote.addr);
        }

        public void OnServerAddress(OverlayEndpoint serverHost_)
        {
            if (serverHost == null)
            {
                serverHost = serverHost_;

                Log.Console("Server at {0}", serverHost);

                /*server = */myHost.ConnectAsync(serverHost);
                myHost.BroadcastGroup(Client.hostName, MessageType.SERVER_ADDRESS, serverHost);

                onServerReadyHook();
            }
            else
                MyAssert.Assert(serverHost == serverHost_);
        }

        void OnNewPlayerRequestApproved(PlayerInfo inf)
        {
            MyAssert.Assert(myPlayerAgents.Contains(inf.id));
            all.AddPlayerAgent(inf);

            onNewMyPlayerHook(inf);
        }

        public void OnNewWorld(WorldInfo inf)
        {
            //gameInfo.AddWorld(inf);
            //Log.LogWriteLine("New world\n{0}", inf.GetFullInfo());
            //myHost.TryConnectAsync(inf.host);
            myHost.ConnectSendMessage(inf.host, MessageType.SUBSCRIBE);
        }
        void OnNewWorldVar(WorldInitializer wrld)
        {
            World w = new World(wrld, OnNeighbor);

            MyAssert.Assert(!knownWorlds.ContainsKey(w.Info.position));
            
            knownWorlds.Add(w.Position, w);
            w.onMoveHook = (player, pos, mv) => onMoveHook(w, player, pos, mv);
            w.onPlayerLeaveHook = (player, tel) => onPlayerLeaveHook(w, player, tel);

            onNewWorldHook(w);

            //Log.LogWriteLine("New world {0}", w.Position);
            //w.ConsoleOut();

            if (trackedWorlds.TryGetValue(w.Position) == 0)
            {
                myHost.TryCloseNode(w.Info.host);
                knownWorlds.Remove(w.Position);
            }
        }

        void OnNeighbor(WorldInfo inf, bool isNewWorld)
        {
            if (trackedWorlds.TryGetValue(inf.position) > 0)
                OnNewWorld(inf);
        }

        void OnPlayerValidateRequest(Guid actionId, PlayerInfo info)
        {
            all.AddPlayerValidator(info);
            //Log.LogWriteLine("Validating for {0}", info);
            myHost.SendMessage(serverHost, MessageType.ACCEPT, actionId);
        }
        void OnWorldValidateRequest(Guid actionId, WorldInitializer init)
        {
            all.AddWorldValidator(init);
            //Log.LogWriteLine("Validating for world {0}", info.worldPos);
            myHost.SendMessage(serverHost, MessageType.ACCEPT, actionId);
        }

        public bool TryConnect(IPEndPoint ep)
        {
            return myHost.TryConnectAsync(new OverlayEndpoint(ep, Client.hostName)) != null;
        }
        public void NewMyPlayer(Guid id)
        {
            myPlayerAgents.Add(id);
            myHost.SendMessage(serverHost, MessageType.NEW_PLAYER_REQUEST, id);
        }
        public void NewWorld(Point pos)
        {
            myHost.SendMessage(serverHost, MessageType.NEW_WORLD_REQUEST, pos);
        }
        public void Validate()
        {
            myHost.SendMessage(serverHost, MessageType.NEW_VALIDATOR);
        }
    }
}
