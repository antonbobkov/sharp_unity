using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Network;
using Tools;

namespace ServerClient
{
    class GameNodeException : Exception
    {
        public GameNodeException(string msg) : base(msg) { }
    }

    class GameNode
    {
        protected virtual void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            throw new GameNodeException(Log.StDump(mt, n.info, inf));
        }
        protected virtual void ProcessPlayerAgentMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            throw new GameNodeException(Log.StDump(mt, n.info, inf));
        }
        protected virtual void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            throw new GameNodeException(Log.StDump(mt, n.info, inf));
        }
        protected virtual void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            throw new GameNodeException(Log.StDump(mt, n.info));
        }
        protected virtual void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            throw new GameNodeException(Log.StDump(mt, n.info));
        }

        protected virtual void ProcessPlayerValidatorDisconnect(NodeDisconnectInfo di, PlayerInfo inf) { }
        protected virtual void ProcessPlayerAgentDisconnect(NodeDisconnectInfo di, PlayerInfo inf) { }
        protected virtual void ProcessWorldDisconnect(NodeDisconnectInfo di, WorldInfo inf) { }
        protected virtual void ProcessServerDisconnect(NodeDisconnectInfo di) { }
        protected virtual void ProcessClientDisconnect(NodeDisconnectInfo di) { }

        protected virtual bool AuthorizePlayerValidator(Node n, PlayerInfo inf) {return false;}
        protected virtual bool AuthorizePlayerAgent(Node n, PlayerInfo inf) {return false;}
        protected virtual bool AuthorizeWorld(Node n, WorldInfo inf) {return false;}
        protected virtual bool AuthorizeServer(Node n) {return false;}
        protected virtual bool AuthorizeClient(Node n) {return false;}

        protected GameNodeProcessors AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (role == NodeRole.CLIENT)
            {
                if (!AuthorizeClient(n))
                    throw new GameNodeException(Log.StDump(n.info, role));

                return new GameNodeProcessors(ProcessClientMessage, ProcessClientDisconnect);
            }

            if (role == NodeRole.SERVER)
            {
                if (n.info.remote != serverHost)
                    throw new GameNodeException(Log.StDump(n.info, role, "bad server host"));

                if(!AuthorizeServer(n))
                    throw new GameNodeException(Log.StDump(n.info, role));

                return new GameNodeProcessors(ProcessServerMessage, ProcessServerDisconnect);
            }

            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);

                if(!AuthorizePlayerValidator(n, inf))
                    throw new GameNodeException(Log.StDump(role, n.info, inf));

                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf),
                    (di) => ProcessPlayerValidatorDisconnect(di, inf));
            }

            if (role == NodeRole.PLAYER_AGENT)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);

                if(!AuthorizePlayerAgent(n, inf))
                    throw new GameNodeException(Log.StDump(role, n.info, inf));
                
                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessPlayerAgentMessage(mt, stm, nd, inf),
                    (di) => ProcessPlayerAgentDisconnect(di, inf));

            }

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = Serializer.Deserialize<WorldInfo>(nodeInfo);

                if(!AuthorizeWorld(n, inf))
                    throw new GameNodeException(Log.StDump(role, n.info, inf));

                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf),
                    (di) => ProcessWorldDisconnect(di, inf));
            }

            throw new GameNodeException(Log.StDump(n.info, role, "unexpected"));
        }

        protected void MessagePlayerValidator(PlayerInfo inf, MessageType mt, params object[] arg)
        {
            Host.TryConnectAsync(inf.validatorHost, (di) => ProcessPlayerValidatorDisconnect(di, inf));
            Host.SendMessage(inf.validatorHost, mt, arg);
        }
        protected void MessagePlayerAgent(PlayerInfo inf, MessageType mt, params object[] arg)
        {
            Host.TryConnectAsync(inf.playerHost, (di) => ProcessPlayerAgentDisconnect(di, inf));
            Host.SendMessage(inf.playerHost, mt, arg);
        }
        protected void MessageWorld(WorldInfo inf, MessageType mt, params object[] arg)
        {
            Host.TryConnectAsync(inf.host, (di) => ProcessWorldDisconnect(di, inf));
            Host.SendMessage(inf.host, mt, arg);
        }
        protected void MessageClient(OverlayEndpoint addr, MessageType mt, params object[] arg)
        {
            Host.TryConnectAsync(addr, ProcessClientDisconnect);
            Host.SendMessage(addr, mt, arg);
        }
        protected void MessageServer(MessageType mt, params object[] arg)
        {
            MyAssert.Assert(serverHost != null);
            
            Host.TryConnectAsync(serverHost, ProcessServerDisconnect);
            Host.SendMessage(serverHost, mt, arg);
        }

        protected void ConnectAsync(OverlayEndpoint addr, Node.DisconnectProcessor dp)
        {
            Host.ConnectAsync(addr, dp);
        }
        protected void SetConnectionHook(Action<Node> a) { Host.onNewConnectionHook = a; }

        protected OverlayHost Host {get; set;}
        protected OverlayEndpoint serverHost;
    }
}
