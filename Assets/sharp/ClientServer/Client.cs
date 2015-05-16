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

        private Action<WorldInfo> recordWorldInfo;
        private Action<Point> track;
        private Action<Point> untrack;

        public PlayerDatabase(Action<Point> track, Action<Point> untrack, Action<WorldInfo> recordWorldInfo)
        {
            this.track = track;
            this.untrack = untrack;
            this.recordWorldInfo = recordWorldInfo;
        }

        public void Set(Guid player, WorldInfo inf)
        {
            Point pos = inf.position;

            recordWorldInfo.Invoke(inf);

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

    struct TrackedWorldData
    {
        public Func<WorldInitializer, World> generateWorld;

        public Action<WorldInfo> recordWorldInfo;
        public Action<WorldInfo> subscribe;
        public Action<WorldInfo> unsubscribe;
    }

    class TrackedWorld
    {
        private class WorldHook : WorldMutator
        {
            private TrackedWorld parent;
            public WorldHook(TrackedWorld parent) { this.parent = parent; }

            public override void NET_AddNeighbor(WorldInfo worldInfo)
            {
                parent.data.recordWorldInfo(worldInfo);
            }
        }

        private TrackedWorldData data;
        private WorldHook worldHook;

        private Point position;

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
                isSubscribed = true;
                data.subscribe(Info.Value);
                //data.host.ConnectSendMessage(Info.Value.host, MessageType.SUBSCRIBE);
            }
        }
        private void Unsubscribe()
        {
            if (isSubscribed)
            {
                isSubscribed = false;
                MyAssert.Assert(Info != null);
                data.unsubscribe(Info.Value);
                //data.host.ConnectSendMessage(Info.Value.host, MessageType.UNSUBSCRIBE);

                if (world != null)
                {
                    world.Dispose();
                    world = null;
                }
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

        public TrackedWorld(Point position, TrackedWorldData data)
        {
            this.position = position;
            this.data = data;

            worldHook = new WorldHook(this);
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
                if (Info == newInfo || newInfo.generation < Info.Value.generation)  // old information
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

            world = data.generateWorld(init);
            foreach (WorldInfo inf in world.GetKnownNeighbors())
                data.recordWorldInfo(inf);
        }
        public void WorldAction(ForwardFunctionCall ffc, WorldInfo info)
        {
            if (!ValidateWorldInfo(info))
                return;

            MyAssert.Assert(world != null);
            ffc.Apply(world);
            ffc.Apply(worldHook);
        }

        public World GetWorld() { return world; }
    }

    class WorldDatabase
    {
        private Dictionary<Point, TrackedWorld> worlds = new Dictionary<Point, TrackedWorld>();
        private TrackedWorldData data;

        public WorldDatabase(TrackedWorldData data)
        {
            data.recordWorldInfo = inf => this.At(inf.position).SetWorldInfo(inf);
            this.data = data;
        }
        public TrackedWorld At(Point p)
        {
            if(!worlds.ContainsKey(p))
                worlds.Add(p, new TrackedWorld(p, data));
            return worlds[p];
        }
        public World TryGetWorld(Point p)
        {
            return At(p).GetWorld();
        }
    }

    class Client : GameNode
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("client");

        Aggregator all;

        public PlayerDatabase connectedPlayers;
        public WorldDatabase worlds;

        public HashSet<Guid> myPlayerAgents = new HashSet<Guid>();

        public Action onServerReadyHook = () => { };

        //public Action<World> onNewWorldHook = (a) => { };
        //public Action<World> onDeleteWorldHook = (a) => { };

        //public Action<PlayerInfo> onNewMyPlayerHook = (a) => { };

        //public Action<World, PlayerInfo, Point, ActionValidity> onMoveHook = (a, b, c, d) => { };
        //public Action<World, PlayerInfo, bool> onPlayerLeaveHook = (a, b, c) => { };

        //Func<WorldInitializer, World> generateWorld;

        public Client(GlobalHost globalHost, Aggregator all, Func<WorldInitializer, World> generateWorld)
        {
            this.all = all;
            //this.generateWorld = generateWorld;

            Host = globalHost.NewHost(Client.hostName, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.CLIENT), Aggregator.longInactivityWait);

            SetConnectionHook(ProcessNewConnection);

            Action<WorldInfo> subscribe = wi => MessageWorld(wi, MessageType.SUBSCRIBE);

            Action<WorldInfo> unsubscribe = wi => MessageWorld(wi, MessageType.UNSUBSCRIBE);

            TrackedWorldData data = new TrackedWorldData()
                { generateWorld = generateWorld, recordWorldInfo = null,subscribe = subscribe, unsubscribe = unsubscribe };

            worlds = new WorldDatabase(data);

            connectedPlayers = new PlayerDatabase(p => worlds.At(p).Track(), p => worlds.At(p).Untrack(), 
                inf => worlds.At(inf.position).SetWorldInfo(inf));
        }

        protected override void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.SERVER_ADDRESS)
            {
                OverlayEndpoint host = Serializer.Deserialize<OverlayEndpoint>(stm);
                OnServerAddress(host);
            }
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        protected override void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.PLAYER_VALIDATOR_ASSIGN)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);
                PlayerInfo info = Serializer.Deserialize<PlayerInfo>(stm);
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                OnPlayerValidateRequest(n, remoteActionId, info, pd);
            }
            else if (mt == MessageType.WORLD_VALIDATOR_ASSIGN)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);
                WorldInitializer init = Serializer.Deserialize<WorldInitializer>(stm);
                OnWorldValidateRequest(n, remoteActionId, init);
            }
            else if (mt == MessageType.NEW_PLAYER_REQUEST_SUCCESS)
            {
                PlayerInfo info = Serializer.Deserialize<PlayerInfo>(stm);
                OnNewPlayerRequestApproved(info);
            }
            else
                throw new Exception("Client.ProcessServerMessage bad message type " + mt.ToString());
        }
        protected override void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
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

                ConnectAsync(serverHost, ProcessServerDisconnect);
                Host.BroadcastGroup(Client.hostName, MessageType.SERVER_ADDRESS, serverHost);

                onServerReadyHook();
            }
            else
                MyAssert.Assert(serverHost == serverHost_);
        }

        void OnNewPlayerRequestApproved(PlayerInfo inf)
        {
            MyAssert.Assert(myPlayerAgents.Contains(inf.id));
            all.AddPlayerAgent(inf);
        }

        void OnNewWorldVar(WorldInitializer wrld)
        {
            Point pos = wrld.info.position;

            worlds.At(pos).WorldInit(wrld);
            
        }

        void OnPlayerValidateRequest(Node n, Guid actionId, PlayerInfo info, PlayerData pd)
        {
            all.AddPlayerValidator(info, pd);
            //Log.LogWriteLine("Validating for {0}", info);
            //myHost.SendMessage(serverHost, MessageType.ACCEPT, actionId);
            RemoteAction.Sucess(n, actionId);
        }
        void OnWorldValidateRequest(Node n, Guid actionId, WorldInitializer init)
        {
            all.AddWorldValidator(init);
            //Log.LogWriteLine("Validating for world {0}", info.worldPos);
            //myHost.SendMessage(serverHost, MessageType.ACCEPT, actionId);
            RemoteAction.Sucess(n, actionId);
        }

        public bool TryConnect(IPEndPoint ep)
        {
            return Host.TryConnectAsync(new OverlayEndpoint(ep, Client.hostName), ProcessClientDisconnect) != null;
        }
        public void NewMyPlayer(Guid id)
        {
            myPlayerAgents.Add(id);
            MessageServer(MessageType.NEW_PLAYER_REQUEST, id);
        }
        public void NewWorld(Point pos)
        {
            MessageServer(MessageType.NEW_WORLD_REQUEST, pos);
        }
        public void Validate()
        {
            MessageServer(MessageType.NEW_VALIDATOR);
        }
        public void StopValidating()
        {
            MessageServer(MessageType.STOP_VALIDATING);
        }

        public OverlayEndpoint GetServer() { return serverHost; }
    }
}
