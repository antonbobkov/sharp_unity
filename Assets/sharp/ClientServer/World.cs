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
        public double wallDensity;
        public double lootDensity;

        public WorldInitializer() { }
        internal WorldInitializer(int seed_, double wallDensity_ = .5, double lootDensity_ = .05)
        {
            seed = seed_;
            wallDensity = wallDensity_;
            lootDensity = lootDensity_;
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
            return GetFullInfo();
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

    [Serializable]
    public class WorldSerialized
    {
        public Plane<Tile> map;
        public WorldInfo myInfo;
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
    
    class World
    {
        static readonly Point worldSize = new Point(20, 10);

        public readonly WorldInfo myInfo;
        public Plane<Tile> map;

        public Point Position { get { return myInfo.worldPos; } }

        public Dictionary<Guid, Point> playerPositions = new Dictionary<Guid,Point>();
        GameInfo info;

        public Action<PlayerInfo> onLootHook = (info) => { };
        //public Action<PlayerInfo> onMoveHook = (info) => { };

        public World(WorldSerialized ws, GameInfo info_)
        {
            myInfo = ws.myInfo;
            info = info_;
            map = ws.map;
        }
        public World(WorldInfo myInfo_, WorldInitializer init, GameInfo info_)
        {
            myInfo = myInfo_;
            info = info_;

            map = new Plane<Tile>(worldSize);

            foreach (Point p in Point.Range(map.Size))
                map[p] = new Tile();

            Generate(init);
        }

        public WorldSerialized Serialize()
        {
            return new WorldSerialized() { map = map, myInfo = myInfo };
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
            foreach (Point p in Point.Range(map.Size))
            {
                Tile t = map[p];
                if (t.IsEmpty() && t.loot == false)
                    yield return new KeyValuePair<Point, Tile>(p, t);
            }
        }

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
                    pic[p] = info.GetPlayerById(t.player).name[0];
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
            PlayerInfo p = info.GetPlayerById(player);

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

            //onMoveHook(p);
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
        public Point BoundaryMove(ref Point p)
        {
            int px = p.x;
            int py = p.y;

            Point ret = new Point(BoundaryMove(ref px, map.Size.x), BoundaryMove(ref py, map.Size.y));

            p = new Point(px, py);

            return Position + ret;
        }
        
    }

    class WorldValidator
    {
        Random rand = new Random();

        World world;
        Action<Action> sync;
        OverlayHost myHost;

        GameInfo gameInfo;

        Dictionary<string, Action<MessageType>> networkLocks = new Dictionary<string, Action<MessageType>>();
        void FinishLock(string s, MessageType mt)
        {
            networkLocks.GetValue(s).Invoke(mt);
            networkLocks.Remove(s);
        }

        public WorldValidator(WorldInfo info, WorldInitializer init, Action<Action> sync_, GlobalHost globalHost, GameInfo gameInfo_)
        {
            gameInfo = gameInfo_;
            sync = sync_;

            world = new World(info, init, gameInfo);

            myHost = globalHost.NewHost(info.host.hostname, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }
        
        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return (mt, stm, nd) => { throw new Exception("WorldValidator not expecting messages from Client." + mt + " " + nd.info); };
            
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

            throw new InvalidOperationException("WorldValidator.AssignProcessor unexpected connection " + n.info.remote + " " + role);
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
                sync.Invoke(() => FinishLock(inf.validatorHost.ToString(), mt));
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }

        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
                sync.Invoke(() => OnSpawnRequest(inf));
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }

        void OnSpawnRequest(PlayerInfo inf)
        {
            if(networkLocks.ContainsKey(inf.validatorHost.ToString()))
            {
                Log.LogWriteLine("Spawn failed, spawn in progress.\n World {0} \n Player {1}", world.myInfo, inf);
                return;
            }
            
            myHost.ConnectSendMessage(inf.validatorHost, MessageType.SPAWN_REQUEST);

            networkLocks.Add(inf.validatorHost.ToString(), (mt) =>
                {
                    if (mt == MessageType.SPAWN_FAIL)
                    {
                        Log.LogWriteLine("Spawn failed, already spawned.\n World {0} \n Player {1}", world.myInfo, inf);
                    }
                    else if (mt == MessageType.SPAWN_SUCCESS)
                    {
                        var spawn = world.GetSpawn().ToList();
                        if (!spawn.Any())
                        {
                            Log.LogWriteLine("Spawn failed, no space.\n World {0} \n Player {1}", world.myInfo, inf);
                            return;
                        }

                        Point spawnPos = spawn.Random((n) => rand.Next(n)).Key;
                        
                        world.AddPlayer(inf.id, spawnPos);
                        myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_JOIN, inf.id, spawnPos);
                    }
                    else
                        throw new Exception("Unexpected response in OnSpawnRequest " + mt);
                });
        }
        void OnMoveValidate(PlayerInfo inf, Point currPos, Point newPos)
        {
            
            
            /*
            if (movementLocks.ContainsKey(p.id))
            {
                Log.LogWriteLine("Validator: {0} can't move, locked", p.FullName);
                return;
            }
            */

            MoveValidity v = world.CheckValidMove(inf.id, newPos);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("World {4}: Invalid move {0} from {1} to {2} by {3}", v,
                    currPos, newPos, inf.GetShortInfo(), world.myInfo.GetShortInfo());
                return;
            }

            world.Move(inf.id, newPos, MoveValidity.VALID);

            myHost.BroadcastGroup(Client.hostName, MessageType.MOVE, inf.id, newPos);
            //Broadcast(MessageType.MOVE, myId, p.id, newPos);
        }
    }
}
