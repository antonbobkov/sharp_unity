using UnityEngine;
using System.Collections;

public class script : MonoBehaviour {

	// Use this for initialization
	void Start () {
		gameObject.GetComponent<Renderer> ().material.color = new Color (1, 0, 0);
		var go = new GameObject ("light");
		var light = go.AddComponent<Light> ();
		//light.range = 1000;
		//light.type = LightType.Point;
		//light.intensity = 1000;
		light.transform.position = Camera.main.transform.position;

	}
	
	// Update is called once per frame
	void Update () {
		//transform.rotation *= Quaternion.AngleAxis (Random.Range (0, 360), new Vector3(0,1,0));
		transform.rotation *= Quaternion.AngleAxis (Time.deltaTime*100, Random.onUnitSphere);
		if (Input.GetKeyDown (KeyCode.Space))
						HasBody = HasBody;
	}

	bool HasBody {
		set {
			if (value) gameObject.AddComponent<Rigidbody>();
			else GameObject.Destroy(gameObject.GetComponent<Rigidbody>());
		}
		get{
			return rigidbody == null;
		}
	}
	
	public bool IsCool{ get; private set; }

}
