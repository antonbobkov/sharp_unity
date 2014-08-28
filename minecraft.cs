using UnityEngine;
using System.Collections.Generic;
using System;
using System.Net;

using ServerClient;

public class minecraft : MonoBehaviour {

    ActionSyncronizer sync;
    NodeHost myHost;

    Vector3 vCorner;

    bool gameStarted = false;

	GameObject[,] go;
    Dictionary<System.Guid, GameObject> players = new Dictionary<System.Guid, GameObject>();


    Vector3 GetPositionAtGrid(int x, int y)
    {
        return vCorner + new Vector3(x, y, 0);
    }

    void MeshConnect()
    {
        IPEndPoint ep = new IPEndPoint(NodeHost.GetMyIP(), NodeHost.nStartPort);
        
        DataCollection.LogWriteLine("Connecting to {0}:{1}", ep.Address, ep.Port);
        
        myHost.dc.Sync_TryConnect(ep);
        myHost.dc.Sync_AskForTable(ep);
    }
    
    void StartGame()
    {
        Game g = myHost.dc.game;
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
        DataCollection.log = msg => Debug.Log(msg); 

        //DataCollection.LogWriteLine("{0}", new System.Random(12).Next());

        //throw new UnityException();

        sync = new ActionSyncronizer();
        myHost = new NodeHost(sync.GetAsDelegate());

        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 20;

        camera.isOrthoGraphic = true;

        vCorner = camera.ViewportToWorldPoint(new Vector3(.5f, .5f, 0));
        camera.orthographicSize = 10;

        Application.runInBackground = true;

        MeshConnect();
    }

    void OnApplicationQuit () {
        sync.Add(null);
        myHost.dc.TerminateThreads();
    }

    void ProcessMovement()
    {
        try{
            //Game g = myHost.dc.game;
            //Player me = 

            //int x = myHost.dc.game.players [myHost.dc.Id];

            Point p = new Point();

            if (Input.GetKeyDown(KeyCode.UpArrow))
                p = new Point(0, 1);
            if (Input.GetKeyDown(KeyCode.DownArrow))
                p = new Point(0, -1);
            if (Input.GetKeyDown(KeyCode.LeftArrow))
                p = new Point(-1, 0);
            if (Input.GetKeyDown(KeyCode.RightArrow))
                p = new Point(1, 0);

            if (p.x != 0 || p.y != 0)
            {
                Game g = myHost.dc.game;
                Player pl = g.players[myHost.dc.Id];

                p = pl.pos + p;
                if(g.world[p.x, p.y].solid)
                    return;
                pl.pos = p;
                myHost.dc.Sync_UpdateMyPosition();
            }
        }
        catch(IndexOutOfRangeException){
        }
    }
	
	// Update is called once per frame
	void Update () {

        ProcessMovement();
        if (!gameStarted)
        {
            lock(myHost.dc)
            {
                if(myHost.dc.game == null)
                    return;
                gameStarted = true;
                StartGame();
                Debug.Log("Game started");
            }
        }

        lock (myHost.dc)
        {
            foreach (var pair in myHost.dc.game.players)
            {
                System.Guid id = pair.Key;
                Player p = pair.Value;
                
                players[id].transform.position = GetPositionAtGrid(p.pos.x, p.pos.y);
            }

            camera.transform.position = players[myHost.dc.Id].transform.position;
            camera.transform.position += new Vector3(0f, 0f, -cameraDistance);
            //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
        }
	}
}