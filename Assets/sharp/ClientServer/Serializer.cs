using ServerClient.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Xml.Serialization;
using System.Diagnostics;

namespace ServerClient
{
    class Serializer
    {
        public static string lastRead = "";

        public const int SizeSize = 4;

        public static void Serialize(MemoryStream output, params object[] objs)
        {
            long initialPosition = output.Position;
            
            WriteSize(output, 0);   // empty bytes

            foreach (object m in objs)
            {
                long initialPos = output.Position;
                WriteSize(output, 0);   // empty bytes
                SerializeOne(output, m);
                long finalPos = output.Position;

                long objectSize = finalPos - initialPos - SizeSize;
                MyAssert.Assert(objectSize > 0);

                output.Position = initialPos;
                WriteSize(output, (int)objectSize);
                output.Position = finalPos;
            }

            long finalPosition = output.Position;
            long totalSize = finalPosition - initialPosition - SizeSize;

            output.Position = initialPosition;
            MyAssert.Assert(totalSize >= 0);
            if (objs.Length > 0)
                MyAssert.Assert(totalSize > 0);
            WriteSize(output, (int)totalSize);

            output.Position = finalPosition;
        }

        static void SerializeOne(Stream output, object obj)
        {
            //IFormatter formatter = new BinaryFormatter();
            //formatter.Serialize(stm, obj);

            XmlSerializer ser = new XmlSerializer(obj.GetType());
            ser.Serialize(output, obj);
        }

        static MemoryStream ReadChunk(Stream input, int size)
        {
            if (size == 0)
                return new MemoryStream();

            byte[] data = new byte[size];
            int total = 0;
            while (true)
            {
                int bytesRead = input.Read(data, total, size - total);

                total += bytesRead;

                if (bytesRead == 0)
                    throw new IOException("End of stream");

                if (size == total)
                    break;
            }

            return new MemoryStream(data);
        }

        public static MemoryStream DeserializeChunk(Stream input)
        {
            int size = ReadSize(input);

            MemoryStream chunk = ReadChunk(input, size);

            lastRead = System.Text.Encoding.Default.GetString(chunk.ToArray());
            //Log.LogWriteLine("Received XML:\n{0}", System.Text.Encoding.Default.GetString(data));

            return chunk;
        }

        public static T Deserialize<T>(Stream input)
        {
            //IFormatter formatter = new BinaryFormatter();
            //return (T)formatter.Deserialize(stm);

            /*
            int size = ReadSize(input);
            byte[] data = new byte[size];
            int total = 0;
            while(true)
            {
                int bytesRead = input.Read(data, total, size - total);
                
                total += bytesRead;

                MyAssert.Assert(bytesRead != 0);
                
                if (size == total)
                    break;
            }

            lastRead = System.Text.Encoding.Default.GetString(data);
            //Log.LogWriteLine("Received XML:\n{0}", System.Text.Encoding.Default.GetString(data));

            MemoryStream ms = new MemoryStream(data);
            */

            int size = ReadSize(input);
            MemoryStream chunk = ReadChunk(input, size);

            XmlSerializer ser = new XmlSerializer(typeof(T));
            return (T)ser.Deserialize(chunk);
        }
        
        public static void SendStream(Stream network, Stream message)
        {
            //message.CopyTo(network);
            CopyStream(message, network);
        }

        public static void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[1000];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
        }

        static void WriteSize(Stream output, int size)
        {
            byte[] intBytes = BitConverter.GetBytes(size);

            MyAssert.Assert(intBytes.Length == sizeof(int));

            if (BitConverter.IsLittleEndian)
                Array.Reverse(intBytes);

            output.Write(intBytes, 0, intBytes.Length);
        }

        static int ReadSize(Stream input)
        {
            byte[] intBytes = new byte[sizeof(int)];
            int c = input.Read(intBytes, 0, intBytes.Length);

            MyAssert.Assert(c == intBytes.Length);

            if (BitConverter.IsLittleEndian)
                Array.Reverse(intBytes);

            return BitConverter.ToInt32(intBytes, 0);
        }
    }
}
