using System;
using UnityEngine;

[CreateAssetMenu(fileName = "Block", menuName = "Scriptable Objects/Block")]
public class Block : ScriptableObject
{
    internal ushort id;
    public int durability;
    public bool isTransparent;

    public int backTexture;
    public int frontTexture;
    public int topTexture;
    public int bottomTexture;
    public int leftTexture;
    public int rightTexture;

    public int GetTexture(int face)
    {
        switch (face)
        {
            case 0:
                return backTexture;
            case 1:
                return frontTexture;
            case 2:
                return topTexture;
            case 3:
                return bottomTexture;
            case 4:
                return leftTexture;
            case 5:
                return rightTexture;
            default:
                Debug.LogWarning("Invalid face: " + face);
                return backTexture;
        }
    }
}
