namespace ServerClient
{
    public enum MessageType : byte { HANDSHAKE, TABLE_REQUEST, TABLE, ROLE, GENERATE, VALIDATE_MOVE, MOVE };
    public enum MoveValidity { VALID, BOUNDARY, OCCUPIED, OCCUPIED_PLAYER, OCCUPIED_WALL, TELEPORT };
}
