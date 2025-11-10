using UnityEngine;

namespace BenScr.MCC
{
    public class SettingsContainer : MonoBehaviour
    {
        public bool DebugRendering = false;
        public bool DebugGizmos = false;

        public static SettingsContainer Settings;

        private void Awake()
        {
            Settings = this;
        }
    }
}
