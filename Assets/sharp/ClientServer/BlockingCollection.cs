using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Text;

namespace ServerClient.Concurrent
{
    class BlockingCollection<T>
    {
        object syncLock = new object();
        
        Queue<T> q = new Queue<T>();
        ManualResetEvent arrayReady = new ManualResetEvent(false);

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
    }
}
