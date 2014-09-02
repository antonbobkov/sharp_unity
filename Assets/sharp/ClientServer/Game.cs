using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace ServerClient
{
    [Serializable]
    public struct Point
    {
        public int x, y;

        public Point(int x_, int y_) { x = x_; y = y_; }
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("[{0}, {1}]", x, y);
            return sb.ToString();
        }

        public static Point operator +(Point p1, Point p2) { return new Point(p1.x + p2.x, p1.y + p2.y); }
        public static Point operator -(Point p1) { return new Point(-p1.x, -p1.y); }
        //public static bool operator ==(Point p1, Point p2) { return (p1.x == p2.x) && (p1.y == p2.y); }

        public static Point operator -(Point p1, Point p2) { return p1 + -p2; }
        //public static bool operator !=(Point p1, Point p2) { return !(p1 == p2); }
    }

    class Inventory
    {
        public int teleport = 0;
        public int frozenTeleport = 0;
    }

    class Player
    {
        public readonly Guid id;
        public readonly string sName;
        public readonly Guid validator;

        public Point pos;
        public Inventory inv = new Inventory();

        public string FullName { get { return "Player " + sName; }  }

        public Player(Point pos_, Guid id_, string sName_, Guid verifier_)
        {
            pos = pos_;
            id = id_;
            sName = sName_;
            validator = verifier_;
        }
    }

    [Serializable]
    public class PlayerMoveInfo
    {
        public Guid id;
        public Point pos;

        public PlayerMoveInfo() { }
        public PlayerMoveInfo(Guid id_, Point pos_)
        {
            id = id_;
            pos = pos_;
        }
    }

    class Tile
    {
        public bool solid = false;
        public Player p = null;
        public bool loot = false;

        public bool IsEmpty() { return (p == null) && !solid; }

        public Tile(){solid = false;}
    }

    [Serializable]
    public class GameInitializer
    {
        public int numberOfPlayers;
        public int numberOfValidators;
        public int worldWidth = 20;
        public int worldHeight = 10;
        public int seed;
        public double density = .2;

        public GameInitializer() { }
        internal GameInitializer(int seed_, NodeRoles roles)
        { 
            seed = seed_;
            numberOfPlayers = roles.players.Count();
            numberOfValidators = roles.validators.Count();
        }
    }

    [Serializable]
    public class Role
    {
        public HashSet<Guid> player = new HashSet<Guid>();
        public HashSet<Guid> validator = new HashSet<Guid>();

        public static string PrintList(IEnumerable<Guid> ls)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var id in from n in ls orderby n.ToString() select n)
                sb.AppendFormat("\t{0}\n", id);
            return sb.ToString();
        }

        public string PrintRoles()
        {
            StringBuilder sb = new StringBuilder();
            if (player.Any())
            {
                sb.AppendLine("Player:");
                sb.Append(PrintList(player));
            }
            if (validator.Any())
            {
                sb.AppendLine("Validator:");
                sb.Append(PrintList(validator));
            }
            return sb.ToString();
        }
    }

    class NodeRoles
    {
        public Dictionary<Guid, Node> players = new Dictionary<Guid,Node>();
        public Dictionary<Guid, Node> validators = new Dictionary<Guid,Node>();

        static void Add(Dictionary<Guid, Node> d, IEnumerable<Guid> l, Node n)
        {
            foreach (var id in l)
                d[id] = n;
        }

        public void Add(Role r, Node n)
        {
            Add(players, r.player, n);
            Add(validators, r.validator, n);
        }
    }

    class Game
    {
        public GameInitializer info;
        public NodeRoles roles;

        public Tile[,] world;
        public Dictionary<Guid, Player> players = new Dictionary<Guid, Player>();
        public Guid worldValidator;

        public Game(GameInitializer info_, NodeRoles roles_)
        {
            info = info_;
            roles = roles_;

            Generate();
        }

        string PlayerNameMap(int value)
        {
            char[] baseChars = new char[] { '0','1','2','3','4','5','6','7','8','9',
            'A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
            'a','b','c','d','e','f','g','h','i','j','k','l','m','n','o','p','q','r','s','t','u','v','w','x','y','z'};

            Debug.Assert(value >= 0);
            
            string result = string.Empty;
            int targetBase = baseChars.Length;

            do
            {
                result = baseChars[value % targetBase] + result;
                value = value / targetBase;
            } 
            while (value > 0);

            return result;
        }

        void Generate()
        {
            ServerClient.MyRandom seededRandom = new ServerClient.MyRandom(info.seed);

            Debug.Assert(info.numberOfPlayers == roles.players.Count());
            Debug.Assert(info.numberOfValidators == roles.validators.Count());
            Debug.Assert(info.numberOfValidators != 0);

            int szX = info.worldWidth;
            int szY = info.worldHeight;

            Debug.Assert(szX >= 3);
            Debug.Assert(szY >= 3);

            // validators
            var validators = (from kv in roles.validators
                                 orderby kv.Key.ToString()
                                 select kv.Key).ToList();

            Debug.Assert(info.numberOfValidators == validators.Count);
            worldValidator = validators.Random(n => seededRandom.Next(n));

            // random terrain
            world = new Tile[szX, szY];

            for (int x = 0; x < szX; ++x)
                for (int y = 0; y < szY; ++y)
                {
                    world[x, y] = new Tile();

                    if (seededRandom.NextDouble() < info.density)
                        world[x, y].solid = true;
                    else
                    {
                        world[x, y].solid = false;

                        if (seededRandom.NextDouble() < .1)
                            world[x, y].loot = true;
                    }

                }

            // boundary will be spawning points

            Dictionary<Point, Tile> spawnDic = new Dictionary<Point,Tile>();
            Action<Point> addSpawn = (p) => spawnDic[p] = world[p.x, p.y];

            for (int x = 0; x < szX; ++x)
            {
                addSpawn(new Point(x, 0));
                addSpawn(new Point(x, szY-1));
            }

            for (int y = 0; y < szY; ++y)
            {
                addSpawn(new Point(0, y));
                addSpawn(new Point(szX - 1, y));
            }

            List<KeyValuePair<Point, Tile>> spawnLst = (from pair in spawnDic
                                                        orderby pair.Key.ToString()
                                                        select pair).ToList();
            // clear spawning area
            foreach (var kv in spawnLst)
            {
                kv.Value.solid = false;
                kv.Value.loot = false;
                Debug.Assert(kv.Value.IsEmpty());
            }

            // deterministic shuffle
            spawnLst.Shuffle((n) => seededRandom.Next(n));


            var playersInOrder = (from pair in roles.players
                                  orderby pair.Key.ToString()
                                  select pair.Key).ToList();

            Debug.Assert(playersInOrder.Count() == info.numberOfPlayers);
            Debug.Assert(spawnLst.Count() > info.numberOfPlayers);

            // spawn players
            for (int i = 0; i < playersInOrder.Count(); ++i)
            {
                Point position = spawnLst[i].Key;
                Guid id = playersInOrder[i];
                Tile tile = spawnLst[i].Value;
                Guid validator = validators.Random(n => seededRandom.Next(n));

                Player newPlayer = new Player(position, id, PlayerNameMap(i), validator);
                
                Debug.Assert(tile.IsEmpty());
                tile.p = newPlayer;

                players.Add(id, newPlayer);
            }
        }

        public void ConsoleOut()
        {
            int szX = world.GetLength(0);
            int szY = world.GetLength(1);

            char[,] pic = new char[szX, szY];

            for (int x = 0; x < szX; ++x)
                for (int y = szY-1; y >= 0; --y)
                {
                    if (world[x, y].solid)
                        pic[x, y] = '*';
                    else if (world[x, y].p != null)
                        pic[x, y] = world[x, y].p.sName[0];
                    else if (world[x, y].loot)
                        pic[x, y] = '$';
                }

            for (int y = szY - 1; y >= 0; --y)
            {
                for (int x = 0; x < szX; ++x)
                    Console.Write(pic[x, y]);
                Console.WriteLine();
            }
        }

        public MoveValidity CheckValidMove(PlayerMoveInfo mv)
        {
            Debug.Assert(players.ContainsKey(mv.id));
            Player pl = players[mv.id];

            Tile t;
            try
            {
                t = world[mv.pos.x, mv.pos.y];
            }
            catch (IndexOutOfRangeException)
            {
                return MoveValidity.BOUNDARY;
            }

            if (!t.IsEmpty())
            {
                if(t.p != null)
                    return MoveValidity.OCCUPIED_PLAYER;
                if(t.solid)
                    return MoveValidity.OCCUPIED_WALL;

                throw new InvalidOperationException("CheckValidMove: unexpected tile status");
            }

            Point diff = pl.pos - mv.pos;
            if (Math.Abs(diff.x) > 1)
                return MoveValidity.TELEPORT;
            if (Math.Abs(diff.y) > 1)
                return MoveValidity.TELEPORT;

            return MoveValidity.VALID;
        }

        public void Move(PlayerMoveInfo mv)
        {
            //Debug.Assert(CheckValidMove(mv) == MoveValidity.VALID);
            if(CheckValidMove(mv) != MoveValidity.VALID)
                Log.LogWriteLine("Game.Move Warning: Invalid move {0} from {1} to {2} by {3}", CheckValidMove(mv), players[mv.id].pos, mv.pos, players[mv.id].FullName);

            Debug.Assert(players.ContainsKey(mv.id));
            Player p = players[mv.id];
            world[p.pos.x, p.pos.y].p = null;

            Tile t = world[mv.pos.x, mv.pos.y];
            Debug.Assert(t.IsEmpty());

            p.pos = mv.pos;
            t.p = p;
            t.loot = false;
        }
    }
}
