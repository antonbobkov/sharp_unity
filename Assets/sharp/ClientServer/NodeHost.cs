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
        public object syncLock = new object();
        
        void ExecuteThread()
        {
            while (true)
            {
                var a = msgs.Take();
                if (a == null)
                    return;
                lock(syncLock)
                    a.Invoke();
            }
        }
        
        BlockingCollection<Action> msgs = new BlockingCollection<Action>();

        public void Add(Action a) { msgs.Add(a); }
        public Action<Action> GetAsDelegate() { return (a) => this.Add(a); }

        public void Start()
        {
            ThreadManager.NewThread(() => this.ExecuteThread(),
                //() => msgs.Add(null),
                () => { },
                "global syncronizer");
        }

    }

    class GlobalHost
    {
        public const int nStartPort = 3000;
        const int nPortTries = 25;

        Dictionary<OverlayHostName, OverlayHost> hosts = new Dictionary<OverlayHostName, OverlayHost>();

        SocketListener sl;
        Action<Action> processQueue;

        public IPEndPoint MyAddress { get; private set; }

        public GlobalHost(Action<Action> processQueue_)
        {
            processQueue = processQueue_;
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
                int i;
                for (i = 0; i < nPortTries; ++i)
                {
                    try
                    {
                        int nPort = nStartPort + i;
                        MyAddress = new IPEndPoint(ip, nPort);
                        sckListen.Bind(MyAddress);
                        Log.LogWriteLine("Listening at {0}:{1}", ip, nPort);
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
                            processQueue(() => this.NewIncomingConnection(info, sck))
                    ), sckListen);
            }
            catch (Exception)
            {
                sckListen.Close();
                throw;
            }
        }

        void NewIncomingConnection(Handshake info, Socket sck)
        {
            MyAssert.Assert(hosts.ContainsKey(info.local.hostname));
            hosts.GetValue(info.local.hostname).NewIncomingConnection(info, sck);
        }

        public void Close()
        {
            foreach (var h in hosts.Values)
                h.Close();

            sl.TerminateThread();
        }
        public OverlayHost NewHost(OverlayHostName hostName, OverlayHost.ProcessorAssigner messageProcessor)
        {
            MyAssert.Assert(!hosts.ContainsKey(hostName));
            OverlayHost host = new OverlayHost(hostName, MyAddress, processQueue, messageProcessor);
            hosts.Add(hostName, host);

            Log.LogWriteLine("New host: {0}", hostName);
            
            return host;
        }
    }

    class OverlayHost
    {
        OverlayHostName hostName;

        Dictionary<OverlayEndpoint, Node> nodes = new Dictionary<OverlayEndpoint, Node>();

        public delegate Node.MessageProcessor ProcessorAssigner(Node n);
        
        public Action<Node> onNewConnectionHook = (n) => { };
        
        ProcessorAssigner messageProcessorAssigner;

        Action<Action> processQueue;

        public IPEndPoint IpAddress { get; private set; }
        public OverlayEndpoint Address { get { return new OverlayEndpoint(IpAddress, hostName); } }

        public OverlayHost(OverlayHostName hostName_, IPEndPoint address_, Action<Action> processQueue_,
            ProcessorAssigner messageProcessorAssigner_)
        {
            hostName = hostName_;
            IpAddress = address_;
            processQueue = processQueue_;
            messageProcessorAssigner = messageProcessorAssigner_;
        }

        /*
        public OverlayHost(OverlayHostName hostName_, IPEndPoint address_, Action<Action> processQueue_,
        MessageProcessor messageProcessor)
            :this(hostName_, address_, processQueue_, (n) => messageProcessor){}
        */

        void ProcessDisconnect(Node n, Exception ioex, DisconnectType ds)
        {
            Log.LogWriteLine("{0} disconnected on {1} ({2})", n.info.remote, ds, (ioex == null) ? "" : ioex.Message);
            RemoveNode(n);
        }
        internal void NewIncomingConnection(Handshake info, Socket sck)
        {
            try
            {
                MyAssert.Assert(info.local.hostname == hostName);

                Node targetNode = FindNode(info.remote);

                bool newConnection = (targetNode == null);
                if (newConnection)
                {
                    targetNode = new Node(info, processQueue, (n, e, t) => this.ProcessDisconnect(n, e, t));
                    AddNode(targetNode);
                }

                MyAssert.Assert(targetNode.readerStatus != Node.ReadStatus.DISCONNECTED);
                MyAssert.Assert(targetNode.writerStatus != Node.WriteStatus.DISCONNECTED);
                MyAssert.Assert(!targetNode.IsClosed);

                if (targetNode.readerStatus != Node.ReadStatus.READY)
                {
                    Log.LogWriteLine("New connection {0} rejected: node already connected", info.remote);
                    sck.Close();
                    return;
                }

                targetNode.AcceptReaderConnection(sck, ProcessMessageWrap(messageProcessorAssigner(targetNode)));

                if (newConnection)
                    onNewConnectionHook.Invoke(targetNode);

                if (targetNode.writerStatus == Node.WriteStatus.READY)
                    ConnectNodeAsync(targetNode);

                if (targetNode.AreBothConnected())
                    OnNewConnectionCompletelyReady(targetNode);
                    
            }
            catch (NodeException) // FIXME
            {
                sck.Close();
                throw;
            }
        }

        Node.MessageProcessor ProcessMessageWrap(Node.MessageProcessor messageProcessor)
        {
            //return messageProcessor;

            return (mt, str, n) =>
                {
                    try
                    {
                        messageProcessor(mt, str, n);
                    }
                    catch (XmlSerializerException e)
                    {
                        Log.LogWriteLine("Error while reading from socket:\n{0}\n\nLast read:{1}", e, Serializer.lastRead.GetData());
                        throw new Exception("Fatal");
                    }
                };
        }
        
        void Sync_OutgoingConnectionReady(Node n)
        {
            if (n.AreBothConnected())
                OnNewConnectionCompletelyReady(n);        
        }
        void OnNewConnectionCompletelyReady(Node n)
        {
            //Log.LogWriteLine("New connection: {0} -> {1}", n.info.local.hostname, n.info.remote);
        }

        public Node ConnectAsync(OverlayEndpoint theirInfo)
        {
            Node targetNode = FindNode(theirInfo);

            Handshake info = new Handshake(new OverlayEndpoint(IpAddress, hostName), theirInfo);

            bool newConnection = (targetNode == null);
            if (newConnection)
            {
                targetNode = new Node(info, processQueue, (n, e, t) => this.ProcessDisconnect(n, e, t));
                AddNode(targetNode);
            }

            MyAssert.Assert(targetNode.readerStatus != Node.ReadStatus.DISCONNECTED);
            MyAssert.Assert(targetNode.writerStatus != Node.WriteStatus.DISCONNECTED);
            MyAssert.Assert(!targetNode.IsClosed);

            if (targetNode.writerStatus == Node.WriteStatus.CONNECTED)
                throw new NodeException("Already connected to " + targetNode.Address);
            else if (targetNode.writerStatus == Node.WriteStatus.CONNECTING)
                throw new NodeException("Connection in progress " + targetNode.Address);
            else
                ConnectNodeAsync(targetNode);

            if (newConnection)
                onNewConnectionHook.Invoke(targetNode);

            return targetNode;
        }
        public Node TryConnectAsync(OverlayEndpoint theirInfo)
        {
            try
            {
                return ConnectAsync(theirInfo);
            }
            catch (NodeException)
            {
                return null;
            }
        }

        void ConnectNodeAsync(Node n)
        {
            n.ConnectAsync(
                            () => this.Sync_OutgoingConnectionReady(n)
                          );
        }

        void DisconnectNode(Node n)
        {
            n.Disconnect();
            // done automatically
            //RemoveNode(n);
        }

        public IEnumerable<Node> GetAllNodes()
        {
            return from n in nodes.Values
                   select n;
        }
        public void Close()
        {
            foreach (var n in GetAllNodes().ToArray())
                DisconnectNode(n);

            MyAssert.Assert(!nodes.Any());
        }
        public Node FindNode(OverlayEndpoint theirInfo)
        {
            try
            {
                return nodes.GetValue(theirInfo);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public void SendMessage(OverlayEndpoint remote, MessageType mt, params object[] objs)
        {
            Node n = FindNode(remote);

            MyAssert.Assert(n != null);

            n.SendMessage(mt, objs);
        }

        public void ConnectSendMessage(OverlayEndpoint remote, MessageType mt, params object[] objs)
        {
            Node n = FindNode(remote);
            if (n == null)
                n = ConnectAsync(remote);

            n.SendMessage(mt, objs);
        }

        public void BroadcastGroup(Func<Node, bool> group, MessageType mt, params object[] objs)
        {
            foreach (Node n in GetAllNodes().Where(group))
                    n.SendMessage(mt, objs);
        }

        public void BroadcastGroup(OverlayHostName name, MessageType mt, params object[] objs)
        {
            BroadcastGroup((n) => n.info.remote.hostname == name, mt, objs);
        }

        public void Broadcast(MessageType mt, params object[] objs)
        {
            BroadcastGroup((n) => true, mt, objs);
        }

        void AddNode(Node n)
        {
            MyAssert.Assert(FindNode(n.info.remote) == null);
            nodes.Add(n.info.remote, n);
        }
        void RemoveNode(Node n)
        {
            MyAssert.Assert(FindNode(n.info.remote) != null);
            nodes.Remove(n.info.remote);
        }
    }
}
