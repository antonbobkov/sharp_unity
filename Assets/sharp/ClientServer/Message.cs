using System;

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

using System.Reflection;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting.Messaging;

using Tools;
using Network;


namespace ServerClient
{
    [Serializable]
    public class ForwardFunctionCall
    {
        public string functionName;
        public object[] argumemts;

        public void Apply<T>(T t)
        {
            MethodInfo mb = typeof(T).GetMethod(functionName);
            mb.Invoke(t, argumemts);
        }

        public object[] Serialize()
        {
            object[] arr = new object[argumemts.Length + 1];

            arr[0] = functionName;

            for (int i = 0; i < argumemts.Length; ++i)
                arr[1 + i] = argumemts[i];

            return arr;
        }

        public static ForwardFunctionCall Deserialize(Stream stm, Type t)
        {
            ForwardFunctionCall ffc = new ForwardFunctionCall();

            ffc.functionName = Serializer.Deserialize<string>(stm);

            MethodInfo mb = t.GetMethod(ffc.functionName);
            ParameterInfo[] param = mb.GetParameters();
            ffc.argumemts = new object[param.Length];

            for (int i = 0; i < param.Length; ++i)
                ffc.argumemts[i] = Serializer.Deserialize(stm, param[i].ParameterType);

            return ffc;
        }
    }

    //[System.AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public class Forward : Attribute { }

    class ForwardProxy<T> : RealProxy
        where T : MarshalByRefObject
    {
        T obj; 
        Action<ForwardFunctionCall> onCall;

        public ForwardProxy(T obj_, Action<ForwardFunctionCall> onCall_)
            : base(typeof(T))
        {
            obj = obj_;
            onCall = onCall_;
        }

        public T GetProxy()
        {
            return (T)GetTransparentProxy();
        }

        public override IMessage Invoke(IMessage msg)
        {
            var methodCall = msg as IMethodCallMessage;

            if (methodCall != null)
                return HandleMethodCall(methodCall);

            return null;
        }

        IMessage HandleMethodCall(IMethodCallMessage methodCall)
        {
            if (HasAttribute<Forward>(methodCall.MethodBase))
            {
                ForwardFunctionCall ffc = new ForwardFunctionCall() { functionName = methodCall.MethodName, argumemts = methodCall.InArgs };
                onCall.Invoke(ffc);
            }
            

            //try
            //{
            var result = methodCall.MethodBase.Invoke(obj, methodCall.InArgs);
            return new ReturnMessage(result, null, 0, methodCall.LogicalCallContext, methodCall);
            //}
            //catch (TargetInvocationException invocationException)
            //{
            //    var exception = invocationException.InnerException;
            //    return new ReturnMessage(exception, methodCall);
            //}
        }

        static bool HasAttribute<N>(MethodBase inf)
        {
            return inf.GetCustomAttributes(typeof(N), false).Length != 0;
        }
    }

    interface IManualLock
    {
        bool Locked { get; }
        void Unlock();
    }

    class ManualLock<T>: IManualLock
    {
        Action unlock = () => { throw new Exception("not locked"); };
        
        public bool Locked { get; private set; }

        public ManualLock(HashSet<T> locks, T t)
        {
            if (locks.Contains(t))
                Locked = false;
            else
            {
                locks.Add(t);
                unlock = () => locks.Remove(t);
                Locked = true;
            }
        }

        public void Unlock()
        {
            unlock.Invoke();
        }
    }

    public enum Response { SUCCESS, FAIL }

    class RemoteActionIdInject { }

    class RemoteAction
    {
        public Guid Id { get; private set; }
        OverlayEndpoint remoteHost = new OverlayEndpoint();
        IManualLock l = null;

        Action<Response, Stream> followUp;

        // questionable design choices for sake of smoother application

        public void Respond(ref RemoteAction ra, Action<Response, Stream> followUp_)
        {
            followUp = followUp_;

            MyAssert.Assert(ra == null);
            ra = this;
        }
        public void Respond(RemoteActionRepository rar, IManualLock l_, Action<Response, Stream> followUp_)
        {
            l = l_;
            followUp = followUp_;

            if (l != null && !l.Locked)
                throw new Exception("Cannot remote unlocked object");

            rar.AddAction(this);
        }

        static public RemoteAction Send(OverlayHost myHost, OverlayEndpoint remoteHost, Node.DisconnectProcessor dp,
            MessageType mt, params object[] args)
        {
            RemoteAction ra = new RemoteAction();

            ra.remoteHost = remoteHost;

            bool assigned = false;
            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i].GetType() == typeof(RemoteActionIdInject))
                {
                    args[i] = ra.Id;        // id is injected at first RemoteActionIdInject argument
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
            {
                List<object> lst = new List<object>();
                lst.Add(ra.Id);             // or in front if there isn't one
                lst.AddRange(args);
                args = lst.ToArray();
            }

            if(dp != null)
                myHost.TryConnectAsync(remoteHost, dp);
            myHost.SendMessage(remoteHost, mt, args);

            return ra;
        }

        RemoteAction()
        {
            Id = Guid.NewGuid();
        }

        void FollowUp(Node n, Stream st, Response r)
        {
            MyAssert.Assert(n.info.remote == remoteHost);

            if (l != null)
                l.Unlock();

            followUp.Invoke(r, st);

        }

        static public void Process(ref RemoteAction ra, Node n, Stream st)
        {
            Response r = Serializer.Deserialize<Response>(st);
            Guid id = Serializer.Deserialize<Guid>(st);

            MyAssert.Assert(ra.Id == id);

            ra.FollowUp(n, st, r);

            ra = null;
        }
        static public void Process(RemoteActionRepository rar, Node n, Stream st)
        {
            Response r = Serializer.Deserialize<Response>(st);
            Guid id = Serializer.Deserialize<Guid>(st);

            RemoteAction ra = rar.Get(id);
            
            ra.FollowUp(n, st, r);

            rar.Remove(id);
        }

        static private void Respond(Node n, bool success, Guid id, object[] args)
        {
            List<object> lst = new List<object>();
            lst.Add(success ? Response.SUCCESS : Response.FAIL);
            lst.Add(id);
            lst.AddRange(args);

            n.SendMessage(MessageType.RESPONSE, lst.ToArray());
        }

        static public void Sucess(Node n, Guid id, params object[] args)
        {
            Respond(n, true, id, args);
        }
        static public void Fail(Node n, Guid id, params object[] args)
        {
            Respond(n, false, id, args);
        }
    }

    class RemoteActionRepository
    {
        private Dictionary<Guid, RemoteAction> remotes = new Dictionary<Guid,RemoteAction>();
        private Action onClear = null;

        private void EmptyCheck()
        {
            if (!remotes.Any() && onClear != null)
            {
                onClear.Invoke();
                onClear = null;
            }
        }

        public void AddAction(RemoteAction ra)
        {
            MyAssert.Assert(!remotes.ContainsKey(ra.Id));

            remotes.Add(ra.Id, ra);
        }
        public RemoteAction Get(Guid id)
        {
            MyAssert.Assert(remotes.ContainsKey(id));

            return remotes[id];
        }
        public void Remove(Guid id)
        {
            MyAssert.Assert(remotes.ContainsKey(id));

            remotes.Remove(id);

            EmptyCheck();
        }
        public void TriggerOnEmpty(Action a)
        {
            MyAssert.Assert(onClear == null);
            onClear = a;

            EmptyCheck();
        }

        public int Count { get { return remotes.Count; } }

    }
}
