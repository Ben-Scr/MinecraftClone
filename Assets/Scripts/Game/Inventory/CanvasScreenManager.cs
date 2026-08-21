using BenScr.MinecraftClone;
using System;
using UnityEngine;

public class CanvasScreenManager : MonoBehaviour
{
    public static GameObject ActiveScreen;
    public static CanvasScreenManager Instance { get; private set; }

    public static Action<GameObject> OnOpenScreen;
    public static Action<GameObject> OnCloseScreen;

    private void Awake()
    {
        ActiveScreen = null;
        Instance = this;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            CloseActiveScreen();
        }
    }

    public bool OpenScreen(GameObject screen)
    {
        if (ActiveScreen != null) return false;

        CloseActiveScreen();
        ActiveScreen = screen;
        screen.SetActive(true);
        InventoryManager.Instance.SelectedBarSlotImage.gameObject.SetActive(false);
        //WorldUIManager.instance.bgAnimator.SetBool("Active", true);
        OnOpenScreen?.Invoke(ActiveScreen);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        return true;
    }

    public void CloseActiveScreen()
    {
        if (ActiveScreen != null)
        {
            ActiveScreen.SetActive(false);
            InventoryManager.Instance.SelectedBarSlotImage.gameObject.SetActive(true);
            //WorldUIManager.instance.bgAnimator.SetBool("Active", false);
            OnCloseScreen?.Invoke(ActiveScreen);
            ActiveScreen = null;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}