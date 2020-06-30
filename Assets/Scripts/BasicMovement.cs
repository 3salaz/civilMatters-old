using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BasicMovement : MonoBehaviour
{
    public Animator animator;



        //  Input.GetAxis("Horizontal") - give back a number between -1 to 1 depending on which arrow keys we press ~>
        // 
        // change tranform.position on X axis , this position is a 3d vector which uses 3 variables to select a point in space  

    // Update is called once per frame
    void Update()
    {
        animator.SetFloat("Horizontal", Input.GetAxis("Horizontal"));
        Vector3 horizontal = new Vector3(Input.GetAxis("Horizontal"), 0.0f, 0.0f);
        transform.position = transform.position + horizontal * Time.deltaTime;
    }
}
