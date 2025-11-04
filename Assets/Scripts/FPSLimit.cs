using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FPSLimit : MonoBehaviour
{

    void Start()
    {
        Application.targetFrameRate = 60;
    }

}
