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
    public enum MessageType : byte { HANDSHAKE, TABLE_REQUEST, TABLE, ROLE, GENERATE,
        VALIDATE_MOVE, MOVE,
        LOOT_PICKUP, LOOT_PICKUP_BROADCAST,
        VALIDATE_TELEPORT, FREEZE_ITEM, FREEZING_SUCCESS, UNFREEZE_ITEM, CONSUME_FROZEN_ITEM, TELEPORT, LOOT_CONSUMED
    };

    public enum MoveValidity { VALID, BOUNDARY, OCCUPIED_PLAYER, OCCUPIED_WALL, TELEPORT };

    public enum NodeRole { PLAYER, WORLD, PLAYER_VALIDATOR, WORLD_VALIDATOR };

    class MessageReceiver
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

        readonly public HashSet<NodeRole> validators;

        public PlayerActorProcessor playerMessageReciever = new PlayerActorProcessor();
        public PlayerValidatorProcessor playerValidatorMessageReciever = new PlayerValidatorProcessor();
        public WorldValidatorProcessor playerWorldVerifierMessageReciever = new WorldValidatorProcessor();

        MessageTypeManager()
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
        public Dictionary<Guid, Guid> validators;
        public MultiValueDictionary<Guid, NodeRole> roles;
        public Dictionary<Guid, Node> nodes;
        public HashSet<Guid> controlledByMe;

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
    }

    class MessageProcessor
    {
        MessageTypeManager mtm;

        AssignmentInfo gameInfo;

        ActionSyncronizer globalSync;

        void Verify(Node nd, MessageType mt, Guid sender, Guid receiver)
        {
            MessageInfo info = mtm.messages.GetValue(mt);

            if(mtm.validators.Contains(info.senderRole))
                sender = gameInfo.GetValidtor(sender);

            if(mtm.validators.Contains(info.receiverRole))
                receiver = gameInfo.GetValidtor(receiver);
            
            // check valid sender/receiver id
            Debug.Assert(gameInfo.NodeById(sender) == nd);
            Debug.Assert(gameInfo.IsMyRole(receiver));

            // check compatibility with the message type
            Debug.Assert(gameInfo.HasRole(sender, info.senderRole));
            Debug.Assert(gameInfo.HasRole(receiver, info.receiverRole);
        }

        void ProcessMessage(Node nd, MessageType mt, Stream stm)
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
