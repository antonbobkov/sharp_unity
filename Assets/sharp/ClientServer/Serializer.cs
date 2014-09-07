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
        public static void Serialize(Stream output, object obj)
        {
            //IFormatter formatter = new BinaryFormatter();
            //formatter.Serialize(stm, obj);

            MemoryStream ms = new MemoryStream();

            XmlSerializer ser = new XmlSerializer(obj.GetType());
            ser.Serialize(ms, obj);

            //Log.LogWriteLine("Send XML of size {1}:\n{0}", System.Text.Encoding.Default.GetString(ms.ToArray()), ms.Length);

            WriteSize(output, (int)ms.Length);

            ms.Position = 0;
            CopyStream(ms, output);
        }

        public static T Deserialize<T>(Stream input)
        {
            //IFormatter formatter = new BinaryFormatter();
            //return (T)formatter.Deserialize(stm);

            int size = ReadSize(input);
            byte[] data = new byte[size];
            int c = input.Read(data, 0, size);
            /*
            for (int i = 0; i < size; ++i)
            {
                input.Read(data, i, 1);
                Console.Write((char)data[i]);
            }
            */


            MyAssert.Assert(c == size);

            lastRead = System.Text.Encoding.Default.GetString(data);
            //Log.LogWriteLine("Received XML:\n{0}", System.Text.Encoding.Default.GetString(data));

            MemoryStream ms = new MemoryStream(data);

            XmlSerializer ser = new XmlSerializer(typeof(T));
            return (T)ser.Deserialize(ms);
        }
        
        public static void SendStream(Stream network, Stream message)
        {
            //message.CopyTo(network);
            CopyStream(message, network);
        }

        public static void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[32768];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
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
