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
    public enum MessageType : byte { HANDSHAKE,
    SERVER_ADDRESS, GAME_INFO, NEW_VALIDATOR, GAME_INFO_CHANGE, NEW_PLAYER_REQUEST, NEW_WORLD_REQUEST,
    PLAYER_VALIDATOR_ASSIGN, WORLD_VALIDATOR_ASSIGN, ACCEPT,
    WORLD_INIT, PLAYER_JOIN, PLAYER_LEAVE,
    MOVE, REALM_MOVE, REALM_MOVE_SUCCESS, REALM_MOVE_FAIL,
    PICKUP_ITEM, FREEZE_ITEM, FREEZE_SUCCESS, FREEZE_FAIL, UNFREEZE_ITEM, CONSUME_FROZEN_ITEM, TELEPORT_MOVE,
    PLAYER_INFO,
    SPAWN_REQUEST, SPAWN_SUCCESS, SPAWN_FAIL};
    
    public enum NodeRole { PLAYER, PLAYER_VALIDATOR, WORLD_VALIDATOR };

    public enum MoveType { MOVE, LEAVE, JOIN };

    public enum PlayerDataUpdate { NEW, JOIN, INVENTORY };

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

        public T Get()
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

    [Serializable]
    public class GameInfoSerialized
    {
        public PlayerInfo[] players;
        public WorldInfo[] worlds;

        public GameInfoSerialized() { }
    }

    class GameInfo : MarshalByRefObject
    {
        // ----- constructors -----
        public GameInfo() { }

        public GameInfoSerialized Serialize()
        {
            return new GameInfoSerialized() { players = playerById.Values.ToArray(), worlds = worldByPoint.Values.ToArray() };
        }
        public void Deserialize(GameInfoSerialized info)
        {
            foreach (PlayerInfo p in info.players)
                AddPlayer(p);
            foreach (WorldInfo w in info.worlds)
                AddWorld(w);
        }

        // ----- read only infromation -----
        public NodeRole GetRoleOfHost(OverlayEndpoint host) { return roles.GetValue(host); }

        public PlayerInfo GetPlayerByHost(OverlayEndpoint host) { return playerByHost.GetValue(host); }
        public WorldInfo GetWorldByHost(OverlayEndpoint host) { return worldByHost.GetValue(host); }

        public PlayerInfo GetPlayerById(Guid player) { return playerById.GetValue(player); }
        public WorldInfo GetWorldByPos(Point pos) { return worldByPoint.GetValue(pos); }

        public WorldInfo? TryGetWorldByPos(Point pos)
        {
            if (worldByPoint.ContainsKey(pos))
                return worldByPoint[pos];
            else
                return null;
        }

        public OverlayEndpoint GetPlayerHost(Guid player) { return playerById.GetValue(player).playerHost; }
        public OverlayEndpoint GetPlayerValidatorHost(Guid player) { return playerById.GetValue(player).validatorHost; }
        public OverlayEndpoint GetWorldHost(Point worldPos) { return worldByPoint.GetValue(worldPos).host; }

        // ----- modifiers -----
        [Forward] public void AddPlayer(PlayerInfo info)
        {
            roles.Add(info.playerHost, NodeRole.PLAYER);
            roles.Add(info.validatorHost, NodeRole.PLAYER_VALIDATOR);

            playerById.Add(info.id, info);
            playerByHost.Add(info.playerHost, info);
            playerByHost.Add(info.validatorHost, info);

            onNewPlayer.Invoke(info);
        }
        [Forward] public void AddWorld(WorldInfo info)
        {
            roles.Add(info.host, NodeRole.WORLD_VALIDATOR);

            worldByPoint.Add(info.worldPos, info);
            worldByHost.Add(info.host, info);
            
            onNewWorld.Invoke(info);
        }

        // ----- hooks -----
        public Action<PlayerInfo> onNewPlayer = (inf) => { };
        public Action<WorldInfo> onNewWorld = (inf) => { };

        // ----- private data -----
        Dictionary<OverlayEndpoint, NodeRole> roles = new Dictionary<OverlayEndpoint, NodeRole>();
        Dictionary<Guid, PlayerInfo> playerById = new Dictionary<Guid, PlayerInfo>();
        Dictionary<OverlayEndpoint, PlayerInfo> playerByHost = new Dictionary<OverlayEndpoint, PlayerInfo>();
        Dictionary<Point, WorldInfo> worldByPoint = new Dictionary<Point, WorldInfo>();
        Dictionary<OverlayEndpoint, WorldInfo> worldByHost = new Dictionary<OverlayEndpoint, WorldInfo>();
    }
}
