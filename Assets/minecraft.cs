using UnityEngine;
using System.Collections.Generic;
using System;
using System.Net;

using ServerClient;

public class minecraft : MonoBehaviour {

    Aggregate all;

    Vector3 vCorner;

    bool gameStarted = false;

	GameObject[,] go;
    Dictionary<System.Guid, GameObject> players = new Dictionary<System.Guid, GameObject>();

    Vector3 GetPositionAtGrid(int x, int y)
    {
        return vCorner + new Vector3(x, y, 0);
    }

    void StartGame()
    {
        Game g = all.game;
        int szX = g.world.GetLength(0);
        int szY = g.world.GetLength(1);
        go = new GameObject[szX, szY];

        for (int i = 0; i < szX; ++i)
        {
            for (int j = 0; j < szY; ++j)
            {
                if (g.world[i, j].solid)
                {
                    go [i, j] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go [i, j].transform.position = GetPositionAtGrid(i, j);
                }
            }
        }

        foreach (var pair in g.players)
        {
            System.Guid id = pair.Key;
            Player p = pair.Value;

            var avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);

            //Convert.ToInt32( id.ToByteArray()
            var r = new ServerClient.Random(BitConverter.ToInt32(id.ToByteArray(), 0));

            avatar.renderer.material.color = new Color(
                (float) r.NextDouble(),
                (float) r.NextDouble(),
                (float) r.NextDouble());

            avatar.transform.position = GetPositionAtGrid(p.pos.x, p.pos.y);
            players.Add(id, avatar);
        }
    }

    const float cameraDistance = 10f;

	// Use this for initialization
	void Start () {
        Log.log = msg => Debug.Log(msg); 

        all = new Aggregate();

        all.myRole.player = Guid.NewGuid();
        Log.LogWriteLine("Player {0}", all.myRole.player);

        // mesh connect
        Program.MeshConnect(all);

        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 20;

        camera.isOrthoGraphic = true;

        vCorner = camera.ViewportToWorldPoint(new Vector3(.5f, .5f, 0));
        camera.orthographicSize = 10;

        Application.runInBackground = true;
    }

    void OnApplicationQuit () {
        all.sync.Add(null);
        all.peers.Close();
    }

    void ProcessMovement()
    {
        Point p = new Point();

        if (Input.GetKeyDown(KeyCode.UpArrow))
            p = p + new Point(0, 1);
        if (Input.GetKeyDown(KeyCode.DownArrow))
            p = p + new Point(0, -1);
        if (Input.GetKeyDown(KeyCode.LeftArrow))
            p = p + new Point(-1, 0);
        if (Input.GetKeyDown(KeyCode.RightArrow))
            p = p + new Point(1, 0);

        if (Input.GetKeyDown(KeyCode.Space))
        {
            p = p + new Point(1,1);
        }

        if (p.x != 0 || p.y != 0)
        {
            Game g = all.game;
            Player pl = g.players[all.myRole.player];

            PlayerMoveInfo mv = new PlayerMoveInfo(pl.id, pl.pos + p);
            if (g.CheckValidMove(mv) == MoveValidity.VALID)
            {
                //Log.LogWriteLine("Move from {0} to {1}", pl.pos, mv.pos);
                all.Move(mv);
            }

            if (Input.GetKeyDown(KeyCode.Space))
            {
                for(int i = 0; i < 10000; ++i)
                    all.Move(mv);
            }
        }
    }
	
	// Update is called once per frame
	void Update () {

        if (!gameStarted)
        {
            lock(all.sync.syncLock)
            {
                if (Input.GetKeyDown(KeyCode.G))
                    all.GenerateGame();
                if(all.game == null)
                    return;
                gameStarted = true;
                StartGame();
                Debug.Log("Game started");
            }
        }

        lock (all.sync.syncLock)
        {
            ProcessMovement();

            foreach (var pair in all.game.players)
            {
                System.Guid id = pair.Key;
                Player p = pair.Value;
                
                players[id].transform.position = GetPositionAtGrid(p.pos.x, p.pos.y);
            }

            camera.transform.position = players[all.myRole.player].transform.position;
            camera.transform.position += new Vector3(0f, 0f, -cameraDistance);
            //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
        }
	}
}