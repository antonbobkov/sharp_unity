using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Tools;

namespace Network
{
    class ActionSyncronizer
    {
        private int? executeThreadId;
        public object syncLock = new object();

        public ActionSyncronizer(int? executeThreadId = null)
        {
            this.executeThreadId = executeThreadId;
        }


        void ExecuteThread()
        {
            lock (syncLock)
                if (executeThreadId == null)
                    executeThreadId = Thread.CurrentThread.ManagedThreadId;

            while (true)
            {
                var a = msgs.Take();
                if (a == null)
                    return;
                lock (syncLock)
                    a.Invoke();
            }
        }

        BlockingCollection<Action> msgs = new BlockingCollection<Action>();

        public void Add(Action a)
        {
            lock (syncLock) 
                if (a != null && executeThreadId != null)
                    MyAssert.Assert(executeThreadId != Thread.CurrentThread.ManagedThreadId);

            msgs.Add(a);
        }
        public Queue<Action> TakeAll() { return msgs.TakeAll(); }

        public Action<Action> GetAsDelegate() { return (a) => this.Add(a); }

        public void Start()
        {
            ThreadManager.NewThread(() => this.ExecuteThread(),
                //() => msgs.Add(null),
                () => { },
                "global syncronizer");
        }

        public ActionSyncronizerProxy GetProxy() { return new ActionSyncronizerProxy(this); }
    }

    class ActionSyncronizerProxy
    {
        private ActionSyncronizer sync;

        public ActionSyncronizerProxy(ActionSyncronizer sync) { this.sync = sync; }

        public SyncAction Convert(Action a) { return new SyncAction(a, sync); }
        public SyncAction<T> Convert<T>(Action<T> a) { return new SyncAction<T>(a, sync); }
        public SyncAction<T, S> Convert<T, S>(Action<T, S> a) { return new SyncAction<T, S>(a, sync); }
    }

    class SyncAction
    {
        private Action a;
        private ActionSyncronizer sync;

        public SyncAction(Action a, ActionSyncronizer sync)
        {
            this.a = a;
            this.sync = sync;
        }

        public void Invoke()
        {
            sync.Add(a);
        }
    }
    class SyncAction<T>
    {
        private Action<T> a;
        private ActionSyncronizer sync;

        public SyncAction(Action<T> a, ActionSyncronizer sync)
        {
            this.a = a;
            this.sync = sync;
        }

        public void Invoke(T t)
        {
            sync.Add(() => a(t));
        }
    }
    class SyncAction<T, S>
    {
        private Action<T, S> a;
        private ActionSyncronizer sync;

        public SyncAction(Action<T, S> a, ActionSyncronizer sync)
        {
            this.a = a;
            this.sync = sync;
        }

        public void Invoke(T t, S s)
        {
            sync.Add(() => a(t,s));
        }
    }
}
