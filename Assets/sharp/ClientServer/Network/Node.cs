using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Linq;

using Tools;

namespace Network
{
    public enum NetworkMessageType : byte { HANDSHAKE, SOFT_DISCONNECT, MESSAGE }
    
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
    
    [Serializable ]
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

    abstract class NetworkMessage
    {
        public MemoryStream ms = null;
        public TimeSpan delay = TimeSpan.Zero;

        public abstract void LogData(ILog l);
    }
    
    enum DisconnectType { READ, WRITE, WRITE_CONNECT_FAIL, CLOSED }

    class NodeException : Exception
    {
        public NodeException(string s): base(s) {}
    }

    struct NodeDisconnectInfo
    {
        public Node node;
        public Exception exception;
        public DisconnectType disconnectType;
    }

    class Node
    {
        public delegate void MessageProcessor(MemoryStream stm, Node n);
        public delegate void DisconnectProcessor(NodeDisconnectInfo di);

        public ILog LogR { get; private set; }
        public ILog LogW { get; private set; }

        public Node(Handshake info_, ActionSyncronizerProxy actionQueue_, DisconnectProcessor processDisonnect_)
        {
            info = info_;
            sync = actionQueue_;
            notifyDisonnect = processDisonnect_;

            writerStatus = WriteStatus.READY;
            readerStatus = ReadStatus.READY;
            IsClosed = false;

            if (MasterLog.LogLevel > 1)
            {
                LogR = MasterLog.GetFileLog("network", info.local.hostname.ToString(),
                    info.remote.ToString().Replace(':', '.') + " read.xml");

                LogW = MasterLog.GetFileLog("network", info.local.hostname.ToString(),
                    info.remote.ToString().Replace(':', '.') + " write.xml");
            }

            UpdateUseTime();
        }

        public bool IsClosed { get; private set; }
        public bool IsConnected() { return readerStatus == ReadStatus.READING || writerStatus == WriteStatus.WRITING; }
        
        public string FullDescription()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Address: {0} Read: {1} Write: {2}", info, readerStatus, writerStatus);
            return sb.ToString();
        }

        public void SendMessage(NetworkMessage nm)
        {
            if (IsClosed)
                throw new NodeException("SendMessage: node is disconnected " + FullDescription());
            if (writerStatus != WriteStatus.WRITING)
                ConnectAsync();

            SocketWriterMessage swm = new SocketWriterMessage(nm.ms);
            swm.delay = nm.delay;

            //string sentMsg = nm.ToString();
            //if (MasterFileLog.LogLevel > 2)
            //    sentMsg += new ChunkDebug(swm.message, Serializer.SizeSize).GetData() + "\n\n";

            //Log.EntryVerbose(LogW, nm.GetLogData());
            nm.LogData(LogW);
            
            writer.SendMessage(swm);

            UpdateUseTime();
        }

        public void SoftDisconnect()
        {
            if (IsClosed)
                throw new NodeException("SoftDisconnect: node is disconnected " + FullDescription());
            if (writerStatus != WriteStatus.WRITING)
            {
                if(readerStatus != ReadStatus.READING)
                    return;
                else
                    ConnectAsync();
            }

            //Log.Console(info.remote + " SoftDisconnect() called");

            writer.SendMessage(new SocketWriterMessage(SocketWriterMessageType.SOFT_DISCONNECT));
        }

        //public void SendMessage(NetworkMessage nm)
        //{
        //    SocketWriterMessage swm = new SocketWriterMessage()

        //    string sentMsg = mt.ToString();
        //    if (MasterFileLog.LogLevel > 2)
        //        sentMsg += new ChunkDebug(swm.message, Serializer.SizeSize).GetData() + "\n\n";

        //    Log.EntryVerbose(LogW, sentMsg);

        //    SendMessage(swm);
        //}

        //public void SendMessage(MessageType mt, params object[] messages)
        //{
        //    SocketWriterMessage swm = SocketWriter.SerializeMessage(mt, messages);

        //    string sentMsg = mt.ToString();
        //    if (MasterFileLog.LogLevel > 2)
        //        sentMsg += new ChunkDebug(swm.message, Serializer.SizeSize).GetData() + "\n\n";

        //    Log.EntryVerbose(LogW, sentMsg);

        //    SendMessage(swm);
        //}

        public IPEndPoint Address { get { return info.remote.addr; } }
        public readonly Handshake info;

        public DateTime LastUsed { get; private set; }
        public void UpdateUseTime() { LastUsed = DateTime.Now; }

        // --- private ---

        internal enum ReadStatus { READY, READING, DISCONNECTED };
        internal enum WriteStatus { READY, WRITING, DISCONNECTED };

        SocketWriter writer = null;
        internal WriteStatus writerStatus { get; private set; }

        SocketReader reader = null;
        internal ReadStatus readerStatus { get; private set; }

        ActionSyncronizerProxy sync;
        internal DisconnectProcessor notifyDisonnect;

        internal void AcceptReaderConnection(Socket readingSocket,
            MessageProcessor messageProcessor)
        {
            try
            {
                if (IsClosed)
                    throw new NodeException("AcceptConnection: node is disconnected " + FullDescription());

                if (readerStatus != ReadStatus.READY)
                    throw new NodeException("AcceptConnection: reader is aready initialized " + FullDescription());

                reader = new SocketReader(sync,
                    (stm) => 
                    {
                        //string sentMsg = mtp.ToString();
                        //if (MasterFileLog.LogLevel > 2)
                        //    sentMsg += new ChunkDebug(stm).GetData() + "\n\n";

                        //Log.EntryVerbose(LogR, sentMsg); 
                        
                        messageProcessor(stm, this);
                    },
                    (ioex) =>
                    {
                        readerStatus = ReadStatus.DISCONNECTED;
                        Close(ioex, DisconnectType.READ);
                    },
                    OnSoftDisconnectReader,
                    readingSocket);

                readerStatus = ReadStatus.READING;
            }
            catch (Exception)
            {
                readingSocket.Close();
                throw;
            }
        }

        internal void ConnectAsync()
        {
            if(IsClosed)
                throw new NodeException("SendMessage: node is closed " + FullDescription());
            if (writerStatus != WriteStatus.READY)
                throw new NodeException("SendMessage: node is not ready " + FullDescription());

            MyAssert.Assert(writer == null);

            Action<Exception, DisconnectType> errorResponse = (e, dt) =>
            {
                writerStatus = WriteStatus.DISCONNECTED;
                Close(e, dt);
            };

            writerStatus = WriteStatus.WRITING;
            writer = new SocketWriter(Address, sync, errorResponse, OnSoftDisconnectWriter, info);
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

            notifyDisonnect(new NodeDisconnectInfo() { node = this, exception = ex, disconnectType = dt });
        }

        void TerminateThreads()
        {
            if(reader != null)
                reader.TerminateThread();
            if(writer != null)
                writer.TerminateThread();
        }

        private void OnSoftDisconnectReader()
        {
            Log.EntryNormal(LogR, "Reader soft disconnect");
            //Log.Console(info.remote + " reader softly disconnected");

            reader = null;
            readerStatus = ReadStatus.READY;

            //SoftDisconnect();
        }
        
        private void OnSoftDisconnectWriter()
        {
            Log.EntryNormal(LogW, "Writer soft disconnect");
            //Log.Console(info.remote + " writer softly disconnected");

            Queue<SocketWriterMessage> msgs = writer.ExtractAllMessages();
            
            writer = null;
            writerStatus = WriteStatus.READY;

            while (msgs.Any() && msgs.Peek().type == SocketWriterMessageType.SOFT_DISCONNECT)
                msgs.Dequeue();
            
            if(msgs.Any())
            {
                ConnectAsync();

                while (msgs.Any())
                    writer.SendMessage(msgs.Dequeue());
            }
        }
    }
}
