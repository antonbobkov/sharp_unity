using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;


namespace ServerClient
{
    class DataCollection
    {
        List<Node> nodes = new List<Node>();
        Handshake my_info;

        public IEnumerable<Node> GetNodes() { return nodes; }

        public string Name
        {
            get { return my_info.name; }
            set { my_info.name = value; }
        }

        Action<Action> processQueue;

        public DataCollection(IPEndPoint myAddr, string name, Action<Action> processQueue_)
        {
            my_info = new Handshake(myAddr, name);
            processQueue = processQueue_;
        }

        public void StartListening(Socket sckListen)
        {
            SocketListener sl = new SocketListener(
                ConnectionProcessor.ProcessWithHandshake(
                    (info, sck) =>
                        processQueue(() => this.Sync_NewIncomingConnection(info, sck))
                ), sckListen);        
        }

        Node NodeByEP(IPEndPoint ep)
        {
            var res = from nd in nodes
                      where nd.Address.Equals(ep)
                      select nd;
            int n = res.Count();

            if (n > 1)
                throw new InvalidDataException("several nodes with same endpoint " + res.First().Address.ToString());
            else
                return res.FirstOrDefault();
        }

        void ProcessMessage(IPEndPoint their_addr, Stream stm, MessageType mt)
        {
            if (mt == MessageType.MESSAGE)
            {
                string msg = (string)SocketReader.ReadSerializedMessage(stm);

                processQueue(() => this.Sync_NewMessage(this.NodeByEP(their_addr), msg));
            }
            else if (mt == MessageType.NAME)
            {
                string msg = (string)SocketReader.ReadSerializedMessage(stm);

                processQueue(() => this.Sync_NewName(this.NodeByEP(their_addr), msg));
            }
            else
            {
                throw new InvalidOperationException("Unexpected message type " + mt.ToString());
            }
        }

        void Sync_NewMessage(Node n, string msg)
        {
            Console.WriteLine("{0} says: {1}", n.Name, msg);
        }

        void Sync_NewName(Node n, string msg)
        {
            Console.WriteLine("{0} changes name to \"{1}\"", n.Name, msg);
            n.Name = msg;
        }

        void Sync_ProcessDisconnect(IOException ioex, Disconnect ds, Node n)
        {
            Console.WriteLine("Node {0} disconnected ({1})", n.Name, ds);
        }

        void StartConnecting(Node n)
        {
            n.StartConnecting(
                () => this.Sync_OutgoingConnectionReady(n),
                my_info,
                (ioex, ds) => this.Sync_ProcessDisconnect(ioex, ds, n)
                );
        }

        void Sync_NewIncomingConnection(Handshake theirInfo, Socket sck)
        {
            Node targetNode;

            targetNode = NodeByEP(theirInfo.addr);
            if (targetNode == null)
            {
                targetNode = new Node(theirInfo, processQueue);
                nodes.Add(targetNode);
            }
            targetNode.Name = theirInfo.name;

            targetNode.AcceptConnection(sck,
                (ep, stm, mt) => this.ProcessMessage(ep, stm, mt),
                (ioex, ds) => this.Sync_ProcessDisconnect(ioex, ds, targetNode));

            if (targetNode.CanConnect())
                StartConnecting(targetNode);

            if(targetNode.Ready())
                processQueue(() => this.Sync_ConnectionFinalized(targetNode));
        }

        void Sync_OutgoingConnectionReady(Node n)
        {
            if(n.Ready())
                processQueue(() => this.Sync_ConnectionFinalized(n));
        }
        
        void Sync_ConnectionFinalized(Node n)
        {
            Console.WriteLine("New connection: {0}", n.Name);
        }

        void Sync_Connect(IPEndPoint their_addr)
        {
            Node n = NodeByEP(their_addr);

            if (n == null)
            {
                n = new Node(new Handshake(their_addr, ""), processQueue);
                nodes.Add(n);
            }

            if (n.CanWrite())
                throw new InvalidOperationException("Already connected to " + their_addr.ToString());
            else if (!n.CanConnect())
                throw new InvalidOperationException("Connection in progress " + their_addr.ToString());
            else
                StartConnecting(n);
        }

        public bool Sync_TryConnect(IPEndPoint their_addr)
        {
            try
            {
                Sync_Connect(their_addr);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            return true;
        }
    }

}
