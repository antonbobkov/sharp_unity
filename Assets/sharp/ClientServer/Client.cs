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

        public GameInfo gameInfo = null;
        public OverlayEndpoint serverHost = null;
        Node server = null;

        Action<Action> sync;
        OverlayHost myHost;

        Aggregator all;

        public Dictionary<Point, World> knownWorlds = new Dictionary<Point, World>();
        public Dictionary<Guid, PlayerData> knownPlayers = new Dictionary<Guid, PlayerData>();

        public HashSet<Guid> myPlayerAgents = new HashSet<Guid>();

        public Action hookServerReady = () => { };
        public Action<World, PlayerInfo, MoveType> onMoveHook = (a, b, c) => { };
        public Action<World> onNewWorldHook = (a) => { };
        public Action<PlayerInfo> onNewPlayerHook = (a) => { };
        public Action<PlayerInfo> onPlayerNewRealm = (a) => { };

        public Client(Action<Action> sync_, GlobalHost globalHost, Aggregator all_)
        {
            sync = sync_;
            all = all_;

            myHost = globalHost.NewHost(Client.hostName, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }


        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return ProcessClientMessage;

            if (remoteName == Server.hostName)
            {
                MyAssert.Assert(serverHost == n.info.remote);
                return ProcessServerMessage;
            }

            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = gameInfo.GetWorldByHost(n.info.remote);
                return (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf);
            }

            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = gameInfo.GetPlayerByHost(n.info.remote);
                return (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf);
            }

            throw new InvalidOperationException("Client.AssignProcessor unexpected connection " + n.info.remote + " " + role);
        }
        void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.SERVER_ADDRESS)
            {
                OverlayEndpoint host = Serializer.Deserialize<OverlayEndpoint>(stm);
                sync.Invoke(() => OnServerAddress(host));
            }
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.GAME_INFO)
            {
                GameInfoSerialized info = Serializer.Deserialize<GameInfoSerialized>(stm);
                sync.Invoke(() => OnGameInfo(info));
            }
            else if (mt == MessageType.NEW_PLAYER)
            {
                PlayerInfo info = Serializer.Deserialize<PlayerInfo>(stm);
                sync.Invoke(() => OnNewPlayer(info));
            }
            else if (mt == MessageType.NEW_WORLD)
            {
                WorldInfo info = Serializer.Deserialize<WorldInfo>(stm);
                sync.Invoke(() => OnNewWorld(info));
            }
            else if (mt == MessageType.PLAYER_VALIDATOR_ASSIGN)
            {
                Guid actionId = Serializer.Deserialize<Guid>(stm);
                PlayerInfo info = Serializer.Deserialize<PlayerInfo>(stm);
                sync.Invoke(() => OnPlayerValidateRequest(actionId, info));
            }
            else if (mt == MessageType.WORLD_VALIDATOR_ASSIGN)
            {
                Guid actionId = Serializer.Deserialize<Guid>(stm);
                WorldInfo info = Serializer.Deserialize<WorldInfo>(stm);
                WorldInitializer init = Serializer.Deserialize<WorldInitializer>(stm);
                sync.Invoke(() => OnWorldValidateRequest(actionId, info, init));
            }
            else
                throw new Exception("Client.ProcessServerMessage bad message type " + mt.ToString());
        }
        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.WORLD_INIT)
            {
                WorldSerialized wrld = Serializer.Deserialize<WorldSerialized>(stm);
                sync.Invoke(() => OnNewWorld(new World(wrld, gameInfo)));
            }
            else if (mt == MessageType.PLAYER_JOIN)
            {
                Guid id = Serializer.Deserialize<Guid>(stm);
                Point pos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnPlayerJoin(knownWorlds.GetValue(inf.worldPos), gameInfo.GetPlayerById(id), pos));
            }
            else if (mt == MessageType.PLAYER_LEAVE)
            {
                Guid id = Serializer.Deserialize<Guid>(stm);
                sync.Invoke(() => OnPlayerLeave(knownWorlds.GetValue(inf.worldPos), gameInfo.GetPlayerById(id)));
            }
            else if (mt == MessageType.MOVE)
            {
                Guid id = Serializer.Deserialize<Guid>(stm);
                Point pos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnPlayerMove(knownWorlds.GetValue(inf.worldPos), gameInfo.GetPlayerById(id), pos));
            }
            else
                throw new Exception(Log.Dump(this, mt, "unexpected"));
        }
        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.PLAYER_INFO)
            {
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                PlayerDataUpdate pdu = Serializer.Deserialize<PlayerDataUpdate>(stm);
                sync.Invoke(() => OnPlayerData(inf, pd, pdu));
            }
            else
                throw new Exception("Client.ProcessPlayerValidatorMessage bad message type " + mt.ToString());
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

                hookServerReady();
            }
            else
                MyAssert.Assert(serverHost == serverHost_);
        }
        void OnGameInfo(GameInfoSerialized gameStateSer)
        {
            MyAssert.Assert(gameInfo == null);
            gameInfo = new GameInfo(gameStateSer);

            foreach (WorldInfo w in gameStateSer.worlds)
                myHost.ConnectAsync(w.host);
            foreach (PlayerInfo p in gameStateSer.players)
                myHost.ConnectAsync(p.validatorHost);

            Log.LogWriteLine("Recieved game info");
            Program.GameInfoOut(gameInfo);
        }
        void OnNewPlayer(PlayerInfo inf)
        {
            gameInfo.AddPlayer(inf);
            Log.LogWriteLine("New player\n{0}", inf.GetFullInfo());
            myHost.ConnectAsync(inf.validatorHost);

            if (myPlayerAgents.Contains(inf.id))
                all.AddPlayerAgent(inf);

            onNewPlayerHook(inf);
        }
        void OnNewWorld(WorldInfo inf)
        {
            gameInfo.AddWorld(inf);
            Log.LogWriteLine("New world\n{0}", inf.GetFullInfo());
            myHost.ConnectAsync(inf.host);
        }
        void OnNewWorld(World w)
        {
            knownWorlds.Add(w.Position, w);
            w.onMoveHook = (player, mv) => OnMove(w, player, mv);

            onNewWorldHook(w);

            Log.LogWriteLine("New world {0}", w.Position);
            w.ConsoleOut();
        }
        void OnPlayerData(PlayerInfo inf, PlayerData pd, PlayerDataUpdate pdu)
        {
            knownPlayers[inf.id] = pd;
            Log.LogWriteLine("{0} data update {1}", inf.GetShortInfo(), pd);
            onPlayerNewRealm(inf);
        }

        void OnPlayerValidateRequest(Guid actionId, PlayerInfo info)
        {
            all.AddPlayerValidator(info);
            Log.LogWriteLine("Validating for {0}", info);
            server.SendMessage(MessageType.ACCEPT, actionId);
        }
        void OnWorldValidateRequest(Guid actionId, WorldInfo info, WorldInitializer init)
        {
            all.AddWorldValidator(info, init);
            Log.LogWriteLine("Validating for world {0}", info.worldPos);
            server.SendMessage(MessageType.ACCEPT, actionId);
        }

        void OnPlayerJoin(World world, PlayerInfo playerInfo, Point pos)
        {
            world.AddPlayer(playerInfo.id, pos);

            Log.LogWriteLine("{0} joined {1} at {2}", playerInfo, world.info, pos);
        }
        void OnPlayerLeave(World world, PlayerInfo playerInfo)
        {
            world.RemovePlayer(playerInfo.id);

            Log.LogWriteLine("{0} left {1}", playerInfo, world.info);
        }
        void OnPlayerMove(World world, PlayerInfo playerInfo, Point newPos)
        {
            world.Move(playerInfo.id, newPos);
        }

        public bool TryConnect(IPEndPoint ep)
        {
            return myHost.TryConnectAsync(new OverlayEndpoint(ep, Client.hostName)) != null;
        }
        public void NewMyPlayer(Guid id)
        {
            myPlayerAgents.Add(id);
            server.SendMessage(MessageType.NEW_PLAYER, id);
        }
        public void NewWorld(Point pos)
        {
            server.SendMessage(MessageType.NEW_WORLD, pos);
        }
        public void Validate()
        {
            server.SendMessage(MessageType.NEW_VALIDATOR);
        }

        void OnMove(World w, PlayerInfo player, MoveType mv)
        {
            onMoveHook.Invoke(w, player, mv);
        }
    }
}
