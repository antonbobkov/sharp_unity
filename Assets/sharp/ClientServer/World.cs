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
    [Serializable]
    public class Tile
    {
        public bool solid = false;
        public bool loot = false;
        public bool spawn = false;

        [XmlIgnoreAttribute]
        public Guid player = Guid.Empty;

        public string PlayerGuid
        {
            get
            {
                if (player != Guid.Empty)
                    return player.ToString();
                else
                    return "";
            }

            set
            {
                if (value != "")
                    player = new Guid(value);
                else
                    player = Guid.Empty;
            }
        }

        public bool IsEmpty() { return (player == Guid.Empty) && !solid; }

        public Tile() { }
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
    public class WorldInfo
    {
        public Point worldPos;
        public OverlayEndpoint host;

        public WorldInfo() { }
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

    class WorldMove
    {
        public Point newWorld;
        public Point newPosition;
    }
    
    class World
    {
        static readonly Point worldSize = new Point(25, 15);

        public readonly WorldInfo info;
        public Plane<Tile> map;

        public Point Position { get { return info.worldPos; } }

        public Dictionary<Guid, Point> playerPositions = new Dictionary<Guid,Point>();
        GameInfo gameInfo;

        public Action<PlayerInfo> onLootHook = (info) => { };
        public Action<PlayerInfo, MoveType> onMoveHook = (a,b) => { };

        public World(WorldSerialized ws, GameInfo info_)
        {
            info = ws.myInfo;
            gameInfo = info_;
            map = ws.map;

            foreach (Point p in Point.Range(map.Size))
                if (map[p].player != Guid.Empty)
                    playerPositions.Add(map[p].player, p);
        }
        public World(WorldInfo myInfo_, WorldInitializer init, GameInfo info_)
        {
            info = myInfo_;
            gameInfo = info_;

            map = new Plane<Tile>(worldSize);

            foreach (Point p in Point.Range(map.Size))
                map[p] = new Tile();

            Generate(init);
        }

        public WorldSerialized Serialize()
        {
            return new WorldSerialized() { map = map, myInfo = info };
        }

        public IEnumerable<Point> GetBoundary()
        {
            HashSet<Point> bnd = new HashSet<Point>();

            for (int x = 0; x < map.Size.x; ++x)
            {
                bnd.Add(new Point(x, 0));
                bnd.Add(new Point(x, map.Size.y - 1));
            }

            for (int y = 0; y < map.Size.y; ++y)
            {
                bnd.Add(new Point(0, y));
                bnd.Add(new Point(map.Size.x - 1, y));
            }

            return from p in bnd
                   orderby p.ToString()
                   select p;
        }
        public IEnumerable<KeyValuePair<Point, Tile>> GetSpawn()
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
                        yield return new KeyValuePair<Point, Tile>(p, t);
                }
            }
        }

        Point? spawnPos = null;

        void Generate(WorldInitializer init)
        {
            ServerClient.MyRandom seededRandom = new ServerClient.MyRandom(init.seed);

            // random terrain
            foreach (Tile t in map.GetTiles())
            {
                if (seededRandom.NextDouble() < init.wallDensity)
                    t.solid = true;
                else
                {
                    t.solid = false;

                    if (seededRandom.NextDouble() < init.lootDensity)
                        t.loot = true;
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
                spawn.Value.spawn = true;
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
        public void ConsoleOut()
        {
            Plane<char> pic = new Plane<char>(map.Size);

            foreach (var kv in map.GetEnum())
            {
                Point p = kv.Key;
                Tile t = kv.Value;

                if (t.solid)
                    pic[p] = '*';
                else if (t.player != Guid.Empty)
                    pic[p] = gameInfo.GetPlayerById(t.player).name[0];
                else if (t.loot)
                    pic[p] = '$';
            }

            for (int y = map.Size.y - 1; y >= 0; --y)
            {
                for (int x = 0; x < map.Size.x; ++x)
                    Console.Write(pic[x, y]);
                Console.WriteLine();
            }
        }
        
        public void AddPlayer(Guid player, Point pos)
        {
            MyAssert.Assert(!playerPositions.ContainsKey(player));
            Move(player, pos, MoveValidity.NEW);
        }
        public void RemovePlayer(Guid player)
        {
            Point pos = playerPositions.GetValue(player);
            MyAssert.Assert(map[pos].player == player);
            map[pos].player = Guid.Empty;

            playerPositions.Remove(player);

            onMoveHook(gameInfo.GetPlayerById(player), MoveType.LEAVE);
        }

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
                if (tile.player != Guid.Empty)
                    mv |= MoveValidity.OCCUPIED_PLAYER;
                if (tile.solid)
                    mv |= MoveValidity.OCCUPIED_WALL;
            }

            return mv;
        }
        public void Move(Guid player, Point newPos, MoveValidity mv = MoveValidity.VALID)
        {
            PlayerInfo p = gameInfo.GetPlayerById(player);

            if (mv != MoveValidity.NEW)
            {
                Point curPos = playerPositions.GetValue(player);

                MoveValidity v = CheckValidMove(player, newPos) & ~mv;
                if (v != MoveValidity.VALID)
                    Log.LogWriteLine("Game.Move Warning: Invalid move {0} from {1} to {2} by {3}",
                        v, curPos, newPos, p.name);

                MyAssert.Assert(map[curPos].player == player);
                map[curPos].player = Guid.Empty;
            }

            Tile tile = map[newPos];
            MyAssert.Assert(tile.IsEmpty());

            if (tile.loot == true)
                onLootHook(p);
            
            playerPositions[player] = newPos;
            tile.player = player;
            tile.loot = false;

            MoveType mt = mv == MoveValidity.NEW ? MoveType.JOIN : MoveType.MOVE;
            
            onMoveHook(p, mt);
        }

        int BoundaryMove(ref int p, int sz)
        {
            int ret = 0;

            while (p < 0)
            {
                p += sz;
                ret--;
            }
            
            while(p >= sz)
            {
                p -= sz;
                ret++;
            }

            return ret;
        }
        public WorldMove BoundaryMove(Point p)
        {
            int px = p.x;
            int py = p.y;

            Point ret = new Point(BoundaryMove(ref px, map.Size.x), BoundaryMove(ref py, map.Size.y));

            p = new Point(px, py);

            return new WorldMove() { newPosition = p, newWorld = ret + Position };
        }
        
    }

    class WorldValidator
    {
        Random rand = new Random();

        World world;
        Action<Action> sync;
        OverlayHost myHost;

        GameInfo gameInfo;
        OverlayEndpoint serverHost;

        Dictionary<Guid, Action<MessageType>> playerLocks = new Dictionary<Guid, Action<MessageType>>();
        void FinishLock(Guid id, MessageType mt)
        {
            var act = playerLocks.GetValue(id);
            
            playerLocks.Remove(id);
            
            act.Invoke(mt);
        }

        public WorldValidator(WorldInfo info, WorldInitializer init, Action<Action> sync_, GlobalHost globalHost, GameInfo gameInfo_, OverlayEndpoint serverHost_)
        {
            gameInfo = gameInfo_;
            sync = sync_;
            serverHost = serverHost_;

            world = new World(info, init, gameInfo);

            myHost = globalHost.NewHost(info.host.hostname, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;

            world.onLootHook = OnLootPickup;
        }
        
        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return (mt, stm, nd) => { throw new Exception(Log.StDump( n.info, mt, "not expecting messages")); };
            if (remoteName == Server.hostName)
                return ProcessServerMessage;
            
            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = gameInfo.GetPlayerByHost(n.info.remote);
                return (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf);
            }
            
            if (role == NodeRole.PLAYER)
            {
                PlayerInfo inf = gameInfo.GetPlayerByHost(n.info.remote);
                return (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf);
            }

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = gameInfo.GetWorldByHost(n.info.remote);
                return (mt, stm, nd) => ProcessWorldValidatorMessage(mt, stm, nd, inf);
            }

            throw new InvalidOperationException(Log.StDump( n.info, role, "unexpected connection"));
        }
        
        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
        }

        void OnNewClient(Node n)
        {
            n.SendMessage(MessageType.WORLD_INIT, world.Serialize());
        }

        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_FAIL || mt == MessageType.SPAWN_SUCCESS)
                sync.Invoke(() => FinishLock(inf.id, mt));
            else if(mt == MessageType.FREEZE_FAIL || mt == MessageType.FREEZE_SUCCESS)
                sync.Invoke(() => FinishLock(inf.id, mt));
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessWorldValidatorMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.REALM_MOVE_FAIL || mt == MessageType.REALM_MOVE_SUCCESS)
            {
                Guid id = Serializer.Deserialize<Guid>(stm);
                sync.Invoke(() => FinishLock(id, mt));
            }
            else if (mt == MessageType.REALM_MOVE)
            {
                Guid player = Serializer.Deserialize<Guid>(stm);
                Point newPos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnRealmMove(gameInfo.GetPlayerById(player), newPos, n));
            }
            else
                throw new Exception(Log.StDump( mt, "bad message type"));
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.MOVE)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnMoveValidate(inf, newPos));
            }
            else if (mt == MessageType.REALM_MOVE)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnValidateRealmMove(inf, newPos));
            }
            else if (mt == MessageType.TELEPORT_MOVE)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnValidateTeleport(inf, newPos));
            }
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.SPAWN_REQUEST)
            {
                Guid playerId = Serializer.Deserialize<Guid>(stm);
                sync.Invoke(() => OnSpawnRequest(gameInfo.GetPlayerById(playerId)));
            }
            else
                throw new Exception(Log.StDump("unexpected", world.info, mt));
        }

        void AddPlayer(PlayerInfo player, Point newPos)
        {
            if (!world.playerPositions.Any())   // on first spawn
                BoundaryRequest();

            world.AddPlayer(player.id, newPos);        
        }

        void OnSpawnRequest(PlayerInfo player)
        {
            if(playerLocks.ContainsKey(player.id))
            {
                Log.LogWriteLine(Log.StDump( world.info, player, "Spawn failed, locked"));
                return;
            }
            
            myHost.ConnectSendMessage(player.validatorHost, MessageType.SPAWN_REQUEST);

            playerLocks.Add(player.id, (mt) =>
                {
                    if (mt == MessageType.SPAWN_FAIL)
                    {
                        Log.LogWriteLine(Log.StDump( world.info, player, "Spawn failed, SPAWN_FAIL"));
                    }
                    else if (mt == MessageType.SPAWN_SUCCESS)
                    {
                        var spawn = world.GetSpawn().ToList();

                        if (!spawn.Any())
                        {
                            Log.Dump(world.info, player, "Spawn failed, no space");
                            myHost.ConnectSendMessage(player.validatorHost, MessageType.SPAWN_FAIL);
                            return;
                        }

                        Point spawnPos = spawn.Random((n) => rand.Next(n)).Key;

                        AddPlayer(player, spawnPos);

                        myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_JOIN, player.id, spawnPos);
                        myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_JOIN);
                    }
                    else
                        throw new Exception(Log.StDump( world.info, mt, "unexpected"));
                });
        }
        void OnMoveValidate(PlayerInfo inf, Point newPos)
        {
            if (playerLocks.ContainsKey(inf.id))
            {
                Log.LogWriteLine("World {0}: {1} can't move, locked", world.info.GetShortInfo(), inf.GetShortInfo());
                return;
            }

            if (!world.playerPositions.ContainsKey(inf.id))
            {
                Log.LogWriteLine("World {0}: Invalid move {1} by {2}: player absent from this world",
                    world.info.GetShortInfo(), newPos, inf.GetShortInfo());
                return;
            }
            
            Point currPos = world.playerPositions.GetValue(inf.id);

            MoveValidity v = world.CheckValidMove(inf.id, newPos);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("World {4}: Invalid move {0} from {1} to {2} by {3}", v,
                    currPos, newPos, inf.GetShortInfo(), world.info.GetShortInfo());
                return;
            }

            world.Move(inf.id, newPos, MoveValidity.VALID);

            myHost.BroadcastGroup(Client.hostName, MessageType.MOVE, inf.id, newPos);
        }
        void OnValidateRealmMove(PlayerInfo player, Point newPos) { OnValidateRealmMove(player, newPos, false, (mt) => { }); }
        void OnValidateRealmMove(PlayerInfo player, Point newPos, bool teleporting, Action<MessageType> postProcess)
        {
            bool success = false;

            try
            {
                if (playerLocks.ContainsKey(player.id))
                {
                    Log.LogWriteLine(Log.StDump(world.info, player, "can't move, locked"));
                    return;
                }

                Point currPos = world.playerPositions.GetValue(player.id);

                MoveValidity v = world.CheckValidMove(player.id, newPos);

                if (teleporting)
                    v &= ~MoveValidity.TELEPORT;

                if (v != MoveValidity.BOUNDARY)
                {
                    Log.LogWriteLine(Log.StDump(world.info, player, v, currPos, newPos, "invalid move"));
                    return;
                }

                Point currentRealmPos = world.Position;

                WorldMove wm = world.BoundaryMove(newPos);
                newPos = wm.newPosition;
                Point targetRealmPos = wm.newWorld;

                MyAssert.Assert(currentRealmPos != targetRealmPos);
                WorldInfo targetRealm = gameInfo.TryGetWorldByPos(targetRealmPos);
                if (targetRealm == null)
                {
                    Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "no realm to move in"));
                    return;
                }

                Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "realm request"));

                myHost.ConnectSendMessage(targetRealm.host, MessageType.REALM_MOVE, player.id, newPos);

                playerLocks.Add(player.id, (mt) =>
                {
                    if (mt == MessageType.REALM_MOVE_FAIL)
                    {
                        Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "realm move fail"));
                    }
                    else if (mt == MessageType.REALM_MOVE_SUCCESS)
                    {
                        Log.LogWriteLine(Log.StDump(world.info, player, currentRealmPos, targetRealmPos, newPos, "realm move success"));

                        world.RemovePlayer(player.id);
                        myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_LEAVE, player.id, targetRealmPos);
                        myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_LEAVE, targetRealmPos);
                    }
                    else
                        throw new Exception(Log.StDump(world.info, mt, "unexpected"));

                    postProcess.Invoke(mt);
                });

                success = true;
                return;
            }
            finally
            {
                if(success == false)
                    postProcess.Invoke(MessageType.REALM_MOVE_FAIL);
            }
        }

        void OnRealmMove(PlayerInfo player, Point newPos, Node n)
        {
            if (playerLocks.ContainsKey(player.id))
            {
                Log.LogWriteLine(Log.StDump( world.info, player, "can't join, locked"));
                return;
            }

            Tile t = world.map[newPos];

            if (!t.IsEmpty())
            {
                MoveValidity mv = MoveValidity.VALID;

                if (t.player != Guid.Empty)
                    mv = MoveValidity.OCCUPIED_PLAYER;
                else if (t.solid)
                    mv = MoveValidity.OCCUPIED_WALL;
                else
                    throw new Exception(Log.StDump( world.info, player, mv, "bad tile status"));

                Log.LogWriteLine(Log.StDump( world.info, player, mv, "can't join, blocked"));

                n.SendMessage(MessageType.REALM_MOVE_FAIL, player.id);

                return;
            }

            n.SendMessage(MessageType.REALM_MOVE_SUCCESS, player.id);

            AddPlayer(player, newPos);
            myHost.ConnectSendMessage(player.validatorHost, MessageType.PLAYER_JOIN);
            myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_JOIN, player.id, newPos);
        }

        void OnValidateTeleport(PlayerInfo player, Point newPos)
        {
            if (playerLocks.ContainsKey(player.id))
            {
                Log.LogWriteLine(Log.StDump( world.info, player, "can't join, locked"));
                return;
            }

            Point currPos = world.playerPositions.GetValue(player.id);
            MoveValidity v = world.CheckValidMove(player.id, newPos) & ~(MoveValidity.TELEPORT | MoveValidity.BOUNDARY);
            //MoveValidity v = MoveValidity.VALID;// world.CheckValidMove(player.id, newPos) & ~(MoveValidity.TELEPORT);

            if (v != MoveValidity.VALID)
            {
                Log.Dump("Invalid teleport", world.info, player, v, currPos, newPos);
                return;
            }

            Log.Dump("Requesting teleport", world.info, player, currPos, newPos);

            myHost.ConnectSendMessage(player.validatorHost, MessageType.FREEZE_ITEM);

            playerLocks.Add(player.id, (mt) =>
            {
                if (mt == MessageType.FREEZE_FAIL)
                {
                    Log.Dump("Invalid teleport, freeze failed", world.info, player, currPos, newPos);
                }
                else if (mt == MessageType.FREEZE_SUCCESS)
                {
                    Teleport(player, currPos, newPos);
                }
                else
                    throw new Exception(Log.StDump(world.info, mt, "unexpected"));
            });

        }

        void Teleport(PlayerInfo player, Point currPos, Point newPos)
        {
            MoveValidity v = world.CheckValidMove(player.id, newPos) & ~(MoveValidity.TELEPORT);

            if (v != MoveValidity.VALID)
            {
                if (v == MoveValidity.BOUNDARY) // not done yet - realm teleport
                {
                    Log.Dump("Realm teleport request", world.info, player, v, currPos, newPos);

                    OnValidateRealmMove(player, newPos, true, (mt) =>
                    {
                        if (mt == MessageType.REALM_MOVE_FAIL)
                        {
                            Log.Dump("Realm teleport fail", world.info, player, v, currPos, newPos);
                            myHost.ConnectSendMessage(player.validatorHost, MessageType.UNFREEZE_ITEM);
                        }
                        else if (mt == MessageType.REALM_MOVE_SUCCESS)
                        {
                            Log.Dump("Realm teleport success", world.info, player, v, currPos, newPos);
                            myHost.ConnectSendMessage(player.validatorHost, MessageType.CONSUME_FROZEN_ITEM);
                        }
                        else
                            throw new Exception(Log.StDump(world.info, mt, "unexpected"));
                    });
                }
                else
                {
                    // teleporting fail
                    Log.Dump("Invalid teleport (check 2)", world.info, player, v, currPos, newPos);
                    myHost.ConnectSendMessage(player.validatorHost, MessageType.UNFREEZE_ITEM);
                }
            }
            else // teleporting success
            {
                Log.Dump("Teleported", world.info, player, currPos, newPos);
                world.Move(player.id, newPos, MoveValidity.TELEPORT);

                myHost.ConnectSendMessage(player.validatorHost, MessageType.CONSUME_FROZEN_ITEM);
                myHost.BroadcastGroup(Client.hostName, MessageType.TELEPORT_MOVE, player.id, newPos);
            }
        }

        void OnLootPickup(PlayerInfo inf)
        {
            myHost.ConnectSendMessage(inf.validatorHost, MessageType.PICKUP_ITEM);
        }

        void BoundaryRequest()
        {
            Point myPosition = world.Position;
            foreach (Point delta in Point.SymmetricRange(new Point(1, 1)))
            {
                Point newPos = myPosition + delta;

                if (gameInfo.TryGetWorldByPos(newPos) == null)
                {
                    Log.LogWriteLine(Log.StDump( newPos));
                    myHost.ConnectSendMessage(serverHost, MessageType.NEW_WORLD, newPos);
                }
            }
        }
    }
}
