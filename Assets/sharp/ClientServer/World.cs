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
using System.Xml.Serialization;

namespace ServerClient
{
    interface ITile
    {
        Point Position { get; }

        bool Solid { get; }
        bool Loot { get; }
        bool Spawn { get; }

        Guid PlayerId { get; }

        bool IsEmpty();
    }

    [Serializable]
    public class Tile : ITile
    {
        public Point Position { get; set; }

        public bool Solid { get; set; }
        public bool Loot { get; set; }
        public bool Spawn { get; set; }

        public Guid PlayerId { get; set; }

        public bool IsEmpty() { return (PlayerId == Guid.Empty) && !Solid; }

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
        public int seed;
        public double wallDensity = .2;
        public double lootDensity = .05;
        public bool hasSpawn = false;

        public WorldInitializer() { }
        internal WorldInitializer(int seed_)
        {
            seed = seed_;
        }
    }

    [Serializable]
    public struct WorldInfo
    {
        public Point worldPos;
        public OverlayEndpoint host;

        public WorldInfo(Point worldPos_, OverlayEndpoint host_)
        {
            worldPos = worldPos_;
            host = host_;
        }

        public override string ToString()
        {
            return GetShortInfo();
        }

        public string GetFullInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("World pos: {0}\n", worldPos);
            sb.AppendFormat("Validator host: {0}\n", host);

            return sb.ToString();
        }
        public string GetShortInfo() { return "World " + worldPos.ToString(); }
    }

    public enum MoveValidity
    {
        VALID = 0,
        BOUNDARY = 1,
        OCCUPIED_PLAYER = 2,
        OCCUPIED_WALL = 4,
        TELEPORT = 8,
        NEW = 16
    };

    [Serializable]
    public class WorldSerialized
    {
        public Plane<Tile> map;
        public WorldInfo myInfo;
    }

    class World : MarshalByRefObject
    {
        // ----- constructors -----
        public World(WorldSerialized ws, GameInfo info_) : this(ws.myInfo, info_)
        {
            map = ws.map;

            foreach (Point p in Point.Range(map.Size))
                if (map[p].PlayerId != Guid.Empty)
                    playerPositions.Add(map[p].PlayerId, p);
        }
        public WorldSerialized Serialize()
        {
            return new WorldSerialized() { map = map, myInfo = Info };
        }
        
        // ----- read only infromation -----
        public WorldInfo Info { get; private set; }

        public Point Position { get { return Info.worldPos; } }
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
                    if (t.IsEmpty())
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
        public IEnumerable<KeyValuePair<Guid, Point>> GetAllPlayerPositions()
        {
            return playerPositions;
        }
        public IEnumerable<Guid> GetAllPlayers()
        {
            return playerPositions.Keys;
        }
        public bool HasPlayer(Guid id) { return playerPositions.ContainsKey(id); }

        public MoveValidity CheckValidMove(Guid player, Point newPos)
        {
            MoveValidity mv = MoveValidity.VALID;

            Point curPos = playerPositions.GetValue(player);

            Point diff = newPos - curPos;
            if (Math.Abs(diff.x) > 1)
                mv |= MoveValidity.TELEPORT;
            if (Math.Abs(diff.y) > 1)
                mv |= MoveValidity.TELEPORT;

            Tile tile;
            try
            {
                tile = map[newPos];
            }
            catch (IndexOutOfRangeException)
            {
                mv |= MoveValidity.BOUNDARY;
                return mv;
            }

            if (!tile.IsEmpty())
            {
                if (tile.PlayerId != Guid.Empty)
                    mv |= MoveValidity.OCCUPIED_PLAYER;
                if (tile.Solid)
                    mv |= MoveValidity.OCCUPIED_WALL;
            }

            return mv;
        }

        // ----- modifiers -----
        [Forward] public void NET_AddPlayer(Guid player, Point pos)
        {
            MyAssert.Assert(!playerPositions.ContainsKey(player));
            NET_Move(player, pos, MoveValidity.NEW);
        }
        [Forward] public void NET_RemovePlayer(Guid player)
        {
            Point pos = playerPositions.GetValue(player);
            MyAssert.Assert(map[pos].PlayerId == player);
            map[pos].PlayerId = Guid.Empty;

            playerPositions.Remove(player);

            onPlayerLeaveHook(gameInfo.GetPlayerById(player));
        }
        [Forward] public void NET_Move(Guid player, Point newPos, MoveValidity mv)
        {
            PlayerInfo p = gameInfo.GetPlayerById(player);

            if (mv != MoveValidity.NEW)
            {
                Point curPos = playerPositions.GetValue(player);

                MoveValidity v = CheckValidMove(player, newPos) & ~mv;
                MyAssert.Assert(v == MoveValidity.VALID);

                //if (v != MoveValidity.VALID)
                    //Log.LogWriteLine("Game.Move Warning: Invalid move {0} from {1} to {2} by {3}", v, curPos, newPos, p.name);

                MyAssert.Assert(map[curPos].PlayerId == player);
                map[curPos].PlayerId = Guid.Empty;
            }

            Tile tile = map[newPos];
            MyAssert.Assert(tile.IsEmpty());

            if (tile.Loot == true)
                onLootHook(p);
            
            playerPositions[player] = newPos;
            tile.PlayerId = player;
            tile.Loot = false;

            //Log.Dump(player, newPos, mv);
            onMoveHook(p, newPos, mv);
        }

        // ----- hooks -----
        public Action<PlayerInfo> onLootHook = (info) => { };
        public Action<PlayerInfo, Point, MoveValidity> onMoveHook = (a, b, c) => { };
        public Action<PlayerInfo> onPlayerLeaveHook = (a) => { };

        // ----- generating -----
        static public World Generate(WorldInfo myInfo_, WorldInitializer init, GameInfo info_)
        {
            World w = new World(myInfo_, info_);

            w.map = new Plane<Tile>(worldSize);

            foreach (Point p in Point.Range(w.map.Size))
                w.map[p] = new Tile(p);

            w.Generate(init);

            return w;
        }

        static public readonly Point worldSize = new Point(25, 15);
        private void Generate(WorldInitializer init)
        {
            ServerClient.MyRandom seededRandom = new ServerClient.MyRandom(init.seed);

            // random terrain
            foreach (Tile t in map.GetTiles())
            {
                if (seededRandom.NextDouble() < init.wallDensity)
                    t.Solid = true;
                else
                {
                    t.Solid = false;

                    if (seededRandom.NextDouble() < init.lootDensity)
                        t.Loot = true;
                }
            }

            if (init.hasSpawn)
            {
                var a = (from t in map.GetEnum()
                         where !t.Value.IsEmpty()
                         select t).ToList();

                if (!a.Any())
                    a = map.GetEnum().ToList();

                MyAssert.Assert(a.Any());

                var spawn = a.Random(n => seededRandom.Next(n));
                spawn.Value.Spawn = true;
                spawnPos = spawn.Key;
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
        private World(WorldInfo myInfo_, GameInfo info_)
        {
            Info = myInfo_;
            gameInfo = info_;
        }

        Plane<Tile> map;
        Dictionary<Guid, Point> playerPositions = new Dictionary<Guid, Point>();
        Point? spawnPos = null;

        GameInfo gameInfo;
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
        static public void ConsoleOut(World w, GameInfo gameInfo)
        {
            Plane<char> pic = new Plane<char>(w.Size);

            foreach (ITile t in w.GetAllTiles())
            {
                Point p = t.Position;

                if (t.Solid)
                    pic[p] = '*';
                else if (t.PlayerId != Guid.Empty)
                    pic[p] = gameInfo.GetPlayerById(t.PlayerId).name[0];
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
        Random rand = new Random();

        World world;
        OverlayHost myHost;

        GameInfo gameInfo;
        OverlayEndpoint serverHost;

        Dictionary<Guid, RemoteAction> remoteActions = new Dictionary<Guid,RemoteAction>();

        HashSet<Guid> playerLocks = new HashSet<Guid>();

        public WorldValidator(WorldInfo info, WorldInitializer init, GlobalHost globalHost, GameInfo gameInfo_, OverlayEndpoint serverHost_)
        {
            gameInfo = gameInfo_;
            serverHost = serverHost_;

            World newWorld = World.Generate(info, init, gameInfo);

            Action<ForwardFunctionCall> onChange = (ffc) => myHost.BroadcastGroup(Client.hostName, MessageType.WORLD_VAR_CHANGE, ffc.Serialize());
            world = new ForwardProxy<World>(newWorld, onChange).GetProxy();

            myHost = globalHost.NewHost(info.host.hostname, AssignProcessor,
                OverlayHost.GenerateHandshake(NodeRole.WORLD_VALIDATOR, info));
            myHost.onNewConnectionHook = ProcessNewConnection;

            newWorld.onLootHook = OnLootPickup;
            newWorld.onMoveHook = OnMoveHook;
        }

        Node.MessageProcessor AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (role == NodeRole.CLIENT)
                return (mt, stm, nd) => { throw new Exception(Log.StDump( n.info, mt, "not expecting messages")); };

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
        
        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
        }

        void OnNewClient(Node n)
        {
            n.SendMessage(MessageType.WORLD_VAR_INIT, world.Serialize());
        }

        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.RESPONSE)
                RemoteAction.Process(remoteActions, n, stm);
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessWorldValidatorMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.RESPONSE)
                RemoteAction.Process(remoteActions, n, stm);
            else if (mt == MessageType.REALM_MOVE)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);
                Guid player = Serializer.Deserialize<Guid>(stm);
                Point newPos = Serializer.Deserialize<Point>(stm);
                OnRealmMove(gameInfo.GetPlayerById(player), newPos, n, remoteActionId);
            }
            else
                throw new Exception(Log.StDump( mt, "bad message type"));
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.MOVE_REQUEST)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);
                MoveValidity mv = Serializer.Deserialize<MoveValidity>(stm);

                if(mv == MoveValidity.VALID)
                    OnMoveValidate(inf, newPos);
                else if(mv == MoveValidity.BOUNDARY)
                    OnValidateRealmMove(inf, newPos);
                else if(mv == MoveValidity.TELEPORT)
                    OnValidateTeleport(inf, newPos);
                else
                    throw new Exception(Log.StDump(mt, inf, newPos, mv, "unexpected move request"));
            }
            else
                throw new Exception(Log.StDump(mt, inf, "unexpected message"));
        }
        void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.SPAWN_REQUEST)
            {
                Guid playerId = Serializer.Deserialize<Guid>(stm);
                OnSpawnRequest(gameInfo.GetPlayerById(playerId));
            }
            else
                throw new Exception(Log.StDump("unexpected", world.Info, mt));
        }

        void OnMoveHook(PlayerInfo inf, Point newPos, MoveValidity mv)
        {
            //Log.Dump(inf, newPos, mv);
            
            if (mv == MoveValidity.NEW)
            {
                BoundaryRequest();
            }
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
                            if (pd.connected == true)
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

                            world.NET_AddPlayer(player.id, spawnPos);
                            //myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_JOIN, player.id, spawnPos);
                            //myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_WORLD_MOVE, WorldMove.JOIN);

                            pd.connected = true;
                            pd.worldPos = world.Position;

                            //Log.Dump(world.Info, player, "Spawn success");
                            spawnSuccess = true;
                        }
                        finally
                        {
                            if(!spawnSuccess)
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.FAIL, remoteLockId);
                            else
                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.SUCCESS, remoteLockId, PlayerDataUpdate.SPAWN, pd);
                        }
                    }
                    else
                    {
                        Log.LogWriteLine(Log.StDump(world.Info, player, "Spawn failed, remote lock rejected"));
                    }
                });
        }
        void OnMoveValidate(PlayerInfo inf, Point newPos)
        {
            if (playerLocks.Contains(inf.id))
            {
                Log.LogWriteLine("World {0}: {1} can't move, locked", world.Info.GetShortInfo(), inf.GetShortInfo());
                return;
            }

            if (!world.HasPlayer(inf.id))
            {
                Log.LogWriteLine("World {0}: Invalid move {1} by {2}: player absent from this world",
                    world.Info.GetShortInfo(), newPos, inf.GetShortInfo());
                return;
            }
            
            Point currPos = world.GetPlayerPosition(inf.id);

            MoveValidity v = world.CheckValidMove(inf.id, newPos);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("World {4}: Invalid move {0} from {1} to {2} by {3}", v,
                    currPos, newPos, inf.GetShortInfo(), world.Info.GetShortInfo());
                return;
            }

            world.NET_Move(inf.id, newPos, MoveValidity.VALID);
            //myHost.BroadcastGroup(Client.hostName, MessageType.MOVE, inf.id, newPos);
        }
        void OnValidateRealmMove(PlayerInfo player, Point newPos) { OnValidateRealmMove(player, newPos, false, (mt) => { }); }
        void OnValidateRealmMove(PlayerInfo player, Point newPos, bool teleporting, Action<Response> postProcess)
        {
            bool success = false;

            try
            {
                if (playerLocks.Contains(player.id))
                {
                    Log.LogWriteLine(Log.StDump(world.Info, player, "can't move, locked"));
                    return;
                }

                if (!world.HasPlayer(player.id))
                {
                    Log.LogWriteLine(Log.StDump(world.Info, player, "can't move, not in the world"));
                    return;
                }
                
                Point currPos = world.GetPlayerPosition(player.id);

                MoveValidity v = world.CheckValidMove(player.id, newPos);

                if (teleporting)
                    v &= ~MoveValidity.TELEPORT;

                if (v != MoveValidity.BOUNDARY)
                {
                    Log.LogWriteLine(Log.StDump(world.Info, player, v, currPos, newPos, "invalid move"));
                    return;
                }

                Point currentRealmPos = world.Position;

                RealmMove wm = WorldTools.BoundaryMove(newPos, world.Position);
                newPos = wm.newPosition;
                Point targetRealmPos = wm.newWorld;

                MyAssert.Assert(currentRealmPos != targetRealmPos);
                WorldInfo? targetRealm = gameInfo.TryGetWorldByPos(targetRealmPos);
                if (targetRealm == null)
                {
                    Log.LogWriteLine(Log.StDump(world.Info, player, currentRealmPos, targetRealmPos, newPos, "no realm to move in"));
                    return;
                }

                //Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "realm request"));

                ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, player.id);

                RemoteAction
                    .Send(myHost, targetRealm.Value.host, MessageType.REALM_MOVE, player.id, newPos)
                    .Respond(remoteActions, lck, (res, stm) =>
                    {
                        if (res == Response.SUCCESS)
                        {
                            //Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "realm move success"));

                            world.NET_RemovePlayer(player.id);
                            //myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_LEAVE, player.id, targetRealmPos);
                            myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_WORLD_MOVE, WorldMove.LEAVE, targetRealmPos);
                        }
                        else
                        {
                            Log.LogWriteLine(Log.StDump(world.Info, player, currentRealmPos, targetRealmPos, newPos, res, "remote world refused"));
                        }

                        postProcess.Invoke(res);
                    });

                success = true;
                return;
            }
            finally
            {
                if(success == false)
                    postProcess.Invoke(Response.FAIL);
            }
        }

        void OnRealmMove(PlayerInfo player, Point newPos, Node n, Guid remoteActionId)
        {
            bool success = false;

            try
            {
                if (playerLocks.Contains(player.id))
                {
                    Log.LogWriteLine(Log.StDump(world.Info, player, "can't join, locked"));
                    return;
                }

                ITile t = world[newPos];

                if (!t.IsEmpty())
                {
                    MoveValidity mv = MoveValidity.VALID;

                    if (t.PlayerId != Guid.Empty)
                        mv = MoveValidity.OCCUPIED_PLAYER;
                    else if (t.Solid)
                        mv = MoveValidity.OCCUPIED_WALL;
                    else
                        throw new Exception(Log.StDump(world.Info, player, mv, "bad tile status"));

                    Log.LogWriteLine(Log.StDump(world.Info, player, mv, "can't join, blocked"));

                    return;
                }

                world.NET_AddPlayer(player.id, newPos);
                myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_WORLD_MOVE, WorldMove.JOIN);

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

        void OnValidateTeleport(PlayerInfo player, Point newPos)
        {
            if (playerLocks.Contains(player.id))
            {
                Log.LogWriteLine(Log.StDump( world.Info, player, "can't teleport, locked"));
                return;
            }

            if (!world.HasPlayer(player.id))
            {
                Log.LogWriteLine(Log.StDump(world.Info, player, "can't teleport, not in the world"));
                return;
            }

            Point currPos = world.GetPlayerPosition(player.id);
            MoveValidity v = world.CheckValidMove(player.id, newPos) & ~(MoveValidity.TELEPORT | MoveValidity.BOUNDARY);
            //MoveValidity v = MoveValidity.VALID;// world.CheckValidMove(player.id, newPos) & ~(MoveValidity.TELEPORT);

            if (v != MoveValidity.VALID)
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

                                myHost.ConnectSendMessage(player.validatorHost, MessageType.UNLOCK_VAR, Response.SUCCESS, remoteLockId,
                                    PlayerDataUpdate.INVENTORY_CHANGE, pd);
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
            MoveValidity v = world.CheckValidMove(player.id, newPos) & ~(MoveValidity.TELEPORT);

            if (v != MoveValidity.VALID)
            {
                if (v == MoveValidity.BOUNDARY) // not done yet - realm teleport
                {
                    //Log.Dump("Realm teleport request", world.info, player, v, currPos, newPos);

                    OnValidateRealmMove(player, newPos, true, (res) =>
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
                world.NET_Move(player.id, newPos, MoveValidity.TELEPORT);
                //myHost.BroadcastGroup(Client.hostName, MessageType.TELEPORT_MOVE, player.id, newPos);

                postProcess.Invoke(Response.SUCCESS);
            }
        }

        void OnLootPickup(PlayerInfo inf)
        {
            myHost.ConnectSendMessage(inf.validatorHost, MessageType.PICKUP_ITEM);
        }

        void BoundaryRequest()
        {
            //Log.Dump();
            Point myPosition = world.Position;
            foreach (Point delta in Point.SymmetricRange(new Point(1, 1)))
            {
                Point newPos = myPosition + delta;

                if (gameInfo.TryGetWorldByPos(newPos) == null)
                {
                    //Log.Dump(newPos);
                    myHost.ConnectSendMessage(serverHost, MessageType.NEW_WORLD_REQUEST, newPos);
                }
            }
        }
    }
}
