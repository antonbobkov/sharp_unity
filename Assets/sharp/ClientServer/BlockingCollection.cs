using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Text;

namespace ServerClient.Concurrent
{
    class BlockingCollection<T>
    {
        public void Add(T t)
        {
            lock (syncLock)
            {
                q.Enqueue(t);
                arrayReady.Set();
            }
        }
        public T Take()
        {
            while (true)
            {
                lock (syncLock)
                {
                    if (q.Any())
                        return q.Dequeue();
                    else
                        arrayReady.Reset();
                }

                arrayReady.WaitOne();
            }
        }
        public Queue<T> TakeAll()
        {
            lock (syncLock)
            {
                Queue<T> ret = q;
                q = new Queue<T>();
                return ret;
            }        
        }

        private object syncLock = new object();
        private Queue<T> q = new Queue<T>();
        private ManualResetEvent arrayReady = new ManualResetEvent(false);
    }
}
