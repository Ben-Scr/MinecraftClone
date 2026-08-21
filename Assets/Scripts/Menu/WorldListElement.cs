using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class WorldListElement : MonoBehaviour
{
    [SerializeField] private TMP_Text worldNameText;
    [SerializeField] private TMP_Text lastPlayedText;
    [SerializeField] private Button selectButton;
    [SerializeField] private Image background;

    private WorldInfo worldInfo;
    private Action<WorldInfo> onSelect;

    public string WorldGuid => worldInfo?.Guid;

    public void Initialize(WorldInfo info, Action<WorldInfo> selectCallback)
    {
        worldInfo = info;
        onSelect = selectCallback;

        worldNameText.text = info.WorldName;
        lastPlayedText.text = $"Last played: {FormatLastPlayed(info)}";

        selectButton.onClick.RemoveAllListeners();
        selectButton.onClick.AddListener(Select);
    }

    public void SetSelected(bool selected)
    {
        background.color = selected
            ? new Color(0.18f, 0.5f, 0.8f, 0.95f)
            : new Color(0.12f, 0.12f, 0.12f, 0.95f);
    }

    private void Select()
    {
        onSelect?.Invoke(worldInfo);
    }

    private static string FormatLastPlayed(WorldInfo info)
    {
        if (info.LastPlayedUtcTicks <= 0)
            return "Never";

        return info.LastPlayedUtc.ToLocalTime().ToString("g");
    }
}
