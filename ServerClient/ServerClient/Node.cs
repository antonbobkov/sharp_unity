using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading;

namespace ServerClient
{
    [Serializable]
    class Handshake
    {
        public IPEndPoint addr;
        public string name;

        public Handshake(IPEndPoint addr_, string name_)
        {
            addr = addr_;
            name = name_;
        }
    }

    enum Disconnect {READ, WRITE};

    class Node
    {
        Handshake info;

        public IPEndPoint Address
        {
            get{ return info.addr; }
            private set{ info.addr = value; }
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

        public string Description()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("{0} ({1}) R:{2} W:{3} CW:{4}", Address, Name, CanRead(), CanWrite(), CanConnect());
            return sb.ToString();
        }

        public void SendMessage(MessageType mt, object message = null)
        {
            if (!Ready())
                Console.WriteLine("Warning: socket not ready yet {0}", Description());
            if (!CanWrite())
                Console.WriteLine("Warning: socket not ready for writing yet - buffering ({0})", Description());

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
    }
}
