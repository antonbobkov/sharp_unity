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
        public IPEndPointSer _addr = new IPEndPointSer();

        [XmlIgnoreAttribute]
        public IPEndPoint Addr
        {
            get { return _addr.Addr; }
            set { _addr.Addr = value; }
        }

        public string name;
        public Guid id;

        public Handshake() { }
        public Handshake(IPEndPoint Addr_, string name_, Guid id_)
        {
            Addr = Addr_;
            name = name_;
            id = id_;
        }
    }

    enum Disconnect { READ, WRITE };

    class Node
    {
        Handshake info;

        public void UpdateHandshake(Handshake info_)
        {
            Debug.Assert(info_.Addr.ToString() == info.Addr.ToString());
            info = info_;
        }

        public Guid Id
        {
            get { return info.id; }
        }

        public IPEndPoint Address
        {
            get{ return info.Addr; }
        }
       
        public string Name
        {
            get
            {
                if (info.name == "")
                    return Address.ToString();
                return info.name;
            }
            set { info.name = value; }
        }

        bool writerConnectionInProgress = false;
        SocketWriter writer = new SocketWriter();
        SocketReader reader;

        Action<Action> actionQueue;

        public bool CanRead() { return reader != null; }
        public bool CanWrite() { return writer.CanWrite(); }
        public bool CanConnect() { return !CanWrite() && !writerConnectionInProgress; }

        public bool Ready() { return CanRead() && CanWrite(); }

        public Node(Handshake info_, Action<Action> actionQueue_)
        {
            info = info_;
            actionQueue = actionQueue_;
        }

        public Node(IPEndPoint addr_, Action<Action> actionQueue_)
            :this(new Handshake(addr_, "", new Guid()), actionQueue_)
        {
        }

        public string Description()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("{0} ({1}, {5}) R:{2} W:{3} CW:{4}", Address, Name, CanRead(), CanWrite(), CanConnect(), info.id.ToString("D"));
            return sb.ToString();
        }

        public void SendMessage(MessageType mt)
        {
            writer.SendMessage<object>(mt, null);
        }

        public void SendMessage<T>(MessageType mt, T message)
        {
            writer.SendMessage(mt, message);
        }

        public void AcceptConnection(Socket readingSocket,
            Action<IPEndPoint, Stream, MessageType> messageProcessor,
            Action<IOException, Disconnect> processDisonnect)
        {
            if (CanRead())
                throw new InvalidOperationException("reader is aready initialized in " + Description());

            reader = new SocketReader((stm, mtp) => messageProcessor(this.Address, stm, mtp),
                                        (ioex) => actionQueue( () =>
                                            {
                                                this.DisconnectReader();
                                                processDisonnect(ioex, Disconnect.READ);
                                            }),

            readingSocket);
        }

        void DisconnectReader()
        {
            reader = null;
        }

        void DisconnectWriter()
        {
            writer = new SocketWriter();
        }

        public void Connect(Handshake my_info, Action<IOException, Disconnect> processDisonnect)
        {
            Socket sck = GetReadyForNewWritingConnection(my_info);

            sck.Connect(Address);

            AcceptWritingConnection(sck, processDisonnect);
        }

        public void StartConnecting(Action doWhenConnected, Handshake my_info, Action<IOException, Disconnect> processDisonnect)
        {
            Socket sck = GetReadyForNewWritingConnection(my_info);

            new Thread
                (() =>
                    {
                        sck.Connect(Address);
                        actionQueue(() =>
                            {
                                AcceptWritingConnection(sck, processDisonnect);
                                doWhenConnected.Invoke();
                            });
                    }
                ).Start();
        }

        Socket GetReadyForNewWritingConnection(Handshake my_info)
        {
            if (!CanConnect())
                throw new InvalidOperationException("Unexpected connection request in " + Description());

            writerConnectionInProgress = true;
            SendMessage(MessageType.HANDSHAKE, my_info);
            //SendMessage(MessageType.HANDSHAKE, my_info);            

            return new Socket(
                    Address.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);
        }

        void AcceptWritingConnection(Socket sck, Action<IOException, Disconnect> processDisonnect)
        {
            writerConnectionInProgress = false;

            writer.StartWriting(sck,
                                (ioex) => actionQueue( () =>
                                    {
                                        this.DisconnectWriter();
                                        processDisonnect(ioex, Disconnect.WRITE);
                                    })
                               );
        }

        public void TerminateThreads()
        {
            if(reader != null)
                reader.TerminateThread();
            writer.TerminateThread();
        }
    }
}
