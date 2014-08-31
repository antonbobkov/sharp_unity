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

    class Player
    {
        public Point pos;
        public Guid id;

        public Player(Point pos_, Guid id_) { pos = pos_; id = id_; }
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
        public Guid player = Guid.Empty;
        public Guid validator = Guid.Empty;
    }

    class NodeRoles
    {
        public Dictionary<Guid, Node> players = new Dictionary<Guid,Node>();
        public Dictionary<Guid, Node> validators = new Dictionary<Guid,Node>();

        public void Add(Role r, Node n)
        {
            if (r.player != Guid.Empty)
                players.Add(r.player, n);
            if (r.validator != Guid.Empty)
                validators.Add(r.validator, n);
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
        
        void Generate()
        {
            ServerClient.Random seededRandom = new ServerClient.Random(info.seed);

            Debug.Assert(info.numberOfPlayers == roles.players.Count());
            Debug.Assert(info.numberOfValidators == roles.validators.Count());
            Debug.Assert(info.numberOfValidators != 0);
            
            world = new Tile[info.worldWidth, info.worldHeight];

            for(int x = 0; x < world.GetLength(0); ++x)
                for (int y = 0; y < world.GetLength(1); ++y)
                {
                    world[x, y] = new Tile();
                    if (seededRandom.NextDouble() < info.density)
                        world[x, y].solid = true;
                    else
                        world[x, y].solid = false;
                }

            var playersInOrder = from pair in roles.players
                                 orderby pair.Key.ToString()
                                 select pair.Key;

            foreach (Guid id in playersInOrder)
            {
                while (true)
                {
                    Point pos;
                    pos.x = seededRandom.Next(0, world.GetLength(0));
                    pos.y = seededRandom.Next(0, world.GetLength(1));

                    Tile t = world[pos.x, pos.y];

                    if (t.IsEmpty())
                    {
                        Player newPlayer = new Player(pos, id);
                        t.p = newPlayer;
                        players.Add(id, newPlayer);
                        break;
                    }
                }
            }

            Guid[] validators = (from kv in roles.validators
                                 orderby kv.Key.ToString()
                                 select kv.Key).ToArray();
            Debug.Assert(info.numberOfValidators == validators.Length);
            worldValidator = validators[seededRandom.Next(0, validators.GetLength(0))];
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
                        pic[x, y] = world[x, y].p.id.ToString("N")[0];
                    else
                        pic[x, y] = ' ';
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

            Point diff = pl.pos - mv.pos;
            if (Math.Abs(diff.x) > 1)
                return MoveValidity.TELEPORT;
            if (Math.Abs(diff.y) > 1)
                return MoveValidity.TELEPORT;

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
                return MoveValidity.OCCUPIED;
            }

            return MoveValidity.VALID;
        }

        public void Move(PlayerMoveInfo mv)
        {
            Debug.Assert(players.ContainsKey(mv.id));
            Player p = players[mv.id];
            world[p.pos.x, p.pos.y].p = null;

            Tile t = world[mv.pos.x, mv.pos.y];
            Debug.Assert(t.IsEmpty());

            p.pos = mv.pos;
            t.p = p;
        }
    }
}
