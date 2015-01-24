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
using System.Runtime.CompilerServices;

namespace ServerClient
{
    static class Log
    {
        static public Action<string> log = msg => Console.WriteLine(msg);

        static public void LogWriteLine(string msg, params object[] vals)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(msg, vals);
            log(sb.ToString());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static public string StDump(params object[] vals)
        {
            StackTrace st = new StackTrace();
            StackFrame sf = null;
            for (int i = 0; i < 10; ++i )
            {
                sf = st.GetFrame(i);
                if (sf == null)
                    break;

                string strClass = sf.GetMethod().DeclaringType.Name;

                if (strClass != typeof(Log).Name)
                    break;
            }

            string sMethod = sf.GetMethod().Name;
            string sClass = sf.GetMethod().DeclaringType.Name;

            string ret = sClass + "." + sMethod + " ";

            foreach (object o in vals)
                ret += o.ToString() + " ";

            return ret;
        }

        static public void Dump(params object[] vals)
        {
            LogWriteLine(StDump(vals));
        }
    }

    static class NetTools
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
        public static IPEndPoint GetRemoteIP(Socket sck)
        {
            return sck.RemoteEndPoint as IPEndPoint;
        }
        public static IPEndPoint GetLocalIP(Socket sck)
        {
            return sck.LocalEndPoint as IPEndPoint;
        }
    }

    public static class SocketExtensions
    {
        /// <summary>
        /// Connects the specified socket.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="endpoint">The IP endpoint.</param>
        /// <param name="timeout">The timeout.</param>
        public static void Connect(this Socket socket, EndPoint endpoint, TimeSpan timeout)
        {
            var result = socket.BeginConnect(endpoint, null, null);

            bool success = result.AsyncWaitHandle.WaitOne(timeout, true);
            if (success)
            {
                socket.EndConnect(result);
            }
            else
            {
                socket.Close();
                throw new SocketException(10060); // Connection timed out.
            }
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

    class ThreadInfo
    {
        public Thread thread;
        public Action terminate;
        public string name;
    }

    static class ThreadManager
    {
        static List<ThreadInfo> threads = new List<ThreadInfo>();

        static public void NewThread(ThreadStart threadFunction, Action terminate, string name)
        {
            ThreadInfo ti = new ThreadInfo() { thread = new Thread(threadFunction), terminate = terminate, name = name };
            //Log.LogWriteLine("Thread: {0}", name);
            ti.thread.Start();

            lock (threads)
                threads.Add(ti);
        }

        static public string Status()
        {
            lock (threads)
            {
                StringBuilder sb = new StringBuilder();

                foreach (ThreadInfo ti in threads)
                {
                    if ( (ti.thread.ThreadState & System.Threading.ThreadState.Stopped) != 0)
                        continue;
                    sb.AppendFormat("Thread \"{0}\": status {1}\n", ti.name, ti.thread.ThreadState);
                }

                return sb.ToString();
            }
        }

        static public void Terminate()
        {
            lock (threads)
            {
                foreach (ThreadInfo ti in threads)
                {
                    if ((ti.thread.ThreadState & System.Threading.ThreadState.Stopped) != 0)
                        continue;
                    Log.LogWriteLine("Terminating {0}", ti.name);
                    ti.terminate.Invoke();
                }
            }       
        }
    };

    static class MyAssert
    {
        static public void Assert(bool b)
        {
            Debug.Assert(b);
            if (!b)
                throw new Exception("Assert failed");
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

        public static V GetValue<K, V>(this Dictionary<K, V> dict, K key)
        {
            if (dict.ContainsKey(key))
                return dict[key];
            throw new InvalidOperationException("Key not found " + key.ToString());
        }

        public static V TryGetValue<K, V>(this Dictionary<K, V> dict, K key)
        {
            if (dict.ContainsKey(key))
                return dict[key];
            else
                return default(V);
        }
    }

    [Serializable]
    public class MyTuple<A, B>
    {
        public A a;
        public B b;

        public MyTuple() { }
        public MyTuple(A a_, B b_)
        {
            a = a_;
            b = b_;
        }
    }
}
