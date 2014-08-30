using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ServerClient.Concurrent;


namespace ServerClient
{
    class ActionSyncronizer
    {
        void ExecuteThread()
        {
            while (true)
            {
                var a = msgs.Take();
                if (a == null)
                    return;
                a.Invoke();
            }
        }
        
        BlockingCollection<Action> msgs = new BlockingCollection<Action>();

        public void Add(Action a) { msgs.Add(a); }
        public Action<Action> GetAsDelegate() { return (a) => this.Add(a); }

        public ActionSyncronizer()
        {
            new Thread(() => this.ExecuteThread()).Start();
        }
    }

    class NetTools
    {
        public static IPAddress GetMyIP()
        {
            IPHostEntry localDnsEntry = Dns.GetHostEntry(Dns.GetHostName());
            return localDnsEntry.AddressList.First
                (ipaddr =>
                    ipaddr.AddressFamily.ToString() == ProtocolFamily.InterNetwork.ToString());
        }
    }

    class NodeCollection
    {
        public const int nStartPort = 3000;
        const int nPortTries = 10;

        Dictionary<IPEndPointSer, Node> nodes = new Dictionary<IPEndPointSer, Node>();
        Handshake myInfo;
        SocketListener sl;
        Action<Action> processQueue;
        Action<IPEndPointSer, Stream, MessageType> messageProcessor;

        public NodeCollection(IPEndPoint myAddr, string name, Action<Action> processQueue_, Action<IPEndPointSer, Stream, MessageType> messageProcessor_)
        {
            myInfo = new Handshake(new IPEndPointSer(myAddr));
            processQueue = processQueue_;
            messageProcessor = messageProcessor_;
            StartListening();
        }

        void StartListening()
        {
            IPAddress ip = NetTools.GetMyIP();

            Socket sckListen = new Socket(
                    ip.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);

            try
            {
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

                sl = new SocketListener(
                    ConnectionProcessor.ProcessWithHandshake(
                        (info, sck) =>
                            processQueue(() => this.Sync_NewIncomingConnection(info, sck))
                    ), sckListen);
            }
            catch (Exception)
            {
                sckListen.Dispose();
                throw;
            }
        }

        void Sync_ProcessDisconnect(Node n, Exception ioex, DisconnectType ds)
        {
            Console.WriteLine("{0} disconnected on {1} ({2})", n.Address, ds, (ioex == null) ? "" : ioex.Message);
            RemoveNode(n);
        }
        void Sync_NewIncomingConnection(Handshake theirInfo, Socket sck)
        {
            try
            {
                Node targetNode = FindNode(theirInfo.addr);

                if (targetNode == null)
                {
                    targetNode = new Node(theirInfo.addr, processQueue, (n, e, t) => this.Sync_ProcessDisconnect(n, e, t));
                    AddNode(targetNode);
                }

                Debug.Assert(targetNode.readerStatus != Node.ReadStatus.DISCONNECTED);
                Debug.Assert(targetNode.writerStatus != Node.WriteStatus.DISCONNECTED);
                Debug.Assert(!targetNode.IsClosed);

                if (targetNode.readerStatus != Node.ReadStatus.READY)
                {
                    Console.WriteLine("New connection {0} rejected: node already connected", theirInfo.addr);
                    sck.Dispose();
                    return;
                }

                targetNode.AcceptReaderConnection(sck, messageProcessor);

                if (targetNode.writerStatus == Node.WriteStatus.READY)
                    ConnectNodeAsync(targetNode);

                if (targetNode.AreBothConnected())
                    OnNewConnectionCompletelyReady(targetNode);
                    
            }
            catch (Exception)
            {
                sck.Dispose();
                throw;
            }
        }

        void Sync_OutgoingConnectionReady(Node n)
        {
            if (n.AreBothConnected())
                OnNewConnectionCompletelyReady(n);        
        }
        void OnNewConnectionCompletelyReady(Node n)
        {
            Console.WriteLine("New connection: {0}", n.Address);
        }

        public void ConnectAsync(IPEndPointSer their_addr)
        {
            Node targetNode = FindNode(their_addr);

            if (targetNode == null)
            {
                targetNode = new Node(their_addr, processQueue, (n, e, t) => this.Sync_ProcessDisconnect(n, e, t));
                AddNode(targetNode);
            }

            Debug.Assert(targetNode.readerStatus != Node.ReadStatus.DISCONNECTED);
            Debug.Assert(targetNode.writerStatus != Node.WriteStatus.DISCONNECTED);
            Debug.Assert(!targetNode.IsClosed);

            if (targetNode.writerStatus == Node.WriteStatus.CONNECTED)
                throw new NodeException("Already connected to " + their_addr.ToString());
            else if (targetNode.writerStatus == Node.WriteStatus.CONNECTING)
                throw new NodeException("Connection in progress " + their_addr.ToString());
            else
                ConnectNodeAsync(targetNode);       
        }
        public bool TryConnectAsync(IPEndPointSer their_addr)
        {
            try
            {
                ConnectAsync(their_addr);
            }
            catch (NodeException)
            {
                return false;
            }

            return true;
        }

        void ConnectNodeAsync(Node n)
        {
            n.ConnectAsync(
                            () => this.Sync_OutgoingConnectionReady(n),
                            myInfo
                          );
        }

        void AddNode(Node n)
        {
            Debug.Assert( !nodes.ContainsKey(n.Address) );
            nodes.Add(n.Address, n);
        }
        void RemoveNode(Node n)
        {
            Debug.Assert(nodes.ContainsKey(n.Address));
            nodes.Remove(n.Address);
        }
        Node FindNode(IPEndPointSer addr)
        {
            Node n = null;
            nodes.TryGetValue(addr, out n);
            return n;
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
