
namespace BenScr.MinecraftClone
{
    using System.Collections.Generic;
    using System.Linq;
    using TMPro;
    using Unity.Mathematics;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.InputSystem;
    using UnityEngine.UI;

    public class DragAndDropSystem : MonoBehaviour, IPointerDownHandler
    {
        [SerializeField] private GraphicRaycaster graphicRaycaster;

        [SerializeField] private Transform _canvas;

        [SerializeField] private InputActionReference combineReference;
        [SerializeField] private InputActionReference autoMoveReference;

        [SerializeField] private TextMeshProUGUI pointedItemInfo;
        [SerializeField] private GameObject itemInteractionInfo;

        public static Transform canvas => Instance._canvas;

        public static DraggedItem DraggingItem;

        private DraggedItem lastDraggingItem;

        private float dragStartTime = 0;

        public static DragAndDropSystem Instance;

        private Slot hoveringSlotData;
        private Slot lastHorveredSlotData;
        private Slot itemInfoSlotData;
        private ItemData itemInfoItemData;
        private PointerEventData pointerEventData;
        private readonly List<RaycastResult> raycastResults = new();
        public static Transform PointedTransform;

        [Header("Slot Hover Animation")]
        private float progress = 0;
        [SerializeField] private float hoverAnimSpeed;
        [SerializeField] private Color hoverColor;
        private Color originalColor;

        private void Awake()
        {
            Instance = this;
            DraggingItem = null;

        }

        private void OnEnable()
        {
            combineReference.action.Enable();
            autoMoveReference.action.Enable();
        }
        private void OnDisable()
        {

            combineReference.action.Disable();
            autoMoveReference.action.Disable();
        }

        public static bool IsSlotCompatible(Slot slot, ItemType itemType)
        {
            if (slot.Type == SlotType.All || slot.Type == SlotType.Crafting)
                return true;
            if (slot.Type == SlotType.None)
                return false;

            return (slot.Type & (SlotType)itemType) != 0;
        }

        public void OnPointerDown(PointerEventData eventData) // Used for Selecting a SlotItem
        {
            if (eventData.pointerEnter == null || hoveringSlotData == null || hoveringSlotData.Item == null || DraggingItem != null)
            {
                return;
            }

            if (!Input.GetMouseButtonDown(1))
            {
                SelectItem(new DraggedItem(hoveringSlotData, hoveringSlotData.Item));
                hoveringSlotData.Item = null;

                CheckBindings();
            }
            else
            {
                int halfAmount = (int)math.ceil(hoveringSlotData.Item.Amount / 2f);
                int difference = hoveringSlotData.Item.Amount - halfAmount;

                hoveringSlotData.Item.Amount = halfAmount;
                hoveringSlotData.Item.Update();
                SelectItem(new DraggedItem(hoveringSlotData, hoveringSlotData.Item));

                if (difference > 0)
                {
                   InventoryManager. DuplicateItem(hoveringSlotData.Item, hoveringSlotData, difference);
                }

                else
                {
                    hoveringSlotData.Item = null;
                }

                CheckBindings();
            }

            /*if (hoveringSlotData.type == SlotType.Crafting)
            {
                ItemCraftingManager.instance.UpdateCraftingResult();
            }
            else if (hoveringSlotData.transform == ItemCraftingManager.instance.resultSlotTr)
            {
                ItemCraftingManager.instance.Craft();
                ItemCraftingManager.instance.UpdateCraftingResult();
            }*/
        }

        private void ShowItemPointerInfo()
        {
            ItemData hoveredItemData = hoveringSlotData?.Item?.ItemData;
            if (itemInfoSlotData == hoveringSlotData &&
                itemInfoItemData == hoveredItemData &&
                (hoveredItemData != null || !itemInteractionInfo.activeSelf))
            {
                return;
            }

            itemInfoSlotData = hoveringSlotData;
            itemInfoItemData = hoveredItemData;

            if (hoveredItemData != null)
            {
                pointedItemInfo.text = hoveredItemData.Name;
                itemInteractionInfo.transform.position = hoveringSlotData.Transform.position + new Vector3(50, 50, 0);
                if (!itemInteractionInfo.activeSelf)
                    itemInteractionInfo.SetActive(true);
            }
            else if (itemInteractionInfo.activeSelf)
            {
                itemInteractionInfo.SetActive(false);
            }
        }

        private void CheckBindings()
        {
            if (combineReference.action.IsPressed())
            {
                CombineItems();
            }
            else if (autoMoveReference.action.IsPressed())
            {
                //bool chestOpen = ChestHandler.selectedChestInfo != null && !ChestHandler.selectedChestInfo.chestData.luckChest;
                bool chestOpen = false;
                TargetSlotArea targetSlotArea = chestOpen ? TargetSlotArea.Chest : (InventoryManager.BarSlots.Contains(hoveringSlotData) ? TargetSlotArea.Backpack : TargetSlotArea.PlayerBar);
                InventoryManager.AddToTargetSlots(DraggingItem.Item, targetSlotArea);
            }
        }

        public static int IndexOf<T>(in T item, in T[] array)
        {
            int i = 0;
            foreach (var element in array)
            {
                if (element.Equals(item))
                {
                    return i;
                }
                i++;
            }

            throw new System.Exception("Item not found");
        }

        public void CombineItems()
        {
            if (DraggingItem.Item.hasMaxAmount) return;

            // bool destroyWhenEmptyChestSlot = (ChestHandler.selectedChestInfo?.chestData.destroyWhenEmpty ?? false) && (IndexOf(draggingItem.slotData, InventoryManager.slotDatas.ToArray()) >= InventoryManager.playerSlotsCount);
            bool destroyWhenEmptyChestSlot = false;
            int start = destroyWhenEmptyChestSlot ? InventoryManager.PlayerSlotsCount : 0;
            int end = (destroyWhenEmptyChestSlot || /*GameDataRegistry.GameData.settings.Combine_Items_From_Chest*/ true) ? InventoryManager.SlotDatas.Count : InventoryManager.PlayerSlotsCount;

            for (int i = start; i < end; i++)
            {
                if (DraggingItem.Item.hasMaxAmount) return;

                var slotData = InventoryManager.SlotDatas[i];

                if (slotData.Item == null) continue;

                bool itemsFit = slotData.Item.Matches(DraggingItem.Item);
                if (!itemsFit) continue;

                //bool canAddItem = GameDataRegistry.GameData.settings.Comine_Items_With_MaxStackSize || slotData.persistentItem.amount < slotData.persistentItem.itemData.stackSize;
                bool canAddItem = true;

                if (canAddItem)
                {
                    AddToItem(slotData, DraggingItem.Item, slotData.Item.Amount);
                }
            }
        }

        public static void SelectItem(DraggedItem draggingItem)
        {
            DragAndDropSystem.DraggingItem = draggingItem;
            DragAndDropSystem.DraggingItem.Item.Transform.SetParent(canvas);
            Instance.dragStartTime = Time.realtimeSinceStartup;
        }

        public void Update()
        {
            bool shouldProcessPointer = DraggingItem != null ||
                                        (InventoryManager.Instance != null && InventoryManager.Instance.IsBackpackOpen);
            if (!shouldProcessPointer)
            {
                PointedTransform = null;
                hoveringSlotData = null;
                SlotAnimation();
                ShowItemPointerInfo();
                return;
            }

            PointedTransform = GetHoveredTransform();
            hoveringSlotData = GetSlotDataByTransform(PointedTransform);

            SlotAnimation();
            ShowItemPointerInfo();

            if (DraggingItem == null) return;

            DraggingItemLogic();
        }

        public void DraggingItemLogic()
        {
            DraggingItem.Item.Transform.position = Input.mousePosition;

            if (lastDraggingItem != DraggingItem)
                DraggingItem.Item.Transform.GetComponent<RectTransform>().sizeDelta = new Vector2(60, 60);

            lastDraggingItem = DraggingItem;

            if (Time.realtimeSinceStartup - dragStartTime < 0.1f) return;

            bool leftMouse = Input.GetMouseButtonDown(0), rightMouse = Input.GetMouseButtonDown(1);
            int dropAmount = leftMouse ? DraggingItem.Item.Amount : (rightMouse ? 1 : 0);

            if (dropAmount == 0)
            {
                return;
            }

            if (PointedTransform == null)
            {
                DropItem(dropAmount);
                return;
            }

            if (hoveringSlotData == null || !IsSlotCompatible(hoveringSlotData, DraggingItem.Item.ItemData.Type))
            {
                if (PointedTransform.name == "Drop_Layer")
                    DropItem(dropAmount);
                else
                    ReturnItem();

                return;
            }

            if (leftMouse)
            {
                if (hoveringSlotData.Item == null)
                    SetSlotItem(hoveringSlotData);

                else if (!hoveringSlotData.Item.Matches(DraggingItem.Item))
                    SwitchSlotItems(hoveringSlotData);
                else
                    AddToItem(DraggingItem.Item, hoveringSlotData, dropAmount);
            }
            else
            {
                if (hoveringSlotData.Item == null)
                {
                   InventoryManager.DuplicateItem(DraggingItem.Item, hoveringSlotData, 1);
                    DraggingItem.Item.Amount--;

                    if (DraggingItem.Item.Amount == 0)
                        DestroyDragging();

                    else
                        DraggingItem.Item.Update();
                }
                else if (hoveringSlotData.Item.Matches(DraggingItem.Item))
                {
                    AddToItem(DraggingItem.Item, hoveringSlotData, 1);
                }
            }

            if (hoveringSlotData.Type == SlotType.Crafting)
            {
                //ItemCraftingManager.instance.UpdateCraftingResult();
            }
        }

        private void SlotAnimation()
        {
            if (lastHorveredSlotData != hoveringSlotData)
            {
                if (lastHorveredSlotData?.Transform != null)
                {
                    lastHorveredSlotData.Transform.GetComponent<Image>().color = originalColor;
                }

                progress = 0;
                lastHorveredSlotData = hoveringSlotData;
            }

            if (lastHorveredSlotData != null)
            {
                if (progress <= 1)
                {
                    AnimateSlotColor();
                }
            }
        }
        private void AnimateSlotColor()
        {
            var img = lastHorveredSlotData.Transform.GetComponent<Image>();

            if (progress == 0)
            {
                originalColor = img.color;
            }

            img.color = Color.Lerp(originalColor, hoverColor, progress);
            progress += Time.deltaTime * hoverAnimSpeed;
        }

        public static void AddToItem(Item from, Slot toSlot, int amount)
        {
            Item to = toSlot.Item;

            if (to.Amount == to.ItemData.StackSize)
            {
                SwitchSlotItems(toSlot);
                return;
            }

            int difference = to.Amount + amount - from.ItemData.StackSize; // 5 + 10 - 64 = -49
            int amountToAdd = amount - math.clamp(difference, 0, from.Amount);

            to.Amount += amountToAdd;

            to.Update();

            from.Amount -= amountToAdd;

            if (from.Amount <= 0)
                DestroyDragging();

            else
                from.Update();
        }

        public static void AddToItem(Item from, Item to, int amount)
        {
            int difference = (to.Amount + amount) - from.ItemData.StackSize; // 5 + 10 - 64 = -49
            int amountToAdd = amount - math.clamp(difference, 0, from.Amount);

            to.Amount += amountToAdd;

            to.Update();

            from.Amount -= amountToAdd;

            if (from.Amount <= 0)
                DestroyDragging();

            else
                from.Update();
        }

        public static void AddToItem(Slot fromSlot, Item to, int amount)
        {
            int difference = (to.Amount + amount) - fromSlot.Item.ItemData.StackSize; // 5 + 10 - 64 = -49
            int amountToAdd = amount - math.clamp(difference, 0, fromSlot.Item.Amount);

            to.Amount += amountToAdd;

            to.Update();

            fromSlot.Item.Amount -= amountToAdd;

            if (fromSlot.Item.Amount <= 0)
                DestroyItem(fromSlot);

            else
                fromSlot.Item.Update();
        }

        public static void DestroyDragging()
        {
            InventoryManager.Instance.Pool.Release(InventoryManager.Instance.SlotItemPrefab, DraggingItem.Item.Transform.gameObject);
            DraggingItem.Item = null;
            DraggingItem = null;
        }

        public static void DestroyItem(Slot slotData)
        {
            if (slotData.Item == null) return;

            InventoryManager.Instance.Pool.Release(InventoryManager.Instance.SlotItemPrefab, slotData.Item.Transform.gameObject);
            slotData.Item = null;
        }

        public static void SwitchSlotItems(Slot slotData)
        {
            Item draggedItem = DraggingItem.Item;
            Item switchItem = slotData.Item;

            SelectItem(new DraggedItem(DraggingItem.SlotData, switchItem));

            draggedItem.Transform.SetParent(slotData.Transform);
            slotData.Item = draggedItem;

            DraggingItem.Item.Transform.position = Input.mousePosition;
        }

        public void SetSlotItem(Slot slotData)
        {
            slotData.Item = DraggingItem.Item;
            slotData.Item.Transform.SetParent(slotData.Transform);

            DeselectDragging();
        }

        public void DeselectDragging()
        {
            DraggingItem = null;
        }

        public void ReturnItem() //Returns the Dragging Item
        {
            if (DraggingItem.SlotData.Item == null)
            {
                DraggingItem.SlotData.Item = DraggingItem.Item;
                DraggingItem.Item.Transform.SetParent(DraggingItem.SlotData.Transform);
                DraggingItem = null;
            }
            else if (DraggingItem.SlotData.Item.ItemData == DraggingItem.Item.ItemData)
            {
                AddToItem(DraggingItem.Item, DraggingItem.SlotData, DraggingItem.Item.Amount);
            }
            else
            {
                Slot slotData = InventoryManager.FindEmptySlotForItemReturn();

                if (slotData != null)
                {
                    slotData.Item = DraggingItem.Item;
                    DraggingItem.Item.Transform.SetParent(slotData.Transform);
                    DraggingItem = null;
                }
                else
                {
                    DropItem(DraggingItem.Item.Amount);
                }
            }
        }

        public void DropItem(int amount)
        {
            if (DraggingItem?.Item == null || amount <= 0)
                return;

            int amountToDrop = math.clamp(amount, 1, DraggingItem.Item.Amount);

            Item item = DraggingItem.Item;
            if (!DroppedItemManager.TryDrop(item.ItemData, amountToDrop, item.Duration))
                return;

            item.Amount -= amountToDrop;

            if (item.Amount <= 0)
                DestroyDragging();
            else
                item.Update();

            InventoryManager.Instance.UpdateSlot();
        }

        private Transform GetHoveredTransform()
        {
            EventSystem eventSystem = EventSystem.current;
            if (graphicRaycaster == null || eventSystem == null)
                return null;

            pointerEventData ??= new PointerEventData(eventSystem);
            pointerEventData.Reset();
            pointerEventData.position = Input.mousePosition;
            raycastResults.Clear();
            graphicRaycaster.Raycast(pointerEventData, raycastResults);
            if (raycastResults.Count > 0)
                return raycastResults[0].gameObject.transform;

            return null;
        }

        public static Slot GetSlotDataByTransform(in Transform transform)
        {
            foreach (var slotData in InventoryManager.SlotDatas)
            {
                if (slotData.Transform == transform) return slotData;
            }

            return null;
        }
    }

    public class DraggedItem
    {
        public Slot SlotData;
        public Item Item;

        public DraggedItem(Slot from, Item itemData)
        {
            this.SlotData = from;
            this.Item = itemData;
        }
    }
}
