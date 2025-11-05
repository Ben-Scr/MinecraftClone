using TMPro;
using UnityEngine;

namespace BenScr.MCC
{
    public class GameController : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI fpsTxt;
        private void Update()
        {
            fpsTxt.text = "FPS: " + (1f / Time.unscaledDeltaTime).ToString("0");
        }
    }
}