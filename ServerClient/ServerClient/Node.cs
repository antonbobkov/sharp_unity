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

        public bool CanRead() { return reader != null; }
        public bool CanWrite() { return writer.CanWrite(); }
        public bool CanConnect() { return !CanWrite() && !writerConnectionInProgress; }

        public bool Ready() { return CanRead() && CanWrite(); }

        public Node(IPEndPoint ep, string name = "")
        {
            info = new Handshake(ep, "");
        }

        public Node(Handshake info_)
        {
            info = info_;
        }

        public string Description()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("{0} ({1}) R:{2} W:{3} CW:{4}", Address, Name, CanRead(), CanWrite(), CanConnect());
            return sb.ToString();
        }

        public void SendMessage(MessageType mt, object message)
        {
            if (!Ready())
                Console.WriteLine("Warning: socket not ready yet {0}", Description());
            if (!CanWrite())
                Console.WriteLine("Warning: socket not ready for writing yet - buffering ({0})", Description());

            writer.SendMessage(mt, message);
        }

        public void AcceptConnection(Socket readingSocket, Action<IPEndPoint, Stream, MessageType> messageProcessor)
        {
            if (CanRead())
                throw new InvalidOperationException("reader is aready initialized in " + Description());

            reader = new SocketReader((stm, mtp) => messageProcessor(this.Address, stm, mtp), readingSocket);
        }

        public void Connect(Handshake my_info)
        {
            Socket sck = GetReadyForNewWritingConnection();

            sck.Connect(Address);
            
            AcceptWritingConnection(sck, my_info);
        }

        public void StartConnecting(Action<Action> finalize, Handshake my_info)
        {
            Socket sck = GetReadyForNewWritingConnection();

            new Thread
                (() =>
                    {
                        sck.Connect(Address);
                        finalize(() => AcceptWritingConnection(sck, my_info));
                    }
                ).Start();
        }

        Socket GetReadyForNewWritingConnection()
        {
            if (!CanConnect())
                throw new InvalidOperationException("Unexpected connection request in " + Description());

            writerConnectionInProgress = true;

            return new Socket(
                    Address.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);
        }

        void AcceptWritingConnection(Socket sck, Handshake my_info)
        {
            writerConnectionInProgress = false;

            writer.StartWriting(sck);
            SendMessage(MessageType.HANDSHAKE, my_info);            
        }
    }
}
