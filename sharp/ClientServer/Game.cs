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

        public static Point operator +(Point p1, Point p2) { return new Point(p1.x + p2.x, p1.y + p2.y); }
    }

    class Player
    {
        public Point pos;
        public Node n;
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
        public bool solid;
        public Tile(){solid = false;}
    }

    [Serializable]
    public class GameInitializer
    {
        public int numberOfPlayers;
        public int worldWidth = 20;
        public int worldHeight = 10;
        public int seed;
        public double density = .3;

        public GameInitializer() { }
        public GameInitializer(int seed_, int numberOfPlayers_)
        { 
            seed = seed_;
            numberOfPlayers = numberOfPlayers_;
        }
    }

    class Game
    {
        public GameInitializer info;

        public Tile[,] world;
        public Dictionary<Guid, Player> players = new  Dictionary<Guid, Player>();

        public Game(IEnumerable<Node> nodes, Guid me, GameInitializer info_)
        {
            info = info_;

            foreach (Node n in nodes)
                players.Add(n.Id, new Player(){ n = n });
            players.Add(me, new Player() { n = null });

            Generate();
        }
        
        void Generate()
        {
            ServerClient.Random seededRandom = new ServerClient.Random(info.seed);

            Debug.Assert(info.numberOfPlayers == players.Count());

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

            var playersInOrder = from pair in players.OrderBy(p => p.Key)
                                 select pair.Value;

            foreach (Player p in playersInOrder)
            {
                while (true)
                {
                    p.pos.x = seededRandom.Next(0, world.GetLength(0));
                    p.pos.y = seededRandom.Next(0, world.GetLength(1));

                    Tile t = world[p.pos.x, p.pos.y];

                    if (t.solid == false)
                    {
                        t.solid = true;
                        break;
                    }
                }
            }

            foreach (Player p in playersInOrder)
                world[p.pos.x, p.pos.y].solid = false;
        }

        public void ConsoleOut()
        {
            int szX = world.GetLength(0);
            int szY = world.GetLength(1);

            char[,] pic = new char[szX, szY];

            for (int x = 0; x < szX; ++x)
                for (int y = 0; y < szY; ++y)
                    if (world[x, y].solid)
                        pic[x, y] = '*';
                    else
                        pic[x, y] = ' ';

            foreach (var pair in players)
                pic[pair.Value.pos.x, pair.Value.pos.y] = pair.Key.ToString("N")[0];

            for (int y = 0; y < szY; ++y)
            {
                for (int x = 0; x < szX; ++x)
                    Console.Write(pic[x, y]);
                Console.WriteLine();
            }
        }
    }
}
