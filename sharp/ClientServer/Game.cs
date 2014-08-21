using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ServerClient
{
    class Player
    {
        public int xPos;
        public int yPos;
    }

    class Tile
    {
        public bool solid;
        public Tile(){solid = false;}
    }

    [Serializable]
    class GameInitializer
    {
        public int numberOfPlayers = 5;
        public int worldWidth = 20;
        public int worldHeight = 10;
        public int seed;
        public double density = .3;

        public GameInitializer(int seed_) { seed = seed_; }
    }

    class Game
    {
        public GameInitializer info;

        public Tile[,] world;
        public List<Player> players = new List<Player>();

        public static Game GenerateGame(GameInitializer info)
        {
            Random seededRandom = new Random(info.seed);

            Game g = new Game { world = new Tile[info.worldWidth, info.worldHeight] };

            for(int x = 0; x < g.world.GetLength(0); ++x)
                for (int y = 0; y < g.world.GetLength(1); ++y)
                {
                    g.world[x, y] = new Tile();
                    if (seededRandom.NextDouble() < info.density)
                        g.world[x, y].solid = true;
                    else
                        g.world[x, y].solid = false;
                }

            for (int pl = 0; pl < info.numberOfPlayers; ++pl)
            { 
                Player p = new Player();
                while (true)
                {
                    p.xPos = seededRandom.Next(0, g.world.GetLength(0));
                    p.yPos = seededRandom.Next(0, g.world.GetLength(1));

                    if (g.world[p.xPos, p.yPos].solid == false)
                    {
                        g.world[p.xPos, p.yPos].solid = true;
                        break;
                    }
                }
                g.players.Add(p);
            }

            foreach(Player p in g.players)
                g.world[p.xPos, p.yPos].solid = false;

            g.info = info;
            return g;
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

            foreach (Player p in players)
                pic[p.xPos, p.yPos] = '@';

            for (int y = 0; y < szY; ++y)
            {
                for (int x = 0; x < szX; ++x)
                    Console.Write(pic[x, y]);
                Console.WriteLine();
            }
        }
    }
}
