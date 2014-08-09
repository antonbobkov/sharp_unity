using System;
using System.IO;
using System.Net;

namespace ServerClient
{
    public enum MessageType : byte { HANDSHAKE, MESSAGE, NAME, DISCONNECT };

    class Message
    {
        public MessageType tp;
        public byte[] sMessage;

        public static Message FromText(MessageType tp, string s)
        {
            Message m = new Message();
            m.tp = tp;


            using (MemoryStream ms = new MemoryStream())
            {
                byte[] b = System.Text.Encoding.Default.GetBytes(s);
                if (b.Length >= 256)
                    throw new InvalidOperationException("Message too long");

                ms.WriteByte((byte)b.Length);
                ms.Write(b, 0, b.Length);

                m.sMessage = ms.ToArray();
            }

            return m;
        }

        public static string ToText(Message s)
        {
            return new string(System.Text.Encoding.Default.GetChars(s.sMessage));
        }

        public static Message ipMessage(IPEndPoint ep)
        {
            Message m = new Message();
            m.tp = MessageType.HANDSHAKE;
            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(ep.Address.GetAddressBytes(), 0, 4);
                ms.Write(BitConverter.GetBytes((UInt16)ep.Port), 0, 2);
                m.sMessage = ms.ToArray();
            }

            return m;
        }

        public static Message EmptyMessage(MessageType tp)
        {
            Message m = new Message();
            m.tp = tp;
            return m;
        }
    }
}
