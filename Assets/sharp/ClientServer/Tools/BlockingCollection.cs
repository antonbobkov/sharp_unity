using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Text;

namespace Tools
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
        
        public T Take() { return GetFront(true); }
        public T Peek() { return GetFront(false); }

        private T GetFront(bool remove)
        {
            while (true)
            {
                lock (syncLock)
                {
                    if (q.Any())
                    {
                        T ret = q.Peek();
                        
                        if (remove)
                            q.Dequeue();

                        return ret;
                    }
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
        public bool IsEmpty { get { lock (syncLock) return !q.Any(); } }

        private object syncLock = new object();
        private Queue<T> q = new Queue<T>();
        private ManualResetEvent arrayReady = new ManualResetEvent(false);
    }
}
