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

        OverlayHost myHost;

        Aggregator all;

        public Dictionary<Point, World> knownWorlds = new Dictionary<Point, World>();
        public Dictionary<Guid, PlayerData> knownPlayers = new Dictionary<Guid, PlayerData>();

        public HashSet<Guid> myPlayerAgents = new HashSet<Guid>();

        public Action onServerReadyHook = () => { };

        public Action<World> onNewWorldHook = (a) => { };
        public Action<PlayerInfo> onNewPlayerHook = (a) => { };

        public Action<World, PlayerInfo, Point, MoveValidity> onMoveHook = (a, b, c, d) => { };
        public Action<World, PlayerInfo> onPlayerLeaveHook = (a, b) => { };
        
        public Action<PlayerInfo, PlayerData> onNewPlayerDataHook = (a, b) => { };
        public Action<PlayerInfo, PlayerData> onPlayerNewRealm = (a, b) => { };

        public Client(GlobalHost globalHost, Aggregator all_)
        {
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
                OnServerAddress(host);
            }
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.GAME_INFO_VAR_INIT)
            {
                GameInfoSerialized info = Serializer.Deserialize<GameInfoSerialized>(stm);
                OnGameInfo(info);
            }
            else if (mt == MessageType.GAME_INFO_VAR_CHANGE)
            {
                MyAssert.Assert(gameInfo != null);

                ForwardFunctionCall ffc = ForwardFunctionCall.Deserialize(stm, typeof(GameInfo));
                ffc.Apply(gameInfo);
            }
            else if (mt == MessageType.PLAYER_VALIDATOR_ASSIGN)
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
            else
                throw new Exception("Client.ProcessServerMessage bad message type " + mt.ToString());
        }
        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.WORLD_VAR_INIT)
            {
                WorldSerialized wrld = Serializer.Deserialize<WorldSerialized>(stm);
                OnNewWorldVar(new World(wrld, gameInfo));
            }
            else if (mt == MessageType.WORLD_VAR_CHANGE)
            {
                ForwardFunctionCall ffc = ForwardFunctionCall.Deserialize(stm, typeof(World));
                ffc.Apply(knownWorlds.GetValue(inf.worldPos));
            }
            else
                throw new Exception(Log.StDump(mt, inf, "unexpected"));
        }
        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.PLAYER_INFO_VAR)
            {
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                PlayerDataUpdate pdu = Serializer.Deserialize<PlayerDataUpdate>(stm);
                OnPlayerData(inf, pd, pdu);
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

                onServerReadyHook();
            }
            else
                MyAssert.Assert(serverHost == serverHost_);
        }
        void OnGameInfo(GameInfoSerialized gameStateSer)
        {
            MyAssert.Assert(gameInfo == null);
            gameInfo = new GameInfo();

            gameInfo.onNewPlayer = OnNewPlayer;
            gameInfo.onNewWorld = OnNewWorld;

            gameInfo.Deserialize(gameStateSer);

            Log.LogWriteLine("Recieved game info");
            Program.GameInfoOut(gameInfo);
        }
        void OnNewPlayer(PlayerInfo inf)
        {
            //gameInfo.AddPlayer(inf);
            Log.LogWriteLine("New player\n{0}", inf.GetFullInfo());
            myHost.ConnectAsync(inf.validatorHost);

            if (myPlayerAgents.Contains(inf.id))
                all.AddPlayerAgent(inf);

            onNewPlayerHook(inf);
        }
        void OnNewWorld(WorldInfo inf)
        {
            //gameInfo.AddWorld(inf);
            //Log.LogWriteLine("New world\n{0}", inf.GetFullInfo());
            myHost.ConnectAsync(inf.host);
        }
        void OnNewWorldVar(World w)
        {
            knownWorlds.Add(w.Position, w);
            w.onMoveHook = (player, pos, mv) => OnMove(w, player, pos, mv);
            w.onPlayerLeaveHook = (player) => onPlayerLeaveHook(w, player);

            onNewWorldHook(w);

            //Log.LogWriteLine("New world {0}", w.Position);
            //w.ConsoleOut();
        }
        void OnPlayerData(PlayerInfo inf, PlayerData pd, PlayerDataUpdate pdu)
        {
            knownPlayers[inf.id] = pd; 
            //Log.LogWriteLine(Log.StDump( inf, pd, pdu));
            
            if(pdu == PlayerDataUpdate.INIT)
                onNewPlayerDataHook(inf, pd);
            else if (pdu == PlayerDataUpdate.JOIN_WORLD || pdu == PlayerDataUpdate.SPAWN)
                onPlayerNewRealm(inf, pd);
            else if(pdu != PlayerDataUpdate.INVENTORY_CHANGE)
                throw new Exception(Log.StDump(pdu, "unexpected"));
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

        void OnMove(World w, PlayerInfo player, Point newPos, MoveValidity mv)
        {
            onMoveHook.Invoke(w, player, newPos, mv);
        }
        void OnPlayerLeave(World w, PlayerInfo player)
        {
            onPlayerLeaveHook.Invoke(w, player);
        }
    }
}
