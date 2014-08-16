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
        const int nStartPort = 3000;
        const int nPortTries = 10;

        static void PewPewPewThread(BlockingCollection<Action> msgs)
        {
            while (true)
                msgs.Take().Invoke();
        }

        static void Main(string[] args)
        {
            IPHostEntry localDnsEntry = Dns.GetHostEntry(Dns.GetHostName());
            IPAddress ip = localDnsEntry.AddressList.First
                (ipaddr => 
                    ipaddr.AddressFamily.ToString() == ProtocolFamily.InterNetwork.ToString());

 
            Socket sckListen = new Socket(
                    ip.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);

            IPEndPoint my_addr = null;
            int i;
            for (i = 0; i < nPortTries; ++i)
            {
                try
                {
                    int nPort = nStartPort + i;
                    my_addr = new IPEndPoint(ip, nPort);
                    sckListen.Bind(my_addr);
                    Console.WriteLine("Listening at {0}:{1}", ip, nPort);
                    break;
                }
                catch (SocketException)
                { }
            }

            if (i == nPortTries)
            {
                Console.WriteLine("Unsucessful binding to ports");
                return;
            }
            sckListen.Listen(10);

            var msgs = new BlockingCollection<Action>();

            DataCollection dc = new DataCollection(my_addr, "", (action) => msgs.Add(action));

            dc.StartListening(sckListen);

            new Thread(() => PewPewPewThread(msgs)).Start();

            while (true)
            {
                string sInput = Console.ReadLine();
                var param = new List<string>(sInput.Split(' '));
                string sCommand = param.FirstOrDefault();

                if ("message".StartsWith(sCommand))
                {
                    msgs.Add(() =>
                    {
                        if (param.Count < 3)
                            return;
                        string name = param[1];

                        param.RemoveRange(0, 2);

                        string message = String.Join(" ", param.ToArray());

                        Node n = dc.GetNodes().FirstOrDefault(nd => nd.Name == name);
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
                    msgs.Add(() =>
                    {
                        if (param.Count != 2)
                            return;
                        string name = param[1];

                        Console.WriteLine("Name set to \"{0}\"", name);
                        dc.Name = name;

                        foreach (Node n in dc.GetNodes())
                            n.SendMessage(MessageType.NAME, name);
                    });
                }
                else if ("list".StartsWith(sCommand))
                {
                    msgs.Add(() =>
                    {
                        StringBuilder sb = new StringBuilder();

                        foreach (Node n in dc.GetNodes())
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
                    msgs.Add(() =>
                    {
                        string sIpAddr = ip.ToString();
                        string sPort = nStartPort.ToString();
                        bool askForTable = "mconnect".StartsWith(sCommand);

                        if (param.Count >= 2)
                            sIpAddr = param[1];

                        if (param.Count >= 3)
                            sPort = param[2];

                        IPEndPoint ep = new IPEndPoint(IPAddress.Parse(sIpAddr), Convert.ToUInt16(sPort));

                        Console.WriteLine("Connecting to {0}:{1}", ep.Address, ep.Port);

                        if (!dc.Sync_TryConnect(ep))
                            Console.WriteLine("Already connected/connecting");
                        else
                            Console.WriteLine("Connection started");

                        if (askForTable)
                            dc.Sync_AskForTable(ep);
                    });
                }
                else
                    Console.WriteLine("Unknown command");
            }
        }
    }
}
