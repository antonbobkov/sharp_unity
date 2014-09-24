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

        GameInfo gameState = null;
        OverlayEndpoint serverHost = null;
        Node server = null;

        Action<Action> sync;
        OverlayHost myHost;

        Aggregator all;

        Dictionary<Point, World> knownWorlds = new Dictionary<Point, World>();
        Dictionary<Guid, Inventory> knownInventories = new Dictionary<Guid, Inventory>();

        public Action hookServerReady = () => { };

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

            NodeRole role = gameState.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = gameState.GetWorldByHost(n.info.remote);
                return (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf);
            }

            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = gameState.GetPlayerByHost(n.info.remote);
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
                sync.Invoke(() => OnNewWorld(new World(wrld)));
            }
            else
                throw new Exception("Client.ProcessWorldMessage bad message type " + mt.ToString());
        }
        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.INVENTORY_INIT)
            {
                Inventory i = Serializer.Deserialize<Inventory>(stm);
                sync.Invoke(() => OnInventory(inf, i);
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
            MyAssert.Assert(gameState == null);
            gameState = new GameInfo(gameStateSer);

            foreach (WorldInfo w in gameStateSer.worlds)
                myHost.ConnectAsync(w.host);
            foreach (PlayerInfo p in gameStateSer.players)
                myHost.ConnectAsync(p.validatorHost);

            Log.LogWriteLine("Recieved game info");
            Program.GameInfoOut(gameState);
        }
        void OnNewPlayer(PlayerInfo inf)
        {
            gameState.AddPlayer(inf);
            Log.LogWriteLine("New player\n{0}", inf.GetFullInfo());
            myHost.ConnectAsync(inf.validatorHost);
        }
        void OnNewWorld(WorldInfo inf)
        {
            gameState.AddWorld(inf);
            Log.LogWriteLine("New world\n{0}", inf.GetFullInfo());
            myHost.ConnectAsync(inf.host);
        }
        void OnNewWorld(World w)
        {
            knownWorlds.Add(w.Position, w);
            Log.LogWriteLine("New world {0}", w.Position);
            w.ConsoleOut();
        }
        void OnInventory(PlayerInfo inf, Inventory i)
        {
            knownInventories.Add(inf.id, i);
            Log.LogWriteLine("New inventory for {0} ({1} teleports)", inf, i.teleport);
            w.ConsoleOut();
        }

        void OnPlayerValidateRequest(Guid actionId, PlayerInfo info)
        {
            Log.LogWriteLine("Validating for {0}", info);
            server.SendMessage(MessageType.ACCEPT, actionId);
        }
        void OnWorldValidateRequest(Guid actionId, WorldInfo info, WorldInitializer init)
        {
            all.AddWorldValidator(info, init);
            Log.LogWriteLine("Validating for world {0}", info.worldPos);
            server.SendMessage(MessageType.ACCEPT, actionId);
        }

        public bool TryConnect(IPEndPoint ep)
        {
            return myHost.TryConnectAsync(new OverlayEndpoint(ep, Client.hostName)) != null;
        }
        public void NewMyPlayer()
        {
            server.SendMessage(MessageType.NEW_PLAYER, Guid.NewGuid());
        }
        public void NewWorld(Point pos)
        {
            server.SendMessage(MessageType.NEW_WORLD, pos);
        }
        public void Validate()
        {
            server.SendMessage(MessageType.NEW_VALIDATOR);
        }
    }
}
