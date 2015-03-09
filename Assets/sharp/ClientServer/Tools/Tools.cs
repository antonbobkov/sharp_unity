using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Text;

namespace Tools
{
    static class NetTools
    {
        public static ILog errorLog = null;

        public static IPAddress GetMyIP()
        {
            try
            {
                //IPHostEntry localDnsEntry = Dns.GetHostEntry(string.Empty);
                IPHostEntry localDnsEntry = Dns.GetHostEntry(Dns.GetHostName());
                return localDnsEntry.AddressList.First
                    (ipaddr =>
                        ipaddr.AddressFamily.ToString() == ProtocolFamily.InterNetwork.ToString());
            }
            catch (Exception e)
            {
                Log.Console("GetMyIP Error: {0}\nDefault to 127.0.0.1", e.Message);

                if (errorLog == null)
                    errorLog = MasterLog.GetFileLog("GetMyIP_exception.log");

                Log.EntryError(errorLog, e.ToString());
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

        static ILog threadLog = MasterLog.GetFileLog("Threads.log");

        static public void NewThread(ThreadStart threadFunction, Action terminate, string name)
        {
            lock (threads)
            {
                ThreadInfo ti = new ThreadInfo() { thread = new Thread(threadFunction), terminate = terminate, name = name };
                Log.EntryNormal(threadLog, "New Thread: " + name);
                ti.thread.Start();

                threads.Add(ti);
            }
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
                    Log.EntryError(threadLog, "Terminating " + ti.name);
                    ti.terminate.Invoke();
                }
            }       
        }
    };

    static class MyAssert
    {
        //static private ILog assertLog = MasterLog.GetFileLog("Assert.log");

        static private void LogEntry(ILog log)
        {
            Log.EntryError(log, "Assert failed\n" + new System.Diagnostics.StackTrace() + "\n\n");
            Log.EntryError(log, "Assert failed\n" + System.Environment.StackTrace + "\n\n");
        }

        static public void Assert(bool b)
        {
            if (!b)
            {
                LogEntry(MasterLog.GetFileLog("Assert " + Thread.CurrentThread.ManagedThreadId + ".log"));

                Debug.Assert(b);

                throw new Exception("Assert failed");
            }
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

    class TestHandle : IDisposable
    {
        public void Dispose() { Console.WriteLine("disposing"); }
    }

    public static class DisposeHandle
    {
        public static DisposeHandle<T> Get<T>(T t)
            where T : class, IDisposable
        { return new DisposeHandle<T>(t); }

        public static void Test()
        {
            using (var h = DisposeHandle.Get(new TestHandle())) {
                h.Disengage();
            }
        }
    }

    public class DisposeHandle<T> : IDisposable
        where T : class, IDisposable
        
    {
        private T handle;
        
        public DisposeHandle(T handle) { this.handle = handle; }
        public void Disengage() { handle = null; }

        public void Dispose()
        {
            if (handle != null)
                handle.Dispose();
        }
    }
}
