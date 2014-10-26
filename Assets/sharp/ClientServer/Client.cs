using System;
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
    class Client
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("client");

        //public GameInfo gameInfo = null;
        public OverlayEndpoint serverHost = null;
        Node server = null;

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
                if (!trackedWorlds.ContainsKey(q))
                    trackedWorlds.Add(q, 0);

                trackedWorlds[q]--;

            }
        }

        public HashSet<Guid> myPlayerAgents = new HashSet<Guid>();

        public Action onServerReadyHook = () => { };

        public Action<World> onNewWorldHook = (a) => { };
        public Action<PlayerInfo> onNewMyPlayerHook = (a) => { };

        public Action<World, PlayerInfo, Point, MoveValidity> onMoveHook = (a, b, c, d) => { };
        public Action<World, PlayerInfo> onPlayerLeaveHook = (a, b) => { };
        
        public Client(GlobalHost globalHost, Aggregator all_)
        {
            all = all_;

            myHost = globalHost.NewHost(Client.hostName, AssignProcessor,
                OverlayHost.GenerateHandshake(NodeRole.CLIENT));

            myHost.onNewConnectionHook = ProcessNewConnection;
        }

        Node.MessageProcessor AssignProcessor(Node n, MemoryStream nodeInfo)
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
                WorldInfo info = Serializer.Deserialize<WorldInfo>(stm);
                WorldInitializer init = Serializer.Deserialize<WorldInitializer>(stm);
                OnWorldValidateRequest(actionId, info, init);
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
                WorldSerialized wrld = Serializer.Deserialize<WorldSerialized>(stm);
                OnNewWorldVar(wrld);
            }
            else if (mt == MessageType.WORLD_VAR_CHANGE)
            {
                ForwardFunctionCall ffc = ForwardFunctionCall.Deserialize(stm, typeof(World));
                ffc.Apply(knownWorlds.GetValue(inf.position));
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
        }

        public void OnServerAddress(OverlayEndpoint serverHost_)
        {
            if (serverHost == null)
            {
                serverHost = serverHost_;

                Log.LogWriteLine("Server at {0}", serverHost);

                server = myHost.ConnectAsync(serverHost);
                myHost.BroadcastGroup(Client.hostName, MessageType.SERVER_ADDRESS, serverHost);

                onServerReadyHook();
            }
            else
                MyAssert.Assert(serverHost == serverHost_);
        }
        //void OnGameInfo(GameInfoSerialized gameStateSer)
        //{
        //    MyAssert.Assert(gameInfo == null);
        //    gameInfo = new GameInfo();

        //    //gameInfo.onNewWorld = OnNewWorld;

        //    gameInfo.Deserialize(gameStateSer);

        //    Log.LogWriteLine("Recieved game info");
        //    Program.GameInfoOut(gameInfo);
        //}

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
            myHost.TryConnectAsync(inf.host);
        }
        void OnNewWorldVar(WorldSerialized ws)
        {
            World w = new World(ws, OnNeighbor);
            
            knownWorlds.Add(w.Position, w);
            w.onMoveHook = (player, pos, mv) => onMoveHook(w, player, pos, mv);
            w.onPlayerLeaveHook = (player) => onPlayerLeaveHook(w, player);

            onNewWorldHook(w);

            //Log.LogWriteLine("New world {0}", w.Position);
            //w.ConsoleOut();
        }

        void OnNeighbor(WorldInfo inf)
        {
            if (trackedWorlds.TryGetValue(inf.position) > 0)
                OnNewWorld(inf);
        }

        void OnPlayerValidateRequest(Guid actionId, PlayerInfo info)
        {
            all.AddPlayerValidator(info);
            //Log.LogWriteLine("Validating for {0}", info);
            server.SendMessage(MessageType.ACCEPT, actionId);
        }
        void OnWorldValidateRequest(Guid actionId, WorldInfo info, WorldInitializer init)
        {
            all.AddWorldValidator(info, init);
            //Log.LogWriteLine("Validating for world {0}", info.worldPos);
            server.SendMessage(MessageType.ACCEPT, actionId);
        }

        public bool TryConnect(IPEndPoint ep)
        {
            return myHost.TryConnectAsync(new OverlayEndpoint(ep, Client.hostName)) != null;
        }
        public void NewMyPlayer(Guid id)
        {
            myPlayerAgents.Add(id);
            server.SendMessage(MessageType.NEW_PLAYER_REQUEST, id);
        }
        public void NewWorld(Point pos)
        {
            server.SendMessage(MessageType.NEW_WORLD_REQUEST, pos);
        }
        public void Validate()
        {
            server.SendMessage(MessageType.NEW_VALIDATOR);
        }
    }
}
