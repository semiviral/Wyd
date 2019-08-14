using Controllers.Game;
using UnityEngine;
using UnityEngine.UI;

namespace Controllers.UI.Components
{
    public class ExitButtonController : MonoBehaviour
    {
        public Button ExitButton;

        private void Start()
        {
            ExitButton.onClick.AddListener(() => GameController.Current.ApplicationClose(0));
        }
    }
}