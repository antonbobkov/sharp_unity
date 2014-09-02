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
            new Thread(() => this.ExecuteThread()).Start();
        }
    }

    

    class NodeCollection
    {
        public const int nStartPort = 3000;
        const int nPortTries = 25;

        Dictionary<string, Node> nodes = new Dictionary<string, Node>();
        Handshake myInfo;
        SocketListener sl;
        Action<Action> processQueue;
        Action<Node, Stream, MessageType> messageProcessor;
        Action<Node> onNewConnection;

        public IPEndPoint MyAddress { get { return myInfo.addr; } }

        public NodeCollection(Action<Action> processQueue_, Action<Node, Stream, MessageType> messageProcessor_, Action<Node> onNewConnection_)
        {
            processQueue = processQueue_;
            messageProcessor = messageProcessor_;
            onNewConnection = onNewConnection_;
            StartListening();
            ConnectAsync(myInfo.addr);
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

                myInfo = new Handshake(my_addr);

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
                    Log.LogWriteLine("New connection {0} rejected: node already connected", theirInfo.addr);
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
            try
            {
                messageProcessor(n, str, mt);
            }
            catch (Exception e)
            {
                processQueue.Invoke( () =>
                {
                    Log.LogWriteLine("Error while reading from socket: {0}\nNode {1}\nLast read:{2}", e.ToString(), n.FullDescription(), Serializer.lastRead);
                    DisconnectNode(n);
                });

                throw new IOException("Unhandled error while processing message", e);
            }
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

        public Node ConnectAsync(IPEndPoint their_addr)
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

            return targetNode;
        }
        public Node TryConnectAsync(IPEndPoint their_addr)
        {
            try
            {
                return ConnectAsync(their_addr);
            }
            catch (NodeException)
            {
                return null;
            }
        }

        void ConnectNodeAsync(Node n)
        {
            n.ConnectAsync(
                            () => this.Sync_OutgoingConnectionReady(n),
                            myInfo
                          );
        }

        void DisconnectNode(Node n)
        {
            n.Disconnect();
            RemoveNode(n);
        }

        public IEnumerable<Node> GetAllNodes()
        {
            return from n in nodes select n.Value;
        }
        public void Close()
        {
            foreach (var n in GetAllNodes().ToArray())
                DisconnectNode(n);

            Debug.Assert(!nodes.Any());

            sl.TerminateThread();
        }
        public Node FindNode(IPEndPoint addr)
        {
            Node n = null;
            nodes.TryGetValue(addr.ToString(), out n);
            return n;
        }

        void AddNode(Node n)
        {
            Debug.Assert(!nodes.ContainsKey(n.Address.ToString()));
            nodes.Add(n.Address.ToString(), n);
        }
        void RemoveNode(Node n)
        {
            Debug.Assert(nodes.ContainsKey(n.Address.ToString()));
            nodes.Remove(n.Address.ToString());
        }
    }
}
