using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MainCamera : MonoBehaviour
{
    [SerializeField] private Transform followTransform;
    private Vector3 offset;

    private void Awake()
    {
        offset = transform.position - followTransform.position;
    }

    private void Update()
    {
        transform.position = new Vector3(followTransform.position.x, 0f, followTransform.position.z) + offset;
    }
}
