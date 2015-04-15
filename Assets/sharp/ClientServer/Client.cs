using System;

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
        private Action<Point> track;
        private Action<Point> untrack;

        public PlayerDatabase(Action<Point> track, Action<Point> untrack)
        {
            this.track = track;
            this.untrack = untrack;
        }

        public void Set(Guid player, Point pos)
        {
            foreach (Point p in Point.SymmetricRange(Point.One))
                track.Invoke(pos + p);

            if (playerPositions.ContainsKey(player))
                Remove(player);

            playerPositions.Add(player, pos);
        }
        public void Remove(Guid player)
        {
            MyAssert.Assert(playerPositions.ContainsKey(player));
            
            Point prevPos = playerPositions[player];

            foreach (Point p in Point.SymmetricRange(Point.One))
                untrack.Invoke(prevPos + p);

            playerPositions.Remove(player);
        }
    }

    class TrackedWorld
    {
        private Point position;
        private OverlayHost myHost;

        private bool isSubscribed = false;

        private int tracker = 0;

        private WorldInfo? _info = null;
        private WorldInfo? Info
        {
            get { return _info; }
            set
            {
                MyAssert.Assert(value.HasValue);
                MyAssert.Assert(value.Value.position == position);
                _info = value;
            }
        }
        private World world = null;

        private void TrySubscribe()
        {
            if (tracker > 0 && !isSubscribed && Info != null)
            {
                isSubscribed = true; // subscribe!
            }
        }
        private void Unsubscribe()
        {
            if (isSubscribed)
            {
                isSubscribed = false;   // unsubscribe!
                world = null;
            }
        }
        private bool ValidateWorldInfo(WorldInfo info)
        {
            MyAssert.Assert(Info != null);
            MyAssert.Assert(info.position == position);

            if (info != Info)    // old world information - ignore
            {
                MyAssert.Assert(info.generation < Info.Value.generation);
                return false;
            }

            if (!isSubscribed)
                return false;

            return true;
        }

        public TrackedWorld(Point position, OverlayHost myHost)
        {
            this.position = position;
            this.myHost = myHost;
        }

        public void Track()
        {
            ++tracker;
            TrySubscribe();
        }
        public void Untrack()
        {
            --tracker;
            MyAssert.Assert(tracker >= 0);

            if (tracker == 0)
                Unsubscribe();
        }

        public void SetWorldInfo(WorldInfo newInfo)
        {
            // first ever value - initialize
            if (Info == null)
            {
                Info = newInfo;
                TrySubscribe();
            }
            else // already initialized
            {
                if (Info == newInfo && newInfo.generation < Info.Value.generation)  // old information
                    return;

                MyAssert.Assert(newInfo.generation != Info.Value.generation);

                Unsubscribe();

                Info = newInfo;

                TrySubscribe();
                
            }
        }

        public void WorldInit(WorldInitializer init)
        {
            if (!ValidateWorldInfo(init.info))
                return;

            MyAssert.Assert(world == null);

            world = new World(init, null);  // wrong: should have non-null action
        }
        public void WorldAction(ForwardFunctionCall ffc, WorldInfo info)
        {
            if (!ValidateWorldInfo(info))
                return;

            MyAssert.Assert(world != null);
            ffc.Apply(world);
        }

        public World GetWorld() { return world; }
    }

    class WorldDatabase
    {
        private Dictionary<Point, TrackedWorld> worlds = new Dictionary<Point, TrackedWorld>();
        private OverlayHost myHost;

        public WorldDatabase(OverlayHost myHost) { this.myHost = myHost; }
        public TrackedWorld At(Point p)
        {
            if(!worlds.ContainsKey(p))
                worlds.Add(p, new TrackedWorld(p, myHost));
            return worlds[p];
        }
    }

    class Client
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("client");

        public OverlayEndpoint serverHost = null;

        OverlayHost myHost;

        Aggregator all;

        PlayerDatabase connectedPlayers;
        WorldDatabase worlds;

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

            worlds = new WorldDatabase(myHost);

            connectedPlayers = new PlayerDatabase(p => worlds.At(p).Track(), p => worlds.At(p).Untrack());
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
                ForwardFunctionCall ffc = ForwardFunctionCall.Deserialize(stm, typeof(World));
                worlds.At(inf.position).WorldAction(ffc, inf);
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

        void OnNewWorldVar(WorldInitializer wrld)
        {
            Point pos = wrld.info.position;
            
            worlds.At(pos).WorldInit(wrld);
            
            worlds.At(wrld.info.position)
            w.onMoveHook = (player, pos, mv) => onMoveHook(w, player, pos, mv);
            w.onPlayerLeaveHook = (player, tel) => onPlayerLeaveHook(w, player, tel);

            onNewWorldHook(w);

            //Log.LogWriteLine("New world {0}", w.Position);
            //w.ConsoleOut();
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
