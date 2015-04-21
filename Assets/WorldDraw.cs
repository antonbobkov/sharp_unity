using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEngine;

using ServerClient;
using Network;
using Tools;



class WorldDraw : World
{
    //minecraft main;
    private Point sz;

	private GameObject background;
	private Plane<GameObject> walls;
	private Plane<GameObject> loots;
    private Dictionary<Guid, GameObject> playerAvatars = new Dictionary<Guid, GameObject>();

    private HashSet<GameObject> teleportAnimations = new HashSet<GameObject>();
    private Action<Guid, GameObject> updateCamera;
    private Action<WorldDraw> onDestruction;


    private void NewTeleportAnimation(Vector3 pos, Color c)
    {
        var avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        avatar.renderer.material.color = c;
        avatar.transform.position = pos;// minecraft.GetPositionAtGrid(w.Position, pos);

        //Log.Console(avatar.transform.localScale.ToString());

        teleportAnimations.Add(avatar);
    }
	private void MessBackground()
    {
        //background.transform.rotation = Quaternion.AngleAxis(2f, UnityEngine.Random.onUnitSphere);
        background.transform.localScale *= .99f;
    }

    public WorldDraw(WorldInitializer init, bool isOwnedByUs, Action<Guid, GameObject> updateCamera, Action<WorldDraw> onDestruction)
        :base(init, null)
	{
        this.updateCamera = updateCamera;
        this.onDestruction = onDestruction;
        sz = World.worldSize;

		walls = new Plane<GameObject>(sz);
		loots = new Plane<GameObject>(sz);
		
		foreach(Point pos in Point.Range(sz))
		{
			ITile t = this[pos];
			if (t.Solid)
                PlaceBlock(pos);
			else if(t.Loot)
			{
				GameObject loot = GameObject.CreatePrimitive(PrimitiveType.Quad);
				loots [pos] = loot;
				
				loot.transform.localScale = new Vector3(.5f, .5f, 1);
				loot.renderer.material.color = Color.blue;
				loot.transform.position = minecraft.GetPositionAtGrid(Position, pos);
				loot.transform.rotation = Quaternion.AngleAxis(15f, UnityEngine.Random.onUnitSphere);
				loot.AddComponent<RotateSlowly>();
			}
		}

		background = GameObject.CreatePrimitive(PrimitiveType.Quad);
		background.transform.localScale = new Vector3(sz.x, sz.y, 1);
		background.transform.position = minecraft.GetPositionAtGrid(Position, new Point(0,0)) + new Vector3(sz.x-1, sz.y-1, 1)/2;
		if(isOwnedByUs)
            background.renderer.material.color = new Color(.6f, .6f, .6f);
        else
            background.renderer.material.color = new Color(.7f, .7f, .7f);

        MessBackground();
        
        foreach (PlayerInfo inf in GetAllPlayers())
			AddPlayer(inf);
    }

    public void TickAnimations(float deltaTime)
    {
        Vector3 change = deltaTime * 2 * Vector3.one;

        foreach (var k in teleportAnimations.ToArray())
        {
            k.transform.localScale -= change;
            if (k.transform.localScale.x <= 0)
            {
                teleportAnimations.Remove(k);
                UnityEngine.Object.Destroy(k);
            }
        }

        foreach (var k in playerAvatars.Values)
        {
            if (k.transform.localScale.x < 1)
                k.transform.localScale += change;
            if (k.transform.localScale.x > 1)
                k.transform.localScale = Vector3.one;
        }
    }
    
    public override void Dispose()
	{
		foreach(Point pos in Point.Range(sz))
		{
			UnityEngine.Object.Destroy (walls[pos]);
			UnityEngine.Object.Destroy (loots[pos]);
		}

        foreach(GameObject pl in playerAvatars.Values)
            UnityEngine.Object.Destroy(pl);

		UnityEngine.Object.Destroy(background);

        onDestruction(this);
	}

    public override void NET_AddPlayer(PlayerInfo player, Point pos, bool teleporting)
    {
        base.NET_AddPlayer(player, pos, teleporting);

        //AddPlayer(player, pos); <- will be handled in NET_Move
    }
    private void AddPlayer(PlayerInfo player)
    {
        MyAssert.Assert(HasPlayer(player.id));

        Point pos = base.GetPlayerPosition(player.id);

        GameObject avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);

        var r = new MyRandom(BitConverter.ToInt32(player.id.ToByteArray(), 0));

        avatar.renderer.material.color = new Color(
            (float)r.NextDouble(),
            (float)r.NextDouble(),
            (float)r.NextDouble());

        avatar.transform.position = minecraft.GetPositionAtGrid(Position, pos);
        playerAvatars.Add(player.id, avatar);

        updateCamera(player.id, avatar);
        //if (player.id == main.me)
        //    main.UpdateCamera(avatar);
    }

    public override void NET_RemovePlayer(Guid player, bool teleporting)
    {
        if (!playerAvatars.ContainsKey(player))
            return;

        GameObject avatar = playerAvatars.GetValue(player);

        if (teleporting)
            NewTeleportAnimation(avatar.transform.position, avatar.renderer.material.color);

        //MyAssert.Assert(w.playerPositions.ContainsKey(player));
        UnityEngine.Object.Destroy(avatar);
		playerAvatars.Remove(player);
    }

    public override void NET_Move(Guid player, Point newPos, ActionValidity mv)
    {
        base.NET_Move(player, newPos, mv);

        bool teleported = mv.Has(ActionValidity.REMOTE);

        if (mv.Has(ActionValidity.NEW))
            AddPlayer(base.GetPlayerInfo(player));

        GameObject obj = loots[newPos];
        if (obj != null)
        {
            GameObject.Destroy(obj);
            loots[newPos] = null;
        }

        GameObject movedPlayer = playerAvatars.GetValue(player);

        if (teleported && !mv.Has(ActionValidity.NEW))
            NewTeleportAnimation(movedPlayer.transform.position, movedPlayer.renderer.material.color);

        movedPlayer.transform.position = minecraft.GetPositionAtGrid(Position, newPos);

        if (teleported)
            movedPlayer.transform.localScale = Vector3.one * .1f;

        updateCamera(player, movedPlayer);
    }

    public override void NET_RemoveBlock(Point pos)
    {
        ITile t = this[pos];

        MyAssert.Assert(t.IsEmpty());
        MyAssert.Assert(walls[pos] != null);

        UnityEngine.Object.Destroy(walls[pos]);
    }

    public override void NET_PlaceBlock(Point pos)
    {
        base.NET_PlaceBlock(pos);
        PlaceBlock(pos);
    }
    private void PlaceBlock(Point pos)
    {
        ITile t = this[pos];

        MyAssert.Assert(t.Solid);
        MyAssert.Assert(walls[pos] == null);

        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        walls[pos] = wall;

        wall.transform.position = minecraft.GetPositionAtGrid(Position, pos);
        if (!t.Spawn)
        {
            wall.renderer.material.color = new Color((float)t.Block.R / 255, (float)t.Block.G / 255, (float)t.Block.B / 255);
        }
        else
            wall.renderer.material.color = Color.yellow;
    }

}
