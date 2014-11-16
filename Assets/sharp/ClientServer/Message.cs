using System;
using ServerClient.Concurrent;
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


namespace ServerClient
{
    public enum MessageType : byte { HANDSHAKE, ROLE,
    SERVER_ADDRESS, NEW_VALIDATOR,
    NEW_PLAYER_REQUEST, NEW_WORLD_REQUEST, NEW_PLAYER_REQUEST_SUCCESS,
    PLAYER_VALIDATOR_ASSIGN, WORLD_VALIDATOR_ASSIGN, ACCEPT,
    WORLD_VAR_INIT, WORLD_VAR_CHANGE, NEW_NEIGHBOR,
    MOVE_REQUEST, REALM_MOVE,
    PICKUP_TELEPORT, PICKUP_BLOCK,
    PLAYER_INFO_VAR,
    SPAWN_REQUEST,
    RESPONSE, LOCK_VAR, UNLOCK_VAR,
    PLACE_BLOCK, TAKE_BLOCK};
    
    public enum NodeRole { CLIENT, SERVER, PLAYER_AGENT, PLAYER_VALIDATOR, WORLD_VALIDATOR };

    public enum WorldMove { LEAVE, JOIN };

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
        Guid id = Guid.Empty;
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
        public void Respond(Dictionary<Guid, RemoteAction> remotes, IManualLock l_, Action<Response, Stream> followUp_)
        {
            l = l_;
            followUp = followUp_;

            if (l != null && !l.Locked)
                throw new Exception("Cannot remote unlocked object");

            remotes.Add(id, this);
        }

        static public RemoteAction Send(OverlayHost myHost, OverlayEndpoint remoteHost, MessageType mt, params object[] args)
        {
            RemoteAction ra = new RemoteAction();

            ra.remoteHost = remoteHost;

            bool assigned = false;
            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i].GetType() == typeof(RemoteActionIdInject))
                {
                    args[i] = ra.id;        // id is injected at first RemoteActionIdInject argument
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
            {
                List<object> lst = new List<object>();
                lst.Add(ra.id);             // or in front if there isn't one
                lst.AddRange(args);
                args = lst.ToArray();
            }

            myHost.ConnectSendMessage(remoteHost, mt, args);

            return ra;
        }

        RemoteAction()
        {
            id = Guid.NewGuid();
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

            MyAssert.Assert(ra.id == id);

            ra.FollowUp(n, st, r);

            ra = null;
        }
        static public void Process(Dictionary<Guid, RemoteAction> remotes, Node n, Stream st)
        {
            Response r = Serializer.Deserialize<Response>(st);
            Guid id = Serializer.Deserialize<Guid>(st);

            MyAssert.Assert(remotes.ContainsKey(id));
            
            remotes[id].FollowUp(n, st, r);

            remotes.Remove(id);
        }

        static void Respond(Node n, bool success, Guid id, object[] args)
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
}
