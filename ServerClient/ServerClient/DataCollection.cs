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

        BlockingCollection<Action> msgs;
        Action<Action> ProcessLater(Action action2)
        {
            return (action1) =>
                {
                    msgs.Add(() =>
                        {
                            action1.Invoke();
                            action2.Invoke();
                        });
                };
        }

        public DataCollection(IPEndPoint myAddr, string name, BlockingCollection<Action> msgs_)
        {
            my_info = new Handshake(myAddr, name);
            msgs = msgs_;
        }

        public void StartListening(Socket sckListen)
        {
            SocketListener sl = new SocketListener(
                ConnectionProcessor.ProcessWithHandshake(
                    (info, sck) =>
                        msgs.Add(() => this.Sync_NewIncomingConnection(info, sck))
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

                msgs.Add(() => this.Sync_NewMessage(this.NodeByEP(their_addr), msg));
            }
            else if (mt == MessageType.NAME)
            {
                string msg = (string)SocketReader.ReadSerializedMessage(stm);

                msgs.Add(() => this.Sync_NewName(this.NodeByEP(their_addr), msg));
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

        void Sync_NewIncomingConnection(Handshake theirInfo, Socket sck)
        {
            Node targetNode;

            targetNode = NodeByEP(theirInfo.addr);
            if (targetNode == null)
            {
                targetNode = new Node(theirInfo);
                nodes.Add(targetNode);
            }

            targetNode.AcceptConnection(sck, (ep, stm, mt) => this.ProcessMessage(ep, stm, mt));

            if(targetNode.CanConnect())
                targetNode.StartConnecting(ProcessLater(
                                                         () => this.Sync_OutgoingConnectionReady(targetNode)
                                                       ), my_info);

            if(targetNode.Ready())
                msgs.Add(() => this.Sync_ConnectionFinalized(targetNode));
        }

        void Sync_OutgoingConnectionReady(Node n)
        {
            if(n.Ready())
                msgs.Add(() => this.Sync_ConnectionFinalized(n));
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
                n = new Node(their_addr);
                nodes.Add(n);
            }

            if(n.CanWrite())
                throw new InvalidOperationException("Already connected to " + their_addr.ToString());
            else if(!n.CanConnect())
                throw new InvalidOperationException("Connection in progress " + their_addr.ToString());
            else
                n.StartConnecting(ProcessLater  (
                                                    () => this.Sync_OutgoingConnectionReady(n)
                                                ), my_info);
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
