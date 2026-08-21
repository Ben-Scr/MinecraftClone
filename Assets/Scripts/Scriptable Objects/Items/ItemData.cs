using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace BenScr.MinecraftClone
{

    public abstract class ItemData : ScriptableObject
    {
        [Header("General")]

        [FormerlySerializedAs("id")]
        public int Id;
        [FormerlySerializedAs("sprite")]
        public Sprite Sprite;
        [FormerlySerializedAs("stackSize")]
        public ushort StackSize = 16;
        [SerializeField] internal string _name;
        public string Name => _name;
        internal string NameEnglish;

        [FormerlySerializedAs("description")]
        public string Description;
        [FormerlySerializedAs("type")]
        public ItemType Type = ItemType.None;

        [Header("Duration")]
        [FormerlySerializedAs("durable")]
        public bool Durable;
        [FormerlySerializedAs("maxDuration")]
        public int MaxDuration;

        [Header("Positioning")]
        [FormerlySerializedAs("offset")]
        public Vector2 Offset;
        [FormerlySerializedAs("size")]
        public Vector2 Size = new Vector2(0.55f, 0.55f);
        [FormerlySerializedAs("rotation")]
        public Vector3 Rotation;
        [FormerlySerializedAs("hand")]
        public Hand Hand;

        [Header("Prizing")]
        [FormerlySerializedAs("prizeData")]
        public PrizeData PrizeData;
        public bool isFree => PrizeData.PrizeItems == null || PrizeData.PrizeAmounts == null || PrizeData.PrizeItems.Length == 0;
    }
    [System.Flags]
    public enum ItemType
    {
        None = 0,
        Weapon = 1 << 0,
        Armor = 1 << 1,
        Potion = 1 << 2,
        QuestItem = 1 << 3,
    }

    [Serializable]
    public struct PrizeData
    {
        [FormerlySerializedAs("prizeItems")]
        public ItemData[] PrizeItems;
        [FormerlySerializedAs("prizeAmounts")]
        public int[] PrizeAmounts;
    }

    public enum Hand { Left, Right, Both }
}
