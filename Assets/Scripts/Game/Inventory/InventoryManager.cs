using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace BenScr.MinecraftClone
{
    public class InventoryManager : MonoBehaviour
    {
        [SerializeField] private Transform barSlotLayout, backpackSlotLayout;
        [SerializeField] private GameObject backPackScreen;
        [FormerlySerializedAs("slotItemPrefab")]
        public GameObject SlotItemPrefab;

        public static List<Slot> SlotDatas;
        public static Slot[] BarSlots;
        public static int barSlotsCount => BarSlots.Length;
        public static int PlayerSlotsCount;

        private Slot selectedSlot => BarSlots[currentSlotIndex];
        public int CurrentSlotIndex => currentSlotIndex;

        [FormerlySerializedAs("selectedBarSlotImage")]
        public Image SelectedBarSlotImage;

        public static Item SelectedItem = null;

        public static Action<Slot> OnSwitchSlot;
        public static Action<Slot> OnUpdateSlot;

        private int currentSlotIndex;
        public const int MAX_DURATION_VALUE = -1;

        public static InventoryManager Instance;
        public DynamicObjectPool<GameObject> Pool = new DynamicObjectPool<GameObject>();
        public bool IsBackpackOpen =>
            backPackScreen != null &&
            CanvasScreenManager.ActiveScreen == backPackScreen &&
            backPackScreen.activeInHierarchy;

        [FormerlySerializedAs("addItem")]
        public bool ShouldAddItem;
        [FormerlySerializedAs("prize")]
        public PrizeData Prize;

        private void Awake()
        {
            Instance = this;
            SelectedItem = null;
            InitSlots(); 
        }

        private System.Collections.IEnumerator Start()
        {
            if (SaveController.TryRestoreLoadedInventory(this))
                ShouldAddItem = false;

            UpdateSlot();
            yield return null;
            SwitchedSlot();
        }

        private void Update()
        {
            if (Input.mouseScrollDelta.y >= 1.0)
            {
                currentSlotIndex = (currentSlotIndex - 1 + barSlotsCount) % barSlotsCount;
                SwitchedSlot();
            }
            else if (Input.mouseScrollDelta.y <= -1.0)
            {
                currentSlotIndex = (currentSlotIndex + 1) % barSlotsCount;
                SwitchedSlot();
            }

            if (ShouldAddItem)
            {
                for (int i = 0; i < Prize.PrizeAmounts.Length; i++)
                {
                    AddItem(Prize.PrizeItems[i], Prize.PrizeAmounts[i]);
                }

                ShouldAddItem = false;
            }

            if (Input.GetKeyDown(KeyCode.E))
            {
                if (CanvasScreenManager.ActiveScreen != backPackScreen)
                    CanvasScreenManager.Instance.OpenScreen(backPackScreen);
                else
                    CanvasScreenManager.Instance.CloseActiveScreen();
            }

            if (Input.GetKeyDown(KeyCode.Q) && SelectedItem != null)
            {
                DroppedItemManager.TryDrop(SelectedItem.ItemData, 1, SelectedItem.Duration);
                RemoveItem(selectedSlot, 1);
            }
        }

        private void InitSlots()
        {
            int barSlotsCount = barSlotLayout.childCount;
            int backpackSlotsCount = backpackSlotLayout.childCount;
            PlayerSlotsCount = barSlotsCount + backpackSlotsCount;
            BarSlots = new Slot[barSlotsCount];

            SlotDatas = new List<Slot>(PlayerSlotsCount);

            int i = 0;
            foreach (Transform tr in barSlotLayout)
            {
                SlotDatas.Add(new Slot(tr));
                BarSlots[i] = SlotDatas[i];
                i++;
            }
            foreach (Transform tr in backpackSlotLayout)
            {
                SlotDatas.Add(new Slot(tr));
                i++;
            }
        }

        public static void AddToTargetSlots(Item itemToAdd, TargetSlotArea targetSlotArea)
        {
            int start, end;
            if (targetSlotArea == TargetSlotArea.Backpack) { start = barSlotsCount; end = PlayerSlotsCount; }
            else if (targetSlotArea == TargetSlotArea.Chest) { start = PlayerSlotsCount; end = SlotDatas.Count; }
            else { start = 0; end = barSlotsCount; }

            for (int i = start; i < end; i++)
            {
                if (itemToAdd.Amount <= 0) return;

                Item item = SlotDatas[i].Item;

                if (item != null && item.Matches(itemToAdd))
                {
                    DragAndDropSystem.AddToItem(itemToAdd, item, itemToAdd.Amount);
                }
                else if (item == null)
                {
                    DuplicateItem(itemToAdd, SlotDatas[i], itemToAdd.Amount);
                    DragAndDropSystem.DestroyDragging();
                    return;
                }
            }
        }

        public static void CreateNewItem(ItemData item, int amount, int duration, Slot slot)
        {
            slot.Item = new Item(amount, duration, Instance.Pool.Get(Instance.SlotItemPrefab, Instance.SlotItemPrefab, slot.Transform, false).transform, item);
        }

        public static void DuplicateItem(Item item, Slot slotData, int amount)
        {
            slotData.Item = new Item(amount, item.Duration, Instance.Pool.Get(Instance.SlotItemPrefab, Instance.SlotItemPrefab, slotData.Transform, false).transform, item.ItemData);
        }

        public static void AddItem(ItemData item, int amount, int duration = MAX_DURATION_VALUE, bool checkForDrop = true)
        {
            if (amount == 0) return;
            duration = (duration == MAX_DURATION_VALUE) ? item.MaxDuration : duration;

            amount = AddItemInternal(item, amount, duration);

            if (amount > 0 && checkForDrop)
            {
                //DropItemManager.DropItem(item, amount, item.maxDuration, PlayerController.GetRandomizedForwardPos());
            }

            Instance.UpdateSlot();
        }

        public static int AddItemFromOther(Item item, int amount, int duration)
        {
            return AddItemFromOther(item.ItemData, amount, duration);
        }

        public static int AddItemFromOther(ItemData item, int amount, int duration)
        {
            if (item == null || amount <= 0)
                return amount;

            amount = AddItemInternal(item, amount, duration);
            Instance.UpdateSlot();
            return amount;
        }

        public static bool CanAcceptItem(ItemData item, int duration)
        {
            return GetAvailableSpaceForItem(item, duration) > 0;
        }

        public static int GetAvailableSpaceForItem(ItemData item, int duration)
        {
            if (item == null || SlotDatas == null || PlayerSlotsCount <= 0)
                return 0;

            int availableSpace = 0;
            int slotCount = Mathf.Min(PlayerSlotsCount, SlotDatas.Count);

            for (int i = 0; i < slotCount; i++)
            {
                Item slotItem = SlotDatas[i].Item;
                if (slotItem == null)
                {
                    availableSpace += item.StackSize;
                    continue;
                }

                if (slotItem.Matches(item, duration) && slotItem.Amount < item.StackSize)
                    availableSpace += item.StackSize - slotItem.Amount;
            }

            return availableSpace;
        }

        public void UpdateSlot()
        {
            OnUpdateSlot?.Invoke(selectedSlot);
            SelectedItem = selectedSlot.Item;
        }

        public void ClearPlayerInventory()
        {
            if (SlotDatas == null)
                return;

            if (DragAndDropSystem.DraggingItem?.Item != null)
                DragAndDropSystem.DestroyDragging();

            int end = Mathf.Min(PlayerSlotsCount, SlotDatas.Count);
            for (int i = 0; i < end; i++)
                ClearSlot(SlotDatas[i]);

            SelectedItem = null;
        }

        public void SetCurrentSlotIndex(int index)
        {
            if (BarSlots == null || BarSlots.Length == 0)
                return;

            currentSlotIndex = Mathf.Clamp(index, 0, BarSlots.Length - 1);
            SwitchedSlot();
        }

        private static int AddItemInternal(ItemData item, int amount, int duration)
        {
            for (int i = 0; i < PlayerSlotsCount && amount > 0; i++)
            {
                var p = SlotDatas[i].Item;
                if (p != null && p.Matches(item, duration) && p.Amount < item.StackSize)
                {
                    amount = AddAmountToItemData(p, amount);
                }
            }

            for (int i = 0; i < PlayerSlotsCount && amount > 0; i++)
            {
                var s = SlotDatas[i];
                if (s.Item == null)
                {
                    int space = item.StackSize;
                    int add = amount < space ? amount : space;
                    amount -= add;
                    CreateNewItem(item, add, duration, s);
                }
            }

            return amount;
        }

        public static void RemoveItem(PrizeData prizeData)
        {
            int length = prizeData.PrizeItems.Length;
            for (int i = 0; i < length; i++)
            {
                RemoveItem(prizeData.PrizeItems[i], prizeData.PrizeAmounts[i]);
            }
        }

        public static void RemoveItem(ItemData item, int amount)
        {
            if (amount <= 0) return;

            for (int i = 0; i < PlayerSlotsCount && amount > 0; i++)
            {
                var p = SlotDatas[i].Item;
                if (p != null && p.Matches(item))
                {
                    if (RemoveAmountFromItem(p, ref amount))
                      DragAndDropSystem.DestroyItem(SlotDatas[i]);
                }
            }

            Instance.UpdateSlot();
        }

        public static int RemoveItem(Slot slot, int amount)
        {
            if (amount <= 0) return amount;

            var p = slot.Item;
            if (p == null) return amount;

            if (RemoveAmountFromItem(p, ref amount))
                DragAndDropSystem.DestroyItem(slot);

            Instance.UpdateSlot();
            return amount;
        }

        public static void ClearSlot(Slot slotData)
        {
            if (slotData?.Item == null)
                return;

            if (Instance != null && Instance.SlotItemPrefab != null && slotData.Item.Transform != null)
            {
                Instance.Pool.Release(Instance.SlotItemPrefab, slotData.Item.Transform.gameObject);
            }
            else if (slotData.Item.Transform != null)
            {
                Destroy(slotData.Item.Transform.gameObject);
            }

            slotData.Item = null;
        }

        private static bool RemoveAmountFromItem(Item item, ref int amount)
        {
            int toRemove = amount < item.Amount ? amount : item.Amount;
            item.Amount -= toRemove;
            amount -= toRemove;

            if (item.Amount > 0) item.Update();
            return item.Amount <= 0;
        }

        public static int AddAmountToItemData(Item itemData, int amount)
        {
            int space = itemData.ItemData.StackSize - itemData.Amount;
            int add = amount < space ? amount : space;
            itemData.Amount += add;
            itemData.Update();
            return amount - add;
        }

        private void SwitchedSlot()
        {
            SelectedBarSlotImage.transform.position = BarSlots[currentSlotIndex].Transform.position;
            SelectedItem = selectedSlot.Item;
            OnSwitchSlot?.Invoke(selectedSlot);
        }

        public static Slot FindEmptySlotForItemReturn()
        {
            for (int i = 0; i < SlotDatas.Count; i++)
            {
                var s = SlotDatas[i];
                if (s.Item == null && s.Transform.gameObject.activeInHierarchy) return s;
            }
            return null;
        }
        public static Slot FindEmpySlot()
        {
            for (int i = 0; i < PlayerSlotsCount; i++)
                if (SlotDatas[i].Item == null)
                    return SlotDatas[i];
            return null;
        }
    }

    [System.Flags]
    public enum SlotType
    {
        None = 0,
        Weapon = 1 << 0,
        Armor = 1 << 1,
        Potion = 1 << 2,
        QuestItem = 1 << 3,
        Crafting = 1 << 4,
        All = ~0
    }

    public enum TargetSlotArea { PlayerBar, Backpack, Chest }

    public class Slot
    {
        public Transform Transform;
        public Item Item;
        public SlotType Type;

        public Slot(Transform tr)
        {
            Transform = tr;
            Type = SlotType.All;
        }
        public Slot(Transform transform, SlotType slotType)
        {
            this.Transform = transform;
            this.Type = slotType;
        }
    }

    [Serializable]
    public class Item
    {
        public int Amount, Duration;
        public Transform Transform;
        public readonly ItemData ItemData;

        public bool hasMaxAmount => Amount == ItemData.StackSize;

        private TextMeshProUGUI amountTxt;

        public bool Matches(Item other) => other.ItemData == ItemData && other.Duration == Duration;
        public bool Matches(Item other, int otherDuration) => other.ItemData == ItemData && (otherDuration == InventoryManager.MAX_DURATION_VALUE ? other.Duration : otherDuration) == Duration;
        public bool Matches(ItemData other) => other == ItemData && other.MaxDuration == Duration;

        public bool Matches(ItemData other, int otherDuration) => other == ItemData && (otherDuration == InventoryManager.MAX_DURATION_VALUE ? other.MaxDuration : otherDuration) == Duration;

        public Item(int amount, int duration, Transform transform, ItemData itemData)
        {
            this.Amount = amount;
            this.Duration = duration;
            this.Transform = transform;
            this.ItemData = itemData;

            transform.GetComponent<Image>().sprite = itemData.Sprite;
            amountTxt = transform.GetChild(0).GetComponent<TextMeshProUGUI>();
            amountTxt.text = amount.ToString();
        }

        public void Update()
        {
            amountTxt.text = Amount.ToString();
        }
    }
}
