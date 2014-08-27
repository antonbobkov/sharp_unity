using System;
using ServerClient.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ServerClient
{
    class Program
    {
        static Random rand = new Random();
        
        static void PlayerRandomMove(Game g, Player p)
        {
            Point[] moves = { new Point(-1, 0),
                              new Point(1, 0),
                              new Point(0, 1),
                              new Point(0, -1)};
            Point newPosition = p.pos + moves[rand.Next(0, moves.Length)];

            try
            {
                if (!g.world[newPosition.x, newPosition.y].solid)
                    p.pos = newPosition;
            }
            catch (IndexOutOfRangeException)
            {}
        }

        static void RepeatedAction(Action a, int period)
        {
            while (true)
            {
                a.Invoke();
                Thread.Sleep(1000);
            }
        }

        static void MovingThread(Action<Action> sync, Game g, Player p)
        {
            while (true)
            {
                sync.Invoke(() =>
                {
                    PlayerRandomMove(g, p);
                });
                Thread.Sleep(1000);
            }
        }
        static void Main2(string[] args)
        {
            ServerClient.Random r = new ServerClient.Random();
            
            while(true)
                Console.WriteLine("{0}", r.NextDouble());
        }

        static void Main(string[] args)
        {
            ActionSyncronizer sync = new ActionSyncronizer();
            NodeHost myHost = new NodeHost(sync.GetAsDelegate());

            //DataCollection.LogWriteLine("{0}", new System.Random(12).Next());

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
                            DataCollection.LogWriteLine("Invalid name");
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

                        DataCollection.LogWriteLine("Name set to \"{0}\"", name);
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

                        DataCollection.LogWriteLine("Connecting to {0}:{1}", ep.Address, ep.Port);

                        if (!myHost.dc.Sync_TryConnect(ep))
                            DataCollection.LogWriteLine("Already connected/connecting");
                        else
                            DataCollection.LogWriteLine("Connection started");

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
                else
                    DataCollection.LogWriteLine("Unknown command");
            }
        }
    }
}
