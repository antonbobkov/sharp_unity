using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Xml.Serialization;

namespace ServerClient
{
    [Serializable]
    public class IPEndPointSer
    {
        public string ipAddr;
        public int port;

        public IPEndPointSer() { }
        public IPEndPointSer(IPEndPoint Addr_) { Addr = Addr_; }

        [XmlIgnore]
        public IPEndPoint Addr
        {
            get { return new IPEndPoint(IPAddress.Parse(ipAddr), port); }
            set { ipAddr = value.Address.ToString(); port = value.Port; }
        }        
    }

    [Serializable]
    public class OverlayHostName
    {
        public string id;

        public OverlayHostName() { }
        public OverlayHostName(string id_) { id = id_; }

        public override string ToString(){ return id; }

        public override bool Equals(object comparand) { return this.ToString().Equals(comparand.ToString());  }
        public override int GetHashCode() { return this.ToString().GetHashCode(); }

        public static bool operator ==(OverlayHostName o1, OverlayHostName o2) { return Object.Equals(o1, o2); }
        public static bool operator !=(OverlayHostName o1, OverlayHostName o2) { return !(o1 == o2); }
    }
    
    [Serializable]
    public class OverlayEndpoint
    {
        [XmlIgnore]
        public IPEndPoint addr;

        public IPEndPointSer AddrSer
        {
            get { return new IPEndPointSer(addr); }
            set { addr = value.Addr; }
        }
        public OverlayHostName hostname;

        public OverlayEndpoint() { }
        public OverlayEndpoint(IPEndPoint addr_, OverlayHostName host_)
        {
            addr = addr_;
            hostname = host_;
        }

        public override string ToString()
        {
            return addr.ToString() + " " + hostname.ToString();
        }

        public override bool Equals(object comparand) { return this.ToString().Equals(comparand.ToString()); }
        public override int GetHashCode() { return this.ToString().GetHashCode(); }

        public static bool operator ==(OverlayEndpoint o1, OverlayEndpoint o2) { return Object.Equals(o1, o2); }
        public static bool operator !=(OverlayEndpoint o1, OverlayEndpoint o2) { return !(o1 == o2); }
    }

    [Serializable]
    public class Handshake
    {
        public OverlayEndpoint remote;
        public OverlayEndpoint local;

        public Handshake() { }
        public Handshake(OverlayEndpoint local_, OverlayEndpoint remote_, MemoryStream ExtraInfo_)
        {
            remote = remote_;
            local = local_;

            ExtraInfo = ExtraInfo_;
        }

        private MemoryStream _extraInfo = null;
        
        [XmlIgnore]
        public MemoryStream ExtraInfo
        {
            get { return _extraInfo; }
            set
            {
                _extraInfo = value;
                hasExtraInfo = (_extraInfo != null);
            }
        }
        
        public bool hasExtraInfo = false;

        public override string ToString()
        {
            return "Remote: " + remote.ToString() + " Local: " + local.ToString();
        }

        public override bool Equals(object comparand) { return this.ToString().Equals(comparand.ToString()); }
        public override int GetHashCode() { return this.ToString().GetHashCode(); }

        public static bool operator ==(Handshake o1, Handshake o2) { return Object.Equals(o1, o2); }
        public static bool operator !=(Handshake o1, Handshake o2) { return !(o1 == o2); }
    }
    
    enum DisconnectType { READ, WRITE, WRITE_CONNECT_FAIL, CLOSED }

    class NodeException : Exception
    {
        public NodeException(string s): base(s) {}
    }

    class Node
    {
        public delegate void MessageProcessor(MessageType mt, Stream stm, Node n);

        public Node(Handshake info_, Action<Action> actionQueue_, Action<Node, Exception, DisconnectType> processDisonnect_)
        {
            info = info_;
            actionQueue = actionQueue_;
            notifyDisonnect = processDisonnect_;

            writerStatus = WriteStatus.READY;
            readerStatus = ReadStatus.READY;

            //// queue up
            //SendMessage(MessageType.HANDSHAKE, info);

            //if (info.hasExtraInfo)
            //    writer.SendStream(info.ExtraInfo);
        }

        public bool IsClosed { get; private set; }
        
        public string FullDescription()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Address: {0} Read: {1} Write: {2}", info, readerStatus, writerStatus);
            return sb.ToString();
        }

        public void SendMessage(MessageType mt, params object[] messages)
        {
            if (IsClosed)
                throw new NodeException("SendMessage: node is disconnected " + FullDescription());

            writer.SendMessage(mt, messages);
        }

        public IPEndPoint Address { get { return info.remote.addr; } }
        public readonly Handshake info;

        // --- private ---

        internal enum ReadStatus { READY, CONNECTED, DISCONNECTED };
        internal enum WriteStatus { READY, CONNECTING, CONNECTED, DISCONNECTED };

        SocketWriter writer = new SocketWriter();
        internal WriteStatus writerStatus { get; private set; }

        SocketReader reader;
        internal ReadStatus readerStatus { get; private set; }

        Action<Action> actionQueue;
        Action<Node, Exception, DisconnectType> notifyDisonnect;

        internal bool AreBothConnected() { return writerStatus == WriteStatus.CONNECTED && readerStatus == ReadStatus.CONNECTED; }

        internal void AcceptReaderConnection(Socket readingSocket,
            MessageProcessor messageProcessor)
        {
            try
            {
                if (IsClosed)
                    throw new NodeException("AcceptConnection: node is disconnected " + FullDescription());

                if (readerStatus != ReadStatus.READY)
                    throw new NodeException("AcceptConnection: reader is aready initialized " + FullDescription());

                reader = new SocketReader(
                    (stm, mtp) => actionQueue(() => messageProcessor(mtp, stm, this)),
                    (ioex) => actionQueue(() =>
                    {
                        readerStatus = ReadStatus.DISCONNECTED;
                        Close(ioex, DisconnectType.READ);
                    }),

                readingSocket);

                readerStatus = ReadStatus.CONNECTED;
            }
            catch (Exception)
            {
                readingSocket.Close();
                throw;
            }
        }

        /*internal void Connect(Handshake myInfo)
        {
            Socket sck = GetReadyForNewWritingConnection(myInfo);

            try
            {
                sck.Connect(Address.Addr);
            }
            catch (Exception e)
            {
                sck.Close();
                Close(e, DisconnectType.WRITE_CONNECT_FAIL);
                throw;
            }

            AcceptWritingConnection(sck);
        }
        */
        internal void ConnectAsync(Action onSuccess)
        {
            Socket sck = GetReadyForNewWritingConnection();

            try
            {
                ThreadManager.NewThread(() => ConnectingThread(sck, onSuccess),
                    () => sck.Close(), "connect to " + info.ToString());
                //new Thread( () => ConnectingThread(sck, onSuccess) ).Start();
            }
            catch (Exception)
            {
                sck.Close();
                throw;
            }
        }

        internal void Disconnect()
        {
            Close(new Exception("no error"), DisconnectType.CLOSED);
        }
        void Close(Exception ex, DisconnectType dt)
        {
            if (IsClosed)
                return;

            IsClosed = true;

            TerminateThreads();

            notifyDisonnect(this, ex, dt);
        }

        void ConnectingThread(Socket sck, Action onSuccess)
        {
            try
            {
                sck.Connect(Address, TimeSpan.FromSeconds(3));

                // send handshake data
                using (NetworkStream connectionStream = new NetworkStream(sck, false))
                {
                    Serializer.SendStream(connectionStream, SocketWriter.SerializeMessage(MessageType.HANDSHAKE, info));

                    if (info.hasExtraInfo)
                    {
                        info.ExtraInfo.Position = 0;
                        Serializer.SendStream(connectionStream, info.ExtraInfo);
                    }
                }

            }
            catch (Exception e)
            {
                sck.Close();

                actionQueue(() =>
                {
                    Close(e, DisconnectType.WRITE_CONNECT_FAIL);
                });

                return;
            }

            actionQueue(() =>
            {
                AcceptWritingConnection(sck);
                onSuccess.Invoke();
            });
        }
        Socket GetReadyForNewWritingConnection()
        {
            if (IsClosed)
                throw new NodeException("GetReadyForNewWritingConnection: node is disconnected " + FullDescription());

            if (writerStatus != WriteStatus.READY)
                throw new NodeException("Unexpected connection request in " + FullDescription());

            writerStatus = WriteStatus.CONNECTING;

            return new Socket(
                    Address.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);
        }
        void AcceptWritingConnection(Socket sck)
        {
            writerStatus = WriteStatus.CONNECTED;

            writer.StartWriting(sck,
                                (ioex) => actionQueue( () =>
                                    {
                                        writerStatus = WriteStatus.DISCONNECTED;
                                        Close(ioex, DisconnectType.WRITE);
                                    })
                               );
        }
        void TerminateThreads()
        {
            if(reader != null)
                reader.TerminateThread();
            writer.TerminateThread();
        }
    }
}
