using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace ServerClient
{
    [Serializable]
    public class Inventory
    {
        public int teleport = 0;

        public Inventory() { }
        public Inventory(int teleport_) { teleport = teleport_; }
    }

    class PlayerData
    {
        public Point? worldPos = null;

        public Inventory totalInventory = new Inventory(5);
        public Inventory frozenInventory = new Inventory();
    }

    class PlayerValidator
    {
        Action<Action> sync;
        OverlayHost myHost;

        PlayerInfo info;
        
        Inventory totalInventory = new Inventory(5);
        Inventory frozenInventory = new Inventory();

        public PlayerValidator(PlayerInfo info_, Action<Action> sync_, GlobalHost globalHost)
        {
            info = info_;
            sync = sync_;

            myHost = globalHost.NewHost(info.validatorHost.hostname, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }
        
        Node.MessageProcessor AssignProcessor(Node n)
        {
            return (mt, stm, nd) => { throw new Exception("PlayerValidator is not expecting any messages. " + mt.ToString() + " from " + nd.info.ToString()); };
        }
        
        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
            else
                throw new InvalidOperationException("PlayerValidator.ProcessNewConnection unexpected connection " + remoteName.ToString());
        }

        void OnNewClient(Node n)
        {
            n.SendMessage(MessageType.INVENTORY_INIT, totalInventory);
        }
    }
}
