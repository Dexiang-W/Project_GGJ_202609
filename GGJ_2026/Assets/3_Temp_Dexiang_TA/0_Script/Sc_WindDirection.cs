using System.Collections;
using System.Collections.Generic;
using UnityEngine;
[ExecuteAlways]

public class WindDirection : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        Vector4 windDirection = new Vector4(transform.forward.x, 0, transform.forward.z, 0).normalized;
        Shader.SetGlobalVector("_WindDirection", windDirection);
    }
}
