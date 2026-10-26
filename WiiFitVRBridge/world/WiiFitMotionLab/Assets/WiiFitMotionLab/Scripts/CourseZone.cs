using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace WiiFitMotionLab
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CourseZone : UdonSharpBehaviour
    {
        public BalanceLocomotionController controller;
        [Range(1, 3)] public int movementMode = BalanceLocomotionController.ModeWalking;
        public Transform resetPoint;

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!player.isLocal || !Utilities.IsValid(controller))
            {
                return;
            }

            if (movementMode == BalanceLocomotionController.ModeSki)
            {
                controller.BeginSki(resetPoint);
            }
            else if (movementMode == BalanceLocomotionController.ModeSnowboard)
            {
                controller.BeginSnowboard(resetPoint);
            }
            else
            {
                controller.BeginWalking(resetPoint);
            }
        }

        public override void OnPlayerTriggerExit(VRCPlayerApi player)
        {
            if (player.isLocal && Utilities.IsValid(controller))
            {
                controller.EndCourse();
            }
        }
    }
}
