#region

using Controllers.State;
using Controllers.World;
using UnityEngine;
using UnityEngine.EventSystems;

#endregion

namespace Controllers.UI.Components.Button
{
    public class ThreadingModeButtonController : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            int step = eventData.GetButtonAsInt();

            if ((step == -1) && (OptionsController.Current.ThreadingMode == 0))
            {
                OptionsController.Current.ThreadingMode = (ThreadingMode) 1;
            }
            else
            {
                OptionsController.Current.ThreadingMode =
                    (ThreadingMode) ((int) (OptionsController.Current.ThreadingMode + step) % 2);
            }
        }
    }
}
