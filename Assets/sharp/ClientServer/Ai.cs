﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Tools;
using Network;


namespace ServerClient
{
    class Ai
    {
        static Random rand = new Random();

        static PlayerMove PlayerRandomMove(World world, Guid player, Aggregator all)
        {
            if (!world.HasPlayer(player))
                return null;

            Point currPos = world.GetPlayerPosition(player);

            Point[] moves = {
                                new Point(-1, 0),
                                new Point(1, 0),
                                new Point(0, 1),
                                new Point(0, -1),
                                new Point(-1, 1),
                                new Point(1, -1),
                                new Point(1, 1),
                                new Point(-1, -1)
                            };

            Point newPosition = currPos + moves[rand.Next(0, moves.Length)];

            ActionValidity mv = world.CheckValidMove(player, newPosition);

            if (mv == ActionValidity.BOUNDARY)
            {
                RealmMove rm = WorldTools.BoundaryMove(newPosition, world.Position);
                World w = all.myClient.worlds.TryGetWorld(rm.newWorld);

                if (w == null)
                    return null;

                if (!w[rm.newPosition].IsMoveable())
                    return null;
            }

            if (mv == ActionValidity.VALID || mv == ActionValidity.BOUNDARY)
                return new PlayerMove() { mv = mv, newPos = newPosition };
            else
                return null;
        }

        static int PlayerAiMove(Aggregator all, Guid playerId)
        {
            int longSleep = 2;
            int shortSleep = 1;

            Client myClient = all.myClient;

            if (!all.playerAgents.ContainsKey(playerId))
                return longSleep;

            MyAssert.Assert(myClient.myPlayerAgents.Contains(playerId));

            PlayerAgent pa = all.playerAgents.GetValue(playerId);
            PlayerData playerData = pa.Data;

            if (playerData == null)
                return longSleep;

            if (!playerData.IsConnected)
            {
                //if (myClient.knownWorlds.ContainsKey(new Point(0, 0)))
                pa.Spawn();

                return longSleep;
            }

            World playerWorld = myClient.worlds.TryGetWorld(playerData.WorldPosition);
            if (playerWorld == null)
                return longSleep;

            if (playerData.inventory.teleport > 0 && rand.NextDouble() < .1)
            {
                Point teleportRange = Point.Zero; // internal teleport
                
                if (rand.NextDouble() < .1)
                    teleportRange = Point.One; // external teleport

                var teleportPos = (from p in Point.SymmetricRange(teleportRange)
                                   let w = myClient.worlds.TryGetWorld(p + playerWorld.Position)
                                   where w != null
                                   from t in w.GetAllTiles()
                                   where t.IsMoveable()
                                   select WorldTools.Shift(p, t.Position)).ToList();

                if (teleportPos.Any())
                {
                    Point newPos = teleportPos[rand.Next(0, teleportPos.Count)];
                    pa.Move(playerWorld.Info, newPos, ActionValidity.REMOTE);

                    return shortSleep;
                }
            }

            PlayerMove move = null;
            for (int i = 0; i < 5; ++i)
            {
                move = PlayerRandomMove(playerWorld, playerId, all);
                if (move != null)
                    break;
            }

            if (move != null)
            {
                if (move.mv == ActionValidity.VALID || move.mv == ActionValidity.BOUNDARY)
                    pa.Move(playerWorld.Info, move.newPos, move.mv);
                else
                    throw new Exception(Log.StDump(move.mv, move.newPos, "unexpected move"));
            }

            return shortSleep;
        }

        static private Dictionary<Guid, int> timings = null;

        static private void Tick(Aggregator all)
        {
            foreach(var id in timings.Keys.ToArray())
            {
                timings[id]--;

                if (timings[id] <= 0)
                {
                    int timeout = PlayerAiMove(all, id);
                    MyAssert.Assert(timeout > 0 && timeout < 10);
                    timings[id] = timeout;
                }
            }
        }

        public static void StartPlayerAi(Aggregator all, Guid playerId)
        {
            if (timings == null)
            {
                timings = new Dictionary<Guid, int>();
                all.sync.TimedAction.AddAction(() => Tick(all));
            }

            timings.Add(playerId, 0);

            //ThreadManager.NewThread(() =>
            //{
            //    while (true)
            //    {
            //        int sleepTime;

            //        lock (all.sync.syncLock)
            //            sleepTime = PlayerAiMove(all, playerId);

            //        Thread.Sleep(sleepTime);
            //    }
            //}, () => { }, "Ai for player " + playerId);

        }
    }
}
