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
using System.Xml.Serialization;

namespace ServerClient
{
    [Serializable]
    public struct PlayerInfo
    {
        public Guid id;
        public OverlayEndpoint playerHost;
        public OverlayEndpoint validatorHost;
        public string name;

        public PlayerInfo(Guid id_, OverlayEndpoint playerHost_, OverlayEndpoint validatorHost_, string name_)
        {
            id = id_;
            playerHost = playerHost_;
            validatorHost = validatorHost_;
            name = name_;
        }

        public override string ToString()
        {
            return GetShortInfo();
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
        public string GetShortInfo()
        {
            return "Player " + name;
        }
    }

    [Serializable]
    public class Inventory
    {
        public int teleport = 0;

        public Inventory() { }
        public Inventory(int teleport_) { teleport = teleport_; }

        public override string ToString()
        {
            return "teleport: " + teleport;
        }
    }

    [Serializable]
    public class PlayerData
    {
        public bool connected = false;
        public Point worldPos = new Point(0,0);

        public Inventory inventory = new Inventory(5);

        public override string ToString()
        {
            return (connected ? worldPos.ToString() : "[not connected]") + " " + inventory;
        }
    }

    class PlayerValidator
    {
        OverlayHost myHost;

        GameInfo gameInfo;
        
        PlayerInfo info;
        Node playerAgentNode = null;

        PlayerData playerData = new PlayerData(); 

        RemoteAction locked = null;
        Queue<Action> delayedActions = new Queue<Action>();

        void ProcessOrDelay(Action a)
        {
            if (locked == null)
                a.Invoke();
            else
            {
                //Log.Dump(info, "delayed action");
                delayedActions.Enqueue(a);
            }
        }
        void ExecuteDelayedActions()
        {
            while (delayedActions.Any())
            {
                //Log.Dump(info, "executed delayed action");
                delayedActions.Dequeue().Invoke();
            }
        }

        public PlayerValidator(PlayerInfo info_, GlobalHost globalHost, GameInfo gameInfo_)
        {
            info = info_;
            gameInfo = gameInfo_;

            myHost = globalHost.NewHost(info.validatorHost.hostname, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }
        
        Node.MessageProcessor AssignProcessor(Node n)
        {
            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = gameInfo.GetWorldByHost(n.info.remote);
                return (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf);
            }

            if (role == NodeRole.PLAYER)
                return (mt, stm, nd) => { throw new Exception("PlayerValidator not expecting messages from PlayerAgent." + mt + " " + nd.info); };                

            throw new InvalidOperationException("WorldValidator.AssignProcessor unexpected connection " + n.info.remote + " " + role);
        }
        
        void ProcessNewConnection(Node n)
        {
            if (n.info.remote == info.playerHost)
                OnAgentConnect(n);
        }
        void OnAgentConnect(Node n)
        {
            playerAgentNode = n;
            MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData, PlayerDataUpdate.INIT);
        }

        void MessageToAgent(MessageType mt, params object[] args)
        {
            if (playerAgentNode == null)
                return;

            playerAgentNode.SendMessage(mt, args);
        }

        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.LOCK_VAR)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);
                OnLock(n, remoteActionId);
            }
            else if (mt == MessageType.UNLOCK_VAR)
            {
                RemoteAction.Process(ref locked, n, stm);
            }
            else if (mt == MessageType.PLAYER_WORLD_MOVE)
            {
                WorldMove wm = Serializer.Deserialize<WorldMove>(stm);
                Point newWorld = inf.worldPos;

                if (wm == WorldMove.LEAVE)
                    newWorld = Serializer.Deserialize<Point>(stm);

                ProcessOrDelay(() => RealmUpdate(newWorld));
            }
            else if (mt == MessageType.PICKUP_ITEM)
                ProcessOrDelay(() => OnPickupItem());
            else
                throw new Exception(Log.StDump(info, inf, mt, "unexpected message"));
        }

        void OnLock(Node n, Guid remoteActionId)
        {
            //Log.Dump();
            
            if (locked != null)
            {
                Log.Dump(info, "already locked");
                RemoteAction.Fail(n, remoteActionId);
                return;
            }

            RemoteAction
                .Send(myHost, n.info.remote, MessageType.RESPONSE, Response.SUCCESS, remoteActionId, new RemoteActionIdInject(), playerData)
                .Respond(ref locked, (res, str) =>
                {
                    //Log.Dump("unlocking", res);

                    if (res == Response.SUCCESS)
                    {
                        PlayerDataUpdate pdu = Serializer.Deserialize<PlayerDataUpdate>(str);
                        PlayerData modifiedData = Serializer.Deserialize<PlayerData>(str);

                        playerData = modifiedData;
                        //Log.Dump(info, "unlocking got new data", pdu);
                        MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData, pdu);
                    }
                    //else
                    //    Log.Dump(info, "unlocking - unchanged");

                    ExecuteDelayedActions();
                });
        }
        
        void RealmUpdate(Point newWorld)
        {
            //Log.Dump(newWorld);
            
            if ((playerData.worldPos != newWorld))
            {
                playerData.worldPos = newWorld;
                MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData, PlayerDataUpdate.JOIN_WORLD);
            }
        }

        void OnPickupItem()
        {
            //Log.Dump();

            ++playerData.inventory.teleport;
            MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData, PlayerDataUpdate.INVENTORY_CHANGE);
        }
    }

    class PlayerAgent
    {
        OverlayHost myHost;

        OverlayEndpoint serverHost;
        public GameInfo gameInfo;

        public readonly PlayerInfo info;
        public PlayerData data = null;

        public Action<PlayerData> onNewPlayerDataHook = (a) => { };
        public Action<PlayerData> onPlayerNewRealm = (a) => { };

        public PlayerAgent(PlayerInfo info_, GameInfo gameInfo_, GlobalHost globalHost, OverlayEndpoint serverHost_)
        {
            info = info_;
            serverHost = serverHost_;
            gameInfo = gameInfo_;

            myHost = globalHost.NewHost(info.playerHost.hostname, AssignProcessor);

            myHost.ConnectAsync(info.validatorHost);

            Log.LogWriteLine("Player Agent {0}", info.GetShortInfo());
        }

        Node.MessageProcessor AssignProcessor(Node n)
        {
            if (n.info.remote == serverHost)
                return (mt, stm, nd) => { throw new Exception(Log.StDump(mt, nd.info.remote, "unexpected")); };
            
            if (n.info.remote == info.validatorHost)
                return (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd);
            
            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
                return (mt, stm, nd) => { throw new Exception(Log.StDump(mt, nd.info.remote, "unexpected")); };

            throw new Exception(Log.StDump(n.info.remote, "unexpected"));
        }

        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.PLAYER_INFO_VAR)
            {
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                PlayerDataUpdate pdu = Serializer.Deserialize<PlayerDataUpdate>(stm);
                OnPlayerData(pd, pdu);
            }
            else
                throw new Exception(Log.StDump(mt, "unexpected"));
        }

        void OnPlayerData(PlayerData pd, PlayerDataUpdate pdu)
        {
            if (pdu == PlayerDataUpdate.INIT)
                MyAssert.Assert(data == null);
            else
                MyAssert.Assert(data != null);
            
            data = pd;
            //Log.LogWriteLine(Log.StDump( inf, pd, pdu));

            if (pdu == PlayerDataUpdate.INIT)
                onNewPlayerDataHook(pd);
            else if (pdu == PlayerDataUpdate.JOIN_WORLD || pdu == PlayerDataUpdate.SPAWN)
                onPlayerNewRealm(pd);
            else if (pdu != PlayerDataUpdate.INVENTORY_CHANGE)
                throw new Exception(Log.StDump(pdu, "unexpected"));
        }

        
        public void Spawn()
        {
            myHost.ConnectSendMessage(serverHost, MessageType.SPAWN_REQUEST);
        }
        public void Move(WorldInfo worldInfo, Point newPos, MoveValidity mv)
        {
            myHost.ConnectSendMessage(worldInfo.host, MessageType.MOVE_REQUEST, newPos, mv);
        }
    }
}
