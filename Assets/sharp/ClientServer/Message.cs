﻿using System;
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
    public enum MessageType : byte { HANDSHAKE, TABLE_REQUEST, TABLE, ROLE, GENERATE,
        VALIDATE_MOVE, MOVE,
        LOOT_PICKUP, LOOT_PICKUP_BROADCAST,
        VALIDATE_TELEPORT, FREEZE_ITEM, FREEZING_SUCCESS, FREEZING_FAIL,
        UNFREEZE_ITEM, CONSUME_FROZEN_ITEM, TELEPORT, LOOT_CONSUMED
    };

    public enum MoveValidity { VALID, BOUNDARY, OCCUPIED_PLAYER, OCCUPIED_WALL, TELEPORT };

    public enum NodeRole { PLAYER, WORLD, POTENTIAL_VALIDATOR, PLAYER_VALIDATOR, WORLD_VALIDATOR };

    abstract class MessageReceiver
    {
        public abstract void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer);
    }
    
    class MessageInfo
    {
        public readonly NodeRole senderRole;
        public readonly NodeRole receiverRole;

        public MessageReceiver mr = null;

        public MessageInfo(NodeRole senderRole_, NodeRole receiverRole_)
        {
            senderRole = senderRole_;
            receiverRole = receiverRole_;
        }
    }
    
    class MessageTypeManager
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
    }

    class AssignmentInfo
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
    }

    class MessageProcessor
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
    }
}
