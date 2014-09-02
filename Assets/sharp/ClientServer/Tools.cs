using System;
using ServerClient.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Text;
using System.Runtime.Serialization.Formatters.Binary;
using System.Xml.Serialization;

namespace ServerClient
{
    class Log
    {
        static public Action<string> log = msg => Console.WriteLine(msg);

        static public void LogWriteLine(string msg, params object[] vals)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(msg, vals);
            log(sb.ToString());
        }
    }

    class NetTools
    {
        public static IPAddress GetMyIP()
        {
            try
            {
                IPHostEntry localDnsEntry = Dns.GetHostEntry(Dns.GetHostName());
                return localDnsEntry.AddressList.First
                    (ipaddr =>
                        ipaddr.AddressFamily.ToString() == ProtocolFamily.InterNetwork.ToString());
            }
            catch (Exception e)
            {
                Log.LogWriteLine("GetMyIP Error: {0}\nWill try to read ip from file \"myip\"", e.Message);
            }

            try
            {
                var f = new System.IO.StreamReader(File.Open("myip", FileMode.Open));
                string line;
                line = f.ReadLine();
                return IPAddress.Parse(line);
            }
            catch (Exception e)
            {
                Log.LogWriteLine("GetMyIP Error: {0}\nDefault to 127.0.0.1", e.Message);
            }

            return IPAddress.Parse("127.0.0.1");
        }
    }

    public static class ThreadSafeRandom
    {
        [ThreadStatic]
        private static System.Random Local;

        public static System.Random ThisThreadsRandom
        {
            get { return Local ?? (Local = new System.Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId))); }
        }
    }

    static class MyExtensions
    {
        public static void Shuffle<T>(this IList<T> list, Func<int, int> nextRandomInt)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = nextRandomInt(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
 
        public static T Random<T>(this IList<T> list, Func<int, int> nextRandomInt)
        {
            return list[nextRandomInt(list.Count)];
        }
    }
}
