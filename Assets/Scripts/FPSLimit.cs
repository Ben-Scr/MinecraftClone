using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FPSLimit : MonoBehaviour
{
    [SerializeField] private int targetFPS;

    void Start()
    {
        Application.targetFrameRate = targetFPS;
    }

}
