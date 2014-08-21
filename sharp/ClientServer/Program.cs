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


        static void Main(string[] args)
        {
            ActionSyncronizer sync = new ActionSyncronizer();
            NodeHost myHost = new NodeHost(sync.GetAsDelegate());

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

                        Node n = myHost.dc.GetNodes().FirstOrDefault(nd => nd.Name == name);
                        if (n == null)
                        {
                            Console.WriteLine("Invalid name");
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

                        Console.WriteLine("Name set to \"{0}\"", name);
                        myHost.dc.Name = name;

                        foreach (Node n in myHost.dc.GetNodes())
                            n.SendMessage(MessageType.NAME, name);
                    });
                }
                else if ("list".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        StringBuilder sb = new StringBuilder();

                        foreach (Node n in myHost.dc.GetNodes())
                        {
                            sb.Append(n.Address.ToString());
                            if (n.Name != "")
                                sb.Append("\t" + n.Name);
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

                        Console.WriteLine("Connecting to {0}:{1}", ep.Address, ep.Port);

                        if (!myHost.dc.Sync_TryConnect(ep))
                            Console.WriteLine("Already connected/connecting");
                        else
                            Console.WriteLine("Connection started");

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
                else if ("generate".StartsWith(sCommand))
                {
                    GameInitializer init = new GameInitializer(System.DateTime.Now.Millisecond);
                    myHost.dc.Broadcast(MessageType.GENERATE_GAME, init);
                    myHost.dc.Sync_GenerateGame(init);
                }
                else
                    Console.WriteLine("Unknown command");
            }
        }
    }
}
