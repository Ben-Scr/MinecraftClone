using BenScr.CubeDash;
using BenScr.MinecraftClone;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MenuController : MonoBehaviour
{
    [Header("World Browser")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button createWorldButton;
    [SerializeField] private RectTransform worldListContent;
    [SerializeField] private WorldListElement worldElementPrefab;

    [Header("Create World Screen")]
    [SerializeField] private GameObject createWorldScreen;
    [SerializeField] private TMP_InputField worldNameInput;
    [SerializeField] private TMP_InputField seedInput;
    [SerializeField] private Button confirmCreateButton;
    [SerializeField] private Button cancelCreateButton;

    private readonly List<WorldListElement> worldElements = new();
    private WorldInfo selectedWorld;
    private bool isTransitioning;

    private void Awake()
    {
        if (!PersistentSceneManager.Check())
            return;


        playButton.onClick.AddListener(OnClickPlay);
        createWorldButton.onClick.AddListener(OpenCreateWorldScreen);
        confirmCreateButton.onClick.AddListener(OnClickConfirmCreate);
        cancelCreateButton.onClick.AddListener(CloseCreateWorldScreen);

        playButton.interactable = false;
        createWorldScreen.SetActive(false);
        RefreshWorldList();
    }

    private bool HasAllReferences()
    {
        return playButton != null &&
               createWorldButton != null &&
               worldListContent != null &&
               worldElementPrefab != null &&
               createWorldScreen != null &&
               worldNameInput != null &&
               seedInput != null &&
               confirmCreateButton != null &&
               cancelCreateButton != null;
    }

    private void OnDestroy()
    {
        if (playButton != null)
            playButton.onClick.RemoveListener(OnClickPlay);
        if (createWorldButton != null)
            createWorldButton.onClick.RemoveListener(OpenCreateWorldScreen);
        if (confirmCreateButton != null)
            confirmCreateButton.onClick.RemoveListener(OnClickConfirmCreate);
        if (cancelCreateButton != null)
            cancelCreateButton.onClick.RemoveListener(CloseCreateWorldScreen);
    }

    private void RefreshWorldList()
    {
        for (int i = worldListContent.childCount - 1; i >= 0; i--)
            Destroy(worldListContent.GetChild(i).gameObject);

        worldElements.Clear();

        var worlds = SaveController.GetWorldInfos();
        if (worlds.Count == 0)
        {
            selectedWorld = null;
            playButton.interactable = false;
            SetStatus("No worlds yet. Select Create to make one.");
            return;
        }

        foreach (WorldInfo worldInfo in worlds)
        {
            WorldListElement element = Instantiate(worldElementPrefab, worldListContent);
            element.Initialize(worldInfo, SelectWorld);
            element.SetSelected(selectedWorld != null && selectedWorld.Guid == worldInfo.Guid);
            worldElements.Add(element);
        }

        playButton.interactable = selectedWorld != null;

        if (selectedWorld == null)
            SetStatus("Select a world, then press Play.");
    }

    private void SelectWorld(WorldInfo worldInfo)
    {
        selectedWorld = worldInfo;
        playButton.interactable = true;

        foreach (WorldListElement element in worldElements)
            element.SetSelected(element.WorldGuid == worldInfo.Guid);

        SetStatus($"Selected: {worldInfo.WorldName}");
    }

    private async void OnClickPlay()
    {
        if (isTransitioning || selectedWorld == null)
            return;

        isTransitioning = true;
        playButton.interactable = false;
        createWorldButton.interactable = false;
        SetStatus($"Loading {selectedWorld.WorldName}...");

        // Let the status/control state render before file IO and JSON parsing start.
        await Task.Yield();

        SaveController.OperationResult result;
        try
        {
            result = await SaveController.TryLoadWorldAsync(selectedWorld.Guid);
        }
        catch (Exception ex)
        {
            result = SaveController.OperationResult.Failed(ex.Message);
        }

        if (!result.Success)
        {
            isTransitioning = false;
            createWorldButton.interactable = true;
            SetStatus(result.Error);
            RefreshWorldList();
            return;
        }

        await EnterGameScene();
    }

    private void OpenCreateWorldScreen()
    {
        worldNameInput.text = string.Empty;
        seedInput.text = SaveController.CreateRandomSeed().ToString();
        createWorldScreen.SetActive(true);
        worldNameInput.Select();
        worldNameInput.ActivateInputField();
    }

    private void CloseCreateWorldScreen()
    {
        createWorldScreen.SetActive(false);
    }

    private void OnClickConfirmCreate()
    {
        if (!int.TryParse(seedInput.text, out int seed))
        {
            //createWorldStatusText.text = "Seed must be a whole number.";
            return;
        }

        if (!SaveController.TryCreateWorld(worldNameInput.text, seed, out WorldInfo worldInfo, out string error))
        {
            //createWorldStatusText.text = error;
            return;
        }

        createWorldScreen.SetActive(false);
        selectedWorld = worldInfo;
        RefreshWorldList();
        SelectWorld(worldInfo);
    }

    private async Task EnterGameScene()
    {
        isTransitioning = true;
        FluidSimulator.Clear();
        FallingBlockSimulator.Clear();
        TerrainGenerator.Chunks.Clear();

        try
        {
            await PersistentSceneManager.UnLoadAndLoadScene(SceneType.Menu, SceneType.Game);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to enter the world: {ex.Message}");
            SaveController.ClearActiveWorld();
            SetStatus("Could not open the world.");
            isTransitioning = false;
            createWorldButton.interactable = true;
            RefreshWorldList();
        }
    }

    private void SetStatus(string message)
    {
        if (!string.IsNullOrEmpty(message))
            Debug.Log(message);
    }
}
