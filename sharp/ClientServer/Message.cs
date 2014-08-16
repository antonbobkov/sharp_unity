using System;
using System.IO;
using System.Net;

namespace ServerClient
{
    public enum MessageType : byte { HANDSHAKE, MESSAGE, NAME, TABLE_REQUEST, TABLE};
}
