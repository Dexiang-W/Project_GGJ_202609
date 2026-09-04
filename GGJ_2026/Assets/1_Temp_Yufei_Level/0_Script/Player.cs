using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal.Internal;

public class Player : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float jumpForce = 8f;
    public float gravity = -9.81f;

    private CharacterController controller;
    private Vector3 velocity;
    private bool isGrounded;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
    }

    private void Update()
    {
        isGrounded = controller.isGrounded;
        if (isGrounded && velocity.y < 0)
            velocity.y = -2f;

        float horizontal = Input.GetAxis("Horizontal");
        Vector3 move = Vector3.right * horizontal * moveSpeed;

        velocity.y += gravity * Time.deltaTime;

        Vector3 finalMove = move + Vector3.up * velocity.y;
        controller.Move(finalMove * Time.deltaTime);

        if (horizontal > 0.01f)
            transform.rotation = Quaternion.Euler(0, 90, 0);
        else if (horizontal < -0.01f)
            transform.rotation = Quaternion.Euler(0, -90, 0);

        if (Input.GetButtonDown("Jump") && isGrounded)
            velocity.y = Mathf.Sqrt(jumpForce * -2f * gravity);
    }
}
