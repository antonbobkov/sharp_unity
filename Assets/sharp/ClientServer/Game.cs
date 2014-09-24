using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Xml.Serialization;
using System.ComponentModel;


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

        public static IEnumerable<Point> Range(Point size)
        {
            Point p = new Point();
            for (p.y = 0; p.y < size.y; ++p.y)
                for (p.x = 0; p.x < size.x; ++p.x)
                    yield return p;
        }
        public static IEnumerable<Point> SymmetricRange(Point size)
        {
            Point p = new Point();
            for (p.y = -size.y; p.y <= size.y; ++p.y)
                for (p.x = -size.x; p.x <= size.x; ++p.x)
                    yield return p;
        }
    }


    [Serializable]
    public class Plane<T>
    {
        public T[] plane;
        public Point Size { get; set; }

        public Plane() { }
        public Plane(Point size) { plane = new T[size.x * size.y]; Size = size; }

        public T this[Point pos]
        {
            get { return this[pos.x, pos.y]; }
            set { this[pos.x, pos.y] = value; }
        }
        public T this[int x, int y]
        {
            get { return plane[y*Size.x + x]; }
            set { plane[y*Size.x + x] = value; }
        }

        public IEnumerable<T> GetTiles()
        {
            foreach (Point p in Point.Range(Size))
                yield return this[p];
        }
        public IEnumerable<KeyValuePair<Point, T>> GetEnum()
        {
            foreach(Point p in Point.Range(Size))
                yield return new KeyValuePair<Point, T>(p, this[p]);
        }
    }



    /*class Player
    {
        public readonly Guid id;
        public readonly Guid validator;

        readonly string sName;

        public string ShortName { get { return sName; } }
        public string FullName { get { return "Player " + sName; } }

        public Player(Guid id_, Guid verifier_, string sName_)
        {
            id = id_;
            validator = verifier_;
            sName = sName_;
        }
    }*/


    //[Serializable]
    /*public class Role
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
    }*/



    /*class Game
    {
        public GameInitializer info;
        public Role roles;

        Dictionary<Point, World> worldsByPoint = new Dictionary<Point, World>();
        Dictionary<Guid, World> worldsById = new Dictionary<Guid, World>();

        public void AddWorld(World w)
        {
            worldsByPoint.Add(w.worldPosition, w);
            worldsById.Add(w.id, w);
        }

        public World GetWorld(Guid id) { return worldsById.GetValue(id); }
        public World GetWorld(Point p) { return worldsByPoint.GetValue(p); }
        public World TryGetWorld(Point p)
        {
            if (worldsByPoint.ContainsKey(p))
                return worldsByPoint.GetValue(p);
            else
                return null;
        }
        public World GetPlayerWorld(Guid id) { return GetWorld(playerWorld.GetValue(id)); }

        public IEnumerable<World> AllWorlds() { return worldsById.Values; }

        public Dictionary<Guid, Player> players = new Dictionary<Guid, Player>();
        public Dictionary<Guid, Point> playerWorld = new Dictionary<Guid, Point>();
        public Dictionary<Guid, Inventory> playerInventory = new Dictionary<Guid, Inventory>();

        public Game(GameInitializer info_, Role roles_)
        {
            info = info_;
            roles = roles_;

            Generate();
        }

        static string PlayerNameMap(int value)
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
        public static Guid GuidFromPoint(Point p)
        {
            byte[] x = BitConverter.GetBytes(p.x);
            byte[] y = BitConverter.GetBytes(p.y);

            byte[] guid = new byte[16];
            for (int i = 0; i < 4; ++i)
                guid[i] = x[i];
            for (int i = 0; i < 4; ++i)
                guid[i + 4] = y[i];

            return new Guid(guid);
        }
        void Generate()
        {
            ServerClient.MyRandom seededRandom = new ServerClient.MyRandom(info.seed);

            MyAssert.Assert(info.numberOfPlayers == roles.player.Count());
            MyAssert.Assert(info.numberOfValidators == roles.validator.Count());
            MyAssert.Assert(info.numberOfValidators != 0);

            MyAssert.Assert(info.worldSize.x >= 3);
            MyAssert.Assert(info.worldSize.y >= 3);

            // validators
            var validators = (from kv in roles.validator
                              orderby kv.ToString()
                              select kv).ToList();
            MyAssert.Assert(info.numberOfValidators == validators.Count);

            // worlds
            Point p = new Point();
            for (p.y = -info.worlds.y; p.y <= info.worlds.y; ++p.y)
                for (p.x = -info.worlds.x; p.x <= info.worlds.x; ++p.x)
                {
                    Guid worldValidator = validators.Random(n => seededRandom.Next(n));
                    World world = new World(GuidFromPoint(p), worldValidator, p, info.worldSize, players);
                    AddWorld(world);
                    
                    // random terrain
                    foreach (Tile t in world.map.GetTiles())
                    {
                        if (seededRandom.NextDouble() < info.wallDensity)
                            t.solid = true;
                        else
                        {
                            t.solid = false;

                            if (seededRandom.NextDouble() < info.lootDensity)
                                t.loot = true;
                        }                    
                    }

                    // clear spawn points
                    /
                    foreach (Point bp in world.GetBoundary())
                    {
                        Tile t = world.map[bp];

                        //t.solid = false;
                        t.loot = false;
                        //MyAssert.Assert(t.IsEmpty());
                    }
                    /
                }

            // boundary will be spawning points

            Point spawnWorldPoint = new Point(0, 0);
            World spawnWorld = GetWorld(spawnWorldPoint);

            List<KeyValuePair<Point, Tile>> spawnLst = (from pair in spawnWorld.GetSpawn()
                                                        orderby pair.Key.ToString()
                                                        select pair).ToList();

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
                Guid validator = validators.Random(n => seededRandom.Next(n));

                Player newPlayer = new Player(id, validator, PlayerNameMap(i));
                players.Add(id, newPlayer);

                spawnWorld.AddPlayer(id, position);

                playerWorld.Add(id, spawnWorldPoint);
                playerInventory.Add(id, new Inventory());
            }
        }

        public void ConsoleOut()
        {
            World world = GetWorld(new Point(0, 0));

            int szX = world.map.Size.x;
            int szY = world.map.Size.y;

            char[,] pic = new char[szX, szY];

            foreach (var kv in world.map.GetEnum())
            {
                Point p = kv.Key;
                Tile t = kv.Value;

                if (t.solid)
                    pic[p.x, p.y] = '*';
                else if (t.player != Guid.Empty)
                    pic[p.x, p.y] = players.GetValue(t.player).ShortName[0];
                else if (t.loot)
                    pic[p.x, p.y] = '$';
            }

            for (int y = szY - 1; y >= 0; --y)
            {
                for (int x = 0; x < szX; ++x)
                    Console.Write(pic[x, y]);
                Console.WriteLine();
            }
        }
    }*/
}
