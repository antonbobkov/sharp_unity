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

        public ActionSyncronizer()
        {
            ThreadManager.NewThread(() => this.ExecuteThread(),
                //() => msgs.Add(null),
                () => { },
                "global syncronizer");
            //new Thread(() => this.ExecuteThread()).Start();
        }
    }

    class NodeCollection
    {
        public const int nStartPort = 3000;
        const int nPortTries = 25;

        Dictionary<OverlayHost, Dictionary<OverlayEndpoint, Node> > nodes =
            new Dictionary<OverlayHost,Dictionary<OverlayEndpoint,Node>>();
        
        SocketListener sl;
        Action<Action> processQueue;
        Action<Node, Stream, MessageType> messageProcessor;
        Action<Node> onNewConnection;

        public IPEndPoint MyAddress { get; private set; }

        public NodeCollection(Action<Action> processQueue_,
            Action<Node, Stream, MessageType> messageProcessor_,
            Action<Node> onNewConnection_)
        {
            processQueue = processQueue_;
            messageProcessor = messageProcessor_;
            onNewConnection = onNewConnection_;
            StartListening();
            //ConnectAsync(myInfo.addr);
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
                            processQueue(() => this.Sync_NewIncomingConnection(info, sck))
                    ), sckListen);
            }
            catch (Exception)
            {
                sckListen.Close();
                throw;
            }
        }

        void Sync_ProcessDisconnect(Node n, Exception ioex, DisconnectType ds)
        {
            Log.LogWriteLine("{0} disconnected on {1} ({2})", n.Address, ds, (ioex == null) ? "" : ioex.Message);
            RemoveNode(n);
        }
        void Sync_NewIncomingConnection(Handshake info, Socket sck)
        {
            try
            {
                Node targetNode = FindNode(info.local.host, info.remote);

                if (targetNode == null)
                {
                    targetNode = new Node(info, processQueue, (n, e, t) => this.Sync_ProcessDisconnect(n, e, t));
                    AddNode(targetNode);
                }

                MyAssert.Assert(targetNode.readerStatus != Node.ReadStatus.DISCONNECTED);
                MyAssert.Assert(targetNode.writerStatus != Node.WriteStatus.DISCONNECTED);
                MyAssert.Assert(!targetNode.IsClosed);

                if (targetNode.readerStatus != Node.ReadStatus.READY)
                {
                    Log.LogWriteLine("New connection {0} rejected: node already connected", info.remote.addr);
                    sck.Close();
                    return;
                }

                targetNode.AcceptReaderConnection(sck, ProcessMessageWrap);

                if (targetNode.writerStatus == Node.WriteStatus.READY)
                    ConnectNodeAsync(targetNode);

                if (targetNode.AreBothConnected())
                    OnNewConnectionCompletelyReady(targetNode);
                    
            }
            catch (Exception)
            {
                sck.Close();
                throw;
            }
        }

        void ProcessMessageWrap(Node n, Stream str, MessageType mt)
        {
            //try
            //{
                messageProcessor(n, str, mt);
            //}
            /*
            catch (Exception e)
            {
                processQueue.Invoke( () =>
                {
                    Log.LogWriteLine("Error while reading from socket: {0}\nNode {1}\nLast read:{2}", e.ToString(), n.FullDescription(), Serializer.lastRead);
                    DisconnectNode(n);
                });

                throw new IOException("Unhandled error while processing message", e);
            }
             * */
        }
        
        void Sync_OutgoingConnectionReady(Node n)
        {
            if (n.AreBothConnected())
                OnNewConnectionCompletelyReady(n);        
        }
        void OnNewConnectionCompletelyReady(Node n)
        {
            Log.LogWriteLine("New connection: {0}", n.Address);
            onNewConnection.Invoke(n);
        }

        public Node ConnectAsync(OverlayHost ourHost, OverlayEndpoint theirInfo)
        {
            Node targetNode = FindNode(ourHost, theirInfo);

            Handshake info = new Handshake(new OverlayEndpoint(MyAddress, ourHost), theirInfo);

            if (targetNode == null)
            {
                targetNode = new Node(info, processQueue, (n, e, t) => this.Sync_ProcessDisconnect(n, e, t));
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

            return targetNode;
        }
        public Node TryConnectAsync(OverlayHost ourHost, OverlayEndpoint theirInfo)
        {
            try
            {
                return ConnectAsync(ourHost, theirInfo);
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
            return from dict in nodes.Values
                   from n in dict.Values
                   select n;
        }
        public void Close()
        {
            foreach (var n in GetAllNodes().ToArray())
                DisconnectNode(n);

            MyAssert.Assert(!nodes.Any());

            sl.TerminateThread();
        }
        public Node FindNode(OverlayHost ourHost, OverlayEndpoint theirInfo)
        {
            try
            {
                return nodes.GetValue(ourHost).GetValue(theirInfo);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        void AddNode(Node n)
        {
            MyAssert.Assert(FindNode(n.info.local.host, n.info.remote) == null);
            nodes[n.info.local.host].Add(n.info.remote, n);
        }
        void RemoveNode(Node n)
        {
            MyAssert.Assert(FindNode(n.info.local.host, n.info.remote) != null);
            nodes[n.info.local.host].Remove(n.info.remote);
        }
    }
}
