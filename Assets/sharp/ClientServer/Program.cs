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

namespace ServerClient
{
    class Program
    {
        static Random rand = new Random();
        
        static void PlayerRandomMove(Aggregate a, Guid player)
        {
            Game g = a.game;
            if (g == null)
                return;
            Player p = g.players[player];

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
            Point newPosition = p.pos + moves[rand.Next(0, moves.Length)];
            
            if (g.CheckValidMove(p, newPosition) == MoveValidity.VALID)
                if (!a.gameAssignments.NodeById(a.game.worldValidator).IsClosed)
                    a.Move(p, newPosition);
        }

        static void RepeatedAction(Action<Action> queue, Action a, int period)
        {
            while (true)
            {
                queue(a);
                Thread.Sleep(period);
            }
        }

        class InputProcessor
        {
            public Dictionary<string, Action<List<string>>> commands = new Dictionary<string, Action<List<string>>>();
            Action<Action> queueAction;

            public InputProcessor(Action<Action> queueAction_) { queueAction = queueAction_; } 

            public void Process(string input, List<string> param)
            {
                var a = from kv in commands
                        where kv.Key.StartsWith(input)
                        select kv.Key;
                int sz = a.Count();

                if(sz == 0)
                {
                    Console.WriteLine("unknown command");
                }
                else if (sz == 1)
                {
                    queueAction(() => commands[a.First()].Invoke(param));
                }
                else
                {
                    foreach (var s in a)
                        Console.WriteLine(s);
                }
            }
        }

        static public void MeshConnect(Aggregate all)
        {
            all.sync.Add(() =>
                {
                    IPEndPoint ep = Aggregate.ParseParamForIP(new List<string>());
                    if(all.Connect(ep, true))
                        Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);
                });

            try
            {
                var f = new System.IO.StreamReader(File.Open("ips", FileMode.Open));
                string line;
                while ((line = f.ReadLine()) != null)
                {
                    List<string> ls = new List<string>(line.Split(' '));
                    all.sync.Add(() =>
                    {
                        IPEndPoint ep = Aggregate.ParseParamForIP(ls);
                        if (all.Connect(ep, true))
                            Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);
                    });
                }
            }
            catch (Exception)
            {
                //Log.LogWriteLine("Error: {0}", e.Message);
            }
        }
        static public void Ai(Aggregate all)
        {
            foreach (var id in all.gameAssignments.GetMyRoles(NodeRole.PLAYER))
            {
                Guid idCopy = id;
                ThreadManager.NewThread(() => RepeatedAction(all.sync.GetAsDelegate(), () => PlayerRandomMove(all, idCopy), rand.Next(300, 700)),
                    () => { }, "ai for " + id.ToString());
                //new Thread(() => RepeatedAction(all.sync.GetAsDelegate(), () => PlayerRandomMove(all, idCopy), rand.Next(300, 700))).Start();
            }
        }
        
        static void Main(string[] args)
        {
            Aggregate all = new Aggregate();
            all.sync.Add(() =>
            {
                Role myRole = new Role();

                //if (all.peers.MyAddress.Port == NodeCollection.nStartPort)
                    myRole.validator.Add(Guid.NewGuid());
                //else
                {
                    for (int i = 0; i < 2; ++i)
                        myRole.player.Add(Guid.NewGuid());
                }

                all.AddMyRole(myRole);

                Ai(all);

                Console.WriteLine();
                Console.Write(myRole.PrintRoles());
                Console.WriteLine();

                //mesh connect
                Program.MeshConnect(all);
            });

            InputProcessor inputProc = new InputProcessor(all.sync.GetAsDelegate());

            inputProc.commands.Add("connect", (param) => all.ParamConnect(param, false));
            inputProc.commands.Add("mconnect", (param) => all.ParamConnect(param, true));
            
            inputProc.commands.Add("list", (param) =>
            {
                Console.WriteLine("Nodes:");
                foreach (var s in from n in all.peers.GetAllNodes() orderby n.Address.ToString() select n.Address.ToString())
                    Console.WriteLine("\t{0}", s);
                Console.WriteLine("Players:");
                Role allRoles = all.gameAssignments.GetAllRoles();
                foreach (var kv in from n in allRoles.player orderby n.ToString() select n)
                    Console.WriteLine("\t{0}\t{1}", kv, all.gameAssignments.NodeById(kv).Address);
                Console.WriteLine("Validators:");
                foreach (var kv in from n in allRoles.validator orderby n.ToString() select n)
                    Console.WriteLine("\t{0}\t{1}", kv, all.gameAssignments.NodeById(kv).Address);
            });

            inputProc.commands.Add("generate", (param) =>
            {
                all.GenerateGame();
            });

            inputProc.commands.Add("ai", (param) =>
            {
                Ai(all);
            });

            inputProc.commands.Add("draw", (param) =>
            {
                all.game.ConsoleOut();
            });
            
            inputProc.commands.Add("update", (param) =>
            {
                ThreadManager.NewThread(() => RepeatedAction(all.sync.GetAsDelegate(), () => all.game.ConsoleOut(), 500),
                    () => { }, "console drawer");
                //new Thread(() => RepeatedAction(all.sync.GetAsDelegate(), () => all.game.ConsoleOut(), 500)).Start();
            });

            inputProc.commands.Add("status", (param) =>
            {
                Console.WriteLine(ThreadManager.Status());
            });

            inputProc.commands.Add("ping", (param) =>
            {
                Console.WriteLine("ping");
            });

            inputProc.commands.Add("exit", (param) =>
            {
                ThreadManager.Terminate();
            });

            while (true)
            {
                string sInput = Console.ReadLine();
                var param = new List<string>(sInput.Split(' '));
                if (!param.Any())
                    continue;
                string sCommand = param.First();
                param.RemoveRange(0, 1);
                inputProc.Process(sCommand, param);
            }
        }
    }
}
