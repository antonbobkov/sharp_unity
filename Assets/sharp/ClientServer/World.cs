using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Xml.Serialization;

using Tools;
using Network;


namespace ServerClient
{
    public class MyColor
    {
        public byte R;
        public byte G;
        public byte B;

        public MyColor() { }
        public MyColor(byte R_, byte G_, byte B_) { R = R_; G = G_; B = B_; }
    }

    interface ITile
    {
        Point Position { get; }

        bool Solid { get; }
        MyColor Block { get; }

        bool Loot { get; }
        bool Spawn { get; }

        Guid PlayerId { get; }

        bool IsMoveable();
        bool IsEmpty();
        bool IsSpecial();
    }

    [Serializable]
    public class Tile : ITile
    {
        public Point Position { get; set; }

        public bool Solid { get { return Block != null; } }
        public MyColor Block { get; set; }

        public bool Loot { get; set; }
        public bool Spawn { get; set; }

        public Guid PlayerId { get; set; }

        public bool IsEmpty() { return IsMoveable() && !Loot; }
        public bool IsMoveable() { return (PlayerId == Guid.Empty) && !Solid; }
        public bool IsSpecial() { return Spawn; }

        public Tile() { }
        public Tile(Point pos)
        {
            PlayerId = Guid.Empty;
            Position = pos;
        }

        //public string PlayerGuid
        //{
        //    get
        //    {
        //        if (PlayerId != Guid.Empty)
        //            return PlayerId.ToString();
        //        else
        //            return "";
        //    }

        //    set
        //    {
        //        if (value != "")
        //            PlayerId = new Guid(value);
        //        else
        //            PlayerId = Guid.Empty;
        //    }
        //}
    }

    [Serializable]
    public class WorldInitializer
    {
        public WorldInfo info;
        public WorldSeed seed = null;
        public WorldSerialized world = null;

        public WorldInitializer() { }
        public WorldInitializer(WorldInfo info, WorldSeed seed) { this.info = info; this.seed = seed; }
        public WorldInitializer(WorldInfo info, WorldSerialized world) { this.info = info; this.world = world; }
    }

    [Serializable]
    public class WorldSeed
    {
        public int seed;
        public double wallDensity = .2;
        public double lootDensity = .05;
        //public bool hasSpawn = false;
        public MyColor defaultColor;

        public WorldSeed() { }
        internal WorldSeed(int seed_, MyColor defaultColor_)
        {
            seed = seed_;
            defaultColor = defaultColor_;
        }
    }

    [Serializable]
    public struct WorldInfo
    {
        public Point position;
        public OverlayEndpoint host;
        public int generation;
        public bool hasSpawn;

        public WorldInfo(Point position, OverlayEndpoint host, int generation, bool hasSpawn)
        {
            this.host = host;
            this.position = position;
            this.generation = generation;
            this.hasSpawn = hasSpawn;
        }

        public override string ToString()
        {
            return GetFullInfo();   // used for comparison, must contain full info
        }

        public string GetFullInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("World pos: {0}\n", position);
            sb.AppendFormat("Validator host: {0}\n", host);
            sb.AppendFormat("Generation: {0}\n", generation);

            return sb.ToString();
        }
        public string GetShortInfo() { return "World " + position.ToString(); }

        public override bool Equals(object comparand) { return this.ToString().Equals(comparand.ToString()); }
        public override int GetHashCode() { return this.ToString().GetHashCode(); }

        public static bool operator ==(WorldInfo o1, WorldInfo o2) { return Object.Equals(o1, o2); }
        public static bool operator !=(WorldInfo o1, WorldInfo o2) { return !(o1 == o2); }
    }

    [Flags]
    public enum ActionValidity
    {
        VALID = 0,
        BOUNDARY = 1,
        REMOTE = 2,
        NEW = 4,
        OCCUPIED_PLAYER = 8,
        OCCUPIED_WALL = 16,
        OCCUPIED_LOOT = 32,
        OCCUPIED_SPECIAL = 64 
    };

    [Serializable]
    public class WorldSerialized
    {
        public Plane<Tile> map;
        public PlayerInfo[] players;
        public WorldInfo[] neighborWorlds;
        public Point? spawnPos;
    }

    class WorldMutator : MarshalByRefObject
    {
        public virtual void NET_AddPlayer(PlayerInfo player, Point pos, bool teleporting) { }
        public virtual void NET_RemovePlayer(Guid player, bool teleporting) { }
        public virtual void NET_Move(Guid player, Point newPos, ActionValidity mv) { }
        public virtual void NET_AddNeighbor(WorldInfo worldInfo) { }
        public virtual void NET_PlaceBlock(Point pos) { }
        public virtual void NET_RemoveBlock(Point pos) { }

        public virtual void Dispose() { }
    }

    class World : WorldMutator
    {
        // ----- constructors -----
        public World(WorldInitializer init, Action<Point, PlayerInfo> onLootHook)
        {
            Info = init.info;
            this.onLootHook = onLootHook;

            foreach (Point p in Point.SymmetricRange(Point.One))
                if (p != Point.Zero)
                    neighborWorlds.Add(p + Position, null);

            // exactly one is non-null
            MyAssert.Assert((init.seed != null) || (init.world != null));
            MyAssert.Assert((init.seed == null) || (init.world == null));

            if (init.seed != null)
                GenerateWorld(init.seed, init.info.hasSpawn);

            if (init.world != null)
                DeserializeWorld(init.world);
        }
        public WorldInitializer Serialize()
        {
            //SanityCheck();
            
            WorldSerialized ser = new WorldSerialized()
            { 
                map = map,
                players = playerInformation.Values.ToArray(),
                neighborWorlds = GetKnownNeighbors().ToArray(),
                spawnPos = spawnPos
            };

            return new WorldInitializer(Info, ser);
        }

        void SanityCheck()
        {
            MyAssert.Assert(playerPositions.Count == playerInformation.Count);

            int playerCount = 0;
            foreach (Point p in Point.Range(map.Size))
                if (map[p].PlayerId != Guid.Empty)
                    playerCount++;

            MyAssert.Assert(playerCount == playerInformation.Count);
            MyAssert.Assert(playerCount == playerPositions.Count);
        }
        
        // ----- read only infromation -----
        public WorldInfo Info { get; private set; }

        public Point Position { get { return Info.position; } }
        public Point Size { get { return map.Size; } }

        public IEnumerable<Point> GetSpawn()
        {
            /*
            foreach (Point p in Point.Range(map.Size))
            {
                Tile t = map[p];
                if (t.IsEmpty() && t.loot == false)
                    yield return new KeyValuePair<Point, Tile>(p, t);
            }
            */

            if (spawnPos.HasValue)
            {
                foreach (Point delta in Point.SymmetricRange(new Point(1,1)))
                {
                    Point p = spawnPos.Value + delta;
                    
                    if (!map.InRange(p))
                        continue;
                    
                    Tile t = map[p];
                    if (t.IsMoveable())
                        //yield return new KeyValuePair<Point, Tile>(p, t);
                        yield return p;
                }
            }
        }
        public IEnumerable<ITile> GetAllTiles()
        {
            foreach (Point p in Point.Range(Size))
                yield return this[p];
        }
        public ITile this[Point pos]{ get { return map[pos]; } }

        public Point GetPlayerPosition(Guid id) { return playerPositions.GetValue(id); }
        public Point? TryGetPlayerPosition(Guid id)
        {
            if (playerPositions.ContainsKey(id))
                return playerPositions.GetValue(id);
            else
                return null;
        }
        public PlayerInfo GetPlayerInfo(Guid id) { return playerInformation.GetValue(id); }

        public IEnumerable<PlayerInfo> GetAllPlayers()
        {
            return playerInformation.Values;
        }
        public bool HasPlayer(Guid id) { return playerPositions.ContainsKey(id); }

        public IEnumerable<WorldInfo> GetKnownNeighbors()
        {
            return from w in neighborWorlds.Values
                   where w.HasValue
                   select w.Value;
        }
        public IEnumerable<Point> GetUnknownNeighbors()
        {
            return from p in neighborWorlds.Keys
                   where neighborWorlds[p] == null
                   select p;
        }
        public WorldInfo? GetNeighbor(Point p)
        {
            MyAssert.Assert(neighborWorlds.ContainsKey(p));
            return neighborWorlds.GetValue(p);
        }

        public ActionValidity CheckValidMove(Guid player, Point newPos)
        {
            ActionValidity mv = CheckAction(player, newPos);

            mv &= ~ActionValidity.OCCUPIED_LOOT;

            return mv;
        }

        public ActionValidity CheckAction(Guid player, Point newPos)
        {
            ActionValidity mv = ActionValidity.VALID;

            Point curPos = playerPositions.GetValue(player);

            Point diff = newPos - curPos;
            if (Math.Abs(diff.x) > 1)
                mv |= ActionValidity.REMOTE;
            if (Math.Abs(diff.y) > 1)
                mv |= ActionValidity.REMOTE;

            ITile tile;
            try
            {
                tile = map[newPos];
            }
            catch (IndexOutOfRangeException)
            {
                mv |= ActionValidity.BOUNDARY;
                return mv;
            }

            if (!tile.IsEmpty())
            {
                if (tile.PlayerId != Guid.Empty)
                    mv |= ActionValidity.OCCUPIED_PLAYER;
                if (tile.Solid)
                    mv |= ActionValidity.OCCUPIED_WALL;
                if(tile.Loot)
                    mv |= ActionValidity.OCCUPIED_LOOT;
                if (tile.IsSpecial())
                    mv |= ActionValidity.OCCUPIED_SPECIAL;
            }

            return mv;
        }

        // ----- modifiers -----
        [Forward] public override void NET_AddPlayer(PlayerInfo player, Point pos, bool teleporting)
        {
            MyAssert.Assert(!playerPositions.ContainsKey(player.id));
            MyAssert.Assert(!playerInformation.ContainsKey(player.id));

            playerInformation.Add(player.id, player);

            ActionValidity av = ActionValidity.NEW;
            if (teleporting)
                av |= ActionValidity.REMOTE;

            NET_Move(player.id, pos, av);
        }
        [Forward] public override void NET_RemovePlayer(Guid player, bool teleporting)
        {
            Point pos = playerPositions.GetValue(player);
            MyAssert.Assert(map[pos].PlayerId == player);
            map[pos].PlayerId = Guid.Empty;

            //PlayerInfo inf = playerInformation.GetValue(player);

            playerPositions.Remove(player);
            playerInformation.Remove(player);
        }
        [Forward] public override void NET_Move(Guid player, Point newPos, ActionValidity mv)
        {
            PlayerInfo p = playerInformation.GetValue(player);

            if (!mv.Has(ActionValidity.NEW))
            {
                Point curPos = playerPositions.GetValue(player);

                ActionValidity v = CheckValidMove(player, newPos) & ~mv;
                MyAssert.Assert(v == ActionValidity.VALID);

                //if (v != ActionValidity.VALID)
                    //Log.LogWriteLine("Game.Move Warning: Invalid move {0} from {1} to {2} by {3}", v, curPos, newPos, p.name);

                MyAssert.Assert(map[curPos].PlayerId == player);
                map[curPos].PlayerId = Guid.Empty;
            }

            Tile tile = map[newPos];
            MyAssert.Assert(tile.IsMoveable());

            if (tile.Loot == true)
                if(onLootHook != null)
                    onLootHook(newPos, p);
            
            playerPositions[player] = newPos;
            tile.PlayerId = player;
            tile.Loot = false;

            //Log.Dump(player, newPos, mv);
        }
        [Forward] public override void NET_AddNeighbor(WorldInfo worldInfo)
        {
            Point p = worldInfo.position;
            MyAssert.Assert(neighborWorlds.ContainsKey(p));
            neighborWorlds[p] = worldInfo;
        }
        [Forward] public override void NET_PlaceBlock(Point pos)
        {
            Tile t = map[pos];

            MyAssert.Assert(t.IsEmpty());

            t.Block = new MyColor(50, 50, 50);
        }
        [Forward] public override void NET_RemoveBlock(Point pos)
        {
            Tile t = map[pos];

            MyAssert.Assert(t.Solid);
            MyAssert.Assert(!t.IsSpecial());

            t.Block = null;
        }

        // ----- hooks -----
        private Action<Point, PlayerInfo> onLootHook;

        // ----- generating -----
        static public readonly Point worldSize = new Point(10, 10);

        private void DeserializeWorld(WorldSerialized ws)
        {
            map = ws.map;
            spawnPos = ws.spawnPos;

            foreach (Point p in Point.Range(map.Size))
                if (map[p].PlayerId != Guid.Empty)
                    playerPositions.Add(map[p].PlayerId, p);

            foreach (PlayerInfo inf in ws.players)
                playerInformation.Add(inf.id, inf);

            foreach (WorldInfo inf in ws.neighborWorlds)
                neighborWorlds[inf.position] = inf;

            MyAssert.Assert(playerPositions.Count == playerInformation.Count);
        }
        private void GenerateWorld(WorldSeed init, bool hasSpawn)
        {
            map = new Plane<Tile>(worldSize);

            foreach (Point p in Point.Range(map.Size))
                map[p] = new Tile(p);

            Generate(init, hasSpawn);
        }
        private void Generate(WorldSeed init, bool hasSpawn)
        {
            MyRandom seededRandom = new MyRandom(init.seed);

            // random terrain
            foreach (Tile t in map.GetTiles())
            {
                if (seededRandom.NextDouble() < init.wallDensity)
                    t.Block = init.defaultColor;
                else
                {
                    t.Block = null;

                    if (seededRandom.NextDouble() < init.lootDensity)
                        t.Loot = true;
                }
            }

            if (hasSpawn)
            {
                var a = (from t in map.GetEnum()
                         where t.Value.Solid
                         select t).ToList();

                if (a.Any()) // could be empty world
                {
                    var spawn = a.Random(n => seededRandom.Next(n));
                    spawn.Value.Spawn = true;
                    spawnPos = spawn.Key;
                }
            }

            /*/ clear spawn points
            foreach (Point bp in world.GetBoundary())
            {
                Tile t = world.map[bp];

                //t.solid = false;
                //t.loot = false;
                //MyAssert.Assert(t.IsEmpty());
            }
             */

        }

        // ----- private data -----
        Plane<Tile> map;
        Point? spawnPos = null;

        Dictionary<Guid, Point> playerPositions = new Dictionary<Guid, Point>();
        Dictionary<Guid, PlayerInfo> playerInformation = new Dictionary<Guid, PlayerInfo>();

        Dictionary<Point, WorldInfo?> neighborWorlds = new Dictionary<Point,WorldInfo?>();
    }

    class RealmMove
    {
        public Point newWorld;
        public Point newPosition;
    }

    static class WorldTools
    {
        static int BoundaryMove(ref int p, int sz)
        {
            int ret = 0;

            while (p < 0)
            {
                p += sz;
                ret--;
            }

            while (p >= sz)
            {
                p -= sz;
                ret++;
            }

            return ret;
        }
        static public RealmMove BoundaryMove(Point p, Point worldPos)
        {
            int px = p.x;
            int py = p.y;

            Point ret = new Point(BoundaryMove(ref px, World.worldSize.x), BoundaryMove(ref py, World.worldSize.y));

            p = new Point(px, py);

            return new RealmMove() { newPosition = p, newWorld = ret + worldPos };
        }
        static public Point Shift(Point worldPos, Point posInWorld)
        {
            return Point.Scale(worldPos, World.worldSize) + posInWorld;
        }
        static public void ConsoleOut(World w)
        {
            Plane<char> pic = new Plane<char>(w.Size);

            foreach (ITile t in w.GetAllTiles())
            {
                Point p = t.Position;

                if (t.Solid)
                    pic[p] = '*';
                else if (t.PlayerId != Guid.Empty)
                    pic[p] = w.GetPlayerInfo(t.PlayerId).name[0];
                else if (t.Loot)
                    pic[p] = '$';
            }

            for (int y = w.Size.y - 1; y >= 0; --y)
            {
                for (int x = 0; x < w.Size.x; ++x)
                    Console.Write(pic[x, y]);
                Console.WriteLine();
            }
        }

        //public IEnumerable<Point> GetBoundary()
        //{
        //    HashSet<Point> bnd = new HashSet<Point>();

        //    for (int x = 0; x < map.Size.x; ++x)
        //    {
        //        bnd.Add(new Point(x, 0));
        //        bnd.Add(new Point(x, map.Size.y - 1));
        //    }

        //    for (int y = 0; y < map.Size.y; ++y)
        //    {
        //        bnd.Add(new Point(0, y));
        //        bnd.Add(new Point(map.Size.x - 1, y));
        //    }

        //    return from p in bnd
        //           orderby p.ToString()
        //           select p;
        //}
    }

    class WorldValidator
    {
        private class WorldHook : WorldMutator
        {
            private WorldValidator parent;
            public WorldHook(WorldValidator parent) { this.parent = parent; }

            public override void NET_AddPlayer(PlayerInfo player, Point pos, bool teleporting)
            {
                parent.BoundaryRequest();
            }
        }

        Random rand = new Random();

        World world;
        WorldHook worldHook;
        OverlayHost myHost;

        OverlayEndpoint serverHost;
        //Aggregator all;

        RemoteActionRepository remoteActions = new RemoteActionRepository();
        bool finalizing = false;

        HashSet<Guid> playerLocks = new HashSet<Guid>();

        HashSet<OverlayEndpoint> subscribers = new HashSet<OverlayEndpoint>();

        public WorldValidator(WorldInitializer init, GlobalHost globalHost, OverlayEndpoint serverHost)//, Aggregator all)
        {
            this.serverHost = serverHost;
//            this.all = all;

            World newWorld = new World(init, OnLootPickup);
            world = new ForwardProxy<World>(newWorld, RemoteFunctionForward).GetProxy();
            worldHook = new WorldHook(this);

            myHost = globalHost.NewHost(init.info.host.hostname, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.WORLD_VALIDATOR, init.info), Aggregator.longInactivityWait);

        }

        Game.MessageProcessor AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (role == NodeRole.CLIENT)
                return ProcessClientMessage;
                //return (mt, stm, nd) => { throw new Exception(Log.StDump(n.info, mt, "not expecting messages")); };

            if (n.info.remote == serverHost)
                return ProcessServerMessage;
            
            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);
                return (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf);
            }
            
            if (role == NodeRole.PLAYER_AGENT)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);
                return (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf);

            }

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = Serializer.Deserialize<WorldInfo>(nodeInfo);
                return (mt, stm, nd) => ProcessWorldValidatorMessage(mt, stm, nd, inf);
            }

            throw new Exception(Log.StDump(n.info, role, "unexpected"));
        }
        
        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.RESPONSE)
                RemoteAction.Process(remoteActions, n, stm);
            else 
            {
                if (finalizing)
                {
                    Log.Dump(world.Info, "finalizing - ignored player validator message", mt);
                    return;
                }

                if (mt == MessageType.PLAYER_DISCONNECT)
                {
                    if (world.HasPlayer(inf.id))
                        world.NET_RemovePlayer(inf.id, false);
                    else
                        Log.Dump(world.Info, mt, inf, "player not present, possible bug");
                }
                else
                    throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
            }
        }
        void ProcessWorldValidatorMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.RESPONSE)
                RemoteAction.Process(remoteActions, n, stm);
            else if (mt == MessageType.REALM_MOVE)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);

                if (finalizing)
                {
                    RemoteAction.Fail(n, remoteActionId);
                    return;
                }

                PlayerInfo player = Serializer.Deserialize<PlayerInfo>(stm);
                Point newPos = Serializer.Deserialize<Point>(stm);
                bool teleporting = Serializer.Deserialize<bool>(stm);
                RealmMoveIn(player, newPos, teleporting, n, remoteActionId);
            }
            else if (mt == MessageType.REMOTE_TAKE_BLOCK)
            {
                if (finalizing)
                    return;

                PlayerInfo player = Serializer.Deserialize<PlayerInfo>(stm);
                Point blockPos = Serializer.Deserialize<Point>(stm);
                TryRemoveBlock(player, blockPos);
            }
            else if (mt == MessageType.REMOTE_PLACE_BLOCK)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);

                if (finalizing)
                {
                    RemoteAction.Fail(n, remoteActionId);
                    return;
                }

                Point blockPos = Serializer.Deserialize<Point>(stm);

                OnRemotePlaceBlock(blockPos, n, remoteActionId);
            }
            else
                throw new Exception(Log.StDump(mt, "bad message type"));
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (finalizing)
            {
                Log.Dump(world.Info, "finalizing - ignored player message", mt);
                return;
            }

            if (playerLocks.Contains(inf.id))
            {
                Log.Dump("can't act, locked", inf, mt);
                return;
            }

            if (!world.HasPlayer(inf.id))
            {
                Log.Dump("can't act, not in the world", inf, mt);
                return;
            }

            Point curPos = world.GetPlayerPosition(inf.id);

            if (mt == MessageType.MOVE_REQUEST)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);
                ActionValidity mv = Serializer.Deserialize<ActionValidity>(stm);

                if(mv == ActionValidity.VALID)
                    OnMoveValidate(inf, curPos, newPos);
                else if(mv == ActionValidity.BOUNDARY)
                    OnValidateRealmMove(inf, curPos, newPos);
                else if(mv == ActionValidity.REMOTE)
                    OnValidateTeleport(inf, curPos, newPos);
                else
                    throw new Exception(Log.StDump(mt, inf, newPos, mv, "unexpected move request"));
            }
            else if (mt == MessageType.PLACE_BLOCK)
            {
                Point blockPos = Serializer.Deserialize<Point>(stm);
                OnPlaceBlock(inf, curPos, blockPos);
            }
            else if (mt == MessageType.TAKE_BLOCK)
            {
                Point blockPos = Serializer.Deserialize<Point>(stm);
                OnRemoveBlock(inf, curPos, blockPos);
            }
            else
                throw new Exception(Log.StDump(mt, inf, "unexpected message"));
        }
        void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            if (finalizing)
                return;
            
            if (mt == MessageType.SPAWN_REQUEST)
            {
                PlayerInfo playerInfo = Serializer.Deserialize<PlayerInfo>(stm);
                OnSpawnRequest(playerInfo);
            }
            else if (mt == MessageType.NEW_NEIGHBOR)
            {
                WorldInfo neighbor = Serializer.Deserialize<WorldInfo>(stm);
                world.NET_AddNeighbor(neighbor);
            }
            else
                throw new Exception(Log.StDump("unexpected", world.Info, mt));
        }
        void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (finalizing)
                return;
            
            if (mt == MessageType.SUBSCRIBE)
            {
                if(subscribers.Add(n.info.remote))
                    n.SendMessage(MessageType.WORLD_VAR_INIT, world.Serialize());
            }
            else if (mt == MessageType.UNSUBSCRIBE)
            {
                subscribers.Remove(n.info.remote);
            }
            else
                throw new Exception(Log.StDump("unexpected", world.Info, mt, n.info));
        }

        void OnMoveHook(PlayerInfo inf, Point newPos, ActionValidity mv)
        {
            //Log.Dump(inf, newPos, mv);

            if (mv.Has(ActionValidity.NEW))
                BoundaryRequest();
        }
        void OnPlaceBlock(PlayerInfo player, Point currPos, Point blockPos)
        {
            ActionValidity v = world.CheckAction(player.id, blockPos) & ~ActionValidity.REMOTE;

            if ((v & ~ActionValidity.BOUNDARY) != ActionValidity.VALID)
            {
                Log.Dump("Invalid action (check 1)", world.Info, player, v, currPos, blockPos);
                return;
            }

            ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, player.id);

            RemoteAction
                .Send(myHost, player.validatorHost, MessageType.LOCK_VAR)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if (res == Response.SUCCESS)
                    {
                        Guid remoteLockId = Serializer.Deserialize<Guid>(stm);
                        PlayerData pd = Serializer.Deserialize<PlayerData>(stm);

                        Action<Response> postProcess = (rs) =>
                        {
                            if (rs == Response.SUCCESS)
                            {
                                --pd.inventory.blocks;      
                                MyAssert.Assert(pd.inventory.blocks >= 0);

                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.SUCCESS, remoteLockId, pd);
                            }
                            else
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.FAIL, remoteLockId);
                        };

                        bool success = false;
                        try
                        {
                            MyAssert.Assert(pd.inventory.blocks >= 0);
                            if (pd.inventory.blocks == 0)
                            {
                                Log.Dump("Block placing failed, not enough items", world.Info, player, currPos, blockPos);
                                return;
                            }

                            if (v == ActionValidity.VALID)
                            {
                                v = world.CheckAction(player.id, blockPos) & ~ActionValidity.REMOTE;

                                if (v != ActionValidity.VALID)
                                {
                                    Log.Dump("Invalid action (check 2)", world.Info, player, v, currPos, blockPos);
                                    return;
                                }

                                world.NET_PlaceBlock(blockPos);

                                postProcess.Invoke(Response.SUCCESS);   // done
                            }
                            else
                            {
                                // action has to be done remotely
                                MyAssert.Assert(v == ActionValidity.BOUNDARY);

                                RemotePlaceBlock(player, blockPos, postProcess);
                            }

                            success = true;
                        }
                        finally
                        {
                            if(!success)
                                postProcess.Invoke(Response.FAIL);
                        }
                    }
                    else
                    {
                        Log.Dump("Block placing failed, remote lock rejected", world.Info, player, currPos, blockPos);
                    }
                });
        }

        void RemotePlaceBlock(PlayerInfo player, Point blockPos, Action<Response> postProcess)
        {
            bool success = false;

            try
            {
                WorldInfo remoteRealm = GetRemoteRealm(ref blockPos);

                ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, player.id);

                RemoteAction
                    .Send(myHost, remoteRealm.host, MessageType.REMOTE_PLACE_BLOCK, blockPos)
                    .Respond(remoteActions, lck, (res, stm) =>
                    {
                        if (res == Response.SUCCESS)
                        {
                            postProcess.Invoke(Response.SUCCESS);
                        }
                        else
                        {
                            Log.Dump("Block placing failed, remote realm rejected", world.Info, player, blockPos);
                            postProcess.Invoke(Response.FAIL);
                        }
                    });

                success = true;
            }
            catch (GetRealmException) { }
            finally
            {
                if (!success)
                    postProcess(Response.FAIL);
            }
        }
        void OnRemotePlaceBlock(Point blockPos, Node n, Guid remoteActionId)
        {
            bool success = false;

            try
            {
                ITile t = world[blockPos];

                if (!t.IsEmpty())
                {
                    Log.Dump("Invalid action, bad tile", world.Info, blockPos);
                    return;
                }

                world.NET_PlaceBlock(blockPos);

                success = true;
            }
            finally
            {
                if (success)
                    RemoteAction.Sucess(n, remoteActionId);
                else
                    RemoteAction.Fail(n, remoteActionId);
            }
        }
        void OnRemoveBlock(PlayerInfo player, Point currPos, Point blockPos)
        {
            ActionValidity v = world.CheckAction(player.id, blockPos) & ~ActionValidity.OCCUPIED_WALL & ~ActionValidity.REMOTE;

            if ((v & ~ActionValidity.BOUNDARY) != ActionValidity.VALID)
            {
                Log.Dump("Invalid action (check 1)", world.Info, player, v, currPos, blockPos);
                return;
            }

            if (v != ActionValidity.BOUNDARY)
                TryRemoveBlock(player, blockPos);
            else
            {
                try
                {
                    WorldInfo remoteRealm = GetRemoteRealm(ref blockPos);
                    myHost.ConnectSendMessage(remoteRealm.host, MessageType.REMOTE_TAKE_BLOCK, player, blockPos);
                }
                catch (GetRealmException e)
                {
                    Log.Dump(world.Info, player, e.Message);
                }
            }
        }
        bool TryRemoveBlock(PlayerInfo player, Point blockPos)
        {
            ITile t = world[blockPos];

            if (!t.Solid || t.IsSpecial())
            {
                Log.Dump("Invalid action, bad tile", world.Info, player, blockPos);
                return false;
            }

            world.NET_RemoveBlock(blockPos);
            myHost.ConnectSendMessage(player.validatorHost, MessageType.PICKUP_BLOCK);

            return true;
        }

        void OnSpawnRequest(PlayerInfo player)
        {
            //Log.Dump();
            
            ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, player.id);

            if (!lck.Locked)
            {
                Log.Dump(world.Info, player, "Spawn failed, locked");
                return;
            }

            RemoteAction
                .Send(myHost, player.validatorHost, MessageType.LOCK_VAR)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if (res == Response.SUCCESS)
                    {
                        bool spawnSuccess = false;
                        
                        Guid remoteLockId = Serializer.Deserialize<Guid>(stm);
                        PlayerData pd = Serializer.Deserialize<PlayerData>(stm);

                        try
                        {
                            if (pd.IsConnected == true)
                            {
                                Log.Dump(world.Info, player, "Spawn failed, already spawned");
                                return;
                            }

                            var spawn = world.GetSpawn().ToList();

                            if (!spawn.Any())
                            {
                                Log.Dump(world.Info, player, "Spawn failed, no space");
                                return;
                            }

                            Point spawnPos = spawn.Random((n) => rand.Next(n));

                            world.NET_AddPlayer(player, spawnPos, true);
                            //myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_JOIN, player.id, spawnPos);
                            //myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_WORLD_MOVE, WorldMove.JOIN);

                            pd.world = world.Info;

                            //Log.Dump(world.Info, player, "Spawn success");
                            spawnSuccess = true;
                        }
                        finally
                        {
                            if(!spawnSuccess)
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.FAIL, remoteLockId);
                            else
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.SUCCESS, remoteLockId, pd);
                        }
                    }
                    else
                    {
                        Log.Console(Log.StDump(world.Info, player, "Spawn failed, remote lock rejected"));
                    }
                });
        }
        void OnMoveValidate(PlayerInfo inf, Point currPos, Point newPos)
        {
            ActionValidity v = world.CheckValidMove(inf.id, newPos);
            if (v != ActionValidity.VALID)
            {
                Log.Console("World {4}: Invalid move {0} from {1} to {2} by {3}", v,
                    currPos, newPos, inf.GetShortInfo(), world.Info.GetShortInfo());
                return;
            }

            world.NET_Move(inf.id, newPos, ActionValidity.VALID);
            //myHost.BroadcastGroup(Client.hostName, MessageType.MOVE, inf.id, newPos);
        }
        void OnValidateRealmMove(PlayerInfo player, Point currPos, Point newPos)
        {
            ActionValidity v = world.CheckValidMove(player.id, newPos);

            if (v != ActionValidity.BOUNDARY)
            {
                Log.Dump(world.Info, player, v, currPos, newPos, "invalid move");
                return;
            }

            ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, player.id);

            RemoteAction
                .Send(myHost, player.validatorHost, MessageType.LOCK_VAR)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if (res == Response.SUCCESS)
                    {
                        Guid remoteLockId = Serializer.Deserialize<Guid>(stm);
                        PlayerData pd = Serializer.Deserialize<PlayerData>(stm);

                        Action<Response> postProcess = (rs) =>
                        {
                            if (rs == Response.SUCCESS)
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.SUCCESS, remoteLockId, pd);
                            else
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.FAIL, remoteLockId);
                        };

                        RealmMoveOut(player, newPos, pd, false, postProcess);
                    }
                    else
                    {
                        Log.Dump("RealmMove failed, remote lock rejected", world.Info, player, currPos, newPos);
                    }
                });

            
        }

        class GetRealmException : Exception { public GetRealmException(string s) : base(s) { } }
        
        WorldInfo GetRemoteRealm(ref Point targetPos)
        {
            Point currentRealmPos = world.Position;

            RealmMove wm = WorldTools.BoundaryMove(targetPos, world.Position);
            targetPos = wm.newPosition;
            Point targetRealmPos = wm.newWorld;

            var a = from p in Point.SymmetricRange(Point.One)
                    where p != Point.Zero
                    where p + world.Position == targetRealmPos
                    select p;

            if (!a.Any())
                throw new GetRealmException(Log.StDump(currentRealmPos, targetRealmPos, targetPos, "realm too far"));

            MyAssert.Assert(currentRealmPos != targetRealmPos);
            WorldInfo? targetRealm = world.GetNeighbor(targetRealmPos);
            if (targetRealm == null)
            {
                throw new GetRealmException(Log.StDump(currentRealmPos, targetRealmPos, targetPos, "no realm to move in"));
            }

            return targetRealm.Value;
        }
        
        void RealmMoveOut(PlayerInfo player, Point newPos, PlayerData pd, bool teleporting, Action<Response> postProcess)
        {
            bool success = false;

            try
            {
                WorldInfo targetRealm = GetRemoteRealm(ref newPos);

                //Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "realm request"));

                ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, player.id);

                RemoteAction
                    .Send(myHost, targetRealm.host, MessageType.REALM_MOVE, player, newPos, teleporting)
                    .Respond(remoteActions, lck, (res, stm) =>
                    {
                        if (res == Response.SUCCESS)
                        {
                            //Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "realm move success"));

                            world.NET_RemovePlayer(player.id, teleporting);

                            pd.world = targetRealm;
                        }
                        else
                        {
                            Log.Dump(world.Info, player, targetRealm.position, newPos, res, "remote world refused");
                        }

                        postProcess.Invoke(res);
                    });

                success = true;
            }
            catch (GetRealmException e)
            {
                Log.Dump(world.Info, player, e.Message);
            }
            finally
            {
                if(success == false)
                    postProcess.Invoke(Response.FAIL);
            }
        }
        void RealmMoveIn(PlayerInfo player, Point newPos, bool teleporting, Node n, Guid remoteActionId)
        {
            bool success = false;

            try
            {
                if (playerLocks.Contains(player.id))
                {
                    Log.Console(Log.StDump(world.Info, player, "can't join, locked"));
                    return;
                }

                MyAssert.Assert(!world.HasPlayer(player.id));

                ITile t = world[newPos];

                if (!t.IsMoveable())
                {
                    ActionValidity mv = ActionValidity.VALID;

                    if (t.PlayerId != Guid.Empty)
                        mv = ActionValidity.OCCUPIED_PLAYER;
                    else if (t.Solid)
                        mv = ActionValidity.OCCUPIED_WALL;
                    else
                        throw new Exception(Log.StDump(world.Info, player, mv, "bad tile status"));

                    Log.Console(Log.StDump(world.Info, player, mv, "can't join, blocked"));

                    return;
                }

                world.NET_AddPlayer(player, newPos, teleporting);
                //myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_WORLD_MOVE, WorldMove.JOIN);

                success = true;
            }
            finally
            {
                if(success)
                    RemoteAction.Sucess(n, remoteActionId);
                else
                    RemoteAction.Fail(n, remoteActionId);
            }
        }
        void OnValidateTeleport(PlayerInfo player, Point currPos, Point newPos)
        {
            ActionValidity v = world.CheckValidMove(player.id, newPos) & ~(ActionValidity.REMOTE | ActionValidity.BOUNDARY);
            //ActionValidity v = ActionValidity.VALID;// world.CheckValidMove(player.id, newPos) & ~(ActionValidity.REMOTE);

            if (v != ActionValidity.VALID)
            {
                Log.Dump("Invalid teleport (check 1)", world.Info, player, v, currPos, newPos);
                return;
            }

            //Log.Dump("Requesting teleport", world.info, player, currPos, newPos);

            ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, player.id);

            RemoteAction
                .Send(myHost, player.validatorHost, MessageType.LOCK_VAR)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if (res == Response.SUCCESS)
                    {
                        Guid remoteLockId = Serializer.Deserialize<Guid>(stm);
                        PlayerData pd = Serializer.Deserialize<PlayerData>(stm);

                        Action<Response> postProcess = (rs) =>
                        {
                            if (rs == Response.SUCCESS)
                            {
                                --pd.inventory.teleport;
                                MyAssert.Assert(pd.inventory.teleport >= 0);

                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.SUCCESS, remoteLockId, pd);
                            }
                            else
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.FAIL, remoteLockId);
                        };

                        MyAssert.Assert(pd.inventory.teleport >= 0);
                        if (pd.inventory.teleport == 0)
                        {
                            Log.Dump("Teleport failed, not enough items", world.Info, player, currPos, newPos);
                            postProcess.Invoke(Response.FAIL);
                            return;
                        }

                        Teleport(player, currPos, newPos, pd, postProcess);
                    }
                    else
                    {
                        Log.Dump("Teleport failed, remote lock rejected", world.Info, player, currPos, newPos);
                    }
                });
        }
        void Teleport(PlayerInfo player, Point currPos, Point newPos, PlayerData pd, Action<Response> postProcess)
        {
            ActionValidity v = world.CheckValidMove(player.id, newPos) & ~(ActionValidity.REMOTE);

            if (v != ActionValidity.VALID)
            {
                if (v == ActionValidity.BOUNDARY) // not done yet - realm teleport
                {
                    //Log.Dump("Realm teleport request", world.info, player, v, currPos, newPos);

                    RealmMoveOut(player, newPos, pd, true, (res) =>
                    {
                        if (res == Response.SUCCESS)
                        {
                            //Log.Dump("Realm teleport success", world.Info, player, v, currPos, newPos);
                            postProcess.Invoke(Response.SUCCESS);
                        }
                        else
                        {
                            Log.Dump("Realm teleport fail", world.Info, player, v, currPos, newPos);
                            postProcess.Invoke(Response.FAIL);
                        }
                    });
                }
                else
                {
                    // teleporting fail
                    Log.Dump("Invalid teleport (check 2)", world.Info, player, v, currPos, newPos);
                    postProcess.Invoke(Response.FAIL);
                }
            }
            else // teleporting success
            {
                //Log.Dump("Teleported", world.info, player, currPos, newPos);
                world.NET_Move(player.id, newPos, ActionValidity.REMOTE);
                //myHost.BroadcastGroup(Client.hostName, MessageType.TELEPORT_MOVE, player.id, newPos);

                postProcess.Invoke(Response.SUCCESS);
            }
        }
        void OnLootPickup(Point p, PlayerInfo inf)
        {
            myHost.ConnectSendMessage(inf.validatorHost, MessageType.PICKUP_TELEPORT);
        }

        void BoundaryRequest()
        {
            foreach (Point p in world.GetUnknownNeighbors())
                myHost.ConnectSendMessage(serverHost, MessageType.NEW_WORLD_REQUEST, p);
        }

        void RemoteFunctionForward(ForwardFunctionCall ffc)
        {
            object[] ffc_ser = ffc.Serialize();
            
            foreach (OverlayEndpoint remote in subscribers)
                myHost.SendMessage(remote, MessageType.WORLD_VAR_CHANGE, ffc_ser);

            ffc.Apply(worldHook);
        }

        public void FinalizeWorld()
        {
            finalizing = true;

            Log.Dump(remoteActions.Count);

            remoteActions.TriggerOnEmpty(OnFinalizedWorld);
        }

        void OnFinalizedWorld()
        {
            Log.Dump(world.Info.GetShortInfo(), "players", world.GetAllPlayers().Count());

            MyAssert.Assert(!playerLocks.Any());

            foreach (var player in world.GetAllPlayers().ToArray())
            {
                world.NET_RemovePlayer(player.id, false);
                myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_DISCONNECT);
            }

            Log.Dump("all player disconnects sent");

            //all.worldValidators.Remove(world.Position);

            myHost.ConnectSendMessage(serverHost, MessageType.WORLD_HOST_DISCONNECT, world.Serialize());
        }
    }
}
