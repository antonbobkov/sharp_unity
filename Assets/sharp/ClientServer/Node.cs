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
        public byte[] ipAddr;
        public int port;

        public IPEndPointSer() { }
        public IPEndPointSer(IPEndPoint Addr_) { Addr = Addr_; }

        [XmlIgnoreAttribute]
        public IPEndPoint Addr
        {
            get { return new IPEndPoint(new IPAddress(ipAddr), port); }
            set { ipAddr = value.Address.GetAddressBytes(); port = value.Port; }
        }        
    }
    
    [Serializable]
    public class Handshake
    {
        [XmlIgnoreAttribute]
        public IPEndPoint addr;

        public IPEndPointSer AddrSer
        {
            get { return new IPEndPointSer(addr); }
            set { addr = value.Addr; }
        }

        public Handshake() { }
        public Handshake(IPEndPoint addr_)
        {
            addr = addr_;
        }

        public override string ToString()
        {
            return addr.ToString();
        }
    }

    enum DisconnectType { READ, WRITE, WRITE_CONNECT_FAIL, CLOSED }

    class NodeException : Exception
    {
        public NodeException(string s): base(s) {}
    }

    class Node
    {
        public Node(IPEndPoint addr, Action<Action> actionQueue_, Action<Node, Exception, DisconnectType> processDisonnect_)
        {
            info = new Handshake(addr);
            actionQueue = actionQueue_;
            notifyDisonnect = processDisonnect_;

            writerStatus = WriteStatus.READY;
            readerStatus = ReadStatus.READY;
        }

        public IPEndPoint Address{ get { return info.addr; } }
        public bool IsClosed { get; private set; }
        
        public string FullDescription()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Address: {0} Read: {1} Write: {2}", Address, readerStatus, writerStatus);
            return sb.ToString();
        }

        public void SendMessage(MessageType mt, params Object[] messages)
        {
            if (IsClosed)
                throw new NodeException("SendMessage: node is disconnected " + FullDescription());

            writer.SendMessage(mt, messages);
        }

        // --- private ---

        internal enum ReadStatus { READY, CONNECTED, DISCONNECTED };
        internal enum WriteStatus { READY, CONNECTING, CONNECTED, DISCONNECTED };

        SocketWriter writer = new SocketWriter();
        internal WriteStatus writerStatus { get; private set; }

        SocketReader reader;
        internal ReadStatus readerStatus { get; private set; }

        Handshake info;
        Action<Action> actionQueue;
        Action<Node, Exception, DisconnectType> notifyDisonnect;

        internal bool AreBothConnected() { return writerStatus == WriteStatus.CONNECTED && readerStatus == ReadStatus.CONNECTED; }

        internal void AcceptReaderConnection(Socket readingSocket,
            Action<Node, Stream, MessageType> messageProcessor)
        {
            try
            {
                if (IsClosed)
                    throw new NodeException("AcceptConnection: node is disconnected " + FullDescription());

                if (readerStatus != ReadStatus.READY)
                    throw new NodeException("AcceptConnection: reader is aready initialized " + FullDescription());

                reader = new SocketReader((stm, mtp) => messageProcessor(this, stm, mtp),
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
        internal void ConnectAsync(Action onSuccess, Handshake myInfo)
        {
            Socket sck = GetReadyForNewWritingConnection(myInfo);

            try
            {
                new Thread( () => ConnectingThread(sck, onSuccess) ).Start();
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
                sck.Connect(Address);
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
        Socket GetReadyForNewWritingConnection(Handshake myInfo)
        {
            if (IsClosed)
                throw new NodeException("GetReadyForNewWritingConnection: node is disconnected " + FullDescription());

            if (writerStatus != WriteStatus.READY)
                throw new NodeException("Unexpected connection request in " + FullDescription());

            writerStatus = WriteStatus.CONNECTING;

            SendMessage(MessageType.HANDSHAKE, myInfo);

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
