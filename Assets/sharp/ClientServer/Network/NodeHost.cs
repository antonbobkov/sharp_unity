﻿using System;
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
        private const int nPortTries = 25;

        private Dictionary<OverlayHostName, OverlayHost> hosts = new Dictionary<OverlayHostName, OverlayHost>();

        private SocketListener sl;
        private ActionSyncronizerProxy processQueue;
        private TimerThread tt;

        private IPAddress myIP;

        public IPEndPoint MyAddress { get; private set; }

        private ILog log;

        public GlobalHost(ActionSyncronizerProxy processQueue_, IPAddress myIP, TimerThread tt)
        {
            this.myIP = myIP;
            this.tt = tt;
            processQueue = processQueue_;
            log = MasterLog.GetFileLog("network", "globalhost.log");

            StartListening();
        }

        private void StartListening()
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

        private void NewIncomingConnection(Handshake info, Socket sck)
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
        public OverlayHost NewHost(OverlayHostName hostName, OverlayHost.ProcessorAssigner messageProcessor, MemoryStream extraHandshakeInfo,
            TimeSpan inactivityPeriod)
        {
            MyAssert.Assert(!hosts.ContainsKey(hostName));
            
            OverlayHost host = new OverlayHost(hostName, MyAddress, processQueue, messageProcessor, extraHandshakeInfo,
                tt, inactivityPeriod);

            hosts.Add(hostName, host);

            Log.EntryConsole(log, "New host: " + hostName);
            
            return host;
        }

        //private void OnHandshakeError(Exception e)
        //{
        //    Log.EntryConsole
        //}

        public int CountHosts()
        {
            return hosts.Count();
        }

        public int CountConnectedNodes()
        {
            return hosts.Values.Sum(n => n.CountConnectedNodes());
        }
    }

    class NodeProcessors
    {
        public Node.MessageProcessor Message { get; set; }
        public Node.DisconnectProcessor Disconnect { get; set; }

        public NodeProcessors(Node.MessageProcessor message, Node.DisconnectProcessor disconnect)
        {
            this.Message = message;
            this.Disconnect = disconnect;
        }
    }

    class OverlayHost
    {
        OverlayHostName hostName;
        MemoryStream extraHandshakeInfo;

        Dictionary<OverlayEndpoint, Node> nodes = new Dictionary<OverlayEndpoint, Node>();

        public delegate NodeProcessors ProcessorAssigner(Node n, MemoryStream extraInfo);
        
        public Action<Node> onNewConnectionHook = (n) => { };
        
        ProcessorAssigner processorAssigner;

        ActionSyncronizerProxy processQueue;

        public IPEndPoint IpAddress { get; private set; }
        public OverlayEndpoint Address { get { return new OverlayEndpoint(IpAddress, hostName); } }

        private ILog log;
        private TimeSpan inactivityPeriod;

        public OverlayHost(OverlayHostName hostName, IPEndPoint address, ActionSyncronizerProxy processQueue,
            ProcessorAssigner messageProcessorAssigner, MemoryStream extraHandshakeInfo,
            TimerThread tt, TimeSpan inactivityPeriod)
        {
            this.inactivityPeriod = inactivityPeriod;
            this.hostName = hostName;
            this.IpAddress = address;
            this.processQueue = processQueue;
            this.processorAssigner = messageProcessorAssigner;

            this.extraHandshakeInfo = extraHandshakeInfo;

            log = MasterLog.GetFileLog("network", hostName.ToString() + ".log");

            tt.AddAction(this.DisconnectInactiveNodes);
        }

        private void DisconnectInactiveNodes()
        {
            if (inactivityPeriod == TimeSpan.Zero)
                return;
            
            DateTime timeNow = DateTime.Now;
            
            foreach(Node n in nodes.Values)
                if (n.IsConnected())
                {
                    if (timeNow.Subtract(n.LastUsed) > inactivityPeriod)
                    {
                        Log.EntryVerbose(log, n.info.remote + " disconnecting due to inactivity");

                        n.UpdateUseTime();
                        n.SoftDisconnect();
                    }
                }
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
                    targetNode = new Node(info, processQueue, null);
                    AddNode(targetNode);
                }

                NodeProcessors proc = processorAssigner(targetNode, remoteExtraInfo);
                proc.Message = ProcessMessageWrap(proc.Message);
                proc.Disconnect = ProcessDisconnectWrap(proc.Disconnect);

                targetNode.notifyDisonnect = proc.Disconnect;

                MyAssert.Assert(targetNode.readerStatus != Node.ReadStatus.DISCONNECTED);
                MyAssert.Assert(targetNode.writerStatus != Node.WriteStatus.DISCONNECTED);
                MyAssert.Assert(!targetNode.IsClosed);

                if (targetNode.readerStatus != Node.ReadStatus.READY)
                {
                    Log.Console("New connection {0} rejected: node already connected", info.remote);
                    sck.Close();
                    return;
                }

                targetNode.AcceptReaderConnection(sck, proc.Message);

                if (newConnection)
                    onNewConnectionHook.Invoke(targetNode);

                //if (targetNode.writerStatus == Node.WriteStatus.READY)
                //    targetNode.ConnectAsync();
            }
            catch (NodeException) // FIXME
            {
                sck.Close();
                throw;
            }
        }

        Node.MessageProcessor ProcessMessageWrap(Node.MessageProcessor messageProcessor)
        {
            return (str, n) =>
                {
                    if (n.IsClosed)
                        return;

                    n.UpdateUseTime();

                    messageProcessor(str, n);
                };
        }

        Node.DisconnectProcessor ProcessDisconnectWrap(Node.DisconnectProcessor disconnectProcessor)
        {
            return (di) =>
            {
                Log.Entry(log, 0, (di.disconnectType == DisconnectType.WRITE_CONNECT_FAIL) ? LogParam.CONSOLE : LogParam.NO_OPTION,
                    "{0} disconnected on {1} ({2})", di.node.info.remote, di.disconnectType, (di.exception == null) ? "" : di.exception.Message);

                RemoveNode(di.node);

                disconnectProcessor(di);
            };
        }

        public Node ConnectAsync(OverlayEndpoint theirInfo, Node.DisconnectProcessor dp)
        {
            dp = ProcessDisconnectWrap(dp);
            Node targetNode = FindNode(theirInfo);

            Handshake info = new Handshake(Address, theirInfo, extraHandshakeInfo);

            bool newConnection = (targetNode == null);
            if (newConnection)
            {
                targetNode = new Node(info, processQueue, dp);
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
        public Node TryConnectAsync(OverlayEndpoint theirInfo, Node.DisconnectProcessor dp)
        {
            try
            {
                return ConnectAsync(theirInfo, dp);
            }
            catch (NodeException)
            {
                return null;
            }
        }

        public void SoftDisconnect(OverlayEndpoint theirInfo)
        {
            Node targetNode = FindNode(theirInfo);

            MyAssert.Assert(targetNode != null);

            targetNode.SoftDisconnect();
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

        //public void ConnectSendMessage(OverlayEndpoint remote, NetworkMessage nm)
        //{
        //    Node n = FindNode(remote);
        //    if (n == null)
        //        n = ConnectAsync(remote);

        //    n.SendMessage(nm);
        //}

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

        public void PrintStats()
        {
            foreach (var n in nodes.Values)
                if (n.IsConnected())
                    Console.WriteLine(n.info.remote);
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

        public int CountConnectedNodes()
        {
            return (from n in nodes.Values
                    where n.IsConnected()
                    select n).Count();

            //return nodes.Values.Where(n => n.IsConnected()).Count();
                    
        }
    }
}
