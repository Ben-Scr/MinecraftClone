using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BenScr.MCC
{
    public class FPSLimit : MonoBehaviour
    {
        [SerializeField] private int targetFPS;

        void Start()
        {
            Application.targetFrameRate = targetFPS;
        }

    }
}