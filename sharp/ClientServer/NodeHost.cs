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
    class ActionSyncronizer
    {
        void ExecuteThread()
        {
            while (true)
                msgs.Take().Invoke();
        }
        
        BlockingCollection<Action> msgs = new BlockingCollection<Action>();

        public void Add(Action a) { msgs.Add(a); }
        public Action<Action> GetAsDelegate() { return (a) => this.Add(a); }

        public ActionSyncronizer()
        {
            new Thread(() => this.ExecuteThread()).Start();
        }
    }

    class NodeHost
    {
        public const int nStartPort = 3000;
        const int nPortTries = 10;

        public DataCollection dc;
        Action<Action> syncronizedActions;

        public static IPAddress GetMyIP()
        {
            IPHostEntry localDnsEntry = Dns.GetHostEntry(Dns.GetHostName());
            return localDnsEntry.AddressList.First
                (ipaddr =>
                    ipaddr.AddressFamily.ToString() == ProtocolFamily.InterNetwork.ToString());        
        }

        public NodeHost(Action<Action> syncronizedActions_)
        {
            syncronizedActions = syncronizedActions_;


            IPAddress ip = GetMyIP();

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
                    DataCollection.LogWriteLine("Listening at {0}:{1}", ip, nPort);
                    break;
                }
                catch (SocketException)
                { }
            }

            if (i == nPortTries)
            {
                throw new Exception("Unsucessful binding to ports");
            }
            sckListen.Listen(10);

            dc = new DataCollection(my_addr, "", syncronizedActions);

            dc.StartListening(sckListen);
        }
    }
}
