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


namespace ServerClient
{
    /*
    public enum MessageType : byte { HANDSHAKE, TABLE_REQUEST, TABLE, ROLE, GENERATE,
        VALIDATE_MOVE, MOVE,
        LOOT_PICKUP, LOOT_PICKUP_BROADCAST,
        VALIDATE_TELEPORT, FREEZE_ITEM, FREEZING_SUCCESS, FREEZING_FAIL,
        UNFREEZE_ITEM, CONSUME_FROZEN_ITEM, TELEPORT, LOOT_CONSUMED,
        VALIDATE_REALM_MOVE, REALM_MOVE, REALM_MOVE_SUCESS, REALM_MOVE_FAIL,
        REMOVE_PLAYER, ADD_PLAYER
    };

    public enum MoveValidity
    {
        VALID = 0,
        BOUNDARY = 1,
        OCCUPIED_PLAYER = 2,
        OCCUPIED_WALL = 4,
        TELEPORT = 8,
        NEW = 16
    };

    public enum NodeRole { PLAYER, WORLD, POTENTIAL_VALIDATOR, PLAYER_VALIDATOR, WORLD_VALIDATOR };

    public enum MoveType { MOVE, LEAVE, JOIN };
    */

    public enum MessageType : byte { HANDSHAKE, SERVER_ADDRESS, GAME_INFO, NEW_VALIDATOR, NEW_PLAYER, NEW_WORLD,
    PLAYER_VALIDATOR_ASSIGN, WORLD_VALIDATOR_ASSIGN, ACCEPT,
    WORLD_INIT,
    INVENTORY_INIT};
    
    public enum NodeRole { PLAYER, PLAYER_VALIDATOR, WORLD_VALIDATOR };

    /*abstract class MessageReceiver
    {
        public abstract void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer);
    }*/
    
    /*class MessageInfo
    {
        public readonly NodeRole senderRole;
        public readonly NodeRole receiverRole;

        public MessageReceiver mr = null;

        public MessageInfo(NodeRole senderRole_, NodeRole receiverRole_)
        {
            senderRole = senderRole_;
            receiverRole = receiverRole_;
        }
    }*/
    
    /*class MessageTypeManager
    {

        HashSet<MessageType> uncontorlledMessages = new HashSet<MessageType>();
        
        readonly public Dictionary<MessageType, MessageInfo> messages = new Dictionary<MessageType,MessageInfo>();

        readonly public HashSet<NodeRole> validators = new HashSet<NodeRole>();

        public PlayerActorProcessor playerMessageReciever = new PlayerActorProcessor();
        public PlayerValidatorProcessor playerValidatorMessageReciever = new PlayerValidatorProcessor();
        public WorldValidatorProcessor playerWorldVerifierMessageReciever = new WorldValidatorProcessor();

        public MessageTypeManager()
        {
            uncontorlledMessages.Add(MessageType.HANDSHAKE);
            uncontorlledMessages.Add(MessageType.TABLE_REQUEST);
            uncontorlledMessages.Add(MessageType.TABLE);
            uncontorlledMessages.Add(MessageType.ROLE);
            uncontorlledMessages.Add(MessageType.GENERATE);
            
            messages.Add(MessageType.VALIDATE_MOVE, new MessageInfo(NodeRole.PLAYER, NodeRole.WORLD_VALIDATOR));
            messages.Add(MessageType.VALIDATE_TELEPORT, new MessageInfo(NodeRole.PLAYER, NodeRole.WORLD_VALIDATOR));
            messages.Add(MessageType.VALIDATE_REALM_MOVE, new MessageInfo(NodeRole.PLAYER, NodeRole.WORLD_VALIDATOR));

            messages.Add(MessageType.MOVE, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER));
            messages.Add(MessageType.TELEPORT, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER));

            messages.Add(MessageType.LOOT_PICKUP, new MessageInfo(NodeRole.PLAYER_VALIDATOR, NodeRole.PLAYER));
            messages.Add(MessageType.LOOT_CONSUMED, new MessageInfo(NodeRole.PLAYER_VALIDATOR, NodeRole.PLAYER));

            messages.Add(MessageType.LOOT_PICKUP_BROADCAST, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER_VALIDATOR));
            messages.Add(MessageType.FREEZE_ITEM, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER_VALIDATOR));
            messages.Add(MessageType.UNFREEZE_ITEM, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER_VALIDATOR));
            messages.Add(MessageType.CONSUME_FROZEN_ITEM, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER_VALIDATOR));

            messages.Add(MessageType.FREEZING_SUCCESS, new MessageInfo(NodeRole.PLAYER_VALIDATOR, NodeRole.WORLD_VALIDATOR));
            messages.Add(MessageType.FREEZING_FAIL, new MessageInfo(NodeRole.PLAYER_VALIDATOR, NodeRole.WORLD_VALIDATOR));

            messages.Add(MessageType.REALM_MOVE, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.WORLD_VALIDATOR));
            messages.Add(MessageType.REALM_MOVE_FAIL, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.WORLD_VALIDATOR));
            messages.Add(MessageType.REALM_MOVE_SUCESS, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.WORLD_VALIDATOR));

            messages.Add(MessageType.REMOVE_PLAYER, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER));
            messages.Add(MessageType.ADD_PLAYER, new MessageInfo(NodeRole.WORLD_VALIDATOR, NodeRole.PLAYER));


            validators.Add(NodeRole.WORLD_VALIDATOR);
            validators.Add(NodeRole.PLAYER_VALIDATOR);

            foreach (var info in messages.Values)
            {
                if (info.receiverRole == NodeRole.PLAYER)
                    info.mr = playerMessageReciever;
                else if (info.receiverRole == NodeRole.WORLD_VALIDATOR)
                    info.mr = playerWorldVerifierMessageReciever;
                else if (info.receiverRole == NodeRole.PLAYER_VALIDATOR)
                    info.mr = playerValidatorMessageReciever;
            }

            VerifyLogic();
        }

        void VerifyLogic()
        {
            foreach (var kv in messages)
            {
                if (kv.Value.mr == null)
                    throw new Exception("Unassigned message processor for message " + kv.Key.ToString());
            }

            foreach (MessageType mt in Enum.GetValues(typeof(MessageType)))
            {
                if (messages.ContainsKey(mt) && uncontorlledMessages.Contains(mt))
                    throw new Exception("Message duplication " + mt.ToString());

                if (uncontorlledMessages.Contains(mt))
                    continue;

                if(!messages.ContainsKey(mt))
                    throw new Exception("Unassigned message " + mt.ToString());
            }
        }
    }*/

    [Serializable]
    public class PlayerInfo
    {
        public Guid id;
        public OverlayEndpoint playerHost;
        public OverlayEndpoint validatorHost;
        public string name;

        public PlayerInfo() { }
        public PlayerInfo(Guid id_, OverlayEndpoint playerHost_, OverlayEndpoint validatorHost_, string name_)
        {
            id = id_;
            playerHost = playerHost_;
            validatorHost = validatorHost_;
            name = name_;
        }

        public override string ToString()
        {
            return "Player " + name;
        }
        
        public string GetFullInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Player id: {0}\n", id);
            sb.AppendFormat("Player name: {0}\n", name);
            sb.AppendFormat("Player host: {0}\n", playerHost);
            sb.AppendFormat("Player validator: {0}\n", validatorHost);

            return sb.ToString();
        }
    }

    [Serializable]
    public class GameInfoSerialized
    {
        public PlayerInfo[] players;
        public WorldInfo[] worlds;

        public GameInfoSerialized() { }
    }

    class GameInfo
    {
        public GameInfo() { }
        public GameInfo(GameInfoSerialized info)
        {
            foreach (PlayerInfo p in info.players)
                AddPlayer(p);
            foreach (WorldInfo w in info.worlds)
                AddWorld(w);
        }

        public GameInfoSerialized Serialize()
        {
            return new GameInfoSerialized() { players = playerById.Values.ToArray(), worlds = worldByPoint.Values.ToArray() };
        }

        public NodeRole GetRoleOfHost(OverlayEndpoint host) { return roles.GetValue(host); }

        public PlayerInfo GetPlayerByHost(OverlayEndpoint host) { return playerByHost.GetValue(host); }
        public WorldInfo GetWorldByHost(OverlayEndpoint host) { return worldByHost.GetValue(host); }

        public OverlayEndpoint GetPlayerHost(Guid player) { return playerById.GetValue(player).playerHost; }
        public OverlayEndpoint GetPlayerValidatorHost(Guid player) { return playerById.GetValue(player).validatorHost; }
        public OverlayEndpoint GetWorldHost(Point worldPos) { return worldByPoint.GetValue(worldPos).host; }

        public void AddPlayer(PlayerInfo info)
        {
            roles.Add(info.playerHost, NodeRole.PLAYER);
            roles.Add(info.validatorHost, NodeRole.PLAYER_VALIDATOR);

            playerById.Add(info.id, info);
            playerByHost.Add(info.playerHost, info);
            playerByHost.Add(info.validatorHost, info);
        }
        public void AddWorld(WorldInfo info)
        {
            roles.Add(info.host, NodeRole.WORLD_VALIDATOR);

            worldByPoint.Add(info.worldPos, info);
            worldByHost.Add(info.host, info);
        }

        Dictionary<OverlayEndpoint, NodeRole> roles = new Dictionary<OverlayEndpoint, NodeRole>();

        Dictionary<Guid, PlayerInfo> playerById = new Dictionary<Guid, PlayerInfo>();
        Dictionary<OverlayEndpoint, PlayerInfo> playerByHost = new Dictionary<OverlayEndpoint, PlayerInfo>();

        Dictionary<Point, WorldInfo> worldByPoint = new Dictionary<Point, WorldInfo>();
        Dictionary<OverlayEndpoint, WorldInfo> worldByHost = new Dictionary<OverlayEndpoint, WorldInfo>();
    }

    /*class AssignmentInfo
    {
        public Dictionary<Guid, Guid> validators = new Dictionary<Guid,Guid>();
        public MultiValueDictionary<Guid, NodeRole> roles = new MultiValueDictionary<Guid,NodeRole>();
        public Dictionary<Guid, Node> nodes = new Dictionary<Guid,Node>();
        public HashSet<Guid> controlledByMe = new HashSet<Guid>();

        public Node NodeById(Guid id)
        {
            return nodes.GetValue(id);
        }
        public Guid GetValidtor(Guid id)
        {
            return validators.GetValue(id);
        }
        public bool IsMyRole(Guid id)
        {
            return controlledByMe.Contains(id);
        }
        public bool HasRole(Guid id, NodeRole role)
        {
            return roles.ContainsValue(id, role);
        }

        public void AddRole(Role r)
        {
            foreach (Guid id in r.player)
                roles.Add(id, NodeRole.PLAYER);
            foreach (Guid id in r.validator)
                roles.Add(id, NodeRole.POTENTIAL_VALIDATOR);
        }

        public IEnumerable<Guid> GetMyRoles(NodeRole nr)
        {
            return from id in controlledByMe
                   where roles.ContainsValue(id, nr)
                   select id;
        }

        public Role GetMyRole()
        {
            Role role = new Role();
            role.player = new HashSet<Guid>(GetMyRoles(NodeRole.PLAYER));
            role.validator = new HashSet<Guid>(GetMyRoles(NodeRole.POTENTIAL_VALIDATOR));
            return role;
        }
        public Role GetAllRoles()
        {
            Role role = new Role();
            role.player = new HashSet<Guid>(from id in roles.Keys
                                            where roles.ContainsValue(id, NodeRole.PLAYER)
                                            select id);
            role.validator = new HashSet<Guid>(from id in roles.Keys
                                               where roles.ContainsValue(id, NodeRole.POTENTIAL_VALIDATOR)
                                               select id);
            return role;
        }

        public void SendValidatorMessage(MessageType mt, Guid sender, Guid receiver, params object[] args)
        {
            List<object> l = new List<object>();
            l.Add(sender);
            l.Add(receiver);
            l.AddRange(args);

            object[] newArgs = l.ToArray();

            Guid validator = GetValidtor(receiver);

            NodeById(validator).SendMessage(mt, newArgs);
        }
    }*/

    /*class MessageProcessor
    {
        MessageTypeManager mtm;

        AssignmentInfo gameInfo;

        ActionSyncronizer globalSync;

        public MessageProcessor(MessageTypeManager mtm_, AssignmentInfo gameInfo_, ActionSyncronizer globalSync_)
        {
            mtm = mtm_;
            gameInfo = gameInfo_;
            globalSync = globalSync_;
        }

        void Verify(Node nd, MessageType mt, Guid sender, Guid receiver)
        {
            MessageInfo info = mtm.messages.GetValue(mt);

            if(mtm.validators.Contains(info.senderRole))
                sender = gameInfo.GetValidtor(sender);

            if(mtm.validators.Contains(info.receiverRole))
                receiver = gameInfo.GetValidtor(receiver);
            
            // check valid sender/receiver id
            MyAssert.Assert(gameInfo.NodeById(sender) == nd);
            if (info.receiverRole != NodeRole.PLAYER)
                MyAssert.Assert(gameInfo.IsMyRole(receiver));

            // check compatibility with the message type
            MyAssert.Assert(gameInfo.HasRole(sender, info.senderRole));
            if (info.receiverRole != NodeRole.PLAYER)
                MyAssert.Assert(gameInfo.HasRole(receiver, info.receiverRole));
        }

        public void ProcessMessage(Node nd, MessageType mt, Stream stm)
        {
            MessageInfo info = mtm.messages.GetValue(mt);

            Guid sender = Serializer.Deserialize<Guid>(stm);
            Guid receiver = Serializer.Deserialize<Guid>(stm);

            Action<Action> syncronizer = 
                (f) =>
                {
                    globalSync.Add(() =>
                        {
                            Verify(nd, mt, sender, receiver);
                            f.Invoke();
                        });
                };

            info.mr.ProcessMessage(mt, sender, receiver, stm, syncronizer);
        }
    }*/
}
