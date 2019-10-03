#region

using System.ComponentModel;
using Controllers.State;

#endregion

namespace Controllers.UI.Components.Text
{
    public class CPUCoreUtilizationTextController : OptionDisplayTextController
    {
        protected override void UpdateTextObjectText(PropertyChangedEventArgs args, bool force = false)
        {
            if (force || args.PropertyName.Equals(nameof(OptionsController.Current.CPUCoreUtilization)))
            {
                _TextObject.text = string.Format(_Format, OptionsController.Current.CPUCoreUtilization);
            }
        }
    }
}