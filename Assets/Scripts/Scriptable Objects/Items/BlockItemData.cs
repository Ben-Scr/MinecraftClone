using BenScr.MinecraftClone;
using UnityEngine;
using UnityEngine.Serialization;

namespace BenScr.MinecraftClone
{
    [CreateAssetMenu(fileName = "BlockItemData", menuName = "Scriptable Objects/Items/BlockItemData")]
    public class BlockItemData : ItemData
    {
        [FormerlySerializedAs("block")]
        public BlockData Block;
    }
}
