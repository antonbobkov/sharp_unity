using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Xml.Serialization;
using System.ComponentModel;

using Tools;
using Network;

namespace ServerClient
{
    public enum MessageType : byte
    {
        ROLE, 
        SERVER_ADDRESS, NEW_VALIDATOR, STOP_VALIDATING,
        NEW_PLAYER_REQUEST, NEW_WORLD_REQUEST, NEW_PLAYER_REQUEST_SUCCESS,
        PLAYER_VALIDATOR_ASSIGN, WORLD_VALIDATOR_ASSIGN, ACCEPT,
        WORLD_VAR_INIT, WORLD_VAR_CHANGE, NEW_NEIGHBOR, SUBSCRIBE, UNSUBSCRIBE, WORLD_HOST_DISCONNECT,
        MOVE_REQUEST, REALM_MOVE,
        PICKUP_TELEPORT, PICKUP_BLOCK, PLAYER_DISCONNECT,
        PLAYER_INFO_VAR,
        PLAYER_HOST_DISCONNECT,
        SPAWN_REQUEST,
        RESPONSE, LOCK_VAR, UNLOCK_VAR,
        PLACE_BLOCK, TAKE_BLOCK,
        REMOTE_PLACE_BLOCK, REMOTE_TAKE_BLOCK,
    };
    
    [Serializable]
    public struct Point
    {
        public int x, y;

        public Point(int x_, int y_) { x = x_; y = y_; }
        public override string ToString()
        {
            return "[" + x + ", " + y + "]";
        }

        public static Point operator +(Point p1, Point p2) { return new Point(p1.x + p2.x, p1.y + p2.y); }
        public static Point operator -(Point p1) { return new Point(-p1.x, -p1.y); }

        public static Point operator -(Point p1, Point p2) { return p1 + -p2; }

        public override bool Equals(object comparand) { return this.ToString().Equals(comparand.ToString()); }
        public override int GetHashCode() { return this.ToString().GetHashCode(); }

        public static bool operator ==(Point o1, Point o2) { return Object.Equals(o1, o2); }
        public static bool operator !=(Point o1, Point o2) { return !(o1 == o2); }

        public static IEnumerable<Point> Range(Point size)
        {
            Point p = new Point();
            for (p.y = 0; p.y < size.y; ++p.y)
                for (p.x = 0; p.x < size.x; ++p.x)
                    yield return p;
        }
        public static IEnumerable<Point> SymmetricRange(Point size)
        {
            Point p = new Point();
            for (p.y = -size.y; p.y <= size.y; ++p.y)
                for (p.x = -size.x; p.x <= size.x; ++p.x)
                    yield return p;
        }

        public static Point Zero { get { return new Point(0, 0); } }
        public static Point One { get { return new Point(1, 1); } }

        public static Point Scale(Point p, Point scale) { return new Point(p.x * scale.x, p.y * scale.y); }
    }


    [Serializable]
    public class Plane<T>
    {
        public T[] plane;
        public Point Size { get; set; }

        public Plane() { }
        public Plane(Point size) { plane = new T[size.x * size.y]; Size = size; }

        void AssertRange(int x, int y)
        {
            if(!InRange(x, y))
                throw new IndexOutOfRangeException(new StringBuilder().AppendFormat("Plane<{0}>{1} w/ size {2}", typeof(T).Name, new Point(x, y), Size).ToString());
        }

        public bool InRange(Point p)
        {
            return InRange(p.x, p.y);
        }

        public bool InRange(int x, int y)
        {
            if (x < 0 || x >= Size.x)
                return false;
            if (y < 0 || y >= Size.y)
                return false;
            return true;
        }

        public T this[Point pos]
        {
            get { return this[pos.x, pos.y]; }
            set { this[pos.x, pos.y] = value; }
        }
        public T this[int x, int y]
        {
            get { AssertRange(x, y);  return plane[y * Size.x + x]; }
            set { AssertRange(x, y);  plane[y * Size.x + x] = value; }
        }

        public IEnumerable<T> GetTiles()
        {
            foreach (Point p in Point.Range(Size))
                yield return this[p];
        }
        public IEnumerable<KeyValuePair<Point, T>> GetEnum()
        {
            foreach(Point p in Point.Range(Size))
                yield return new KeyValuePair<Point, T>(p, this[p]);
        }
    }

    static class Game
    {
        public delegate void MessageProcessor(MessageType mt, Stream stm, Node n);

        public delegate Game.MessageProcessor ProcessorAssigner(Node n, MemoryStream extraInfo);

        public static OverlayHost.ProcessorAssigner Convert(Game.ProcessorAssigner pa)
        {
            return (node, handshake) =>
            {
                Game.MessageProcessor gmp = pa(node, handshake);
                
                return (stm, nd) => 
                    {
                        //if (nd.IsClosed)  done elsewhere
                        //    return;

                        ChunkDebug lastRead = new ChunkDebug(stm);

                        try
                        {
                            MessageType mt = Serializer.Deserialize<MessageType>(stm);

                            if (nd.LogR != null && nd.LogR.LogLevel >= 2)
                            {
                                string sentMsg = mt.ToString();
                                if (MasterLog.LogLevel > 2)
                                    sentMsg += lastRead.GetData() + "\n\n";

                                Log.EntryVerbose(nd.LogR, sentMsg); 
                            }

                            gmp(mt, stm, nd);
                        }
                        catch (XmlSerializerException e)
                        {
                            Log.Console("Error while reading from socket:\n{0}\n\nLast read:{1}", e, lastRead.GetData());
                            throw new Exception("Fatal");
                        }
                    };
            };
        }
    }

    class GameMessage : NetworkMessage
    {
        string loggedMessage = "";
        MessageType mt;
        //object[] data;

        public GameMessage(MessageType mt, object[] data)
        {
            this.mt = mt;
            //this.data = data;

            //if (mt == MessageType.REMOTE_PLACE_BLOCK)
            //    delay = TimeSpan.FromSeconds(5);

            ms = new MemoryStream();

            List<object> lst = new List<object>();
            lst.Add(mt);
            lst.AddRange(data);
            
            //ms.WriteByte((byte)mt);
            Serializer.Serialize(ms, lst.ToArray());
        }

        public override void LogData(ILog l)
        {
            if(l == null)
                return;

            if (l.LogLevel <= 2)
            {
                Log.EntryVerbose(l, mt.ToString());
                return;
            }
            
            if(loggedMessage == "")
                loggedMessage = mt.ToString() + new ChunkDebug(ms, Serializer.SizeSize+1).GetData() + "\n\n";

            Log.EntryVerbose(l, loggedMessage);
        }
    }

    static class NodeExtension
    {
        public static void SendMessage(this Node n, MessageType mt, params object[] objs)
        {
            n.SendMessage(new GameMessage(mt, objs));
        }

        public static void SendMessage(this OverlayHost host, OverlayEndpoint remote, MessageType mt, params object[] objs)
        {
            host.SendMessage(remote, new GameMessage(mt, objs));
        }

        public static void ConnectSendMessage(this OverlayHost host, OverlayEndpoint remote, MessageType mt, params object[] objs)
        {
            host.ConnectSendMessage(remote, new GameMessage(mt, objs));
        }

        public static void BroadcastGroup(this OverlayHost host, Func<Node, bool> group, MessageType mt, params object[] objs)
        {
            host.BroadcastGroup(group, new GameMessage(mt, objs));
        }

        public static void BroadcastGroup(this OverlayHost host, OverlayHostName name, MessageType mt, params object[] objs)
        {
            host.BroadcastGroup(name, new GameMessage(mt, objs));
        }
    }
}
