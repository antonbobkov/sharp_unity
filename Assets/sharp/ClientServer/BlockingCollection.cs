using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Text;

namespace ServerClient.Concurrent
{
    class BlockingCollection<T>
    {
        Queue<T> q = new Queue<T>();
        ManualResetEvent arrayReady = new ManualResetEvent(false);

        public void Add(T t)
        {
            lock (q)
            {
                q.Enqueue(t);
                arrayReady.Set();
            }
        }
        public T Take()
        {
            while (true)
            {
                lock (q)
                {
                    if (q.Any())
                        return q.Dequeue();
                    else
                        arrayReady.Reset();
                }

                arrayReady.WaitOne();
            }
        }
    }
}
