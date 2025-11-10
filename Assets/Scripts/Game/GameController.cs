using TMPro;
using UnityEngine;

namespace BenScr.MCC
{
    public class GameController : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI fpsTxt;
        [SerializeField] private TextMeshProUGUI playerPosTxt;
        [SerializeField] private int targetFPS = -1;

        private void Awake()
        {
            Application.targetFrameRate = targetFPS < 0 ? QualitySettings.vSyncCount : targetFPS;
        }
        private void Update()
        {
            fpsTxt.text = "FPS: " + (1f / Time.unscaledDeltaTime).ToString("0");
            Vector3 playerPos = PlayerController.instance.transform.position;
            playerPosTxt.text = $"X: {playerPos.x.ToString("0")} Y: {playerPos.y.ToString("0")} Z: {playerPos.z.ToString("0")}";
        }
    }
}