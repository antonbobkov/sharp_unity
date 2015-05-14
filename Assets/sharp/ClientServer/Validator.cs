using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Network;
using Tools;

namespace ServerClient
{
    class ValidatorException : Exception
    {
        public ValidatorException(string msg) : base(msg) { }
    }

    class Validator
    {
        public virtual void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            throw new ValidatorException(Log.StDump(mt, n.info, inf));
        }
        public virtual void ProcessPlayerAgentMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            throw new ValidatorException(Log.StDump(mt, n.info, inf));
        }
        public virtual void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            throw new ValidatorException(Log.StDump(mt, n.info, inf));
        }
        public virtual void ProcessServerMessage(MessageType mt, Stream stm, Node n)
        {
            throw new ValidatorException(Log.StDump(mt, n.info));
        }
        public virtual void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            throw new ValidatorException(Log.StDump(mt, n.info));
        }

        public virtual void ProcessPlayerValidatorDisconnect(NodeDisconnectInfo di, PlayerInfo inf) { }
        public virtual void ProcessPlayerAgentDisconnect(NodeDisconnectInfo di, PlayerInfo inf) { }
        public virtual void ProcessWorldDisconnect(NodeDisconnectInfo di, WorldInfo inf) { }
        public virtual void ProcessServerDisconnect(NodeDisconnectInfo di) { }
        public virtual void ProcessClientDisconnect(NodeDisconnectInfo di) { }

        public virtual void AcceptPlayerValidator(Node n, PlayerInfo inf)
        {
            throw new ValidatorException(Log.StDump(n.info, inf));
        }
        public virtual void AcceptPlayerAgent(Node n, PlayerInfo inf)
        {
            throw new ValidatorException(Log.StDump(n.info, inf)); 
        }
        public virtual void AcceptWorld(Node n, WorldInfo inf)
        {
            throw new ValidatorException(Log.StDump(n.info, inf)); 
        }
        public virtual void AcceptServer(Node n)
        {
            throw new ValidatorException(Log.StDump(n.info));
        }
        public virtual void AcceptClient(Node n)
        { 
            throw new ValidatorException(Log.StDump(n.info));
        }

        public GameNodeProcessors AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (role == NodeRole.CLIENT)
            {
                AcceptClient(n);
                return new GameNodeProcessors(ProcessClientMessage, ProcessClientDisconnect);
            }

            if (role == NodeRole.SERVER)
            {
                if (n.info.remote != serverHost)
                    throw new ValidatorException(Log.StDump(n.info, role, "bad server host"));

                AcceptServer(n);
                return new GameNodeProcessors(ProcessServerMessage, ProcessServerDisconnect);
            }

            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);

                AcceptPlayerValidator(n, inf);

                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf),
                    (di) => ProcessPlayerValidatorDisconnect(di, inf));
            }

            if (role == NodeRole.PLAYER_AGENT)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);

                AcceptPlayerAgent(n, inf);
                
                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessPlayerAgentMessage(mt, stm, nd, inf),
                    (di) => ProcessPlayerAgentDisconnect(di, inf));

            }

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = Serializer.Deserialize<WorldInfo>(nodeInfo);

                AcceptWorld(n, inf);

                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf),
                    (di) => ProcessWorldDisconnect(di, inf));
            }

            throw new ValidatorException(Log.StDump(n.info, role, "unexpected"));
        }

        public void MessagePlayerValidator(PlayerInfo inf, MessageType mt, params object[] arg)
        {
            myHost.TryConnectAsync(inf.validatorHost, (di) => ProcessPlayerValidatorDisconnect(di, inf));
            myHost.SendMessage(inf.validatorHost, mt, arg);
        }
        public void MessagePlayerAgent(PlayerInfo inf, MessageType mt, params object[] arg)
        {
            myHost.TryConnectAsync(inf.playerHost, (di) => ProcessPlayerAgentDisconnect(di, inf));
            myHost.SendMessage(inf.playerHost, mt, arg);
        }
        public void MessageWorld(WorldInfo inf, MessageType mt, params object[] arg)
        {
            myHost.TryConnectAsync(inf.host, (di) => ProcessWorldDisconnect(di, inf));
            myHost.SendMessage(inf.host, mt, arg);
        }
        public void MessageClient(OverlayEndpoint addr, MessageType mt, params object[] arg)
        {
            myHost.TryConnectAsync(addr, ProcessClientDisconnect);
            myHost.SendMessage(addr, mt, arg);
        }
        public void MessageServer(MessageType mt, params object[] arg)
        {
            MyAssert.Assert(serverHost != null);
            
            myHost.TryConnectAsync(serverHost, ProcessServerDisconnect);
            myHost.SendMessage(serverHost, mt, arg);
        }

        private OverlayHost myHost;
        private OverlayEndpoint serverHost;
    }
}
