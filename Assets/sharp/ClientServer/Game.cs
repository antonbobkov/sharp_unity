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

    /*
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
     * */

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
        internal GameInitializer(int seed_, Role roles)
        { 
            seed = seed_;
            numberOfPlayers = roles.player.Count();
            numberOfValidators = roles.validator.Count();
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

    /*
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
     * */

    class Game
    {
        public GameInitializer info;
        public Role roles;

        public Tile[,] world;
        public Dictionary<Guid, Player> players = new Dictionary<Guid, Player>();
        public Guid worldValidator;

        public Game(GameInitializer info_, Role roles_)
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

            MyAssert.Assert(value >= 0);
            
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

            MyAssert.Assert(info.numberOfPlayers == roles.player.Count());
            MyAssert.Assert(info.numberOfValidators == roles.validator.Count());
            MyAssert.Assert(info.numberOfValidators != 0);

            int szX = info.worldWidth;
            int szY = info.worldHeight;

            MyAssert.Assert(szX >= 3);
            MyAssert.Assert(szY >= 3);

            // validators
            var validators = (from kv in roles.validator
                              orderby kv.ToString()
                              select kv).ToList();

            MyAssert.Assert(info.numberOfValidators == validators.Count);
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
                MyAssert.Assert(kv.Value.IsEmpty());
            }

            // deterministic shuffle
            spawnLst.Shuffle((n) => seededRandom.Next(n));


            var playersInOrder = (from pair in roles.player
                                  orderby pair.ToString()
                                  select pair).ToList();

            MyAssert.Assert(playersInOrder.Count() == info.numberOfPlayers);
            MyAssert.Assert(spawnLst.Count() > info.numberOfPlayers);

            // spawn players
            for (int i = 0; i < playersInOrder.Count(); ++i)
            {
                Point position = spawnLst[i].Key;
                Guid id = playersInOrder[i];
                Tile tile = spawnLst[i].Value;
                Guid validator = validators.Random(n => seededRandom.Next(n));

                Player newPlayer = new Player(position, id, PlayerNameMap(i), validator);
                
                MyAssert.Assert(tile.IsEmpty());
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

        public MoveValidity CheckValidMove(Player p, Point newPos)
        {
            Point oldPos = players.GetValue(p.id).pos;

            Tile t;
            try
            {
                t = world[newPos.x, newPos.y];
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

            Point diff = newPos - oldPos;
            if (Math.Abs(diff.x) > 1)
                return MoveValidity.TELEPORT;
            if (Math.Abs(diff.y) > 1)
                return MoveValidity.TELEPORT;

            return MoveValidity.VALID;
        }

        public void Move(Player p, Point newPos, MoveValidity mv = MoveValidity.VALID)
        {
            if (CheckValidMove(p, newPos) != mv)
                Log.LogWriteLine("Game.Move Warning: Invalid move {0} from {1} to {2} by {3}", CheckValidMove(p, newPos), p.pos, newPos, p.FullName);

            MyAssert.Assert(world[p.pos.x, p.pos.y].p == p);
            world[p.pos.x, p.pos.y].p = null;

            Tile t = world[newPos.x, newPos.y];
            MyAssert.Assert(t.IsEmpty());

            p.pos = newPos;
            t.p = p;
            t.loot = false;
        }
    }
}
