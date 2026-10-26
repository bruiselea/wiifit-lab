using UdonSharp;
using VRC.SDKBase;

namespace WiiFitMotionLab
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CourseReset : UdonSharpBehaviour
    {
        public BalanceLocomotionController controller;

        public override void Interact()
        {
            if (Utilities.IsValid(controller))
            {
                controller.ResetToCourseStart();
            }
        }

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (player.isLocal && Utilities.IsValid(controller))
            {
                controller.ResetToCourseStart();
            }
        }
    }
}
