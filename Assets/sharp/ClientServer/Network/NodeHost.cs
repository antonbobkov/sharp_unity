using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Tools;


namespace Network
{
    class GlobalHost
    {
        public const int nStartPort = 3000;
        const int nPortTries = 25;

        Dictionary<OverlayHostName, OverlayHost> hosts = new Dictionary<OverlayHostName, OverlayHost>();

        SocketListener sl;
        ActionSyncronizerProxy processQueue;

        private IPAddress myIP;

        public IPEndPoint MyAddress { get; private set; }

        private ILog log;

        public GlobalHost(ActionSyncronizerProxy processQueue_, IPAddress myIP)
        {
            this.myIP = myIP;
            processQueue = processQueue_;
            log = MasterFileLog.GetLog("network", "globalhost.log");

            StartListening();
        }

        void StartListening()
        {
            IPAddress ip = myIP;

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
                        Log.EntryConsole(log, "Listening at {0}:{1}", ip, nPort);
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

                sl = new SocketListener(processQueue,
                        (info, sck) => this.NewIncomingConnection(info, sck),
                        sckListen);
            }
            catch (Exception)
            {
                sckListen.Close();
                throw;
            }
        }

        void NewIncomingConnection(Handshake info, Socket sck)
        {
            Log.EntryNormal(log, "New connection: " + info);
            MyAssert.Assert(hosts.ContainsKey(info.local.hostname));
            hosts.GetValue(info.local.hostname).NewIncomingConnection(info, sck);
        }

        public void Close()
        {
            Log.EntryNormal(log, "Closing all");
            
            foreach (var h in hosts.Values)
                h.Close();

            sl.TerminateThread();
        }
        public OverlayHost NewHost(OverlayHostName hostName, OverlayHost.ProcessorAssigner messageProcessor, MemoryStream extraHandshakeInfo)
        {
            MyAssert.Assert(!hosts.ContainsKey(hostName));
            OverlayHost host = new OverlayHost(hostName, MyAddress, processQueue, messageProcessor, extraHandshakeInfo);
            hosts.Add(hostName, host);

            Log.EntryNormal(log, "New host: " + hostName);
            
            return host;
        }

        //private void OnHandshakeError(Exception e)
        //{
        //    Log.EntryConsole
        //}
    }

    class OverlayHost
    {
        OverlayHostName hostName;
        MemoryStream extraHandshakeInfo;

        Dictionary<OverlayEndpoint, Node> nodes = new Dictionary<OverlayEndpoint, Node>();

        public delegate Node.MessageProcessor ProcessorAssigner(Node n, MemoryStream extraInfo);
        
        public Action<Node> onNewConnectionHook = (n) => { };
        
        ProcessorAssigner messageProcessorAssigner;

        ActionSyncronizerProxy processQueue;

        public IPEndPoint IpAddress { get; private set; }
        public OverlayEndpoint Address { get { return new OverlayEndpoint(IpAddress, hostName); } }

        private ILog log;

        public OverlayHost(OverlayHostName hostName_, IPEndPoint address_, ActionSyncronizerProxy processQueue_,
            ProcessorAssigner messageProcessorAssigner_, MemoryStream extraHandshakeInfo_)
        {
            hostName = hostName_;
            IpAddress = address_;
            processQueue = processQueue_;
            messageProcessorAssigner = messageProcessorAssigner_;

            extraHandshakeInfo = extraHandshakeInfo_;

            log = MasterFileLog.GetLog("network", hostName.ToString() + ".log");
        }

        void ProcessDisconnect(Node n, Exception ioex, DisconnectType ds)
        {
            Log.Entry(log, LogLevel.ERROR, (ds == DisconnectType.WRITE_CONNECT_FAIL) ? LogParam.CONSOLE : LogParam.NO_OPTION,
                "{0} disconnected on {1} ({2})", n.info.remote, ds, (ioex == null) ? "" : ioex.Message);
            
            RemoveNode(n);
        }
        internal void NewIncomingConnection(Handshake info, Socket sck)
        {
            try
            {
                MyAssert.Assert(info.local.hostname == hostName);

                MemoryStream remoteExtraInfo = info.ExtraInfo;
                info.ExtraInfo = extraHandshakeInfo;

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
                    Log.Console("New connection {0} rejected: node already connected", info.remote);
                    sck.Close();
                    return;
                }

                targetNode.AcceptReaderConnection(sck, ProcessMessageWrap(messageProcessorAssigner(targetNode, remoteExtraInfo)));

                if (newConnection)
                    onNewConnectionHook.Invoke(targetNode);

                if (targetNode.writerStatus == Node.WriteStatus.READY)
                    targetNode.ConnectAsync();
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

            return (str, n) =>
                {
                    if (n.IsClosed)
                        return;

                    messageProcessor(str, n);

                    //try
                    //{
                    //    messageProcessor(str, n);
                    //}
                    //catch (XmlSerializerException e)
                    //{
                    //    Log.Console("Error while reading from socket:\n{0}\n\nLast read:{1}", e, Serializer.lastRead.GetData());
                    //    throw new Exception("Fatal");
                    //}
                };
        }
        
        public Node ConnectAsync(OverlayEndpoint theirInfo)
        {
            Node targetNode = FindNode(theirInfo);

            Handshake info = new Handshake(Address, theirInfo, extraHandshakeInfo);

            bool newConnection = (targetNode == null);
            if (newConnection)
            {
                targetNode = new Node(info, processQueue, (n, e, t) => this.ProcessDisconnect(n, e, t));
                AddNode(targetNode);
            }

            MyAssert.Assert(targetNode.readerStatus != Node.ReadStatus.DISCONNECTED);
            MyAssert.Assert(targetNode.writerStatus != Node.WriteStatus.DISCONNECTED);
            MyAssert.Assert(!targetNode.IsClosed);

            if (targetNode.writerStatus == Node.WriteStatus.WRITING)
                throw new NodeException("Already connected/connecting to " + targetNode.Address);
            else
                targetNode.ConnectAsync();

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

        public bool TryCloseNode(OverlayEndpoint remote)
        {
            Node n = FindNode(remote);

            if (n != null)
            {
                MyAssert.Assert(!n.IsClosed);
                n.Disconnect();
                return true;
            }

            return false;
        }

        public void SendMessage(OverlayEndpoint remote, NetworkMessage nm)
        {
            Node n = FindNode(remote);

            MyAssert.Assert(n != null);

            n.SendMessage(nm);
        }

        public void ConnectSendMessage(OverlayEndpoint remote, NetworkMessage nm)
        {
            Node n = FindNode(remote);
            if (n == null)
                n = ConnectAsync(remote);

            n.SendMessage(nm);
        }

        public void BroadcastGroup(Func<Node, bool> group, NetworkMessage nm)
        {
            foreach (Node n in GetAllNodes().Where(group))
                n.SendMessage(nm);
        }

        public void BroadcastGroup(OverlayHostName name, NetworkMessage nm)
        {
            BroadcastGroup((n) => n.info.remote.hostname == name, nm);
        }

        public void Broadcast(NetworkMessage nm)
        {
            BroadcastGroup((n) => true, nm);
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
