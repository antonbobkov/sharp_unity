using System;
using ServerClient.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Text;
using System.Runtime.Serialization.Formatters.Binary;
using System.Xml.Serialization;

namespace ServerClient
{
    /*
    class DataCollection
    {
        List<Node> nodes = new List<Node>();
        Handshake myInfo;
        public Game game = null;
        SocketListener sl;
        
        public Guid Id
        {
            get { return myInfo.id; }
        }

        public IEnumerable<Node> GetReadyNodes()
        {
            return from nd in nodes
                   where nd.Ready()
                   select nd;
        }

        public string Name
        {
            get { return myInfo.name; }
            set { myInfo.name = value; }
        }

        Action<Action> processQueue;

        public DataCollection(IPEndPoint myAddr, string name, Action<Action> processQueue_)
        {
            myInfo = new Handshake(myAddr, name, Guid.NewGuid());
            processQueue = processQueue_;

            //var s = new XmlSerializer(typeof(Handshake));
            //var stm = File.Open("xml_out", FileMode.Create);
            //s.Serialize(stm, myInfo);
            //new XmlSerializer(typeof(HandshakeXML)).Deserialize(File.Open("xml_in", FileMode.Open));
            //new BinaryFormatter().Serialize(File.Open("binary_out", FileMode.Create), i);
            //new BinaryFormatter().Deserialize(File.Open("binary_in", FileMode.Open));
        }

        public void StartListening(Socket sckListen)
        {
            sl = new SocketListener(
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

            Debug.Assert(res.Count() <= 1);

            return res.FirstOrDefault();
        }

        public void TerminateThreads()
        {
            sl.TerminateThread();
            foreach (var nd in nodes)
                nd.TerminateThreads();

        }

        public void Broadcast<T>(MessageType mt, T o)
        {
            foreach (Node n in GetReadyNodes())
                n.SendMessage(mt, o);
        }

        public void Sync_AskForTable(IPEndPoint ep)
        {
            Node n = NodeByEP(ep);

            if (n == null)
                throw new InvalidOperationException("No such node " + ep.ToString());

            n.SendMessage(MessageType.TABLE_REQUEST);
        }

        void ProcessMessage(IPEndPoint their_addr, Stream stm, MessageType mt)
        {
            if (mt == MessageType.MESSAGE)
            {
                string msg = Serializer.Deserialize<string>(stm);

                processQueue(() => this.Sync_NewMessage(this.NodeByEP(their_addr), msg));
            }
            else if (mt == MessageType.NAME)
            {
                string msg = Serializer.Deserialize<string>(stm);

                processQueue(() => this.Sync_NewName(this.NodeByEP(their_addr), msg));
            }
            else if (mt == MessageType.TABLE_REQUEST)
            {
                processQueue(() => this.Sync_TableRequest(this.NodeByEP(their_addr)));
            }
            else if (mt == MessageType.TABLE)
            {
                var table = Serializer.Deserialize<IPEndPoint[]>(stm);
                processQueue(() => this.Sync_OnTable(table));
            }
            else if (mt == MessageType.GENERATE_GAME)
            {
                var info = Serializer.Deserialize<GameInitializer>(stm);
                processQueue(() => Sync_GenerateGame(info));
            }
            else if (mt == MessageType.PLAYER_MOVE)
            {
                var move = Serializer.Deserialize<PlayerMoveInfo>(stm);
                processQueue(() => Sync_UpdatePosition(move.id, move.pos));
            }
            else
            {
                throw new InvalidOperationException("Unexpected message type " + mt.ToString());
            }
        }

        public void Sync_UpdatePosition(Guid id, Point pos)
        {
            lock(this)
                game.players[id].pos = pos;
        }
        
        public void Sync_UpdateMyPosition()
        {
            Guid id = Id;
            Point pos = game.players[id].pos;

            Broadcast(MessageType.PLAYER_MOVE, new PlayerMoveInfo(id, pos));
        }

        public void Sync_GenerateGame(GameInitializer info)
        {
            DataCollection.log("Sync_GenerateGame");
            lock (this) 
                game = new Game(GetReadyNodes(), myInfo.id, info);
        }

        void Sync_TableRequest(Node n)
        {
            var listOfPeers = from nd in nodes
                              where nd.Ready()
                              select new IPEndPoint(nd.Address);

            n.SendMessage(MessageType.TABLE, listOfPeers.ToArray());
        }

        void Sync_OnTable(IPEndPoint[] table)
        {
            new List<IPEndPoint>(table).ForEach((ep) => Sync_TryConnect(ep.Addr));
        }
            
        void Sync_NewMessage(Node n, string msg)
        {
            Log.LogWriteLine("{0} says: {1}", n.Name, msg);
        }

        void Sync_NewName(Node n, string msg)
        {
            Log.LogWriteLine("{0} changes name to \"{1}\"", n.Name, msg);
            n.Name = msg;
        }

        void Sync_ProcessDisconnect(IOException ioex, DisconnectType ds, Node n)
        {
            Log.LogWriteLine("Node {0} disconnected ({1})", n.Name, ds);
        }

        void StartConnecting(Node n)
        {
            n.ConnectAsync(
                () => this.Sync_OutgoingConnectionReady(n),
                myInfo,
                (ioex, ds) => this.Sync_ProcessDisconnect(ioex, ds, n)
                );
        }

        void Sync_NewIncomingConnection(Handshake theirInfo, Socket sck)
        {
            Node targetNode;

            targetNode = NodeByEP(theirInfo.Addr);
            
            if (targetNode == null)
            {
                targetNode = new Node(theirInfo, processQueue);
                nodes.Add(targetNode);
            }
            else
                targetNode.UpdateHandshake(theirInfo);

            targetNode.AcceptReaderConnection(sck,
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
            Log.LogWriteLine("New connection: {0}", n.Name);
        }

        void Sync_Connect(IPEndPoint their_addr)
        {
            Node n = NodeByEP(their_addr);

            if (n == null)
            {
                n = new Node(their_addr, processQueue);
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
            //try
            //{
                Sync_Connect(their_addr);
            //}
            //catch (InvalidOperationException)
            //{
                //return false;
            //}

            return true;
        }

        static public Action<string> log = msg => Console.WriteLine(msg);

        static public void LogWriteLine(string msg, params object[] vals)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(msg, vals);
            log(sb.ToString());
        }
    }*/
     

}
