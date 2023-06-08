// Editor/VRCAvatarClippy.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace VRCClippy {
    public class Checker {
        public List<Diagnostics.Diagnostic> diagnostics;
        private HashSet<LayerId>[] trackingOwners;

        private static string[] gParts;

        public static string[] Parts {
            get {
                if (gParts == null) {
                    gParts = new string[] {
                            "Head",
                            "LeftHand",
                            "RightHand",
                            "Hip",
                            "LeftFoot",
                            "RightFoot",
                            "LeftFingers",
                            "RightFingers",
                            "Eyes",
                            "Mouth",
                        };
                }
                return gParts;
            }
        }

        private static HashSet<string> gMecanimBones;

        private static HashSet<string> MecanimBones {
            get {
                if (gMecanimBones != null)
                    return gMecanimBones;

                gMecanimBones = new HashSet<string>();
                foreach (string bone in new string[] {
                    "Chest Front-Back",
                    "Chest Left-Right",
                    "Chest Twist Left-Right",
                    "Head Nod Down-Up",
                    "Head Tilt Left-Right",
                    "Head Turn Left-Right",
                    "Jaw Close",
                    "Jaw Left-Right",
                    "Left Arm Down-Up",
                    "Left Arm Front-Back",
                    "Left Arm Twist In-Out",
                    "Left Eye Down-Up",
                    "Left Eye In-Out",
                    "Left Foot Up-Down",
                    "Left Forearm Stretch",
                    "Left Forearm Twist In-Out",
                    "Left Hand Down-Up",
                    "Left Hand In-Out",
                    "Left Lower Leg Stretch",
                    "Left Lower Leg Twist In-Out",
                    "Left Shoulder Down-Up",
                    "Left Shoulder Front-Back",
                    "Left Toes Up-Down",
                    "Left Upper Leg Front-Back",
                    "Left Upper Leg In-Out",
                    "Left Upper Leg Twist In-Out",
                    "Neck Nod Down-Up",
                    "Neck Tilt Left-Right",
                    "Neck Turn Left-Right",
                    "Right Arm Down-Up",
                    "Right Arm Front-Back",
                    "Right Arm Twist In-Out",
                    "Right Eye Down-Up",
                    "Right Eye In-Out",
                    "Right Foot Twist In-Out",
                    "Right Hand Down-Up",
                    "Right Hand In-Out",
                    "Right Lower Leg Stretch",
                    "Right Lower Leg Twist In-Out",
                    "Right Shoulder Down-Up",
                    "Right Shoulder Front-Back",
                    "Right Toes Up-Down",
                    "Right Upper Leg Front-Back",
                    "Right Upper Leg In-Out",
                    "Right Upper Leg Twist In-Out",
                    "Spine Front-Back",
                    "Spine Left-Right",
                    "Spine Twist Left-Right",
                    "UpperChest Front-Back",
                    "UpperChest Left-Right",
                    "UpperChest Twist Left-Right"
                }) {
                    gMecanimBones.Add(bone);
                }
                return gMecanimBones;
            }
        }

        public Checker() {
            diagnostics = new List<Diagnostics.Diagnostic>();
            trackingOwners = null;
        }

        public void ClearDiagnostics() {
            diagnostics.Clear();
        }

        public void Emit(Diagnostics.Diagnostic diagnostic) {
            diagnostics.Add(diagnostic);
        }

        public void Check() {
            UnityEngine.Object[] objects =
                UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>();
            if (objects.Length == 0) {
                Emit(new Diagnostics.NoAvatarDescriptor());
                return;
            }
            if (objects.Length > 1) {
                Emit(new Diagnostics.MultipleAvatarDescriptors(objects));
                return;
            }

            VRCAvatarDescriptor avatarDescriptor = (VRCAvatarDescriptor)objects[0];

            // Check eye look.
            if (avatarDescriptor.enableEyeLook)
                CheckEyeLook(avatarDescriptor.customEyeLookSettings);

            // Check animation controllers.
            if (avatarDescriptor.customizeAnimationLayers) {
                // Initialize tracking owners list.
                trackingOwners = new HashSet<LayerId>[Parts.Length];
                for (int partIndex = 0; partIndex < trackingOwners.Length; partIndex++)
                    trackingOwners[partIndex] = new HashSet<LayerId>();

                // Initialize layer type list.
                var layerTypes = new HashSet<VRCAvatarDescriptor.AnimLayerType>();

                // Iterate over layers.
                foreach (VRCAvatarDescriptor.CustomAnimLayer customAnimLayer in
                        avatarDescriptor.baseAnimationLayers) {
                    if (layerTypes.Contains(customAnimLayer.type))
                        Emit(new Diagnostics.DuplicateLayerType(customAnimLayer.type));
                    layerTypes.Add(customAnimLayer.type);

                    // TODO: check base layer for conflicts with others.
                    if (customAnimLayer.animatorController == null)
                        continue;
                    CheckAnimationController(
                        (AnimatorController)customAnimLayer.animatorController,
                        customAnimLayer.type,
                        avatarDescriptor);
                }

                for (int partIndex = 0; partIndex < trackingOwners.Length; partIndex++) {
                    HashSet<LayerId> layerIds = trackingOwners[partIndex];
                    if (layerIds.Count > 1) {
                        Emit(new Diagnostics.TrackingModifiedInMultipleLayers(
                            partIndex, layerIds));
                    }
                }
            }

            // Check objects.
            CheckAvatarDescendants(avatarDescriptor);
        }

        private void CheckEyeLook(VRCAvatarDescriptor.CustomEyeLookSettings eyeLook) {
            CheckEyesSameAxis(eyeLook.eyesLookingDown, eyeLook.eyesLookingUp, true);
            CheckEyesSameAxis(eyeLook.eyesLookingLeft, eyeLook.eyesLookingRight, false);

            CheckEyeBonePointsUp(eyeLook.leftEye, false);
            CheckEyeBonePointsUp(eyeLook.rightEye, true);
        }

        private void CheckEyesSameAxis(
                VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations min,
                VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations max,
                bool vertical) {
            CheckEyeSameAxis(min.left, max.left, vertical, false);
            CheckEyeSameAxis(min.right, max.right, vertical, true);
        }

        private void CheckEyeSameAxis(Quaternion min, Quaternion max, bool vertical, bool right) {
            Vector3 minEuler = min.eulerAngles, maxEuler = max.eulerAngles;
            CheckEyeAxis(minEuler.x, maxEuler.x, vertical, right, "X");
            CheckEyeAxis(minEuler.y, maxEuler.y, vertical, right, "Y");
            CheckEyeAxis(minEuler.z, maxEuler.z, vertical, right, "Z");
        }

        private void CheckEyeAxis(float min, float max, bool vertical, bool right, string axis) {
            if (min == 0.0f && max != 0.0f)
                Emit(new Diagnostics.SuspiciousEyeAxis(true, vertical, right, axis));
            else if (min != 0.0f && max == 0.0f)
                Emit(new Diagnostics.SuspiciousEyeAxis(false, vertical, right, axis));
        }

        private void CheckEyeBonePointsUp(Transform eye, bool right) {
            Vector3 angles = eye.localEulerAngles;
            if (angles.x != 0.0f || angles.y != 0.0f || angles.z != 0.0f)
                Emit(new Diagnostics.EyeBoneNotPointingUp(right));
        }

        private void CheckAnimationController(
                AnimatorController controller,
                VRCAvatarDescriptor.AnimLayerType layerType,
                VRCAvatarDescriptor avatarDescriptor) {
            for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++) {
                AnimatorControllerLayer layer = controller.layers[layerIndex];

                LayerId layerId = new LayerId();
                layerId.layerName = layer.name;
                layerId.layerType = layerType;

                if (layer.blendingMode == AnimatorLayerBlendingMode.Additive &&
                        layerType != VRCAvatarDescriptor.AnimLayerType.Additive) {
                    Emit(new Diagnostics.AdditiveLayer(layerId));
                }

                // TODO: Only if nobody else sets it.
                if (layerIndex > 0 &&
                        layer.stateMachine.states.Length > 0 &&
                        layer.defaultWeight == 0.0f) {
                    Emit(new Diagnostics.ZeroWeightLayer(layerId));
                }

                CheckStateMachine(layer.stateMachine, layerId);
            }

            foreach (AnimationClip animationClip in controller.animationClips)
                CheckAnimationClip(animationClip, layerType, avatarDescriptor);
        }

        private void CheckStateMachine(AnimatorStateMachine stateMachine, LayerId layerId) {
            foreach (ChildAnimatorStateMachine childStateMachine in stateMachine.stateMachines)
                CheckStateMachine(childStateMachine.stateMachine, layerId);

            foreach (ChildAnimatorState childState in stateMachine.states)
                CheckState(childState.state, layerId);
        }

        private void CheckState(AnimatorState state, LayerId layerId) {
            // Check transitions.
            if (state.transitions.Length == 0)
                Emit(new Diagnostics.StuckState(layerId, state.name));

            // Check behaviors.
            foreach (StateMachineBehaviour behavior in state.behaviours) {
                if (!(behavior is VRCAnimatorTrackingControl))
                    continue;

                var trackingControl = (VRCAnimatorTrackingControl)behavior;
                RecordTrackingPart(trackingControl.trackingHead, 0, layerId);
                RecordTrackingPart(trackingControl.trackingLeftHand, 1, layerId);
                RecordTrackingPart(trackingControl.trackingRightHand, 2, layerId);
                RecordTrackingPart(trackingControl.trackingHip, 3, layerId);
                RecordTrackingPart(trackingControl.trackingLeftFoot, 4, layerId);
                RecordTrackingPart(trackingControl.trackingRightFoot, 5, layerId);
                RecordTrackingPart(trackingControl.trackingLeftFingers, 6, layerId);
                RecordTrackingPart(trackingControl.trackingRightFingers, 7, layerId);
                RecordTrackingPart(trackingControl.trackingEyes, 8, layerId);
                RecordTrackingPart(trackingControl.trackingMouth, 9, layerId);
            }
        }

        private void CheckAnimationClip(
                AnimationClip animationClip,
                VRCAvatarDescriptor.AnimLayerType layerType,
                VRCAvatarDescriptor avatarDescriptor) {
            EditorCurveBinding[] curveBindings = AnimationUtility.GetCurveBindings(animationClip);

            var allVisemeBlendshapes = new HashSet<string>();
            var usedVisemeBlendshapes = new HashSet<string>();
            string[] visemes = avatarDescriptor.VisemeBlendShapes;
            if (visemes != null) {
                foreach (string viseme in visemes)
                    allVisemeBlendshapes.Add(viseme);
            }

            foreach (EditorCurveBinding curveBinding in curveBindings) {
                string propertyName = curveBinding.propertyName;
                if (layerType != VRCAvatarDescriptor.AnimLayerType.Base &&
                        layerType != VRCAvatarDescriptor.AnimLayerType.Action &&
                        layerType != VRCAvatarDescriptor.AnimLayerType.Gesture &&
                        MecanimBones.Contains(propertyName)) {
                    Emit(new Diagnostics.MuscleAnimationInWrongLayer(
                        animationClip.name, layerType));
                    return;
                }

                if (propertyName.StartsWith("blendShape.")) {
                    string blendshape = propertyName.Substring(propertyName.IndexOf('.') + 1);
                    if (allVisemeBlendshapes.Contains(blendshape))
                        usedVisemeBlendshapes.Add(blendshape);
                }
            }

            if ((float)usedVisemeBlendshapes.Count / (float)allVisemeBlendshapes.Count >= 0.9f)
                Emit(new Diagnostics.AnimatesVisemes(animationClip));
        }

        private void RecordTrackingPart(
                VRC_AnimatorTrackingControl.TrackingType trackingType,
                int index,
                LayerId layerId) {
            if (trackingType != VRC_AnimatorTrackingControl.TrackingType.NoChange)
                trackingOwners[index].Add(layerId);
        }

        private void CheckAvatarDescendants(VRCAvatarDescriptor avatarDescriptor) {
            var gameObjects = new List<GameObject>();
            gameObjects.Add(avatarDescriptor.gameObject);

            while (gameObjects.Count > 0) {
                GameObject gameObject = gameObjects[gameObjects.Count - 1];
                gameObjects.RemoveAt(gameObjects.Count - 1);

                // Check modifications.
                PropertyModification[] modifications =
                    PrefabUtility.GetPropertyModifications(gameObject);
                if (modifications != null) {
                    bool foundMaterialModifications = false;
                    foreach (PropertyModification modification in modifications) {
                        if (modification.propertyPath.StartsWith("m_Materials.Array")) {
                            foundMaterialModifications = true;
                            break;
                        }
                    }
                    if (foundMaterialModifications)
                        Emit(new Diagnostics.OverriddenMaterials(gameObject));
                }

                Renderer renderer = gameObject.GetComponent<Renderer>();
                if (renderer != null) {
                    foreach (Material material in renderer.sharedMaterials)
                        CheckMaterial(material);
                }

                for (int kidIndex = 0; kidIndex < gameObject.transform.childCount; kidIndex++)
                    gameObjects.Add(gameObject.transform.GetChild(kidIndex).gameObject);
            }
        }


        private void CheckMaterial(Material material) {
            Shader shader = material.shader;
            for (int propertyIndex = 0; propertyIndex < shader.GetPropertyCount(); propertyIndex++) {
                string propertyName = shader.GetPropertyName(propertyIndex);
                ShaderPropertyType propertyType = shader.GetPropertyType(propertyIndex);
                if (new Regex(@"^_?spec", RegexOptions.IgnoreCase).IsMatch(propertyName) &&
                        propertyType == ShaderPropertyType.Color) {
                    Color color = material.GetColor(propertyName);
                    if (color[0] == 1.0 && color[1] == 1.0 && color[2] == 1.0)
                        Emit(new Diagnostics.WhiteSpecular(material));
                }
            }
        }
    }

    public class LayerId {
        public VRCAvatarDescriptor.AnimLayerType layerType;
        public string layerName;

        public override bool Equals(object otherObject) {
            if (!(otherObject is LayerId))
                return false;
            LayerId other = (LayerId)otherObject;
            return layerType == other.layerType && layerName.Equals(other.layerName);
        }

        public override int GetHashCode() {
            return layerType.GetHashCode() ^ layerName.GetHashCode();
        }

        public override string ToString() {
            return String.Format("layer {0} of the {1} controller", layerName, layerType);
        }
    }

    public class ClippyWindow : EditorWindow {
        ListView listView;
        private Checker checker;
        private List<Diagnostics.Diagnostic> diagnostics;

        [MenuItem("Tools/Run Clippy...")]
        public static void ShowWindow() {
            ClippyWindow clippyWindow = EditorWindow.GetWindow<ClippyWindow>();
            clippyWindow.Show();
            clippyWindow.Run();
        }

        void OnEnable() {
            titleContent = new GUIContent();
            titleContent.text = "VRChat Avatar Clippy";

            if (checker == null)
                checker = new Checker();

            if (diagnostics == null) {
                diagnostics = new List<Diagnostics.Diagnostic>();
                diagnostics.Add(null);
            }

            listView = new ListView(
                diagnostics,
                24,
                () => new Label(),
                (visualElement, index) => {
                    Label label = (Label)visualElement;
                    if (diagnostics[index] == null)
                        label.text = "No issues found.";
                    else
                        diagnostics[index].PopulateLabel(label);
                });

            listView.style.flexGrow = 1.0f;

            rootVisualElement.Add(listView);
        }

        private void Run() {
            checker.ClearDiagnostics();
            checker.Check();

            Finish();
        }

        private void Finish() {
            diagnostics.Clear();
            diagnostics.AddRange(checker.diagnostics.ToArray());
            if (diagnostics.Count == 0)
                diagnostics.Add(null);

            listView.Refresh();
        }
    }

    namespace Diagnostics {

        public abstract class Diagnostic {
            public abstract int Id { get; }
            public abstract string Message { get; }

            public void PopulateLabel(Label label) {
                label.text = String.Format("A{0,4:D4}: {1}", Id, Message);
            }
        }

        public class NoAvatarDescriptor : Diagnostic {
            public override int Id { get { return 0; } }
            public override string Message { get { return "No avatar descriptor was found."; } }
        }

        public class MultipleAvatarDescriptors : Diagnostic {
            private UnityEngine.Object[] objects;

            public override int Id { get { return 1; } }
            public override string Message {
                get {
                    string message = "Multiple avatar descriptors were found: ";
                    for (int i = 0; i < objects.Length; i++) {
                        if (i > 0)
                            message += ", ";
                        message += objects[i].name;
                    }
                    return message;
                }
            }

            public MultipleAvatarDescriptors(UnityEngine.Object[] objects) {
                this.objects = objects;
            }
        }

        public class SuspiciousEyeAxis : Diagnostic {
            private bool maxDirIsNonzero, vertical, rightEye;
            private string axis;

            public SuspiciousEyeAxis(bool maxDirIsNonzero, bool vertical, bool rightEye, string axis) {
                this.maxDirIsNonzero = maxDirIsNonzero;
                this.vertical = vertical;
                this.rightEye = rightEye;
                this.axis = axis;
            }

            public override int Id { get { return 2; } }
            public override string Message {
                get {
                    string maxDir = vertical ? "up" : "right";
                    string minDir = vertical ? "down" : "left";
                    return String.Format(
                        "For the {0} eye, the rotation corresponding to looking {1} modifies the " +
                        "{2} axis, but the rotation corresponding to looking {3} doesn't.",
                        rightEye ? "right" : "left",
                        maxDirIsNonzero ? maxDir : minDir,
                        axis,
                        maxDirIsNonzero ? minDir : maxDir
                    );
                }
            }
        }

        public class EyeBoneNotPointingUp : Diagnostic {
            private bool rightEye;

            public EyeBoneNotPointingUp(bool rightEye) {
                this.rightEye = rightEye;
            }

            public override int Id { get { return 3; } }
            public override string Message {
                get {
                    return String.Format("The {0} eye bone doesn't point up.",
                        rightEye ? "right" : "left");
                }
            }
        }

        public class TrackingModifiedInMultipleLayers : Diagnostic {
            int partIndex;
            HashSet<LayerId> layerIds;

            public TrackingModifiedInMultipleLayers(int partIndex, HashSet<LayerId> layerIds) {
                this.partIndex = partIndex;
                this.layerIds = layerIds;
            }

            public override int Id { get { return 4; } }

            public override string Message {
                get {
                    string message = "Tracking settings for " + Checker.Parts[partIndex] +
                        " are changed in: ";
                    bool first = true;
                    foreach (LayerId layerId in layerIds) {
                        if (first)
                            first = false;
                        else
                            message += ", ";
                        message += layerId;
                    }
                    return message;
                }
            }
        }

        public abstract class LayerDiagnostic : Diagnostic {
            protected LayerId layerId;

            public LayerDiagnostic(LayerId layerId) {
                this.layerId = layerId;
            }
        }

        public class AdditiveLayer : LayerDiagnostic {
            public AdditiveLayer(LayerId layerId) : base(layerId) { }

            public override int Id { get { return 5; } }

            public override string Message {
                get {
                    return "The additive " + layerId + " isn't in the additive controller.";
                }
            }
        }

        public class ZeroWeightLayer : LayerDiagnostic {
            public ZeroWeightLayer(LayerId layerId) : base(layerId) { }

            public override int Id { get { return 6; } }

            public override string Message {
                get {
                    return "The " + layerId + " has zero weight.";
                }
            }
        }

        public class DuplicateLayerType : Diagnostic {
            private VRCAvatarDescriptor.AnimLayerType layerType;

            public DuplicateLayerType(VRCAvatarDescriptor.AnimLayerType layerType) {
                this.layerType = layerType;
            }

            public override int Id { get { return 7; } }

            public override string Message {
                get {
                    return "The avatar descriptor has more than one controller of type " +
                        layerType;
                }
            }
        }

        public class MuscleAnimationInWrongLayer : Diagnostic {
            private string clipName;
            private VRCAvatarDescriptor.AnimLayerType layerType;

            public MuscleAnimationInWrongLayer(
                    string clipName, VRCAvatarDescriptor.AnimLayerType layerType) {
                this.clipName = clipName;
                this.layerType = layerType;
            }

            public override int Id { get { return 8; } }

            public override string Message {
                get {
                    return "The animation clip \"" + clipName + "\" animates muscles on the " +
                        layerType + " layer.";
                }
            }
        }

        public class StuckState : LayerDiagnostic {
            private string stateName;

            public StuckState(LayerId layerId, string stateName) : base(layerId) {
                this.stateName = stateName;
            }

            public override int Id { get { return 9; } }

            public override string Message {
                get {
                    return "The state \"" + stateName + "\" in " + layerId + " has no " +
                        "transitions.";
                }
            }
        }

        public class WhiteSpecular : Diagnostic {
            private Material material;

            public WhiteSpecular(Material material) {
                this.material = material;
            }

            public override int Id { get { return 10; } }

            public override string Message {
                get {
                    return "The material \"" + material.name + "\" appears to have a fully white " +
                        "specular color. This will appear as black in some worlds.";
                }
            }
        }

        // TODO: Implement
        public class OverriddenMaterials : Diagnostic {
            GameObject theObject;

            public OverriddenMaterials(GameObject theObject) {
                this.theObject = theObject;
            }

            public override int Id { get { return 11; } }

            public override string Message {
                get {
                    return "The object \"" + theObject.name + "\" overrides materials on its " +
                        "SkinnedMeshRenderer. This can cause materials to appear as magenta in " +
                        "VRChat.";
                }
            }
        }

        public class AnimatesVisemes : Diagnostic {
            AnimationClip animationClip;

            public AnimatesVisemes(AnimationClip animationClip) {
                this.animationClip = animationClip;
            }

            public override int Id { get { return 12; } }

            public override string Message {
                get {
                    return "The animation clip \"" + animationClip.name + "\" animates most " +
                        "visemes.";
                }
            }
        }
    }
}
