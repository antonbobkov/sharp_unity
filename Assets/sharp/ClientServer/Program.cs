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
            
            PlayerMoveInfo mv = new PlayerMoveInfo(p.id, newPosition);
            if (g.CheckValidMove(mv) == MoveValidity.VALID)
                if (!a.roles.validators[g.worldValidator].IsClosed)
                    a.Move(mv);
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
            foreach (var id in all.myRole.player)
            {
                Guid idCopy = id;
                new Thread(() => RepeatedAction(all.sync.GetAsDelegate(), () => PlayerRandomMove(all, idCopy), rand.Next(300, 700))).Start();
            }
        }
        
        static void Main(string[] args)
        {
            Aggregate all = new Aggregate();
            all.sync.Add(() =>
            {
                //if (all.peers.MyAddress.Port == NodeCollection.nStartPort)
                    all.myRole.validator.Add(Guid.NewGuid());
                //else
                {
                    for (int i = 0; i < 2; ++i)
                        all.myRole.player.Add(Guid.NewGuid());
                    Ai(all);
                }

                Console.WriteLine();
                Console.Write(all.myRole.PrintRoles());
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
                foreach (var kv in from n in all.roles.players orderby n.Key.ToString() select n)
                    Console.WriteLine("\t{0}\t{1}", kv.Key, kv.Value.Address);
                Console.WriteLine("Validators:");
                foreach (var kv in from n in all.roles.validators orderby n.Key.ToString() select n)
                    Console.WriteLine("\t{0}\t{1}", kv.Key, kv.Value.Address);
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
                new Thread(() => RepeatedAction(all.sync.GetAsDelegate(), () => all.game.ConsoleOut(), 500)).Start();
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


            /*
            while (true)
            {
                string sInput = Console.ReadLine();
                var param = new List<string>(sInput.Split(' '));
                string sCommand = param.FirstOrDefault();

                if ("message".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        if (param.Count < 3)
                            return;
                        string name = param[1];

                        param.RemoveRange(0, 2);

                        string message = String.Join(" ", param.ToArray());

                        Node n = myHost.dc.GetReadyNodes().FirstOrDefault(nd => nd.Name == name);
                        if (n == null)
                        {
                            Log.LogWriteLine("Invalid name");
                            return;
                        }

                        n.SendMessage(MessageType.MESSAGE, message);
                    });
                }
                else if ("name".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        if (param.Count != 2)
                            return;
                        string name = param[1];

                        Log.LogWriteLine("Name set to \"{0}\"", name);
                        myHost.dc.Name = name;

                        foreach (Node n in myHost.dc.GetReadyNodes())
                            n.SendMessage(MessageType.NAME, name);
                    });
                }
                else if ("list".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        StringBuilder sb = new StringBuilder();

                        foreach (Node n in myHost.dc.GetReadyNodes())
                        {
                            sb.Append(n.Address.ToString());
                            if (n.Name != n.Address.ToString())
                                sb.Append("\t" + n.Name);
                            sb.Append("\t" + n.Id.ToString("B"));
                            sb.Append("\n");
                        }

                        Console.Write(sb.ToString());
                    });
                }
                else if ("connect".StartsWith(sCommand) || "mconnect".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        string sIpAddr = NodeHost.GetMyIP().ToString();
                        string sPort = NodeHost.nStartPort.ToString();
                        bool askForTable = "mconnect".StartsWith(sCommand);

                        if (param.Count >= 2)
                            sIpAddr = param[1];

                        if (param.Count >= 3)
                            sPort = param[2];

                        IPEndPoint ep = new IPEndPoint(IPAddress.Parse(sIpAddr), Convert.ToUInt16(sPort));

                        Log.LogWriteLine("Connecting to {0}:{1}", ep.Address, ep.Port);

                        if (!myHost.dc.Sync_TryConnect(ep))
                            Log.LogWriteLine("Already connected/connecting");
                        else
                            Log.LogWriteLine("Connection started");

                        if (askForTable)
                            myHost.dc.Sync_AskForTable(ep);
                    });
                }
                else if ("draw".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        if (myHost.dc.game == null)
                            return;

                        myHost.dc.game.ConsoleOut();
                    });
                }
                else if ("drawing".StartsWith(sCommand))
                {
                    new Thread(() => RepeatedAction(() =>
                        sync.Add(() =>
                        {
                            if (myHost.dc.game == null)
                                return;

                            Console.WriteLine();
                            myHost.dc.game.ConsoleOut();
                        })
                    , 500)).Start();
                }
                else if ("generate".StartsWith(sCommand))
                {
                    GameInitializer init = new GameInitializer(System.DateTime.Now.Millisecond, myHost.dc.GetReadyNodes().Count() + 1);
                    myHost.dc.Broadcast(MessageType.GENERATE_GAME, init);
                    myHost.dc.Sync_GenerateGame(init);
                }
                else if ("ai".StartsWith(sCommand))
                {
                    new Thread(() => RepeatedAction(() =>
                        sync.Add(() =>
                        {
                            PlayerRandomMove(myHost.dc.game, myHost.dc.game.players[myHost.dc.Id]);
                            myHost.dc.Sync_UpdateMyPosition();
                        })
                    , 1000)).Start();
                }
                else if ("exit".StartsWith(sCommand))
                {
                    sync.Add(null);
                    myHost.dc.TerminateThreads();
                    Console.ReadLine();
                    return;
                }
                else
                    Log.LogWriteLine("Unknown command");
            }
             * */
        }
    }
}
